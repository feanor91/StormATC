using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Application.Transmissions;
using StormATC.Desktop.Services;
using StormATC.Infrastructure.FlightPlanning;
using StormATC.Infrastructure.Security;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Desktop.ViewModels;

/// <summary>
/// The Diagnostics page: what is configured, how long each pipeline stage took, and an export
/// the user can attach to a bug report.
/// </summary>
/// <remarks>
/// <para>
/// The export is assembled here field by field rather than by serialising objects wholesale.
/// That is deliberate: a whole-object dump is one careless property away from leaking a
/// credential. Nothing in this file reads <see cref="ISecretStore"/> values, and the export
/// records only whether a key exists.
/// </para>
/// </remarks>
public sealed partial class DiagnosticsViewModel : ObservableObject, IRefreshablePage, IDisposable
{
    private readonly PilotTransmissionCoordinator _coordinator;
    private readonly IPromptCatalog _promptCatalog;
    private readonly ISecretStore _secretStore;
    private readonly ISessionRepository _sessionRepository;
    private readonly SimulatorConnectorRouter _simulatorConnector;
    private readonly FlightPlanProviderRouter _flightPlanProvider;
    private readonly ILanguageModelClient _languageModel;
    private readonly IStoragePaths _storagePaths;
    private readonly ApplicationStartupService _startupService;
    private readonly ProviderStatusService _providerStatus;
    private readonly DiagnosticsOptions _diagnosticsOptions;
    private readonly OpenRouterOptions _openRouterOptions;
    private readonly ILogger<DiagnosticsViewModel> _logger;

    private bool _disposed;

    [ObservableProperty]
    private string _schemaVersion = "—";

    [ObservableProperty]
    private string _databasePath = "—";

    [ObservableProperty]
    private string _promptDirectory = "—";

    [ObservableProperty]
    private string _configuredModel = "Not configured";

    [ObservableProperty]
    private string _simulatorState = "—";

    [ObservableProperty]
    private string _simulatorDetail = string.Empty;

    [ObservableProperty]
    private string _flightPlanSource = "—";

    [ObservableProperty]
    private string _lastLatencySummary = "No transmissions yet";

    [ObservableProperty]
    private string _averageLatencySummary = "No samples yet";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _openRouterKeyPresence = "Unknown";

    [ObservableProperty]
    private string _deepgramKeyPresence = "Unknown";

    /// <summary>Creates the diagnostics view model.</summary>
    public DiagnosticsViewModel(
        PilotTransmissionCoordinator coordinator,
        IPromptCatalog promptCatalog,
        ISecretStore secretStore,
        ISessionRepository sessionRepository,
        SimulatorConnectorRouter simulatorConnector,
        FlightPlanProviderRouter flightPlanProvider,
        ILanguageModelClient languageModel,
        IStoragePaths storagePaths,
        ApplicationStartupService startupService,
        ProviderStatusService providerStatus,
        IOptions<DiagnosticsOptions> diagnosticsOptions,
        IOptions<OpenRouterOptions> openRouterOptions,
        ILogger<DiagnosticsViewModel> logger)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _promptCatalog = promptCatalog ?? throw new ArgumentNullException(nameof(promptCatalog));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _simulatorConnector = simulatorConnector ?? throw new ArgumentNullException(nameof(simulatorConnector));
        _flightPlanProvider = flightPlanProvider ?? throw new ArgumentNullException(nameof(flightPlanProvider));
        _languageModel = languageModel ?? throw new ArgumentNullException(nameof(languageModel));
        _storagePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));
        _startupService = startupService ?? throw new ArgumentNullException(nameof(startupService));
        _providerStatus = providerStatus ?? throw new ArgumentNullException(nameof(providerStatus));
        _diagnosticsOptions = diagnosticsOptions?.Value ?? throw new ArgumentNullException(nameof(diagnosticsOptions));
        _openRouterOptions = openRouterOptions?.Value ?? throw new ArgumentNullException(nameof(openRouterOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        LatencySamples = [];
        PromptHashes = [];
        StartupWarnings = [];

        _coordinator.TransmissionProcessed += OnTransmissionProcessed;
    }

    /// <summary>Per-transmission latency, newest first.</summary>
    public ObservableCollection<LatencySample> LatencySamples { get; }

    /// <summary>Prompt names and their short content hashes.</summary>
    public ObservableCollection<PromptHashEntry> PromptHashes { get; }

    /// <summary>Anything that went wrong during startup.</summary>
    public ObservableCollection<string> StartupWarnings { get; }

    /// <summary>The provider badges, shared with the dashboard.</summary>
    public ProviderStatusService ProviderStatus => _providerStatus;

    /// <inheritdoc />
    public async Task RefreshAsync()
    {
        DatabasePath = _storagePaths.DatabasePath;
        PromptDirectory = _storagePaths.PromptDirectory;

        // The connector name is the one actually running, which may not be the one preferred.
        // Both are shown, because "I chose MSFS and I am watching a scripted aircraft" is the
        // single most confusing state this application can be in.
        SimulatorState = _simulatorConnector.PreferredConnector == _simulatorConnector.ConnectorName
            ? $"{_simulatorConnector.ConnectorName} — {_simulatorConnector.ConnectionState}"
            : $"{_simulatorConnector.ConnectorName} — {_simulatorConnector.ConnectionState} "
                + $"(preferred: {_simulatorConnector.PreferredConnector})";

        SimulatorDetail = _simulatorConnector.StatusMessage;
        FlightPlanSource = _flightPlanProvider.LastProviderUsed;

        ConfiguredModel = _openRouterOptions.Model is { Length: > 0 } model
            ? model
            : "Not configured — deterministic phraseology only";

        StartupWarnings.Clear();
        foreach (string warning in _startupService.StartupWarnings)
        {
            StartupWarnings.Add(warning);
        }

        foreach (string missing in _promptCatalog.MissingPrompts)
        {
            StartupWarnings.Add($"Prompt file missing: {missing}");
        }

        try
        {
            int version = await _sessionRepository.GetSchemaVersionAsync(CancellationToken.None)
                .ConfigureAwait(true);

            SchemaVersion = version.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The database schema version could not be read.");
            SchemaVersion = "unavailable";
        }

        if (_diagnosticsOptions.ShowPromptHashes)
        {
            await LoadPromptHashesAsync().ConfigureAwait(true);
        }

        await LoadKeyPresenceAsync().ConfigureAwait(true);

        await _providerStatus.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task LoadPromptHashesAsync()
    {
        try
        {
            IReadOnlyDictionary<string, string> hashes = await _promptCatalog
                .GetPromptHashesAsync(CancellationToken.None)
                .ConfigureAwait(true);

            PromptHashes.Clear();

            foreach ((string name, string hash) in hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                PromptHashes.Add(new PromptHashEntry(name, hash));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prompt hashes could not be computed.");
        }
    }

    /// <summary>Reads only whether a key exists. The values are never retrieved.</summary>
    private async Task LoadKeyPresenceAsync()
    {
        try
        {
            OpenRouterKeyPresence = await _secretStore
                .HasSecretAsync(SecretKeys.OpenRouterApiKey, CancellationToken.None).ConfigureAwait(true)
                ? "Stored"
                : "Not stored";

            DeepgramKeyPresence = await _secretStore
                .HasSecretAsync(SecretKeys.DeepgramApiKey, CancellationToken.None).ConfigureAwait(true)
                ? "Stored"
                : "Not stored";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The secret store could not be queried.");
            OpenRouterKeyPresence = "Unknown";
            DeepgramKeyPresence = "Unknown";
        }
    }

    /// <summary>
    /// Opens the application data folder in Explorer.
    /// </summary>
    /// <remarks>
    /// Everything StormATC writes — the database, the settings file, the logs, the cached flight
    /// plans, the diagnostics export — is under this one directory, and deleting it removes every
    /// trace. Showing the user where it is is also the simplest way to make that promise checkable.
    /// </remarks>
    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            _storagePaths.EnsureCreated();

            using var explorer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _storagePaths.ApplicationDataDirectory,
                UseShellExecute = true,
            });

            StatusMessage = $"Opened {_storagePaths.ApplicationDataDirectory}.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The application data folder could not be opened.");
            StatusMessage = $"The folder could not be opened. It is at {_storagePaths.ApplicationDataDirectory}.";
        }
    }

    /// <summary>Reloads prompts and airport data from disk without restarting.</summary>
    [RelayCommand]
    private async Task ReloadPromptsAsync()
    {
        _promptCatalog.Invalidate();
        await LoadPromptHashesAsync().ConfigureAwait(true);
        StatusMessage = "Prompts reloaded from disk.";
    }

    /// <summary>Writes a redacted diagnostics report to the application data directory.</summary>
    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            _storagePaths.EnsureCreated();

            string path = Path.Combine(
                _storagePaths.ApplicationDataDirectory,
                $"stormatc-diagnostics-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt");

            await File.WriteAllTextAsync(
                    path,
                    SensitiveDataRedactor.Redact(BuildReport()),
                    CancellationToken.None)
                .ConfigureAwait(true);

            StatusMessage = $"Diagnostics written to {path}. It contains no API keys.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The diagnostics export failed.");
            StatusMessage = "The diagnostics export failed.";
        }
    }

    /// <summary>
    /// Builds the export.
    /// </summary>
    /// <remarks>
    /// Every line is written explicitly. No object is serialised wholesale, and no secret value
    /// is read — only its presence.
    /// </remarks>
    private string BuildReport()
    {
        var builder = new StringBuilder();

        builder.AppendLine("StormATC diagnostics");
        builder.AppendLine("Flight simulation only — not for real-world aviation, navigation, or air traffic control.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Version: {MainWindowViewModel.VersionText}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"OS: {Environment.OSVersion}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {Environment.Version}");
        builder.AppendLine();

        builder.AppendLine("Providers");
        foreach (ProviderStatusItem item in _providerStatus.Items)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  {item.Name}: {item.StateText} — {item.Detail}");
        }

        builder.AppendLine();
        builder.AppendLine("Configuration");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Language model provider: {_languageModel.ProviderName}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Configured model: {ConfiguredModel}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Simulator: {SimulatorState}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Simulator detail: {SimulatorDetail}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Flight plan source: {FlightPlanSource}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Database schema: {SchemaVersion}");
        builder.AppendLine();

        builder.AppendLine("Credentials (presence only — values are never exported)");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  OpenRouter key: {OpenRouterKeyPresence}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Deepgram key: {DeepgramKeyPresence}");
        builder.AppendLine();

        builder.AppendLine("Prompts");
        foreach (PromptHashEntry entry in PromptHashes)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  {entry.Name}: {entry.Hash}");
        }

        builder.AppendLine();
        builder.AppendLine("Startup warnings");
        if (StartupWarnings.Count == 0)
        {
            builder.AppendLine("  None");
        }
        else
        {
            foreach (string warning in StartupWarnings)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  {warning}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Recent latency (milliseconds)");
        builder.AppendLine("  When                 Total  Parse  Engine  Render  Persist");
        foreach (LatencySample sample in LatencySamples)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"  {sample.Time}  {sample.TotalMs,5}  {sample.ParsingMs,5}  {sample.EngineMs,6}  "
                + $"{sample.RenderMs,6}  {sample.PersistenceMs,7}");
        }

        return builder.ToString();
    }

    private void OnTransmissionProcessed(object? sender, TransmissionProcessedEventArgs e)
    {
        if (!_diagnosticsOptions.RecordLatencyMetrics || !e.Result.Processed)
        {
            return;
        }

        TransmissionTimings timings = e.Result.Timings;

        RunOnUiThread(() =>
        {
            LatencySamples.Insert(0, new LatencySample(
                DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                (int)timings.Total.TotalMilliseconds,
                (int)timings.IntentParsing.TotalMilliseconds,
                (int)timings.EngineEvaluation.TotalMilliseconds,
                (int)timings.PhraseologyRendering.TotalMilliseconds,
                (int)timings.Persistence.TotalMilliseconds,
                e.Result.UsedDeterministicFallback));

            while (LatencySamples.Count > _diagnosticsOptions.LatencySampleCount)
            {
                LatencySamples.RemoveAt(LatencySamples.Count - 1);
            }

            LastLatencySummary =
                $"{(int)timings.Total.TotalMilliseconds} ms total  ·  "
                + $"parse {(int)timings.IntentParsing.TotalMilliseconds} ms  ·  "
                + $"engine {(int)timings.EngineEvaluation.TotalMilliseconds} ms  ·  "
                + $"render {(int)timings.PhraseologyRendering.TotalMilliseconds} ms";

            AverageLatencySummary =
                $"{LatencySamples.Average(sample => sample.TotalMs):F0} ms mean over "
                + $"{LatencySamples.Count} transmissions";
        });
    }

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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _coordinator.TransmissionProcessed -= OnTransmissionProcessed;
    }
}

/// <summary>One transmission's stage timings.</summary>
public sealed record LatencySample(
    string Time,
    int TotalMs,
    int ParsingMs,
    int EngineMs,
    int RenderMs,
    int PersistenceMs,
    bool UsedDeterministicFallback);

/// <summary>A prompt file and its short content hash.</summary>
public sealed record PromptHashEntry(string Name, string Hash);
