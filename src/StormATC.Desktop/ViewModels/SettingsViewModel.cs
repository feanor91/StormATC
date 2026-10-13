using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Application.Sessions;
using StormATC.Desktop.Services;
using StormATC.Domain.Intents;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Desktop.ViewModels;

/// <summary>
/// The Settings page: API keys, provider selection, realism, and local data.
/// </summary>
/// <remarks>
/// <para>
/// <b>API keys never enter this view model's state.</b> A key travels from the password box
/// straight to <see cref="ISecretStore"/> and is not retained, echoed, bound, or logged. The UI
/// reports only whether a key exists — the strongest form of masking available, and it removes
/// any path by which a key could reach a screenshot, a crash dump, or a diagnostics export.
/// </para>
/// <para>
/// Everything else is written to <c>usersettings.json</c> through
/// <see cref="UserSettingsService"/>. Some settings — the ones that decide which implementations
/// are registered — only take effect on restart, and the page says so rather than pretending
/// otherwise.
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject, IRefreshablePage
{
    private readonly ISecretStore _secretStore;
    private readonly ILanguageModelClient _languageModel;
    private readonly ISpeechRecognitionService _speechRecognition;
    private readonly ITextToSpeechService _textToSpeech;
    private readonly ISessionRepository _sessionRepository;
    private readonly SimulatorConnectorRouter _simulatorConnector;
    private readonly MsfsSimConnectConnector _msfsConnector;
    private readonly TrafficRadioService _trafficRadio;
    private readonly UserSettingsService _userSettings;
    private readonly ProviderStatusService _providerStatus;
    private readonly AtcSessionContext _sessionContext;
    private readonly IJoystickInputService _joystickInput;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>
    /// Offline mode as it was when the host was built, so the page can tell the difference
    /// between a setting that was changed and one that merely happens to be on.
    /// </summary>
    private readonly bool _offlineModeAtStartup;
    private readonly string _interfaceLanguageAtStartup;

    [ObservableProperty]
    private bool _hasOpenRouterKey;

    [ObservableProperty]
    private bool _hasOpenAIKey;

    [ObservableProperty]
    private bool _hasGroqKey;

    [ObservableProperty]
    private bool _hasDeepSeekKey;

    [ObservableProperty]
    private bool _hasAnthropicKey;

    [ObservableProperty]
    private bool _hasDeepgramKey;

    [ObservableProperty]
    private string _openRouterKeyStatus = UiText.T("No key stored");

    [ObservableProperty]
    private string _openAIKeyStatus = UiText.T("No key stored");

    [ObservableProperty]
    private string _groqKeyStatus = UiText.T("No key stored");

    [ObservableProperty]
    private string _deepSeekKeyStatus = UiText.T("No key stored");

    [ObservableProperty]
    private string _anthropicKeyStatus = UiText.T("No key stored");

    [ObservableProperty]
    private string _deepgramKeyStatus = UiText.T("No key stored");

    [ObservableProperty]
    private string _languageModelTestResult = string.Empty;

    [ObservableProperty]
    private string _deepgramTestResult = string.Empty;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _openRouterModel = string.Empty;

    [ObservableProperty]
    private string _openAIModel = string.Empty;

    [ObservableProperty]
    private string _groqModel = string.Empty;

    [ObservableProperty]
    private string _deepSeekModel = string.Empty;

    [ObservableProperty]
    private string _anthropicModel = string.Empty;

    [ObservableProperty]
    private string _selectedLanguageModelProvider = LanguageModelProviders.OpenRouter;

    [ObservableProperty]
    private string _deepgramVoice = string.Empty;

    [ObservableProperty]
    private string _deepgramSpeechModel = string.Empty;

    [ObservableProperty]
    private string _selectedInputDevice = SystemDefaultDevice;

    [ObservableProperty]
    private string _speechRecognitionLanguage = "en";

    [ObservableProperty]
    private string _trainingExpectedPhrase = string.Empty;

    [ObservableProperty]
    private string _trainingTranscript = string.Empty;

    [ObservableProperty]
    private string _trainingStatus = string.Empty;

    [ObservableProperty]
    private bool _isTrainingVoice;

    [ObservableProperty]
    private int _trainingCorrectionCount;

    [ObservableProperty]
    private string _simBriefUserId = string.Empty;

    [ObservableProperty]
    private string _realismProfile = "Standard";

    [ObservableProperty]
    private string _atcLanguage = EnglishLanguageName;

    [ObservableProperty]
    private string _interfaceLanguage = WindowsLanguageName;

    [ObservableProperty]
    private string _speechToTextProvider = SpeechProviders.Deepgram;

    [ObservableProperty]
    private string _textToSpeechProvider = SpeechProviders.Deepgram;

    [ObservableProperty]
    private string _whisperExecutablePath = string.Empty;

    [ObservableProperty]
    private string _whisperModelPath = string.Empty;

    [ObservableProperty]
    private string _piperExecutablePath = string.Empty;

    [ObservableProperty]
    private string _piperEnglishVoicePath = string.Empty;

    [ObservableProperty]
    private string _piperFrenchVoicePath = string.Empty;

    [ObservableProperty]
    private string _localVoiceTestResult = string.Empty;

    [ObservableProperty]
    private string _defaultPushbackDirection = PushbackDirections.TailLeft;

    [ObservableProperty]
    private bool _offlineMode;

    [ObservableProperty]
    private string _preferredConnector = SimulatorConnectorNames.Msfs;

    [ObservableProperty]
    private string _simulatorStatus = string.Empty;

    [ObservableProperty]
    private bool _useLanguageModelPhraseology;

    [ObservableProperty]
    private bool _enableLiveTraffic;

    [ObservableProperty]
    private bool _enableRadioChatter;

    [ObservableProperty]
    private bool _useLanguageModelForChatter;

    [ObservableProperty]
    private bool _speakChatter;

    [ObservableProperty]
    private bool _pauseChatterWhenOnlineAtcClientDetected = true;

    [ObservableProperty]
    private int _maximumTrafficExchangesPerHour = 6;

    [ObservableProperty]
    private string _trafficIntegrationStatus = UiText.T("Traffic integration is disabled.");

    [ObservableProperty]
    private bool _requireReadbackBeforeProgressing;

    [ObservableProperty]
    private bool _requireCorrectCallsign;

    [ObservableProperty]
    private bool _enforceHoldShort;

    [ObservableProperty]
    private bool _requireCheckInOnFrequencyChange;

    [ObservableProperty]
    private bool _storeTranscripts;

    [ObservableProperty]
    private bool _cloudServicesConsentGiven;

    [ObservableProperty]
    private string _pushToTalkKey = "Space";

    [ObservableProperty]
    private bool _joystickPushToTalkEnabled;

    [ObservableProperty]
    private JoystickDevice? _selectedJoystick;

    [ObservableProperty]
    private int? _joystickButtonIndex;

    [ObservableProperty]
    private bool _isBindingJoystickButton;

    [ObservableProperty]
    private string _joystickBindingStatus = UiText.T("No joystick button assigned.");

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _restartRequired;

    private readonly List<string> _recognitionKeyterms;
    private readonly Dictionary<string, string> _transcriptCorrections;

    /// <summary>Creates the settings view model.</summary>
    public SettingsViewModel(
        ISecretStore secretStore,
        ILanguageModelClient languageModel,
        ISpeechRecognitionService speechRecognition,
        ITextToSpeechService textToSpeech,
        ISessionRepository sessionRepository,
        SimulatorConnectorRouter simulatorConnector,
        MsfsSimConnectConnector msfsConnector,
        TrafficRadioService trafficRadio,
        UserSettingsService userSettings,
        ProviderStatusService providerStatus,
        AtcSessionContext sessionContext,
        IJoystickInputService joystickInput,
        IOptions<ApplicationOptions> applicationOptions,
        IOptions<LanguageModelOptions> languageModelOptions,
        IOptions<OpenRouterOptions> openRouterOptions,
        IOptions<OpenAIOptions> openAIOptions,
        IOptions<GroqOptions> groqOptions,
        IOptions<DeepSeekOptions> deepSeekOptions,
        IOptions<AnthropicOptions> anthropicOptions,
        IOptions<DeepgramOptions> deepgramOptions,
        IOptions<SimBriefOptions> simBriefOptions,
        IOptions<SimulatorOptions> simulatorOptions,
        IOptions<TrafficOptions> trafficOptions,
        IOptions<AtcRealismOptions> realismOptions,
        IOptions<PersistenceOptions> persistenceOptions,
        IOptions<AudioOptions> audioOptions,
        IOptions<LocalVoiceOptions> localVoiceOptions,
        ILogger<SettingsViewModel> logger)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _languageModel = languageModel ?? throw new ArgumentNullException(nameof(languageModel));
        _speechRecognition = speechRecognition ?? throw new ArgumentNullException(nameof(speechRecognition));
        _textToSpeech = textToSpeech ?? throw new ArgumentNullException(nameof(textToSpeech));
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _simulatorConnector = simulatorConnector ?? throw new ArgumentNullException(nameof(simulatorConnector));
        _msfsConnector = msfsConnector ?? throw new ArgumentNullException(nameof(msfsConnector));
        _trafficRadio = trafficRadio ?? throw new ArgumentNullException(nameof(trafficRadio));
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        _providerStatus = providerStatus ?? throw new ArgumentNullException(nameof(providerStatus));
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _joystickInput = joystickInput ?? throw new ArgumentNullException(nameof(joystickInput));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ArgumentNullException.ThrowIfNull(applicationOptions);
        ArgumentNullException.ThrowIfNull(languageModelOptions);
        ArgumentNullException.ThrowIfNull(openRouterOptions);
        ArgumentNullException.ThrowIfNull(openAIOptions);
        ArgumentNullException.ThrowIfNull(groqOptions);
        ArgumentNullException.ThrowIfNull(deepSeekOptions);
        ArgumentNullException.ThrowIfNull(anthropicOptions);
        ArgumentNullException.ThrowIfNull(deepgramOptions);
        ArgumentNullException.ThrowIfNull(simBriefOptions);
        ArgumentNullException.ThrowIfNull(simulatorOptions);
        ArgumentNullException.ThrowIfNull(trafficOptions);
        ArgumentNullException.ThrowIfNull(realismOptions);
        ArgumentNullException.ThrowIfNull(persistenceOptions);
        ArgumentNullException.ThrowIfNull(audioOptions);
        ArgumentNullException.ThrowIfNull(localVoiceOptions);

        _openRouterModel = openRouterOptions.Value.Model;
        _openAIModel = openAIOptions.Value.Model;
        _groqModel = groqOptions.Value.Model;
        _deepSeekModel = deepSeekOptions.Value.Model;
        _anthropicModel = anthropicOptions.Value.Model;
        _selectedLanguageModelProvider =
            LanguageModelProviders.Normalize(languageModelOptions.Value.Provider);
        _deepgramVoice = deepgramOptions.Value.Voice;
        _deepgramSpeechModel = deepgramOptions.Value.SpeechToTextModel;
        _speechRecognitionLanguage = string.IsNullOrWhiteSpace(deepgramOptions.Value.Language)
            ? "en"
            : deepgramOptions.Value.Language;
        _recognitionKeyterms =
        [
            .. deepgramOptions.Value.RecognitionKeyterms
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .Select(term => term.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
        _transcriptCorrections = new Dictionary<string, string>(
            deepgramOptions.Value.TranscriptCorrections,
            StringComparer.OrdinalIgnoreCase);
        _trainingCorrectionCount = _transcriptCorrections.Count;
        _simBriefUserId = simBriefOptions.Value.UserId ?? string.Empty;
        _realismProfile = applicationOptions.Value.RealismProfile;
        _atcLanguage = Domain.Phraseology.PhraseologyLanguages.Parse(applicationOptions.Value.AtcLanguage)
            == Domain.Phraseology.PhraseologyLanguage.French
                ? FrenchLanguageName
                : EnglishLanguageName;
        _interfaceLanguage = applicationOptions.Value.InterfaceLanguage.Trim().ToUpperInvariant() switch
        {
            string code when code.StartsWith("FR", StringComparison.Ordinal) => FrenchInterfaceName,
            string code when code.StartsWith("EN", StringComparison.Ordinal) => EnglishInterfaceName,
            _ => WindowsLanguageName,
        };
        _interfaceLanguageAtStartup = _interfaceLanguage;
        _speechToTextProvider = SpeechProviders.Is(
            localVoiceOptions.Value.SpeechToTextProvider,
            SpeechProviders.Whisper)
                ? SpeechProviders.Whisper
                : SpeechProviders.Deepgram;
        _textToSpeechProvider = SpeechProviders.Is(
            localVoiceOptions.Value.TextToSpeechProvider,
            SpeechProviders.Piper)
                ? SpeechProviders.Piper
                : SpeechProviders.Deepgram;
        _whisperExecutablePath = localVoiceOptions.Value.WhisperExecutablePath;
        _whisperModelPath = localVoiceOptions.Value.WhisperModelPath;
        _piperExecutablePath = localVoiceOptions.Value.PiperExecutablePath;
        _piperEnglishVoicePath = localVoiceOptions.Value.PiperEnglishVoicePath;
        _piperFrenchVoicePath = localVoiceOptions.Value.PiperFrenchVoicePath;
        _defaultPushbackDirection =
            PushbackDirections.Normalize(realismOptions.Value.DefaultPushbackDirection)
            ?? PushbackDirections.TailLeft;
        _offlineMode = applicationOptions.Value.OfflineMode;
        _offlineModeAtStartup = _offlineMode;
        _cloudServicesConsentGiven = applicationOptions.Value.CloudServicesConsentGiven;
        _preferredConnector = SimulatorConnectorNames.Normalize(simulatorOptions.Value.PreferredConnector);
        _simulatorStatus = _simulatorConnector.StatusMessage;
        _enableLiveTraffic = trafficOptions.Value.EnableLiveTraffic;
        _enableRadioChatter = trafficOptions.Value.EnableRadioChatter;
        _useLanguageModelForChatter = trafficOptions.Value.UseLanguageModelForChatter;
        _speakChatter = trafficOptions.Value.SpeakChatter;
        _pauseChatterWhenOnlineAtcClientDetected =
            trafficOptions.Value.PauseChatterWhenOnlineAtcClientDetected;
        _maximumTrafficExchangesPerHour = trafficOptions.Value.MaximumExchangesPerHour;
        _useLanguageModelPhraseology = realismOptions.Value.UseLanguageModelPhraseology;
        _requireReadbackBeforeProgressing = realismOptions.Value.RequireReadbackBeforeProgressing;
        _requireCorrectCallsign = realismOptions.Value.RequireCorrectCallsign;
        _enforceHoldShort = realismOptions.Value.EnforceHoldShort;
        _requireCheckInOnFrequencyChange = realismOptions.Value.RequireCheckInOnFrequencyChange;
        _storeTranscripts = persistenceOptions.Value.StoreTranscripts;
        _pushToTalkKey = audioOptions.Value.PushToTalkKey;
        _joystickPushToTalkEnabled = audioOptions.Value.JoystickPushToTalkEnabled;
        _joystickButtonIndex = audioOptions.Value.JoystickButtonIndex;

        RealismProfiles = ["Relaxed", "Standard", "Strict"];
        AtcLanguages = [EnglishLanguageName, FrenchLanguageName];
        InterfaceLanguages = [WindowsLanguageName, EnglishInterfaceName, FrenchInterfaceName];
        RecognitionEngines = [.. SpeechProviders.RecognitionEngines];
        VoiceEngines = [.. SpeechProviders.VoiceEngines];
        PushbackDirectionChoices = [.. PushbackDirections.All];
        LanguageModelProviderNames = [.. LanguageModelProviders.All];
        Connectors = [.. SimulatorConnectorNames.All];
        SpeechRecognitionLanguages =
        [
            "en",
            "en-US",
            "en-GB",
            "en-AU",
            "en-IN",
            "fr",
            "fr-CA",
            "multi",
        ];
        InputDevices = [SystemDefaultDevice];
        foreach (string device in _speechRecognition.InputDeviceNames)
        {
            InputDevices.Add(device);
        }

        _selectedInputDevice = string.IsNullOrWhiteSpace(deepgramOptions.Value.InputDeviceName)
            ? SystemDefaultDevice
            : InputDevices.FirstOrDefault(device =>
                device.Contains(
                    deepgramOptions.Value.InputDeviceName,
                    StringComparison.OrdinalIgnoreCase))
                ?? deepgramOptions.Value.InputDeviceName;

        TrainingPhrases = [];
        JoystickDevices = [];
        RefreshTrainingPhrases();
        RefreshJoysticks();
    }

    /// <summary>The selectable realism profiles.</summary>
    public ObservableCollection<string> RealismProfiles { get; }

    /// <summary>The microphone choice that follows the Windows default device.</summary>
    private static string SystemDefaultDevice => UiText.T("System default");

    /// <summary>Display name of the English radio language.</summary>
    public const string EnglishLanguageName = "English (ICAO)";

    /// <summary>Display name of the French radio language.</summary>
    public const string FrenchLanguageName = "Français (DSNA)";

    /// <summary>The selectable radio languages.</summary>
    public ObservableCollection<string> AtcLanguages { get; }

    /// <summary>Interface language choice that follows the Windows display language.</summary>
    public static string WindowsLanguageName => UiText.T("Windows language");

    /// <summary>Display name of the English interface.</summary>
    public const string EnglishInterfaceName = "English";

    /// <summary>Display name of the French interface.</summary>
    public const string FrenchInterfaceName = "Français";

    /// <summary>The selectable interface languages.</summary>
    public ObservableCollection<string> InterfaceLanguages { get; }

    /// <summary>The configuration code of the selected interface language; empty follows Windows.</summary>
    private string InterfaceLanguageCode =>
        InterfaceLanguage == FrenchInterfaceName ? "fr"
        : InterfaceLanguage == EnglishInterfaceName ? "en"
        : string.Empty;

    /// <summary>Speech-to-text engines: Deepgram (cloud) or Whisper (local).</summary>
    public ObservableCollection<string> RecognitionEngines { get; }

    /// <summary>Controller voice engines: Deepgram (cloud) or Piper (local).</summary>
    public ObservableCollection<string> VoiceEngines { get; }

    /// <summary>The configuration code of the selected radio language.</summary>
    private string AtcLanguageCode =>
        AtcLanguage == FrenchLanguageName ? "fr" : "en";

    /// <summary>
    /// Tests the selected recognition and voice engines with the saved settings, whichever
    /// they are. Save first after changing a path or an engine.
    /// </summary>
    [RelayCommand]
    private async Task TestLocalVoiceAsync()
    {
        IsTesting = true;
        LocalVoiceTestResult = UiText.T("Testing…");

        try
        {
            ProviderTestResult recognition = await _speechRecognition
                .TestConnectionAsync(CancellationToken.None)
                .ConfigureAwait(true);

            ProviderTestResult synthesis = await _textToSpeech
                .TestConnectionAsync(CancellationToken.None)
                .ConfigureAwait(true);

            LocalVoiceTestResult =
                $"{_speechRecognition.ProviderName}: {Describe(recognition)}  ·  "
                + $"{_textToSpeech.ProviderName}: {Describe(synthesis)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The speech engine test failed.");
            LocalVoiceTestResult = UiText.T("The test failed. See the Diagnostics page.");
        }
        finally
        {
            IsTesting = false;
            await _providerStatus.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>Explains the transcript/readback behaviour of the selected profile.</summary>
    public string RealismProfileDescription => RealismProfile.Trim().ToUpperInvariant() switch
    {
        "RELAXED" =>
            UiText.T("Most forgiving. Callsign wording is optional and ambiguous transcript guesses are "
            + "treated as missing, not wrong. Runway, altitude, squawk, taxi route, and hold-short "
            + "values are still never guessed."),
        "STRICT" =>
            UiText.T("Training challenge. Every structured value returned by the transcript parser is "
            + "judged as intentional, and full callsign/readback discipline is enforced."),
        _ =>
            UiText.T("Recommended. Repairs common speech-to-text formatting, accepts equivalent ICAO and "
            + "numeric wording, remembers partial corrections, and says “negative” only when a "
            + "conflicting value was clearly announced."),
    };

    /// <summary>Safety boundary shared by every transcript-tolerance profile.</summary>
    public static string TranscriptToleranceNotice =>
        UiText.T("All profiles understand spoken digits, written numbers, Alfa/Alpha variants, compact "
        + "transcripts, and named/alphanumeric taxiways. No profile silently changes a conflicting "
        + "runway, altitude, squawk, heading, frequency, taxi route, or hold-short instruction.");

    /// <summary>GSX-style relative tail directions available for pushback.</summary>
    public ObservableCollection<string> PushbackDirectionChoices { get; }

    /// <summary>Language-model providers supported directly by StormATC.</summary>
    public ObservableCollection<string> LanguageModelProviderNames { get; }

    /// <summary>The selectable simulator connectors.</summary>
    public ObservableCollection<string> Connectors { get; }

    /// <summary>Microphone endpoints exposed by the Windows recording stack.</summary>
    public ObservableCollection<string> InputDevices { get; }

    /// <summary>Language hints supported by the configured Nova-3 recognizer.</summary>
    public ObservableCollection<string> SpeechRecognitionLanguages { get; }

    /// <summary>Suggested phrases covering the most important ATC exchanges.</summary>
    public ObservableCollection<string> TrainingPhrases { get; }

    /// <summary>Currently connected Windows joysticks, yokes, and HOTAS controllers.</summary>
    public ObservableCollection<JoystickDevice> JoystickDevices { get; }

    /// <summary>Where the user settings file lives, so the user can find or delete it.</summary>
    public string SettingsFilePath => _userSettings.SettingsFilePath;

    /// <summary>
    /// The mandatory scope statement. Repeated here as well as in the README and at first run.
    /// </summary>
    public static string Disclaimer =>
        UiText.T("StormATC is an entertainment product for flight simulation only. It must not be used "
        + "for real-world aviation, navigation, flight planning, or air traffic control.");

    /// <summary>The cost statement shown beside the API key fields.</summary>
    public static string CostNotice =>
        UiText.T("StormATC is free and open source. OpenRouter, OpenAI, Groq, DeepSeek, Anthropic, and "
        + "Deepgram are independent services that bill their own API accounts. A ChatGPT "
        + "subscription does not include OpenAI API usage. Check current rates before enabling one.");

    /// <summary>True when traffic-dependent options may be changed.</summary>
    public bool IsTrafficEnabled => EnableLiveTraffic;

    /// <summary>True when model and speech options for traffic chatter may be changed.</summary>
    public bool IsTrafficChatterEnabled => EnableLiveTraffic && EnableRadioChatter;

    /// <summary>Credit warning shown beside the opt-in traffic-radio controls.</summary>
    public static string TrafficCreditNotice =>
        UiText.T("Live traffic reading is local and uses no API credits. Language-model chatter uses up "
        + "to one model request per exchange. Spoken chatter uses two TTS requests per exchange. "
        + "All are off by default and the hourly exchange limit is a hard ceiling.");

    partial void OnEnableLiveTrafficChanged(bool value)
    {
        if (!value)
        {
            EnableRadioChatter = false;
            UseLanguageModelForChatter = false;
            SpeakChatter = false;
        }

        OnPropertyChanged(nameof(IsTrafficEnabled));
        OnPropertyChanged(nameof(IsTrafficChatterEnabled));
    }

    partial void OnRealismProfileChanged(string value)
    {
        Domain.Atc.RealismProfile preset =
            Domain.Atc.RealismProfile.FromName(value);

        RequireReadbackBeforeProgressing = preset.RequireReadbackBeforeProgressing;
        RequireCorrectCallsign = preset.RequireCorrectCallsign;
        EnforceHoldShort = preset.EnforceHoldShort;
        RequireCheckInOnFrequencyChange = preset.RequireCheckInOnFrequencyChange;
        OnPropertyChanged(nameof(RealismProfileDescription));
    }

    partial void OnEnableRadioChatterChanged(bool value)
    {
        if (!value)
        {
            UseLanguageModelForChatter = false;
            SpeakChatter = false;
        }

        OnPropertyChanged(nameof(IsTrafficChatterEnabled));
    }

    /// <inheritdoc />
    public async Task RefreshAsync()
    {
        SimulatorStatus = _simulatorConnector.StatusMessage;
        RefreshTrafficIntegrationStatus();
        RefreshInputDevices();
        RefreshTrainingPhrases();
        RefreshJoysticks();
        await RefreshKeyStatusAsync().ConfigureAwait(true);
    }

    private void RefreshTrafficIntegrationStatus()
    {
        if (!EnableLiveTraffic)
        {
            TrafficIntegrationStatus = UiText.T("Disabled — no live traffic scan or recurring API use.");
            return;
        }

        if (_msfsConnector.ConnectionState != Domain.SimulatorConnectionState.Connected)
        {
            TrafficIntegrationStatus = UiText.T("Enabled, waiting for Microsoft Flight Simulator.");
            return;
        }

        IReadOnlyList<MsfsTrafficSnapshot> contacts = _msfsConnector.NearbyTraffic;
        int fslTlContacts = contacts.Count(contact => contact.IsFslTlModel);
        string detected = contacts.Count switch
        {
            0 => UiText.T("No nearby simulator traffic detected yet."),
            1 => UiText.T("1 nearby aircraft detected."),
            _ => UiText.T($"{contacts.Count} nearby aircraft detected."),
        };

        TrafficIntegrationStatus = fslTlContacts > 0
            ? UiText.T($"{detected} {fslTlContacts} use FSLTL models. {_trafficRadio.Status}")
            : $"{detected} {_trafficRadio.Status}";
    }

    // =====================================================================================
    // API keys. Values pass through as method arguments and are never stored on this object.
    // =====================================================================================

    /// <summary>Stores the OpenRouter key. Called from the view with the password box contents.</summary>
    public async Task SaveOpenRouterKeyAsync(string key) =>
        await SaveKeyAsync(SecretKeys.OpenRouterApiKey, key, "OpenRouter").ConfigureAwait(true);

    /// <summary>Stores the OpenAI API key.</summary>
    public async Task SaveOpenAIKeyAsync(string key) =>
        await SaveKeyAsync(SecretKeys.OpenAIApiKey, key, "OpenAI").ConfigureAwait(true);

    /// <summary>Stores the Groq API key.</summary>
    public async Task SaveGroqKeyAsync(string key) =>
        await SaveKeyAsync(SecretKeys.GroqApiKey, key, "Groq").ConfigureAwait(true);

    /// <summary>Stores the DeepSeek API key.</summary>
    public async Task SaveDeepSeekKeyAsync(string key) =>
        await SaveKeyAsync(SecretKeys.DeepSeekApiKey, key, "DeepSeek").ConfigureAwait(true);

    /// <summary>Stores the Anthropic API key.</summary>
    public async Task SaveAnthropicKeyAsync(string key) =>
        await SaveKeyAsync(SecretKeys.AnthropicApiKey, key, "Anthropic").ConfigureAwait(true);

    /// <summary>Stores the Deepgram key. Called from the view with the password box contents.</summary>
    public async Task SaveDeepgramKeyAsync(string key) =>
        await SaveKeyAsync(SecretKeys.DeepgramApiKey, key, "Deepgram").ConfigureAwait(true);

    private async Task SaveKeyAsync(string secretKey, string value, string providerName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            StatusMessage = UiText.T($"Enter a {providerName} key before saving.");
            return;
        }

        try
        {
            await _secretStore.SetSecretAsync(secretKey, value.Trim(), CancellationToken.None)
                .ConfigureAwait(true);

            StatusMessage = UiText.T($"The {providerName} key was saved to the operating system secret store.");
            await RefreshKeyStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The exception message is deliberately not shown: an exception from a secret store
            // can echo the value that failed to store.
            _logger.LogError(ex, "Storing the {Provider} key failed.", providerName);
            StatusMessage = UiText.T($"The {providerName} key could not be saved.");
        }
    }

    /// <summary>Deletes the stored OpenRouter key.</summary>
    [RelayCommand]
    private async Task RemoveOpenRouterKeyAsync() =>
        await RemoveKeyAsync(SecretKeys.OpenRouterApiKey, "OpenRouter").ConfigureAwait(true);

    /// <summary>Deletes the stored OpenAI key.</summary>
    [RelayCommand]
    private async Task RemoveOpenAIKeyAsync() =>
        await RemoveKeyAsync(SecretKeys.OpenAIApiKey, "OpenAI").ConfigureAwait(true);

    /// <summary>Deletes the stored Groq key.</summary>
    [RelayCommand]
    private async Task RemoveGroqKeyAsync() =>
        await RemoveKeyAsync(SecretKeys.GroqApiKey, "Groq").ConfigureAwait(true);

    /// <summary>Deletes the stored DeepSeek key.</summary>
    [RelayCommand]
    private async Task RemoveDeepSeekKeyAsync() =>
        await RemoveKeyAsync(SecretKeys.DeepSeekApiKey, "DeepSeek").ConfigureAwait(true);

    /// <summary>Deletes the stored Anthropic key.</summary>
    [RelayCommand]
    private async Task RemoveAnthropicKeyAsync() =>
        await RemoveKeyAsync(SecretKeys.AnthropicApiKey, "Anthropic").ConfigureAwait(true);

    /// <summary>Deletes the stored Deepgram key.</summary>
    [RelayCommand]
    private async Task RemoveDeepgramKeyAsync() =>
        await RemoveKeyAsync(SecretKeys.DeepgramApiKey, "Deepgram").ConfigureAwait(true);

    private async Task RemoveKeyAsync(string secretKey, string providerName)
    {
        try
        {
            await _secretStore.RemoveSecretAsync(secretKey, CancellationToken.None).ConfigureAwait(true);
            StatusMessage = UiText.T($"The {providerName} key was removed.");
            await RefreshKeyStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Removing the {Provider} key failed.", providerName);
            StatusMessage = UiText.T($"The {providerName} key could not be removed.");
        }
    }

    /// <summary>Verifies the currently selected language-model provider.</summary>
    [RelayCommand]
    private async Task TestLanguageModelAsync()
    {
        IsTesting = true;
        LanguageModelTestResult = UiText.T($"Testing {SelectedLanguageModelProvider}…");

        try
        {
            ProviderTestResult result = await _languageModel
                .TestConnectionAsync(CancellationToken.None)
                .ConfigureAwait(true);

            LanguageModelTestResult = Describe(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The language-model connection test failed.");
            LanguageModelTestResult = UiText.T("The test failed. See the Diagnostics page.");
        }
        finally
        {
            IsTesting = false;
            await _providerStatus.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>Verifies the Deepgram credentials for both speech directions.</summary>
    [RelayCommand]
    private async Task TestDeepgramAsync()
    {
        IsTesting = true;
        DeepgramTestResult = UiText.T("Testing…");

        try
        {
            ProviderTestResult recognition = await _speechRecognition
                .TestConnectionAsync(CancellationToken.None)
                .ConfigureAwait(true);

            ProviderTestResult synthesis = await _textToSpeech
                .TestConnectionAsync(CancellationToken.None)
                .ConfigureAwait(true);

            DeepgramTestResult = UiText.T($"Speech to text: {Describe(recognition)}  ·  Voice: {Describe(synthesis)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The Deepgram connection test failed.");
            DeepgramTestResult = UiText.T("The test failed. See the Diagnostics page.");
        }
        finally
        {
            IsTesting = false;
            await _providerStatus.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Records one guided calibration phrase. It is deliberately kept out of the live ATC
    /// pipeline, so a training sample cannot accidentally request a clearance.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRecordVoiceTraining))]
    private async Task RecordVoiceTrainingAsync()
    {
        IsTrainingVoice = true;
        TrainingTranscript = string.Empty;
        TrainingStatus = UiText.T("Listening for six seconds. Speak the expected phrase naturally...");

        try
        {
            SpeechTrainingSample sample = await _speechRecognition
                .CaptureTrainingSampleAsync(
                    SelectedInputDevice.Equals(SystemDefaultDevice, StringComparison.OrdinalIgnoreCase)
                        ? null
                        : SelectedInputDevice,
                    TimeSpan.FromSeconds(6),
                    CancellationToken.None)
                .ConfigureAwait(true);

            TrainingTranscript = sample.Transcript;
            TrainingStatus = sample.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice-training capture failed.");
            TrainingStatus =
                UiText.T("The sample could not be recorded. Check the microphone selection and Deepgram connection.");
        }
        finally
        {
            IsTrainingVoice = false;
        }
    }

    private bool CanRecordVoiceTraining() =>
        !IsTrainingVoice && !string.IsNullOrWhiteSpace(TrainingExpectedPhrase);

    /// <summary>
    /// Saves the expected phrase as a recognition keyterm and, when the provider heard something
    /// different, saves that explicit correction locally.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveTrainingCorrection))]
    private async Task SaveTrainingCorrectionAsync()
    {
        string expected = TrainingExpectedPhrase.Trim();
        string heard = TrainingTranscript.Trim();

        if (!_recognitionKeyterms.Contains(expected, StringComparer.OrdinalIgnoreCase))
        {
            _recognitionKeyterms.Add(expected);
        }

        if (!heard.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            _transcriptCorrections[heard] = expected;
        }

        await SaveSpeechProfileAsync().ConfigureAwait(true);
        TrainingCorrectionCount = _transcriptCorrections.Count;
        TrainingStatus = heard.Equals(expected, StringComparison.OrdinalIgnoreCase)
            ? UiText.T("Phrase saved as a recognition keyterm. Deepgram already heard it correctly.")
            : UiText.T("Correction saved. Future matching transcripts will be corrected locally.");
    }

    private bool CanSaveTrainingCorrection() =>
        !IsTrainingVoice
        && !string.IsNullOrWhiteSpace(TrainingExpectedPhrase)
        && !string.IsNullOrWhiteSpace(TrainingTranscript);

    /// <summary>Removes personal keyterms and transcript corrections.</summary>
    [RelayCommand]
    private async Task ClearVoiceTrainingAsync()
    {
        _recognitionKeyterms.Clear();
        _transcriptCorrections.Clear();
        await SaveSpeechProfileAsync().ConfigureAwait(true);

        TrainingCorrectionCount = 0;
        TrainingTranscript = string.Empty;
        TrainingStatus = UiText.T("Personal voice-training phrases and corrections were cleared.");
    }

    partial void OnTrainingExpectedPhraseChanged(string value)
    {
        RecordVoiceTrainingCommand.NotifyCanExecuteChanged();
        SaveTrainingCorrectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnTrainingTranscriptChanged(string value) =>
        SaveTrainingCorrectionCommand.NotifyCanExecuteChanged();

    partial void OnIsTrainingVoiceChanged(bool value)
    {
        RecordVoiceTrainingCommand.NotifyCanExecuteChanged();
        SaveTrainingCorrectionCommand.NotifyCanExecuteChanged();
    }

    private async Task SaveSpeechProfileAsync()
    {
        SpeechRecognitionProfile profile = BuildSpeechProfile();

        await _userSettings.SaveManyAsync(
            [
                (DeepgramOptions.SectionName, "InputDeviceName",
                    JsonValue.Create(profile.InputDeviceName)),
                (DeepgramOptions.SectionName, "Language", JsonValue.Create(profile.Language)),
                (DeepgramOptions.SectionName, "RecognitionKeyterms",
                    JsonSerializer.SerializeToNode(profile.Keyterms)),
                (DeepgramOptions.SectionName, "TranscriptCorrections",
                    JsonSerializer.SerializeToNode(profile.TranscriptCorrections)),
            ],
            CancellationToken.None).ConfigureAwait(true);

        _speechRecognition.ApplyRecognitionProfile(profile);
    }

    private SpeechRecognitionProfile BuildSpeechProfile() =>
        new()
        {
            InputDeviceName =
                SelectedInputDevice.Equals(SystemDefaultDevice, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : SelectedInputDevice,
            Language = string.IsNullOrWhiteSpace(SpeechRecognitionLanguage)
                ? "en"
                : SpeechRecognitionLanguage.Trim(),
            Keyterms = _recognitionKeyterms.ToArray(),
            TranscriptCorrections = new Dictionary<string, string>(
                _transcriptCorrections,
                StringComparer.OrdinalIgnoreCase),
        };

    private void RefreshInputDevices()
    {
        string selected = SelectedInputDevice;
        IReadOnlyList<string> current = _speechRecognition.InputDeviceNames;

        InputDevices.Clear();
        InputDevices.Add(SystemDefaultDevice);
        foreach (string device in current)
        {
            if (!InputDevices.Contains(device, StringComparer.OrdinalIgnoreCase))
            {
                InputDevices.Add(device);
            }
        }

        if (!InputDevices.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            InputDevices.Add(selected);
        }

        SelectedInputDevice = selected;
    }

    private void RefreshTrainingPhrases()
    {
        string spokenCallsign = _sessionContext.CurrentSession?.FlightPlan?.Callsign.ToSpoken()
            ?? "Sierra Tango one two three";
        string departureRunway =
            _sessionContext.CurrentSession?.FlightPlan?.DepartureRunway?.ToString()
            ?? "26L";

        string[] suggestions =
        [
            $"{spokenCallsign}, request IFR clearance",
            $"{spokenCallsign}, request pushback",
            $"{spokenCallsign}, ready to taxi",
            $"{spokenCallsign}, holding short runway {departureRunway}",
            $"{spokenCallsign}, ready for departure",
            $"{spokenCallsign}, climbing flight level three seven zero",
            $"{spokenCallsign}, established on the approach",
            $"{spokenCallsign}, runway vacated, request taxi to stand",
        ];

        TrainingPhrases.Clear();
        foreach (string phrase in suggestions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TrainingPhrases.Add(phrase);
        }

        if (string.IsNullOrWhiteSpace(TrainingExpectedPhrase)
            || !TrainingPhrases.Contains(
                TrainingExpectedPhrase,
                StringComparer.OrdinalIgnoreCase))
        {
            TrainingExpectedPhrase = TrainingPhrases[0];
        }
    }

    // =====================================================================================
    // Joystick / HOTAS push-to-talk
    // =====================================================================================

    /// <summary>Refreshes the list of controllers currently visible to Windows.</summary>
    [RelayCommand]
    private void RefreshJoysticks()
    {
        JoystickButtonBinding? binding = _joystickInput.CurrentBinding;
        int? selectedId = SelectedJoystick?.Id ?? binding?.DeviceId;
        string? selectedName = SelectedJoystick?.Name ?? binding?.DeviceName;

        IReadOnlyList<JoystickDevice> devices;
        try
        {
            devices = _joystickInput.GetConnectedDevices();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enumerating joystick devices failed.");
            devices = [];
            JoystickBindingStatus =
                UiText.T("Windows could not enumerate joystick devices. Reconnect the controller and try again.");
        }

        JoystickDevices.Clear();
        foreach (JoystickDevice device in devices)
        {
            JoystickDevices.Add(device);
        }

        SelectedJoystick = devices.FirstOrDefault(
                device => device.Id == selectedId
                    && device.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(
                device => device.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
            ?? (devices.Count > 0 ? devices[0] : null);

        UpdateJoystickBindingStatus();
        BindJoystickButtonCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Listens for one newly pressed button on the selected controller.</summary>
    [RelayCommand(CanExecute = nameof(CanBindJoystickButton))]
    private async Task BindJoystickButtonAsync()
    {
        if (SelectedJoystick is not JoystickDevice device)
        {
            JoystickBindingStatus = UiText.T("Connect and select a controller first.");
            return;
        }

        IsBindingJoystickButton = true;
        JoystickBindingStatus =
            UiText.T($"Listening on {device.Name}… release all buttons, then press the PTT button.");

        try
        {
            JoystickButtonBinding binding = await _joystickInput
                .CaptureNextButtonAsync(
                    device.Id,
                    TimeSpan.FromSeconds(15),
                    CancellationToken.None)
                .ConfigureAwait(true);

            SelectedJoystick = JoystickDevices.FirstOrDefault(
                    candidate => candidate.Id == binding.DeviceId)
                ?? device;
            JoystickButtonIndex = binding.ButtonIndex;
            JoystickPushToTalkEnabled = true;
            _joystickInput.Configure(binding, enabled: true);

            await SaveJoystickBindingAsync(binding, enabled: true).ConfigureAwait(true);
            JoystickBindingStatus =
                UiText.T($"{binding.DisplayName} is assigned and ready. Hold it to talk; release to send.");
        }
        catch (TimeoutException)
        {
            JoystickBindingStatus =
                UiText.T("No new button press was detected. Try again and press the button within 15 seconds.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Binding a joystick push-to-talk button failed.");
            JoystickBindingStatus =
                UiText.T("The button could not be assigned. Reconnect the controller and refresh the list.");
        }
        finally
        {
            IsBindingJoystickButton = false;
        }
    }

    private bool CanBindJoystickButton() =>
        SelectedJoystick is not null && !IsBindingJoystickButton;

    /// <summary>Removes the saved hardware PTT assignment.</summary>
    [RelayCommand]
    private async Task ClearJoystickBindingAsync()
    {
        JoystickPushToTalkEnabled = false;
        JoystickButtonIndex = null;
        _joystickInput.Configure(binding: null, enabled: false);

        try
        {
            await SaveJoystickBindingAsync(binding: null, enabled: false).ConfigureAwait(true);
            JoystickBindingStatus = UiText.T("Joystick push-to-talk assignment cleared.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clearing the joystick push-to-talk binding failed.");
            JoystickBindingStatus = UiText.T("The assignment was cleared for this session but could not be saved.");
        }
    }

    partial void OnSelectedJoystickChanged(JoystickDevice? value) =>
        BindJoystickButtonCommand.NotifyCanExecuteChanged();

    partial void OnIsBindingJoystickButtonChanged(bool value) =>
        BindJoystickButtonCommand.NotifyCanExecuteChanged();

    partial void OnJoystickPushToTalkEnabledChanged(bool value)
    {
        JoystickButtonBinding? binding = _joystickInput.CurrentBinding;
        _joystickInput.Configure(binding, value && binding is not null);
        UpdateJoystickBindingStatus();
    }

    private void UpdateJoystickBindingStatus()
    {
        JoystickButtonBinding? binding = _joystickInput.CurrentBinding;
        if (binding is null)
        {
            JoystickBindingStatus = JoystickDevices.Count == 0
                ? UiText.T("No joystick detected. Connect one, then select Refresh controllers.")
                : UiText.T("No joystick button assigned.");
            return;
        }

        bool connected = JoystickDevices.Any(
            device => device.Name.Equals(
                binding.DeviceName,
                StringComparison.OrdinalIgnoreCase));

        JoystickBindingStatus = !connected
            ? UiText.T($"{binding.DisplayName} is saved, but that controller is disconnected.")
            : JoystickPushToTalkEnabled
                ? UiText.T($"{binding.DisplayName} is assigned and ready.")
                : UiText.T($"{binding.DisplayName} is assigned but disabled.");
    }

    private async Task SaveJoystickBindingAsync(
        JoystickButtonBinding? binding,
        bool enabled) =>
        await _userSettings.SaveManyAsync(
            [
                (AudioOptions.SectionName, "JoystickPushToTalkEnabled", JsonValue.Create(enabled)),
                (AudioOptions.SectionName, "JoystickDeviceId", JsonValue.Create(binding?.DeviceId)),
                (AudioOptions.SectionName, "JoystickDeviceName", JsonValue.Create(binding?.DeviceName)),
                (AudioOptions.SectionName, "JoystickButtonIndex", JsonValue.Create(binding?.ButtonIndex)),
            ],
            CancellationToken.None).ConfigureAwait(true);

    private static string Describe(ProviderTestResult result) =>
        result.Duration is TimeSpan duration
            ? $"{result.Message} ({duration.TotalMilliseconds:F0} ms)"
            : result.Message;

    private async Task RefreshKeyStatusAsync()
    {
        try
        {
            HasOpenRouterKey = await _secretStore
                .HasSecretAsync(SecretKeys.OpenRouterApiKey, CancellationToken.None)
                .ConfigureAwait(true);

            HasOpenAIKey = await _secretStore
                .HasSecretAsync(SecretKeys.OpenAIApiKey, CancellationToken.None)
                .ConfigureAwait(true);

            HasGroqKey = await _secretStore
                .HasSecretAsync(SecretKeys.GroqApiKey, CancellationToken.None)
                .ConfigureAwait(true);

            HasDeepSeekKey = await _secretStore
                .HasSecretAsync(SecretKeys.DeepSeekApiKey, CancellationToken.None)
                .ConfigureAwait(true);

            HasAnthropicKey = await _secretStore
                .HasSecretAsync(SecretKeys.AnthropicApiKey, CancellationToken.None)
                .ConfigureAwait(true);

            HasDeepgramKey = await _secretStore
                .HasSecretAsync(SecretKeys.DeepgramApiKey, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The secret store could not be read.");
            HasOpenRouterKey = false;
            HasOpenAIKey = false;
            HasGroqKey = false;
            HasDeepSeekKey = false;
            HasAnthropicKey = false;
            HasDeepgramKey = false;
        }

        OpenRouterKeyStatus = HasOpenRouterKey ? UiText.T("A key is stored (hidden)") : UiText.T("No key stored");
        OpenAIKeyStatus = HasOpenAIKey ? UiText.T("A key is stored (hidden)") : UiText.T("No key stored");
        GroqKeyStatus = HasGroqKey ? UiText.T("A key is stored (hidden)") : UiText.T("No key stored");
        DeepSeekKeyStatus = HasDeepSeekKey ? UiText.T("A key is stored (hidden)") : UiText.T("No key stored");
        AnthropicKeyStatus = HasAnthropicKey ? UiText.T("A key is stored (hidden)") : UiText.T("No key stored");
        DeepgramKeyStatus = HasDeepgramKey ? UiText.T("A key is stored (hidden)") : UiText.T("No key stored");
    }

    // =====================================================================================
    // Ordinary settings
    // =====================================================================================

    /// <summary>Writes every non-secret setting to the user settings file.</summary>
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            JoystickButtonBinding? joystickBinding = _joystickInput.CurrentBinding;

            await _userSettings.SaveManyAsync(
                [
                    (ApplicationOptions.SectionName, "OfflineMode", JsonValue.Create(OfflineMode)),
                    (ApplicationOptions.SectionName, "RealismProfile", JsonValue.Create(RealismProfile)),
                    (ApplicationOptions.SectionName, "AtcLanguage", JsonValue.Create(AtcLanguageCode)),
                    (ApplicationOptions.SectionName, "InterfaceLanguage", JsonValue.Create(InterfaceLanguageCode)),

                    (LocalVoiceOptions.SectionName, "SpeechToTextProvider", JsonValue.Create(SpeechToTextProvider)),
                    (LocalVoiceOptions.SectionName, "TextToSpeechProvider", JsonValue.Create(TextToSpeechProvider)),
                    (LocalVoiceOptions.SectionName, "WhisperExecutablePath", JsonValue.Create(WhisperExecutablePath.Trim().Trim('"'))),
                    (LocalVoiceOptions.SectionName, "WhisperModelPath", JsonValue.Create(WhisperModelPath.Trim().Trim('"'))),
                    (LocalVoiceOptions.SectionName, "PiperExecutablePath", JsonValue.Create(PiperExecutablePath.Trim().Trim('"'))),
                    (LocalVoiceOptions.SectionName, "PiperEnglishVoicePath", JsonValue.Create(PiperEnglishVoicePath.Trim().Trim('"'))),
                    (LocalVoiceOptions.SectionName, "PiperFrenchVoicePath", JsonValue.Create(PiperFrenchVoicePath.Trim().Trim('"'))),
                    (ApplicationOptions.SectionName, "CloudServicesConsentGiven", JsonValue.Create(CloudServicesConsentGiven)),
                    (ApplicationOptions.SectionName, "FirstRunCompleted", JsonValue.Create(true)),

                    (LanguageModelOptions.SectionName, "Provider",
                        JsonValue.Create(LanguageModelProviders.Normalize(SelectedLanguageModelProvider))),
                    (OpenRouterOptions.SectionName, "Model", JsonValue.Create(OpenRouterModel.Trim())),
                    (OpenAIOptions.SectionName, "Model", JsonValue.Create(OpenAIModel.Trim())),
                    (GroqOptions.SectionName, "Model", JsonValue.Create(GroqModel.Trim())),
                    (DeepSeekOptions.SectionName, "Model", JsonValue.Create(DeepSeekModel.Trim())),
                    (AnthropicOptions.SectionName, "Model", JsonValue.Create(AnthropicModel.Trim())),

                    (DeepgramOptions.SectionName, "Voice", JsonValue.Create(DeepgramVoice.Trim())),
                    (DeepgramOptions.SectionName, "SpeechToTextModel", JsonValue.Create(DeepgramSpeechModel.Trim())),
                    (DeepgramOptions.SectionName, "Language", JsonValue.Create(
                        string.IsNullOrWhiteSpace(SpeechRecognitionLanguage)
                            ? "en"
                            : SpeechRecognitionLanguage.Trim())),
                    (DeepgramOptions.SectionName, "InputDeviceName", JsonValue.Create(
                        SelectedInputDevice.Equals(
                            SystemDefaultDevice,
                            StringComparison.OrdinalIgnoreCase)
                            ? null
                            : SelectedInputDevice)),
                    (DeepgramOptions.SectionName, "RecognitionKeyterms",
                        JsonSerializer.SerializeToNode(_recognitionKeyterms)),
                    (DeepgramOptions.SectionName, "TranscriptCorrections",
                        JsonSerializer.SerializeToNode(_transcriptCorrections)),

                    (SimBriefOptions.SectionName, "UserId", JsonValue.Create(SimBriefUserId.Trim())),

                    (SimulatorOptions.SectionName, "PreferredConnector", JsonValue.Create(PreferredConnector)),

                    (TrafficOptions.SectionName, "EnableLiveTraffic", JsonValue.Create(EnableLiveTraffic)),
                    (TrafficOptions.SectionName, "EnableRadioChatter", JsonValue.Create(
                        EnableLiveTraffic && EnableRadioChatter)),
                    (TrafficOptions.SectionName, "UseLanguageModelForChatter", JsonValue.Create(
                        EnableLiveTraffic
                        && EnableRadioChatter
                        && UseLanguageModelForChatter)),
                    (TrafficOptions.SectionName, "SpeakChatter", JsonValue.Create(
                        EnableLiveTraffic && EnableRadioChatter && SpeakChatter)),
                    (TrafficOptions.SectionName, "PauseChatterWhenOnlineAtcClientDetected",
                        JsonValue.Create(PauseChatterWhenOnlineAtcClientDetected)),
                    (TrafficOptions.SectionName, "MaximumExchangesPerHour", JsonValue.Create(
                        Math.Clamp(MaximumTrafficExchangesPerHour, 1, 30))),

                    (AtcRealismOptions.SectionName, "UseLanguageModelPhraseology", JsonValue.Create(UseLanguageModelPhraseology)),
                    (AtcRealismOptions.SectionName, "DefaultPushbackDirection", JsonValue.Create(
                        PushbackDirections.Normalize(DefaultPushbackDirection)
                            ?? PushbackDirections.TailLeft)),
                    (AtcRealismOptions.SectionName, "RequireReadbackBeforeProgressing", JsonValue.Create(RequireReadbackBeforeProgressing)),
                    (AtcRealismOptions.SectionName, "RequireCorrectCallsign", JsonValue.Create(RequireCorrectCallsign)),
                    (AtcRealismOptions.SectionName, "EnforceHoldShort", JsonValue.Create(EnforceHoldShort)),
                    (AtcRealismOptions.SectionName, "RequireCheckInOnFrequencyChange", JsonValue.Create(RequireCheckInOnFrequencyChange)),

                    (PersistenceOptions.SectionName, "StoreTranscripts", JsonValue.Create(StoreTranscripts)),

                    (AudioOptions.SectionName, "PushToTalkKey", JsonValue.Create(PushToTalkKey)),
                    (AudioOptions.SectionName, "JoystickPushToTalkEnabled", JsonValue.Create(
                        JoystickPushToTalkEnabled && joystickBinding is not null)),
                    (AudioOptions.SectionName, "JoystickDeviceId", JsonValue.Create(joystickBinding?.DeviceId)),
                    (AudioOptions.SectionName, "JoystickDeviceName", JsonValue.Create(joystickBinding?.DeviceName)),
                    (AudioOptions.SectionName, "JoystickButtonIndex", JsonValue.Create(joystickBinding?.ButtonIndex)),
                ],
                CancellationToken.None).ConfigureAwait(true);

            // The realism profile is read live by the engine, so apply it now rather than at
            // the next restart.
            _sessionContext.Realism =
                Domain.Atc.RealismProfile.FromName(RealismProfile) with
                {
                    RequireReadbackBeforeProgressing = RequireReadbackBeforeProgressing,
                    RequireCorrectCallsign = RequireCorrectCallsign,
                    EnforceHoldShort = EnforceHoldShort,
                    RequireCheckInOnFrequencyChange = RequireCheckInOnFrequencyChange,
                };
            _sessionContext.DefaultPushbackDirection =
                PushbackDirections.Normalize(DefaultPushbackDirection)
                ?? PushbackDirections.TailLeft;
            _speechRecognition.ApplyRecognitionProfile(BuildSpeechProfile());

            // The radio language switches the running session too: the next controller
            // transmission, the ATIS and speech recognition all follow it.
            _sessionContext.SetLanguage(
                Domain.Phraseology.PhraseologyLanguages.Parse(AtcLanguageCode));
            _joystickInput.Configure(
                joystickBinding,
                JoystickPushToTalkEnabled && joystickBinding is not null);

            // So is the simulator: the router can change connector while a session is running,
            // so choosing MSFS here connects to it now rather than at the next launch.
            SimulatorStatus = await _simulatorConnector
                .SwitchToAsync(PreferredConnector, CancellationToken.None)
                .ConfigureAwait(true);

            await _providerStatus.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
            RefreshTrafficIntegrationStatus();

            // Offline mode decides which implementations are registered, which happens once when
            // the host is built, and the interface language is chosen before the first window.
            // They are the only settings on this page that still need a restart.
            bool offlineChanged = OfflineMode != _offlineModeAtStartup;
            bool interfaceLanguageChanged = InterfaceLanguage != _interfaceLanguageAtStartup;
            RestartRequired = offlineChanged || interfaceLanguageChanged;

            StatusMessage = offlineChanged
                ? UiText.T($"Saved to {SettingsFilePath}. Offline mode takes effect after a restart.")
                : interfaceLanguageChanged
                    ? UiText.T($"Saved to {SettingsFilePath}. The interface language changes at the next start.")
                    : UiText.T($"Saved to {SettingsFilePath}. {SimulatorStatus}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving settings failed.");
            StatusMessage = UiText.T("The settings could not be saved.");
        }
    }

    /// <summary>Deletes every locally stored session, transcript, and decision.</summary>
    [RelayCommand]
    private async Task ClearLocalHistoryAsync()
    {
        System.Windows.MessageBoxResult confirmation = System.Windows.MessageBox.Show(
            UiText.T("This permanently deletes every stored session, transcript, and decision on this "
            + "computer. It cannot be undone.\n\nContinue?"),
            UiText.T("StormATC — clear local history"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (confirmation != System.Windows.MessageBoxResult.Yes)
        {
            StatusMessage = UiText.T("Local history was not changed.");
            return;
        }

        try
        {
            await _sessionRepository.ClearAllHistoryAsync(CancellationToken.None).ConfigureAwait(true);
            StatusMessage = UiText.T("All locally stored history was deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clearing local history failed.");
            StatusMessage = UiText.T("The local history could not be cleared.");
        }
    }
}
