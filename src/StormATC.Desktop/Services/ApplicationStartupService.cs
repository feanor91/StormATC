using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Atis;
using StormATC.Application.Options;
using StormATC.Application.Sessions;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;
using StormATC.Infrastructure.FlightPlanning;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Desktop.Services;

/// <summary>
/// Brings the application to a usable state after the window is shown.
/// </summary>
/// <remarks>
/// <para>
/// Every step here is allowed to fail. A missing database, an unavailable simulator, or an
/// absent flight plan degrades the session; none of them prevents startup. That is a hard
/// requirement: StormATC must be usable with no API keys, no simulator, and no network.
/// </para>
/// <para>
/// This runs after the first paint so a slow provider never delays the window appearing.
/// </para>
/// </remarks>
public sealed class ApplicationStartupService(
    AtcSessionContext sessionContext,
    SimulatorConnectorRouter simulatorConnector,
    FlightPlanProviderRouter flightPlanProvider,
    ISessionRepository sessionRepository,
    IStoragePaths storagePaths,
    IPromptCatalog promptCatalog,
    AtisBroadcastService atisBroadcastService,
    ProviderStatusService providerStatus,
    IOptions<ApplicationOptions> applicationOptions,
    IOptions<AtcRealismOptions> realismOptions,
    IOptions<SimBriefOptions> simBriefOptions,
    ILogger<ApplicationStartupService> logger)
{
    private readonly AtcSessionContext _sessionContext =
        sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));

    private readonly SimulatorConnectorRouter _simulatorConnector =
        simulatorConnector ?? throw new ArgumentNullException(nameof(simulatorConnector));

    private readonly FlightPlanProviderRouter _flightPlanProvider =
        flightPlanProvider ?? throw new ArgumentNullException(nameof(flightPlanProvider));

    private readonly ISessionRepository _sessionRepository =
        sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));

    private readonly IStoragePaths _storagePaths =
        storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));

    private readonly IPromptCatalog _promptCatalog =
        promptCatalog ?? throw new ArgumentNullException(nameof(promptCatalog));

    private readonly AtisBroadcastService _atisBroadcastService =
        atisBroadcastService ?? throw new ArgumentNullException(nameof(atisBroadcastService));

    private readonly ProviderStatusService _providerStatus =
        providerStatus ?? throw new ArgumentNullException(nameof(providerStatus));

    private readonly ApplicationOptions _applicationOptions = applicationOptions?.Value
        ?? throw new ArgumentNullException(nameof(applicationOptions));

    private readonly AtcRealismOptions _realismOptions = realismOptions?.Value
        ?? throw new ArgumentNullException(nameof(realismOptions));

    private readonly SimBriefOptions _simBriefOptions = simBriefOptions?.Value
        ?? throw new ArgumentNullException(nameof(simBriefOptions));

    private readonly ILogger<ApplicationStartupService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Problems encountered during startup, shown on the Diagnostics page.</summary>
    public IReadOnlyList<string> StartupWarnings => _warnings;

    private readonly List<string> _warnings = [];

    /// <summary>Runs the startup sequence. Never throws.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _warnings.Clear();
        _atisBroadcastService.Start();

        EnsureStorage();
        await InitializeDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await CheckPromptsAsync(cancellationToken).ConfigureAwait(false);

        // The simulator is connected before the session starts so the session records which kind
        // of simulator it actually ran against. Any aircraft updates that arrive in between land
        // on the placeholder session the context is constructed with and are simply superseded.
        await ConnectSimulatorAsync(cancellationToken).ConfigureAwait(false);

        StartSession();

        await LoadFlightPlanAsync(cancellationToken).ConfigureAwait(false);

        await _providerStatus.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureStorage()
    {
        try
        {
            _storagePaths.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not create the application data directories.");
            _warnings.Add("The application data directory could not be created. History will not be saved.");
        }
    }

    private async Task InitializeDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _sessionRepository.InitializeAsync(cancellationToken).ConfigureAwait(false);

            int version = await _sessionRepository.GetSchemaVersionAsync(cancellationToken)
                .ConfigureAwait(false);

            _providerStatus.SetStorageStatus(true, $"Schema v{version}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A broken database must not stop a session. Transcripts simply are not saved.
            _logger.LogError(ex, "The local database could not be initialised.");
            _providerStatus.SetStorageStatus(false, "Unavailable — history will not be saved");
            _warnings.Add("The local database could not be opened. This session will not be saved.");
        }
    }

    private async Task CheckPromptsAsync(CancellationToken cancellationToken)
    {
        foreach (string name in PromptNames.All)
        {
            if (!await _promptCatalog.HasPromptAsync(name, cancellationToken).ConfigureAwait(false))
            {
                _warnings.Add($"Prompt '{name}' is missing. Language-model features that use it are disabled.");
            }
        }
    }

    private void StartSession()
    {
        // The mode records what the aircraft state actually came from, not what was configured:
        // a session flown against the scripted aircraft is a mock session even if everything
        // else about it was live.
        SessionMode mode = _simulatorConnector.IsMockActive ? SessionMode.Mock : SessionMode.Live;

        _sessionContext.Realism = ParseRealism(_applicationOptions.RealismProfile);
        _sessionContext.DefaultPushbackDirection =
            PushbackDirections.Normalize(_realismOptions.DefaultPushbackDirection)
            ?? PushbackDirections.TailLeft;
        _sessionContext.StartNewSession(mode, AircraftState.Unknown(DateTimeOffset.UtcNow));

        // The profile is logged by name, not as the record: the expanded record is four lines of
        // flags that say nothing the name does not.
        _logger.LogInformation(
            "Started a new {Mode} session with the {Realism} realism profile.",
            mode,
            _applicationOptions.RealismProfile);
    }

    /// <summary>Maps the configured profile name, defaulting to Standard on anything unrecognised.</summary>
    private RealismProfile ParseRealism(string name)
    {
        if (!RealismProfile.IsKnownName(name))
        {
            _warnings.Add($"Realism profile '{name}' is not recognised. Using Standard.");
        }

        return RealismProfile.FromName(name) with
        {
            RequireReadbackBeforeProgressing =
                _realismOptions.RequireReadbackBeforeProgressing,
            RequireCorrectCallsign = _realismOptions.RequireCorrectCallsign,
            EnforceHoldShort = _realismOptions.EnforceHoldShort,
            RequireCheckInOnFrequencyChange =
                _realismOptions.RequireCheckInOnFrequencyChange,
            MinimumIntentConfidence = _realismOptions.MinimumIntentConfidence,
        };
    }

    private async Task ConnectSimulatorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _simulatorConnector.ConnectAsync(cancellationToken).ConfigureAwait(false);

            if (_simulatorConnector.ConnectionState != SimulatorConnectionState.Connected)
            {
                _warnings.Add(
                    $"The simulator connector '{_simulatorConnector.ConnectorName}' is "
                    + $"{_simulatorConnector.ConnectionState}. Aircraft state will not update.");
            }
            else if (_simulatorConnector.PreferredConnector != SimulatorConnectorNames.Mock
                && _simulatorConnector.IsMockActive)
            {
                // Not a failure: the session is fully usable. It is recorded so the Diagnostics
                // page can explain why a user who chose MSFS is watching a scripted aircraft.
                _warnings.Add(_simulatorConnector.StatusMessage);
            }

            _logger.LogInformation(
                "Simulator: {Connector} — {Status}",
                _simulatorConnector.ConnectorName,
                _simulatorConnector.StatusMessage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The simulator connector failed to start.");
            _warnings.Add("The simulator could not be connected. Aircraft state will not update.");
        }
    }

    private async Task LoadFlightPlanAsync(CancellationToken cancellationToken)
    {
        try
        {
            var request = new FlightPlanImportRequest { UserId = _simBriefOptions.UserId };

            FlightPlanImportResult result = await _flightPlanProvider
                .ImportLatestAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success || result.FlightPlan is not FlightPlan plan)
            {
                _providerStatus.SetFlightPlanStatus(false, result.ErrorMessage ?? "No flight plan loaded");

                if (result.ErrorMessage is { Length: > 0 } error)
                {
                    _warnings.Add($"{error} {result.SuggestedAction}".TrimEnd());
                }

                return;
            }

            // The sample scenario pins a transponder code so the demonstration is reproducible.
            // Nothing else pins one, and the engine assigns its own when this is null.
            _sessionContext.PreassignedSquawk = _flightPlanProvider.PreassignedSquawk;

            AtcSession session = await _sessionContext
                .LoadFlightPlanAsync(plan, cancellationToken)
                .ConfigureAwait(false);

            if (session.CurrentFrequency is Frequency initialFrequency)
            {
                bool tuned = await _simulatorConnector
                    .TuneCom1Async(initialFrequency, cancellationToken)
                    .ConfigureAwait(false);

                if (!tuned)
                {
                    _warnings.Add(
                        $"COM1 could not be tuned automatically to "
                        + $"{initialFrequency.ToDisplay()}. Tune it in the aircraft before transmitting.");
                }
            }

            // Only a real import's warnings are worth recording. The sample's own note that it is
            // fictional is true of every run and would sit on the Diagnostics page for ever,
            // crowding out the problems that page exists to show.
            if (_flightPlanProvider.LastImportUsedSimBrief)
            {
                foreach (string warning in result.Warnings)
                {
                    _warnings.Add($"SimBrief: {warning}");
                }
            }

            _providerStatus.SetFlightPlanStatus(
                true,
                $"{plan.ToSummary()} · {_flightPlanProvider.LastProviderUsed}");

            _logger.LogInformation(
                "Loaded flight plan {Summary} from {Provider}.",
                plan.ToSummary(),
                _flightPlanProvider.LastProviderUsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The flight plan could not be imported.");
            _providerStatus.SetFlightPlanStatus(false, "Import failed");
            _warnings.Add("The flight plan could not be imported. Load one from the Flight Plan page.");
        }
    }
}
