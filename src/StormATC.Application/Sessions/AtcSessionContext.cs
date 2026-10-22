using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
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
    ILogger<AtcSessionContext> logger,
    IVfrProfileProvider? vfrProfileProvider = null)
{
    private readonly IVfrProfileProvider? _vfrProfileProvider = vfrProfileProvider;

    private readonly Dictionary<string, VfrAerodromeProfile?> _vfrProfileCache =
        new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// The position that answers first on the ground: Delivery where the aerodrome publishes
    /// one, otherwise Ground (as at Lognes, which has Sol and Tour but no Prévol).
    /// </summary>
    private ControllerPosition _departureStartController = ControllerPosition.ClearanceDelivery;
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
    /// The radio language for new sessions. Change it with <see cref="SetLanguage"/> so the
    /// running session switches too.
    /// </summary>
    public PhraseologyLanguage Language { get; private set; } = PhraseologyLanguage.English;

    /// <summary>
    /// Switches the radio language now: the current session, and every session started after
    /// it. Only wording, recognition and voice change; the session's state is untouched.
    /// </summary>
    public void SetLanguage(PhraseologyLanguage language)
    {
        lock (_stateLock)
        {
            Language = language;
        }

        if (CurrentSession.Language != language)
        {
            Mutate(session => session with { Language = language });
        }
    }

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
            initialState ?? AircraftState.Unknown(now)) with
        {
            Language = Language,
        };

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
            _departureStartController =
                departure?.Frequencies is { ClearanceDelivery: null, Ground: not null }
                    ? ControllerPosition.Ground
                    : ControllerPosition.ClearanceDelivery;
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

        AtcSession loaded = Mutate(session =>
        {
            // A plan from another aerodrome starts a new flight: the controller, frequency and
            // clearances of the previous one do not carry over. Without this, importing a plan
            // after the bundled sample left the session on the sample aerodrome's ATIS, so no
            // transmission at the real aerodrome could ever be heard. Reloading the same
            // aerodrome's plan mid-flight keeps the flight where it is.
            bool freshStart = session.FlightPlan is null
                || !string.Equals(
                    session.FlightPlan.DepartureIcao,
                    flightPlan.DepartureIcao,
                    StringComparison.OrdinalIgnoreCase);

            AtcSession updated = session.WithFlightPlan(flightPlan) with
            {
                AssignedDepartureRunway = flightPlan.DepartureRunway,
                AssignedArrivalRunway = flightPlan.ArrivalRunway,
                AssignedStand = stand,
                Phase = freshStart || session.Phase == FlightPhase.Disconnected
                    ? FlightPhase.Preflight
                    : session.Phase,
                CurrentController = freshStart || session.CurrentController == ControllerPosition.None
                    ? hasDistinctAtis
                        ? ControllerPosition.Atis
                        : _departureStartController
                    : session.CurrentController,
                CurrentFrequency = freshStart
                    ? hasDistinctAtis ? atisFrequency : deliveryFrequency
                    : session.CurrentFrequency
                        ?? (hasDistinctAtis ? atisFrequency : deliveryFrequency),
            };

            return freshStart
                ? updated with
                {
                    PendingReadback = null,
                    IfrClearance = null,
                    TaxiClearance = null,
                    DepartureClearance = null,
                    ArrivalClearance = null,
                    ClearedAltitude = null,
                    AssignedHeading = null,
                    HandoffState = HandoffState.None,
                    CircuitLeg = CircuitLeg.None,
                    TouchAndGoRequested = false,
                    RequestedManoeuvre = CircuitManoeuvre.None,
                }
                : updated;
        });

        await AnnounceAerodromeWithoutAtcAsync(flightPlan.DepartureIcao, cancellationToken).ConfigureAwait(false);
        return CurrentSession is { } current && current.SessionId == loaded.SessionId ? current : loaded;
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
                    return updated.WithController(_departureStartController, delivery);
                }
            }

            // A VFR flight at a controlled aerodrome has no clearance to wait for and no fixed
            // order of frequencies: whichever published frequency the pilot tunes is the
            // controller who answers. Listening to the ATIS, then calling Ground or Tower, is
            // simply tuning them in turn.
            if (updated.FlightRules == FlightRules.Vfr
                && updated.FlightPlan is FlightPlan localPlan
                && FollowTunedFrequency(updated, state, localPlan) is AtcSession followed)
            {
                updated = followed;
            }

            // A VFR arrival calls the destination tower directly; nobody hands it over. Once the
            // aircraft is airborne and has tuned that frequency, the tower is its controller, and
            // its transmissions are heard instead of being ignored as off frequency.
            if (updated.FlightRules == FlightRules.Vfr
                && !state.IsOnGround
                && updated.CurrentController != ControllerPosition.Tower
                && updated.FlightPlan is FlightPlan plan
                && _airportCache.TryGetValue(plan.DestinationIcao, out AirportData? destination)
                && destination?.Frequencies.Tower is Frequency tower
                && state.Radio.ActiveTransmitFrequency.IsSameChannel(tower))
            {
                updated = updated.WithController(ControllerPosition.Tower, tower);
                if (updated.Phase is FlightPhase.Preflight or FlightPhase.IFRClearanceIssued
                    or FlightPhase.Departure or FlightPhase.Enroute or FlightPhase.Descent)
                {
                    updated = updated.WithPhase(FlightPhase.Arrival);
                }
            }

            return updated;
        });
    }

    /// <summary>
    /// The session moved to the controller whose published frequency the aircraft has tuned, or
    /// null when the tuned frequency is not one of the aerodrome's or already is the session's.
    /// </summary>
    private AtcSession? FollowTunedFrequency(AtcSession session, AircraftState state, FlightPlan plan)
    {
        string icao = session.Phase is FlightPhase.Descent or FlightPhase.Arrival or FlightPhase.Approach
            or FlightPhase.Circuit or FlightPhase.Landed or FlightPhase.TaxiIn
            ? plan.DestinationIcao
            : plan.DepartureIcao;

        if (!_airportCache.TryGetValue(icao, out AirportData? airport) || airport is null)
        {
            return null;
        }

        Frequency tuned = state.Radio.ActiveTransmitFrequency;
        ControllerPosition[] positions =
        [
            ControllerPosition.Atis,
            ControllerPosition.ClearanceDelivery,
            ControllerPosition.Ground,
            ControllerPosition.Tower,
        ];

        foreach (ControllerPosition position in positions)
        {
            if (airport.Frequencies.For(position) is Frequency published && tuned.IsSameChannel(published))
            {
                return session.CurrentController == position
                    && session.CurrentFrequency is Frequency current
                    && current.IsSameChannel(published)
                        ? null
                        : session.WithController(position, published);
            }
        }

        return null;
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
            VfrProfile = await GetVfrProfileAsync(
                    session.FlightPlan is FlightPlan filed
                        ? (IsArrivalPhase(session.Phase) ? filed.DestinationIcao : filed.DepartureIcao)
                        : null,
                    cancellationToken)
                .ConfigureAwait(false),
            Traffic = traffic,
            Weather = weather,
            CurrentTime = now,
            Realism = Realism,
            DefaultPushbackDirection = DefaultPushbackDirection,
            PreassignedSquawk = PreassignedSquawk,
        };
    }

    /// <summary>
    /// The VFR profile of the aerodrome the flight is at now: the departure aerodrome until the flight is
    /// on its way to the destination.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<VfrAerodromeProfile?> GetCurrentVfrProfileAsync(CancellationToken cancellationToken)
    {
        AtcSession session = CurrentSession;
        return await GetVfrProfileAsync(
                session.FlightPlan is FlightPlan filed
                    ? (IsArrivalPhase(session.Phase) ? filed.DestinationIcao : filed.DepartureIcao)
                    : null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the pilot, in the conversation, that the departure aerodrome has no air traffic service:
    /// there is nobody to call, and the calls are made blind on the air-air frequency.
    /// </summary>
    private async Task AnnounceAerodromeWithoutAtcAsync(string departureIcao, CancellationToken cancellationToken)
    {
        VfrAerodromeProfile? profile = await GetVfrProfileAsync(departureIcao, cancellationToken).ConfigureAwait(false);
        if (profile is not { HasNoAtc: true })
        {
            return;
        }

        string text = AerodromeWithoutAtcMessage(profile);
        Mutate(session => session.Conversation.Any(turn => turn.Speaker == ConversationSpeaker.System && turn.Text == text)
            ? session
            : session.WithConversationTurn(new ConversationTurn
            {
                TurnId = Guid.CreateVersion7(),
                Timestamp = _clock.UtcNow,
                Speaker = ConversationSpeaker.System,
                Text = text,
            }));
    }

    /// <summary>What the pilot is told at an aerodrome that has no air traffic service.</summary>
    /// <param name="profile">The aerodrome's profile.</param>
    public static string AerodromeWithoutAtcMessage(VfrAerodromeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string name = string.IsNullOrWhiteSpace(profile.Name) ? profile.Icao : $"{profile.Name} ({profile.Icao})";
        return profile.AirAirFrequency is Frequency airAir
            ? UiText.T($"{name} has no air traffic service (auto-information): there is no controller to call. Make your calls blind on {airAir.ToDisplay()}; StormATC does not answer.")
            : UiText.T($"{name} has no air traffic service (auto-information): there is no controller to call. Make your calls blind on the aerodrome's frequency; StormATC does not answer.");
    }

    private static bool IsArrivalPhase(FlightPhase phase) => phase is FlightPhase.Descent
        or FlightPhase.Arrival or FlightPhase.Approach or FlightPhase.Final or FlightPhase.Landed
        or FlightPhase.TaxiIn or FlightPhase.Parked or FlightPhase.GoAround or FlightPhase.Circuit;

    /// <summary>Loads an aerodrome's VFR profile, caching the result (including "none").</summary>
    private async Task<VfrAerodromeProfile?> GetVfrProfileAsync(string? icao, CancellationToken cancellationToken)
    {
        if (_vfrProfileProvider is null || string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        if (_vfrProfileCache.TryGetValue(icao, out VfrAerodromeProfile? cached))
        {
            return cached;
        }

        VfrAerodromeProfile? profile = null;
        try
        {
            profile = await _vfrProfileProvider.GetProfileAsync(icao, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The VFR profile of {Icao} could not be loaded.", icao);
        }

        _vfrProfileCache[icao] = profile;
        return profile;
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
