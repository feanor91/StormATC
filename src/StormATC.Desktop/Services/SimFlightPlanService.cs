using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.FlightPlanning;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Application.Sessions;
using StormATC.Domain;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Sessions;

namespace StormATC.Desktop.Services;

/// <summary>What importing the simulator's flight plan came to.</summary>
/// <param name="Imported">True when a plan was loaded into the session.</param>
/// <param name="Message">What to tell the pilot.</param>
public sealed record SimFlightPlanImportResult(bool Imported, string Message);

/// <summary>
/// Brings the flight plan the pilot made in the simulator (the EFB, the world map, or the flight
/// that was just started) into the session.
/// </summary>
/// <remarks>
/// <para>
/// The simulator announces an activated plan with the name of its <c>.PLN</c> file, and a loaded
/// flight with the name of its <c>.FLT</c>. Whatever it announces is written to the log, so what
/// MSFS really sends can be read afterwards. A plan the pilot activates replaces the session's
/// plan; the plan of a flight that was just loaded only replaces a placeholder (the bundled
/// sample), never a plan the pilot imported.
/// </para>
/// <para>
/// The same import is available as a button on the Flight Plan page.
/// </para>
/// </remarks>
public sealed class SimFlightPlanService : IDisposable, ISimRouteReceiver
{
    private readonly ISimulatorFlightPlanEvents _events;
    private readonly AtcSessionContext _sessionContext;
    private readonly ProviderStatusService _providerStatus;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<SimFlightPlanOptions> _options;
    private readonly ILogger<SimFlightPlanService> _logger;
    private readonly IReadOnlyList<string> _folders;
    private string? _lastAnnouncedFile;
    private SimFlightPlan? _lastRoute;

    /// <summary>Creates the service and starts listening.</summary>
    public SimFlightPlanService(
        ISimulatorFlightPlanEvents events,
        AtcSessionContext sessionContext,
        ProviderStatusService providerStatus,
        IClock clock,
        IOptionsMonitor<SimFlightPlanOptions> options,
        ILogger<SimFlightPlanService> logger)
        : this(events, sessionContext, providerStatus, clock, options, logger, SimFlightPlanLocator.DefaultFolders())
    {
    }

    /// <summary>Creates the service with chosen folders for the simulator's plan files.</summary>
    internal SimFlightPlanService(
        ISimulatorFlightPlanEvents events,
        AtcSessionContext sessionContext,
        ProviderStatusService providerStatus,
        IClock clock,
        IOptionsMonitor<SimFlightPlanOptions> options,
        ILogger<SimFlightPlanService> logger,
        IReadOnlyList<string> folders)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _providerStatus = providerStatus ?? throw new ArgumentNullException(nameof(providerStatus));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _folders = folders ?? throw new ArgumentNullException(nameof(folders));

        _events.FlightPlanAnnounced += OnAnnounced;
    }

    /// <summary>Raised after a plan from the simulator was loaded into the session.</summary>
    public event EventHandler<FlightPlan>? PlanImported;

    /// <inheritdoc />
    public void Dispose() => _events.FlightPlanAnnounced -= OnAnnounced;

    /// <inheritdoc />
    /// <remarks>
    /// The routes the in-simulator panel reads from the simulator: the one the pilot filed with ATC
    /// and the one sent to the avionics are applied when they change; the EFB's own route, which
    /// changes with every edit, and the route that was already there when the panel started, are
    /// only remembered, for the import button.
    /// </remarks>
    public async Task<bool> ReceiveRouteAsync(
        string source,
        bool initial,
        string routeJson,
        CancellationToken cancellationToken)
    {
        if (!PlannedRouteParser.TryParse(routeJson, out SimFlightPlan? plan))
        {
            // An empty route: the pilot cleared the plan, or none was made yet.
            return false;
        }

        _lastRoute = plan;
        _logger.LogInformation(
            "The simulator's panel sent the {Source} route {Departure} to {Destination} ({Rules}){Initial}.",
            source,
            plan.DepartureIcao,
            plan.DestinationIcao,
            plan.IsVfr ? "VFR" : "IFR",
            initial ? ", already there when the panel started" : string.Empty);

        if (initial || !_options.CurrentValue.Enabled || source == SimRouteSources.Efb)
        {
            return false;
        }

        SimFlightPlanImportResult result = await ImportPlanAsync(plan, $"the simulator ({source})", cancellationToken)
            .ConfigureAwait(false);
        return result.Imported;
    }

    /// <summary>
    /// Imports the plan the simulator's panel sent last, or else the plan the simulator announced
    /// last, or else the newest plan file it wrote.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SimFlightPlanImportResult> ImportLatestAsync(CancellationToken cancellationToken)
    {
        if (_lastRoute is not null)
        {
            return await ImportPlanAsync(_lastRoute, "the simulator's panel", cancellationToken).ConfigureAwait(false);
        }

        // The folders where the simulator writes plans, then the places a plan saved from the EFB
        // usually ends up. The newest file wins.
        string? file = _lastAnnouncedFile is not null && File.Exists(_lastAnnouncedFile)
            ? _lastAnnouncedFile
            : SimFlightPlanLocator.FindNewest([.. _folders, .. SimFlightPlanLocator.UserFolders()]);

        if (file is null)
        {
            return new SimFlightPlanImportResult(
                false,
                UiText.T("The simulator has not written a flight plan. Activate a plan in the simulator, then try again."));
        }

        return await ImportFileAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private async void OnAnnounced(object? sender, SimulatorFlightPlanEventArgs e)
    {
        try
        {
            if (!_options.CurrentValue.Enabled)
            {
                return;
            }

            switch (e.Kind)
            {
                case SimulatorFlightPlanEventKind.Activated:
                    string? file = e.FileName is not null && File.Exists(e.FileName)
                        ? e.FileName
                        : FreshFile();
                    if (e.FileName is not null)
                    {
                        _lastAnnouncedFile = e.FileName;
                    }

                    if (file is null)
                    {
                        // MSFS 2024 announces ".PLN" and nothing else when the pilot sends a plan to
                        // ATC from the EFB: the plan itself comes through the panel, not through a file.
                        _logger.LogInformation(
                            "A flight plan was activated in the simulator ({Name}), but no readable plan file came with it.",
                            e.FileName ?? "(none)");
                        return;
                    }

                    await ImportFileAsync(file, CancellationToken.None).ConfigureAwait(false);
                    break;

                case SimulatorFlightPlanEventKind.FlightLoaded when _options.CurrentValue.ImportOnFlightLoaded:
                    await ImportLoadedFlightAsync(e.FileName).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // This runs on a SimConnect callback: nothing may escape it.
            _logger.LogWarning(ex, "The flight plan announced by the simulator could not be imported.");
        }
    }

    /// <summary>A plan file the simulator wrote in the last two minutes, for an announcement with no name.</summary>
    private string? FreshFile()
    {
        string? newest = SimFlightPlanLocator.FindNewest(_folders);
        return newest is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(newest) < TimeSpan.FromMinutes(2)
            ? newest
            : null;
    }

    private async Task ImportLoadedFlightAsync(string? flightFile)
    {
        // The plan of a free flight sits next to the .FLT file, with the same name.
        string? plan = flightFile is null ? null : Path.ChangeExtension(flightFile, ".PLN");
        if (plan is null || !File.Exists(plan))
        {
            plan = SimFlightPlanLocator.FindNewest(_folders);
        }

        if (plan is null)
        {
            return;
        }

        FlightPlan? current = _sessionContext.CurrentSession.FlightPlan;
        bool placeholder = current is null || current.SourceIdentifier is "Sample" or "Mock";
        if (!placeholder)
        {
            _logger.LogInformation("A flight was loaded in the simulator; the plan already in use is kept.");
            return;
        }

        await ImportFileAsync(plan, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Imports a <c>.PLN</c> file the pilot chose.</summary>
    /// <param name="file">The file's path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<SimFlightPlanImportResult> ImportPlnFileAsync(string file, CancellationToken cancellationToken) =>
        ImportFileAsync(file, cancellationToken);

    private async Task<SimFlightPlanImportResult> ImportFileAsync(string file, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The simulator's plan file {File} could not be read.", file);
            return new SimFlightPlanImportResult(false, UiText.T("The simulator's flight plan file could not be read."));
        }

        if (!PlnFlightPlanParser.TryParse(text, out SimFlightPlan? simPlan))
        {
            _logger.LogInformation("The file {File} holds no flight plan StormATC can read.", file);
            return new SimFlightPlanImportResult(false, UiText.T("The simulator's flight plan file holds no usable plan."));
        }

        return await ImportPlanAsync(simPlan, file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads a plan read from the simulator into the session.</summary>
    /// <param name="simPlan">The plan.</param>
    /// <param name="origin">Where it was read, for the log: a file name or a route source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<SimFlightPlanImportResult> ImportPlanAsync(
        SimFlightPlan simPlan,
        string origin,
        CancellationToken cancellationToken)
    {
        AtcSession current = _sessionContext.CurrentSession;
        string? callsign = !string.IsNullOrWhiteSpace(current.AircraftState.AtcIdentifier)
            ? current.AircraftState.AtcIdentifier
            : current.FlightPlan is { SourceIdentifier: not ("Sample" or "Mock") } existing
                ? existing.Callsign.Original
                : null;

        if (!SimFlightPlanConverter.TryConvert(simPlan, callsign, _clock.UtcNow, out FlightPlan? plan))
        {
            return new SimFlightPlanImportResult(
                false,
                UiText.T("The aircraft's registration is not known yet: StormATC could not name the flight."));
        }

        _sessionContext.PreassignedSquawk = null;
        await _sessionContext.LoadFlightPlanAsync(plan, cancellationToken).ConfigureAwait(false);
        _sessionContext.Mutate(session => session.WithFlightRules(simPlan.IsVfr ? FlightRules.Vfr : FlightRules.Ifr));

        string rules = simPlan.IsVfr ? "VFR" : "IFR";
        _providerStatus.SetFlightPlanStatus(true, $"{plan.ToSummary()} · {rules} · {UiText.T("simulator")}");
        _logger.LogInformation("Imported the simulator's flight plan {Summary} ({Rules}) from {Origin}.", plan.ToSummary(), rules, origin);

        PlanImported?.Invoke(this, plan);
        return new SimFlightPlanImportResult(
            true,
            UiText.T("Flight plan imported from the simulator: ") + $"{plan.DepartureIcao} → {plan.DestinationIcao} ({rules}).");
    }
}
