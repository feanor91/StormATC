using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.FlightPlanning;
using StormATC.Application.Options;
using StormATC.Application.Sessions;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Atc;
using StormATC.Domain.Airports;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.ValueObjects;

namespace StormATC.Desktop.Services;

/// <summary>
/// Puts the aircraft of a flight that has just started where the pilot wants it: on a parking stand
/// of the departure aerodrome instead of the holding point or the runway, and shut down (cold and dark).
/// </summary>
/// <remarks>
/// <para>
/// Both are options, off by default. They only act on a flight the simulator just loaded, never in the
/// middle of a flight, and only when the aircraft is on the ground, at rest, near the departure aerodrome
/// of the flight plan. What is done, and what the simulator answered, is written to the log.
/// </para>
/// </remarks>
public sealed class StartPositionService : IDisposable
{
    /// <summary>The farthest the aircraft may be from the departure aerodrome for the plan to be its own, in nautical miles.</summary>
    internal const double NearAerodromeNauticalMiles = 5;

    private readonly ISimulatorFlightPlanEvents _events;
    private readonly ISimulatorConnector _simulator;
    private readonly ISimulatorAircraftControl _control;
    private readonly AtcSessionContext _sessionContext;
    private readonly SimFlightPlanService _plans;
    private readonly ITrafficProvider _traffic;
    private readonly IOptionsMonitor<StartPositionOptions> _options;
    private readonly ILogger<StartPositionService> _logger;
    private CancellationTokenSource? _run;
    private TaskCompletionSource? _planImported;

    /// <summary>Creates the service and starts listening for loaded flights.</summary>
    public StartPositionService(
        ISimulatorFlightPlanEvents events,
        ISimulatorConnector simulator,
        ISimulatorAircraftControl control,
        AtcSessionContext sessionContext,
        SimFlightPlanService plans,
        ITrafficProvider traffic,
        IOptionsMonitor<StartPositionOptions> options,
        ILogger<StartPositionService> logger)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _events.FlightPlanAnnounced += OnAnnounced;
        _plans.PlanImported += OnPlanImported;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _events.FlightPlanAnnounced -= OnAnnounced;
        _plans.PlanImported -= OnPlanImported;
        _run?.Cancel();
        _run?.Dispose();
    }

    private void OnPlanImported(object? sender, FlightPlan plan) => _planImported?.TrySetResult();

    private void OnAnnounced(object? sender, SimulatorFlightPlanEventArgs e)
    {
        StartPositionOptions options = _options.CurrentValue;
        if (e.Kind != SimulatorFlightPlanEventKind.FlightLoaded
            || !(options.StartAtParking || options.ColdAndDark)
            || _simulator is StormATC.Infrastructure.Simulator.SimulatorConnectorRouter { IsMockActive: true })
        {
            return;
        }

        CancellationTokenSource? previous = Interlocked.Exchange(ref _run, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        _planImported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = _run!.Token;
        _ = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("A flight was loaded: StormATC will place the aircraft as asked once it is ready.");

            // The menus hand over to the flight a moment after the notice: whatever is on the ground
            // before that is the previous flight's.
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);

            AircraftState? state = await WaitForAircraftAtRestAsync(cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                _logger.LogInformation("The aircraft did not come to rest on the ground in time: it was left as it is.");
                return;
            }

            StartPositionOptions options = _options.CurrentValue;
            if (options.StartAtParking)
            {
                state = await PlaceAtParkingAsync(state, cancellationToken).ConfigureAwait(false) ?? state;
            }

            if (options.ColdAndDark || options.StartAtParking)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                state = _sessionContext.CurrentSession.AircraftState;
            }

            if (options.ColdAndDark && state.IsOnGround && state.GroundSpeedKnots < 5)
            {
                bool done = await _control.ShutDownAircraftAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Cold and dark: the simulator {Answer} the shutdown.", done ? "took" : "did not take");
            }

            if ((options.ColdAndDark || options.StartAtParking) && state.IsOnGround && state.GroundSpeedKnots < 5)
            {
                // The parking brake only has a toggle: press it if it is not already set.
                if (!_sessionContext.CurrentSession.AircraftState.ParkingBrakeSet)
                {
                    await _control.ToggleParkingBrakeAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A newer flight was loaded, or StormATC is closing.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            _logger.LogWarning(ex, "Placing the aircraft at the start of the flight failed.");
        }
    }

    /// <summary>Waits for the pilot to be in an aircraft that stands still on the ground.</summary>
    private async Task<AircraftState?> WaitForAircraftAtRestAsync(CancellationToken cancellationToken)
    {
        GeoPosition? previous = null;
        for (int attempt = 0; attempt < 240; attempt++)
        {
            AircraftState state = _sessionContext.CurrentSession.AircraftState;
            bool ready = state.IsUserInAircraft
                && state.IsOnGround
                && state.GroundSpeedKnots < 5
                && !string.IsNullOrWhiteSpace(state.AtcIdentifier);
            if (ready && previous is GeoPosition before && before.DistanceMetersTo(state.Position) < 2)
            {
                return state;
            }

            previous = ready ? state.Position : null;
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Moves the aircraft to a stand of the departure aerodrome. Returns its new state, or null when it did not move.</summary>
    private async Task<AircraftState?> PlaceAtParkingAsync(AircraftState state, CancellationToken cancellationToken)
    {
        // The plan of the flight that was just loaded arrives a moment after the notice.
        if (_planImported is { } imported)
        {
            await Task.WhenAny(imported.Task, Task.Delay(TimeSpan.FromSeconds(20), cancellationToken)).ConfigureAwait(false);
        }

        FlightPlan? plan = _sessionContext.CurrentSession.FlightPlan;
        if (plan is null || plan.SourceIdentifier is "Sample" or "Mock")
        {
            _logger.LogInformation("There is no flight plan to say where the flight starts: the aircraft was not moved to a parking.");
            return null;
        }

        AirportData? airport = await _sessionContext.GetAirportAsync(plan.DepartureIcao, cancellationToken).ConfigureAwait(false);
        if (airport is null || airport.Stands.Count == 0)
        {
            _logger.LogInformation("{Icao} has no parking stand in the simulator's data: the aircraft was not moved.", plan.DepartureIcao);
            return null;
        }

        double distance = state.Position.DistanceNauticalMilesTo(airport.ReferencePosition);
        if (distance > NearAerodromeNauticalMiles)
        {
            _logger.LogInformation(
                "The aircraft is {Distance:F0} nm from {Icao}, the departure of the plan: it was not moved.",
                distance,
                plan.DepartureIcao);
            return null;
        }

        IReadOnlyList<TrafficContact> traffic = await _traffic
            .GetRelevantTrafficAsync(state, cancellationToken, 3)
            .ConfigureAwait(false);
        GeoPosition[] occupied = [.. traffic.Where(contact => contact.IsOnGround).Select(contact => contact.Position)];
        bool military = (await _sessionContext.GetVfrProfileForAsync(plan.DepartureIcao, cancellationToken).ConfigureAwait(false))?.IsMilitary == true;

        StandData? stand = StartStandSelector.Choose(airport, state.Position, state.WingSpanFeet * 0.3048, occupied, military);
        if (stand is null)
        {
            _logger.LogInformation("{Icao} has no free parking stand that fits the aircraft: it was not moved.", plan.DepartureIcao);
            return null;
        }

        bool placed = await _control
            .PlaceAircraftAsync(stand.Position, stand.HeadingDegrees ?? state.TrueHeadingDegrees, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Start at the parking: {Answer} the move to {Stand} at {Icao}.",
            placed ? "the simulator took" : "the simulator did not take",
            stand.Name,
            plan.DepartureIcao);
        return placed ? _sessionContext.CurrentSession.AircraftState : null;
    }
}
