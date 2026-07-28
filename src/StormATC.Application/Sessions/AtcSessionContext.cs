using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Sessions;

/// <summary>
/// Owns the current session and assembles the context the ATC engine evaluates against.
/// </summary>
/// <remarks>
/// <para>
/// This is the single mutable holder of session state in the application. Everything else works
/// with immutable snapshots. Keeping mutation in one place means there is exactly one answer to
/// "what is the session right now", and the engine never has to reason about a half-applied
/// update.
/// </para>
/// <para>
/// Airport data is cached per ICAO code: it is read repeatedly, once per transmission, and does
/// not change during a session.
/// </para>
/// </remarks>
public sealed class AtcSessionContext(
    IAirportDataProvider airportDataProvider,
    ITrafficProvider trafficProvider,
    IWeatherProvider weatherProvider,
    IClock clock,
    ILogger<AtcSessionContext> logger)
{
    private readonly IAirportDataProvider _airportDataProvider =
        airportDataProvider ?? throw new ArgumentNullException(nameof(airportDataProvider));

    private readonly ITrafficProvider _trafficProvider =
        trafficProvider ?? throw new ArgumentNullException(nameof(trafficProvider));

    private readonly IWeatherProvider _weatherProvider =
        weatherProvider ?? throw new ArgumentNullException(nameof(weatherProvider));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly ILogger<AtcSessionContext> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly Dictionary<string, AirportData?> _airportCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _stateLock = new();
    private Frequency? _departureAtisFrequency;
    private Frequency? _departureDeliveryFrequency;
    private bool _departureAtisWasMonitored;

    private AtcSession _currentSession = AtcSession.Create(
        Guid.CreateVersion7(),
        DateTimeOffset.UtcNow,
        SessionMode.Mock,
        AircraftState.Unknown(DateTimeOffset.UtcNow));

    /// <summary>Raised whenever the session changes.</summary>
    public event EventHandler<AtcSession>? SessionChanged;

    /// <summary>The current session snapshot.</summary>
    public AtcSession CurrentSession
    {
        get
        {
            lock (_stateLock)
            {
                return _currentSession;
            }
        }
    }

    /// <summary>How strictly the engine should enforce procedure.</summary>
    public RealismProfile Realism { get; set; } = RealismProfile.Standard;

    /// <summary>
    /// Relative tail direction used for pushback when the pilot does not request one.
    /// </summary>
    public string DefaultPushbackDirection { get; set; } = PushbackDirections.TailLeft;

    /// <summary>
    /// A transponder code fixed by the scenario. Set by the sample-flight loader so the
    /// demonstration is reproducible; null in normal use.
    /// </summary>
    public SquawkCode? PreassignedSquawk { get; set; }

    /// <summary>Replaces the current session.</summary>
    public void UpdateSession(AtcSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_stateLock)
        {
            _currentSession = session;
        }

        SessionChanged?.Invoke(this, session);
    }

    /// <summary>Applies a change to the current session atomically.</summary>
    public AtcSession Mutate(Func<AtcSession, AtcSession> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        AtcSession updated;
        lock (_stateLock)
        {
            updated = change(_currentSession);
            _currentSession = updated;
        }

        SessionChanged?.Invoke(this, updated);
        return updated;
    }

    /// <summary>Starts a fresh session, discarding the previous one.</summary>
    public AtcSession StartNewSession(SessionMode mode, AircraftState? initialState = null)
    {
        DateTimeOffset now = _clock.UtcNow;

        AtcSession session = AtcSession.Create(
            Guid.CreateVersion7(),
            now,
            mode,
            initialState ?? AircraftState.Unknown(now));

        lock (_stateLock)
        {
            _departureAtisFrequency = null;
            _departureDeliveryFrequency = null;
            _departureAtisWasMonitored = false;
        }

        UpdateSession(session);
        return session;
    }

    /// <summary>Attaches a flight plan and seeds the runway and stand assignments from it.</summary>
    public async Task<AtcSession> LoadFlightPlanAsync(
        FlightPlan flightPlan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(flightPlan);

        AirportData? departure = await GetAirportAsync(flightPlan.DepartureIcao, cancellationToken)
            .ConfigureAwait(false);

        Frequency? atisFrequency = departure?.Frequencies.Atis;
        Frequency? deliveryFrequency =
            departure?.Frequencies.ResolveWithFallback(ControllerPosition.ClearanceDelivery);
        bool hasDistinctAtis = atisFrequency is Frequency atis
            && (deliveryFrequency is not Frequency delivery
                || !atis.IsSameChannel(delivery));

        lock (_stateLock)
        {
            _departureAtisFrequency = hasDistinctAtis ? atisFrequency : null;
            _departureDeliveryFrequency = deliveryFrequency;
            _departureAtisWasMonitored = false;
        }

        // Seed the stand only when the live aircraft is actually close to one. Airport facility
        // arrays are not ordered by the pilot's parking position, so choosing the first stand
        // can produce a physically wrong taxi route.
        string? stand = departure?.TaxiGraph is TaxiGraph graph
            && TaxiPathfinder.FindNearestNode(graph, CurrentSession.AircraftState.Position, 150)
                is TaxiNode { Kind: TaxiNodeKind.Stand, StandName: { Length: > 0 } nearbyStand }
            ? nearbyStand
            : null;

        return Mutate(session => session
            .WithFlightPlan(flightPlan)
            with
            {
                AssignedDepartureRunway = flightPlan.DepartureRunway,
                AssignedArrivalRunway = flightPlan.ArrivalRunway,
                AssignedStand = stand,
                Phase = session.Phase == FlightPhase.Disconnected ? FlightPhase.Preflight : session.Phase,
                CurrentController = session.CurrentController == ControllerPosition.None
                    ? hasDistinctAtis
                        ? ControllerPosition.Atis
                        : ControllerPosition.ClearanceDelivery
                    : session.CurrentController,
                CurrentFrequency = session.CurrentFrequency
                    ?? (hasDistinctAtis ? atisFrequency : deliveryFrequency),
            });
    }

    /// <summary>Records a new aircraft snapshot from the simulator.</summary>
    public AtcSession UpdateAircraftState(AircraftState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Mutate(session =>
        {
            AtcSession updated = session.WithAircraftState(state);

            // ATIS is a receive-only first step. Tuning the published Delivery (or its realistic
            // combined-position fallback) is the explicit action that moves the session from
            // listening to ATIS into two-way ATC.
            if (session.CurrentController == ControllerPosition.Atis
                && _departureAtisFrequency is Frequency atis)
            {
                Frequency tuned = state.Radio.ActiveTransmitFrequency;
                if (tuned.IsSameChannel(atis))
                {
                    _departureAtisWasMonitored = true;
                }
                else if (_departureAtisWasMonitored
                    && _departureDeliveryFrequency is Frequency delivery
                    && tuned.IsSameChannel(delivery))
                {
                    return updated.WithController(ControllerPosition.ClearanceDelivery, delivery);
                }
            }

            return updated;
        });
    }

    /// <summary>
    /// Assembles everything the engine needs for one decision.
    /// </summary>
    /// <remarks>
    /// Provider failures are swallowed deliberately: a traffic or weather provider that is down
    /// must degrade the simulation, not stop it. A null weather snapshot is meaningful — it
    /// tells the engine to omit wind rather than invent it.
    /// </remarks>
    public async Task<AtcEvaluationContext> BuildEvaluationContextAsync(
        AtcSession session,
        PilotIntent? intent,
        string transcript,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        AirportData? departure = null;
        AirportData? destination = null;

        if (session.FlightPlan is FlightPlan plan)
        {
            departure = await GetAirportAsync(plan.DepartureIcao, cancellationToken).ConfigureAwait(false);
            destination = await GetAirportAsync(plan.DestinationIcao, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<TrafficContact> traffic = [];
        try
        {
            traffic = await _trafficProvider
                .GetRelevantTrafficAsync(session.AircraftState, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Traffic provider failed; continuing with no traffic.");
        }

        WeatherSnapshot? weather = null;
        string? weatherIcao = session.Phase is FlightPhase.Descent or FlightPhase.Arrival
            or FlightPhase.Approach or FlightPhase.Final or FlightPhase.Landed
            or FlightPhase.TaxiIn or FlightPhase.Parked
            ? session.FlightPlan?.DestinationIcao
            : session.FlightPlan?.DepartureIcao;

        if (weatherIcao is { Length: > 0 })
        {
            try
            {
                weather = await _weatherProvider
                    .GetAirportWeatherAsync(weatherIcao, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Weather provider failed; wind will be omitted from responses.");
            }
        }

        return new AtcEvaluationContext
        {
            Session = session,
            AircraftState = session.AircraftState,
            FlightPlan = session.FlightPlan,
            Intent = intent,
            Transcript = transcript,
            DepartureAirport = departure,
            DestinationAirport = destination,
            Traffic = traffic,
            Weather = weather,
            CurrentTime = now,
            Realism = Realism,
            DefaultPushbackDirection = DefaultPushbackDirection,
            PreassignedSquawk = PreassignedSquawk,
        };
    }

    /// <summary>Loads an aerodrome, caching the result for the life of the session.</summary>
    public async Task<AirportData?> GetAirportAsync(string icao, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        if (_airportCache.TryGetValue(icao, out AirportData? cached))
        {
            return cached;
        }

        AirportData? airport = null;
        try
        {
            airport = await _airportDataProvider.GetAirportAsync(icao, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load airport data for {Icao}.", icao);
        }

        // Do not cache a miss: MSFS may connect later and make live facility data available.
        if (airport is not null)
        {
            _airportCache[icao] = airport;
        }

        return airport;
    }

    /// <summary>Drops the airport cache so edited data files are picked up.</summary>
    public void InvalidateAirportCache() => _airportCache.Clear();
}
