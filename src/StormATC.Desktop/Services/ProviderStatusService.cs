using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StormATC.Application.Abstractions;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Domain;

namespace StormATC.Desktop.Services;

/// <summary>
/// Tracks whether each external dependency is configured, connected, or absent.
/// </summary>
/// <remarks>
/// <para>
/// StormATC must run with no API keys, no simulator, and no network, so "not configured" is a
/// normal state rather than an error. This service exists to say so honestly: every badge shows
/// what is actually available, and nothing is reported as connected until it has been.
/// </para>
/// <para>
/// The collection is created once and mutated in place, so bindings survive refreshes.
/// </para>
/// </remarks>
public sealed class ProviderStatusService
{
    private readonly ISimulatorConnector _simulatorConnector;
    private readonly ILanguageModelClient _languageModel;
    private readonly ISpeechRecognitionService _speechRecognition;
    private readonly ITextToSpeechService _textToSpeech;
    private readonly IFlightPlanProvider _flightPlanProvider;
    private readonly ISecretStore _secretStore;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<LanguageModelOptions>
        _languageModelOptions;
    private readonly bool _offlineMode;

    /// <summary>Shown on every cloud badge in offline mode, so the reason is never ambiguous.</summary>
    private static string OfflineDetail => UiText.T("Offline mode — no external services are contacted");

    private readonly ProviderStatusItem _simulator;
    private readonly ProviderStatusItem _model;
    private readonly ProviderStatusItem _speechToText;
    private readonly ProviderStatusItem _textToSpeechStatus;
    private readonly ProviderStatusItem _flightPlan;
    private readonly ProviderStatusItem _storage;

    /// <summary>Creates the status service and takes an initial reading.</summary>
    public ProviderStatusService(
        ISimulatorConnector simulatorConnector,
        ILanguageModelClient languageModel,
        ISpeechRecognitionService speechRecognition,
        ITextToSpeechService textToSpeech,
        IFlightPlanProvider flightPlanProvider,
        ISecretStore secretStore,
        Microsoft.Extensions.Options.IOptionsMonitor<LanguageModelOptions> languageModelOptions,
        Microsoft.Extensions.Options.IOptions<StormATC.Application.Options.ApplicationOptions> applicationOptions)
    {
        ArgumentNullException.ThrowIfNull(applicationOptions);
        _offlineMode = applicationOptions.Value.OfflineMode;

        _simulatorConnector = simulatorConnector ?? throw new ArgumentNullException(nameof(simulatorConnector));
        _languageModel = languageModel ?? throw new ArgumentNullException(nameof(languageModel));
        _speechRecognition = speechRecognition ?? throw new ArgumentNullException(nameof(speechRecognition));
        _textToSpeech = textToSpeech ?? throw new ArgumentNullException(nameof(textToSpeech));
        _flightPlanProvider = flightPlanProvider ?? throw new ArgumentNullException(nameof(flightPlanProvider));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _languageModelOptions =
            languageModelOptions ?? throw new ArgumentNullException(nameof(languageModelOptions));

        _simulator = new ProviderStatusItem(UiText.T("Simulator"), _simulatorConnector.ConnectorName);
        _model = new ProviderStatusItem(UiText.T("Language model"), _languageModel.ProviderName);
        _speechToText = new ProviderStatusItem(UiText.T("Speech to text"), _speechRecognition.ProviderName);
        _textToSpeechStatus = new ProviderStatusItem(UiText.T("Controller voice"), _textToSpeech.ProviderName);
        _flightPlan = new ProviderStatusItem(UiText.T("Flight plan"), _flightPlanProvider.ProviderName);
        _storage = new ProviderStatusItem(UiText.T("Local storage"), "SQLite");

        Items =
        [
            _simulator, _model, _speechToText, _textToSpeechStatus, _flightPlan, _storage,
        ];

        _simulatorConnector.ConnectionChanged += OnSimulatorConnectionChanged;

        Refresh();
    }

    /// <summary>Every provider badge, in display order.</summary>
    public ObservableCollection<ProviderStatusItem> Items { get; }

    /// <summary>Records that local storage initialised, or failed to.</summary>
    public void SetStorageStatus(bool available, string detail) =>
        _storage.Update(
            available ? ProviderConnectionState.Connected : ProviderConnectionState.Error,
            detail);

    /// <summary>Records the outcome of a flight-plan import.</summary>
    public void SetFlightPlanStatus(bool loaded, string detail) =>
        _flightPlan.Update(
            loaded ? ProviderConnectionState.Connected : ProviderConnectionState.NotConfigured,
            detail);

    /// <summary>
    /// Re-reads every provider's self-reported state, without touching the secret store.
    /// </summary>
    /// <remarks>
    /// Synchronous, for the constructor and for callers that only need the simulator badge. The
    /// cloud badges it writes are the conservative answer, because it cannot see whether a key is
    /// stored; <see cref="RefreshAsync"/> is what produces the complete one.
    /// </remarks>
    private void Refresh()
    {
        _simulator.Update(MapSimulatorState(_simulatorConnector.ConnectionState), _simulatorConnector.ConnectorName);

        UpdateModelBadge(_languageModel.IsConfigured, hasKey: null);
        UpdateSpeechBadges(hasKey: null);
    }

    /// <summary>
    /// Re-reads every provider, including whether the API keys they need are stored.
    /// </summary>
    /// <remarks>
    /// A provider that holds a key reports itself unconfigured until its first call, which would
    /// make a badge say "not configured" about a key the user had just saved. Asking the secret
    /// store closes that gap. Only presence is read — never a value.
    /// </remarks>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Refresh();

        if (_offlineMode)
        {
            // Refresh already wrote the offline detail on every cloud badge; a stored key makes
            // no difference to any of them.
            return;
        }

        bool hasLanguageModelKey;
        bool hasDeepgramKey;

        try
        {
            hasLanguageModelKey = await _secretStore
                .HasSecretAsync(
                    SecretKeyForProvider(_languageModelOptions.CurrentValue.Provider),
                    cancellationToken)
                .ConfigureAwait(false);

            hasDeepgramKey = await _secretStore
                .HasSecretAsync(SecretKeys.DeepgramApiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // An unreadable secret store must not break the dashboard. The badges fall back to
            // what the providers say about themselves, which is the conservative answer.
            return;
        }

        UpdateModelBadge(_languageModel.IsConfigured, hasLanguageModelKey);
        UpdateSpeechBadges(hasDeepgramKey);
    }

    private static string SecretKeyForProvider(string? provider) =>
        LanguageModelProviders.Normalize(provider) switch
        {
            LanguageModelProviders.OpenAI => SecretKeys.OpenAIApiKey,
            LanguageModelProviders.Groq => SecretKeys.GroqApiKey,
            LanguageModelProviders.DeepSeek => SecretKeys.DeepSeekApiKey,
            LanguageModelProviders.Anthropic => SecretKeys.AnthropicApiKey,
            _ => SecretKeys.OpenRouterApiKey,
        };

    /// <summary>
    /// Writes the language-model badge.
    /// </summary>
    /// <param name="hasModelName">Whether a model identifier is configured.</param>
    /// <param name="hasKey">Whether a key is stored, or null when that has not been read.</param>
    private void UpdateModelBadge(bool hasModelName, bool? hasKey)
    {
        if (_offlineMode)
        {
            _model.Update(ProviderConnectionState.NotConfigured, OfflineDetail);
            return;
        }

        // A model needs both halves. Saying which half is missing is the difference between a
        // badge that helps and one that only reports failure.
        if (hasKey is bool keyStored && !(keyStored && hasModelName))
        {
            _model.Update(
                ProviderConnectionState.NotConfigured,
                keyStored
                    ? UiText.T("No model chosen — enter a model identifier in Settings")
                    : hasModelName
                        ? UiText.T("No API key — add one in Settings to enable the model")
                        : UiText.T("Not configured — deterministic phraseology only"));

            return;
        }

        _model.Update(
            hasModelName ? ProviderConnectionState.Configured : ProviderConnectionState.NotConfigured,
            hasModelName ? _languageModel.ProviderName : UiText.T("Not configured — deterministic phraseology only"));
    }

    /// <summary>Writes the two speech badges.</summary>
    /// <param name="hasKey">Whether a Deepgram key is stored, or null when that has not been read.</param>
    /// <remarks>
    /// A local engine (Whisper, Piper) needs no key and works offline, so its badge reflects
    /// only whether its files are configured.
    /// </remarks>
    private void UpdateSpeechBadges(bool? hasKey)
    {
        bool localRecognition = IsLocalEngine(_speechRecognition.ProviderName);
        bool localVoice = IsLocalEngine(_textToSpeech.ProviderName);

        bool recognitionReady = localRecognition
            ? _speechRecognition.IsConfigured
            : !_offlineMode && (hasKey ?? _speechRecognition.IsConfigured);
        bool voiceReady = localVoice
            ? _textToSpeech.IsConfigured
            : !_offlineMode && (hasKey ?? _textToSpeech.IsConfigured);

        _speechToText.Update(
            recognitionReady ? ProviderConnectionState.Configured : ProviderConnectionState.NotConfigured,
            recognitionReady
                ? _speechRecognition.ProviderName
                : localRecognition
                    ? UiText.T("Whisper is not set up — see Settings, Voice & PTT")
                    : _offlineMode
                        ? OfflineDetail
                        : UiText.T("No API key — use typed transmissions"));

        _textToSpeechStatus.Update(
            voiceReady ? ProviderConnectionState.Configured : ProviderConnectionState.NotConfigured,
            voiceReady
                ? _textToSpeech.ProviderName
                : localVoice
                    ? UiText.T("Piper is not set up — see Settings, Voice & PTT")
                    : _offlineMode
                        ? OfflineDetail
                        : UiText.T("No API key — responses are text only"));
    }

    private static bool IsLocalEngine(string providerName) =>
        providerName.EndsWith("(local)", StringComparison.Ordinal);

    private static ProviderConnectionState MapSimulatorState(SimulatorConnectionState state) => state switch
    {
        SimulatorConnectionState.Connected => ProviderConnectionState.Connected,
        SimulatorConnectionState.Connecting => ProviderConnectionState.Connecting,
        SimulatorConnectionState.Reconnecting => ProviderConnectionState.Connecting,
        SimulatorConnectionState.Unavailable => ProviderConnectionState.Unavailable,
        SimulatorConnectionState.Error => ProviderConnectionState.Error,
        _ => ProviderConnectionState.NotConfigured,
    };

    private void OnSimulatorConnectionChanged(object? sender, SimulatorConnectionChangedEventArgs e) =>
        RunOnUiThread(() => _simulator.Update(
            MapSimulatorState(e.State),
            e.Message ?? _simulatorConnector.ConnectorName));

    /// <summary>Marshals onto the dispatcher; provider events arrive on background threads.</summary>
    private static void RunOnUiThread(Action action)
    {
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}

/// <summary>One provider badge.</summary>
public sealed partial class ProviderStatusItem : ObservableObject
{
    [ObservableProperty]
    private ProviderConnectionState _state = ProviderConnectionState.NotConfigured;

    [ObservableProperty]
    private string _detail;

    /// <summary>Creates a badge.</summary>
    public ProviderStatusItem(string name, string detail)
    {
        Name = name;
        _detail = detail;
    }

    /// <summary>What this badge describes, for example "Simulator".</summary>
    public string Name { get; }

    /// <summary>
    /// A short state word. Shown alongside the colour so the badge never relies on colour alone.
    /// </summary>
    public string StateText => State switch
    {
        ProviderConnectionState.Connected => UiText.T("Connected"),
        ProviderConnectionState.Configured => UiText.T("Ready"),
        ProviderConnectionState.Connecting => UiText.T("Connecting"),
        ProviderConnectionState.Degraded => UiText.T("Degraded"),
        ProviderConnectionState.Unavailable => UiText.T("Unavailable"),
        ProviderConnectionState.Error => UiText.T("Error"),
        _ => UiText.T("Not configured"),
    };

    /// <summary>Applies a new state and detail.</summary>
    public void Update(ProviderConnectionState state, string detail)
    {
        State = state;
        Detail = detail;
    }

    partial void OnStateChanged(ProviderConnectionState value) => OnPropertyChanged(nameof(StateText));
}
