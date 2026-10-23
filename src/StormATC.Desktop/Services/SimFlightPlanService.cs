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
    private readonly ISimulatorConnector _simulator;
    private readonly AtcSessionContext _sessionContext;
    private readonly ProviderStatusService _providerStatus;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<SimFlightPlanOptions> _options;
    private readonly ILogger<SimFlightPlanService> _logger;
    private readonly IReadOnlyList<string> _folders;
    private readonly IApproachProcedureSource? _approaches;
    private string? _lastAnnouncedFile;
    private SimFlightPlan? _lastRoute;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private (string File, DateTime Written)? _lastLoadedPlan;

    /// <summary>Creates the service and starts listening.</summary>
    public SimFlightPlanService(
        ISimulatorFlightPlanEvents events,
        ISimulatorConnector simulator,
        AtcSessionContext sessionContext,
        ProviderStatusService providerStatus,
        IClock clock,
        IOptionsMonitor<SimFlightPlanOptions> options,
        ILogger<SimFlightPlanService> logger,
        IApproachProcedureSource? approaches = null)
        : this(events, simulator, sessionContext, providerStatus, clock, options, logger, SimFlightPlanLocator.DefaultFolders(), approaches)
    {
    }

    /// <summary>Creates the service with chosen folders for the simulator's plan files.</summary>
    internal SimFlightPlanService(
        ISimulatorFlightPlanEvents events,
        ISimulatorConnector simulator,
        AtcSessionContext sessionContext,
        ProviderStatusService providerStatus,
        IClock clock,
        IOptionsMonitor<SimFlightPlanOptions> options,
        ILogger<SimFlightPlanService> logger,
        IReadOnlyList<string> folders,
        IApproachProcedureSource? approaches = null)
    {
        _approaches = approaches;
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _providerStatus = providerStatus ?? throw new ArgumentNullException(nameof(providerStatus));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _folders = folders ?? throw new ArgumentNullException(nameof(folders));

        _events.FlightPlanAnnounced += OnAnnounced;
        _events.RouteReceived += OnRouteReceived;
        _simulator.ConnectionChanged += OnConnectionChanged;
    }

    /// <summary>Raised after a plan from the simulator was loaded into the session.</summary>
    public event EventHandler<FlightPlan>? PlanImported;

    /// <inheritdoc />
    public void Dispose()
    {
        _importGate.Dispose();
        _events.FlightPlanAnnounced -= OnAnnounced;
        _events.RouteReceived -= OnRouteReceived;
        _simulator.ConnectionChanged -= OnConnectionChanged;
    }

    /// <summary>A route published by the StormATC module inside the simulator.</summary>
    private async void OnRouteReceived(object? sender, SimulatorRouteEventArgs e)
    {
        try
        {
            await ReceiveRouteAsync(e.Source, e.Initial, e.RouteJson, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // This runs on a SimConnect callback: nothing may escape it.
            _logger.LogWarning(ex, "The route published by the simulator module could not be imported.");
        }
    }

    /// <summary>
    /// StormATC usually connects after the flight was loaded, so the simulator's "flight loaded"
    /// notice has already gone by: read the plan the flight was started with when the link opens.
    /// </summary>
    private async void OnConnectionChanged(object? sender, SimulatorConnectionChangedEventArgs e)
    {
        // A route the module gave in an earlier session of the simulator is not this one's.
        if (e.State != SimulatorConnectionState.Connected)
        {
            _lastRoute = null;
        }

        // The scripted aircraft also "connects": only the real simulator has a flight to read.
        if (e.State != SimulatorConnectionState.Connected
            || _simulator is StormATC.Infrastructure.Simulator.SimulatorConnectorRouter { IsMockActive: true }
            || !_options.CurrentValue.Enabled
            || !_options.CurrentValue.ImportOnFlightLoaded)
        {
            return;
        }

        try
        {
            await ImportLoadedFlightAsync(null, requireRecent: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "The plan of the flight that was loaded could not be read.");
        }
    }

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
        if (source == SimRouteSources.Hello)
        {
            _logger.LogInformation("The StormATC module is running in the simulator.");
            return false;
        }

        if (!PlannedRouteParser.TryParse(routeJson, out SimFlightPlan? plan))
        {
            // An empty route: the pilot cleared the plan, or none was made yet.
            return false;
        }

        _lastRoute = plan;
        _logger.LogInformation(
            "StormATC received the {Source} route {Departure} to {Destination} ({Rules}) from the simulator{Initial}.",
            source,
            plan.DepartureIcao,
            plan.DestinationIcao,
            plan.IsVfr ? "VFR" : "IFR",
            initial ? " (it was already there when the reader started)" : string.Empty);
        _logger.LogInformation(
            "The {Source} route's points: {Points}.",
            source,
            plan.Waypoints.Count == 0 ? "none" : string.Join(" ", plan.Waypoints.Select(point => point.Identifier)));
        _logger.LogDebug("The {Source} route as the simulator gave it: {Route}", source, routeJson.Length > 3000 ? routeJson[..3000] : routeJson);

        if (!_options.CurrentValue.Enabled || source == SimRouteSources.Efb)
        {
            return false;
        }

        // The route the pilot had already sent when StormATC started is applied too, unless it is
        // the plan already in use (nothing to do) or a SimBrief plan the pilot brought (kept).
        if (initial && _sessionContext.CurrentSession.FlightPlan is { SourceIdentifier: "SimBrief" })
        {
            return false;
        }

        SimFlightPlanImportResult result = await ImportPlanAsync(plan, $"the simulator ({source})", force: false, cancellationToken)
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
            return await ImportPlanAsync(_lastRoute, "the simulator's panel", force: true, cancellationToken).ConfigureAwait(false);
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

        return await ImportFileAsync(file, force: true, cancellationToken).ConfigureAwait(false);
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
                    // A plan sent to ATC from the EFB is announced as ".PLN" with no path, and the plan
                    // itself comes through the StormATC module (or the panel). Only a real path is a file.
                    string? file = e.FileName is not null && Path.IsPathRooted(e.FileName) && File.Exists(e.FileName)
                        ? e.FileName
                        : null;
                    if (file is not null)
                    {
                        _lastAnnouncedFile = file;
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

                    await ImportFileAsync(file, force: false, CancellationToken.None).ConfigureAwait(false);
                    break;

                case SimulatorFlightPlanEventKind.FlightLoaded when _options.CurrentValue.ImportOnFlightLoaded:
                    await ImportLoadedFlightAsync(e.FileName, requireRecent: false).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // This runs on a SimConnect callback: nothing may escape it.
            _logger.LogWarning(ex, "The flight plan announced by the simulator could not be imported.");
        }
    }

    /// <summary>
    /// Reads the plan of the flight that was just started: what the pilot set up before pressing
    /// "Start flight" (aerodromes, runway, VFR or IFR) is written next to the flight file.
    /// </summary>
    /// <param name="flightFile">The flight file the simulator named, or null.</param>
    /// <param name="requireRecent">
    /// True when the file is found by looking rather than by being named, so that the plan of a
    /// flight started long ago is not taken for this one.
    /// </param>
    private async Task ImportLoadedFlightAsync(string? flightFile, bool requireRecent)
    {
        // When the StormATC module is running, the EFB's route is the plan of the flight: the EFB starts
        // with the plan the flight was started with and follows what the pilot does with it. It is
        // newer than the plan file the simulator wrote when it loaded the flight, which may be the plan
        // of an earlier flight (the pilot may be in the menus), so the file is not read.
        if (requireRecent)
        {
            // The module's route is read when the link opens: give it a moment to arrive.
            for (int attempt = 0; attempt < 12 && _lastRoute is null; attempt++)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }

        if (requireRecent && _lastRoute is not null
            && _sessionContext.CurrentSession.FlightPlan is not { SourceIdentifier: "SimBrief" })
        {
            await WaitForRegistrationAsync().ConfigureAwait(false);
            if (_lastRoute is SimFlightPlan route)
            {
                _logger.LogInformation("The StormATC module already gave the EFB's plan; the simulator's plan file is not read.");
                await ImportPlanAsync(route, "the simulator (EFB)", force: false, CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }

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

        DateTime written = File.GetLastWriteTimeUtc(plan);
        if (requireRecent && DateTime.UtcNow - written > TimeSpan.FromHours(24))
        {
            _logger.LogInformation("The simulator's latest plan file {File} is too old to be this flight's plan.", plan);
            return;
        }

        if (_lastLoadedPlan is { } done && done.File == plan && done.Written == written)
        {
            return;
        }

        // The plan of a flight that was just started replaces the plans of earlier flights, but
        // never a plan the pilot brought from SimBrief.
        FlightPlan? current = _sessionContext.CurrentSession.FlightPlan;
        if (current is { SourceIdentifier: "SimBrief" })
        {
            _logger.LogInformation("A flight was loaded in the simulator; the SimBrief plan already in use is kept.");
            return;
        }

        await WaitForRegistrationAsync().ConfigureAwait(false);

        _lastLoadedPlan = (plan, written);
        await ImportFileAsync(plan, force: false, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The aircraft's registration arrives with its first state, a moment after the link opens.</summary>
    private async Task WaitForRegistrationAsync()
    {
        for (int attempt = 0; attempt < 20 && string.IsNullOrWhiteSpace(_sessionContext.CurrentSession.AircraftState.AtcIdentifier); attempt++)
        {
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    /// <summary>Imports a <c>.PLN</c> file the pilot chose.</summary>
    /// <param name="file">The file's path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<SimFlightPlanImportResult> ImportPlnFileAsync(string file, CancellationToken cancellationToken) =>
        ImportFileAsync(file, force: true, cancellationToken);

    private async Task<SimFlightPlanImportResult> ImportFileAsync(string file, bool force, CancellationToken cancellationToken)
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

        return await ImportPlanAsync(simPlan, file, force, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads a plan read from the simulator into the session.</summary>
    /// <param name="simPlan">The plan.</param>
    /// <param name="origin">Where it was read, for the log: a file name or a route source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="force">
    /// True when the pilot asked for it: the plan is loaded again even if it is the one in use.
    /// Otherwise a plan that is already loaded is left alone, so that the simulator announcing the
    /// same plan several times does not reset the session each time.
    /// </param>
    private async Task<SimFlightPlanImportResult> ImportPlanAsync(
        SimFlightPlan simPlan,
        string origin,
        bool force,
        CancellationToken cancellationToken)
    {
        // One plan at a time: the simulator announces a plan more than once in a moment, and each
        // load resets the session.
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadPlanAsync(simPlan, origin, force, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _importGate.Release();
        }
    }

    private async Task<SimFlightPlanImportResult> LoadPlanAsync(
        SimFlightPlan simPlan,
        string origin,
        bool force,
        CancellationToken cancellationToken)
    {
        AtcSession current = _sessionContext.CurrentSession;
        string? callsign = !string.IsNullOrWhiteSpace(current.AircraftState.AtcIdentifier)
            ? current.AircraftState.AtcIdentifier
            : current.FlightPlan is { SourceIdentifier: not ("Sample" or "Mock") } existing
                ? existing.Callsign.Original
                : null;

        // With no simulator (the scripted aircraft) there is no registration to wait for: the
        // flight keeps the callsign it has. With the simulator, the registration is awaited.
        if (callsign is null && _simulator is StormATC.Infrastructure.Simulator.SimulatorConnectorRouter { IsMockActive: true } && current.Callsign is { } placeholder)
        {
            callsign = placeholder.Original;
        }

        if (!SimFlightPlanConverter.TryConvert(simPlan, callsign, _clock.UtcNow, out FlightPlan? plan))
        {
            _logger.LogInformation(
                "The simulator's flight plan from {Origin} was not loaded: the aircraft's registration is not known yet.",
                origin);
            return new SimFlightPlanImportResult(
                false,
                UiText.T("The aircraft's registration is not known yet: StormATC could not name the flight."));
        }

        // The simulator's planner offers VFR and IFR (and a "military" flight it does not pass on): a plan to or from a
        // military base is flown in CAM V or CAM I.
        bool militaryBase =
            (await _sessionContext.GetVfrProfileForAsync(plan.DepartureIcao, cancellationToken).ConfigureAwait(false))?.IsMilitary == true
            || (await _sessionContext.GetVfrProfileForAsync(plan.DestinationIcao, cancellationToken).ConfigureAwait(false))?.IsMilitary == true;
        FlightRules rulesOfPlan = FlightRulesExtensions.ForPlan(simPlan.IsVfr, militaryBase);
        if (!force && IsAlreadyLoaded(_sessionContext.CurrentSession, plan, rulesOfPlan))
        {
            _logger.LogInformation(
                "The simulator's flight plan {Summary} is already the one in use; left as it is.",
                plan.ToSummary());
            return new SimFlightPlanImportResult(
                true,
                UiText.T("Flight plan imported from the simulator: ") + $"{plan.DepartureIcao} → {plan.DestinationIcao}.");
        }

        plan = await WithMissedApproachAsync(plan, simPlan, cancellationToken).ConfigureAwait(false);

        _sessionContext.PreassignedSquawk = null;
        await _sessionContext.LoadFlightPlanAsync(plan, cancellationToken).ConfigureAwait(false);
        _sessionContext.Mutate(session => session.WithFlightRules(rulesOfPlan));

        string rules = rulesOfPlan.ToLabel();
        _providerStatus.SetFlightPlanStatus(true, $"{plan.ToSummary()} · {rules} · {UiText.T("simulator")}");
        _logger.LogInformation("Imported the simulator's flight plan {Summary} ({Rules}) from {Origin}.", plan.ToSummary(), rules, origin);

        PlanImported?.Invoke(this, plan);
        return new SimFlightPlanImportResult(
            true,
            UiText.T("Flight plan imported from the simulator: ") + $"{plan.DepartureIcao} → {plan.DestinationIcao} ({rules}).");
    }

    /// <summary>Adds the missed approach of the planned approach, when the simulator's data gives it.</summary>
    private async Task<FlightPlan> WithMissedApproachAsync(FlightPlan plan, SimFlightPlan simPlan, CancellationToken cancellationToken)
    {
        if (_approaches is null || simPlan.IsVfr || simPlan.Approach is not SimApproach approach)
        {
            return plan;
        }

        try
        {
            IReadOnlyList<MissedApproachPoint>? points = await _approaches
                .GetMissedApproachAsync(simPlan.DestinationIcao, approach, cancellationToken)
                .ConfigureAwait(false);
            if (points is not { Count: > 0 })
            {
                _logger.LogInformation(
                    "The simulator gave no missed approach for {Approach} at {Airport}.",
                    approach,
                    simPlan.DestinationIcao);
                return plan;
            }

            _logger.LogInformation(
                "Missed approach of {Approach} at {Airport}: {Points}.",
                approach,
                simPlan.DestinationIcao,
                string.Join(" ", points.Select(p => p.AltitudeFeet is int feet ? $"{p.Identifier}({feet})" : p.Identifier)));
            return plan with { MissedApproach = points };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return plan;
        }
    }

    /// <summary>Whether the session already runs this plan from the simulator.</summary>
    private static bool IsAlreadyLoaded(AtcSession session, FlightPlan plan, FlightRules rules) =>
        FlightRulesExtensions.SameHandling(session.FlightRules, rules) && SimFlightPlanComparison.IsSamePlan(session.FlightPlan, plan);
}
