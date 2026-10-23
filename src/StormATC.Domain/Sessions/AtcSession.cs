using StormATC.Domain.Aircraft;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Sessions;

/// <summary>
/// The aggregate root: everything that is currently true about one ATC session.
/// </summary>
/// <remarks>
/// <para>
/// The session is an immutable record. Every state change produces a new instance through a
/// <c>With…</c> method, and only the ATC engine calls those methods. This makes it impossible
/// for a view model, an HTTP client, or a language model response handler to mutate clearance
/// state as a side effect — they can only hand an event to the engine and receive a new session.
/// </para>
/// <para>
/// Histories are append-only, which gives the transcript page, the persistence layer, and the
/// end-to-end test a single ordered record of what happened.
/// </para>
/// </remarks>
public sealed record AtcSession
{
    /// <summary>Unique session identifier.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>When the session started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Whether the session is driven by a real simulator or the mock.</summary>
    public required SessionMode Mode { get; init; }

    /// <summary>The active flight plan. Null until one is imported or the sample is loaded.</summary>
    public FlightPlan? FlightPlan { get; init; }

    /// <summary>The most recent aircraft state received from the simulator.</summary>
    public required AircraftState AircraftState { get; init; }

    /// <summary>Current phase of flight, as decided by the engine.</summary>
    public FlightPhase Phase { get; init; } = FlightPhase.Preflight;

    /// <summary>The controller currently working the aircraft.</summary>
    public ControllerPosition CurrentController { get; init; } = ControllerPosition.None;

    /// <summary>The frequency the pilot should be on.</summary>
    public Frequency? CurrentFrequency { get; init; }

    /// <summary>Simulator link state.</summary>
    public SimulatorConnectionState ConnectionState { get; init; } = SimulatorConnectionState.Disconnected;

    /// <summary>The IFR clearance, once issued.</summary>
    public Clearance? IfrClearance { get; init; }

    /// <summary>The current taxi clearance, outbound or inbound.</summary>
    public Clearance? TaxiClearance { get; init; }

    /// <summary>The current departure-related clearance — line-up or takeoff.</summary>
    public Clearance? DepartureClearance { get; init; }

    /// <summary>The current arrival-related clearance — approach or landing.</summary>
    public Clearance? ArrivalClearance { get; init; }

    /// <summary>The readback the pilot currently owes, if any.</summary>
    public PendingReadback? PendingReadback { get; init; }

    /// <summary>Every clearance issued this session, oldest first.</summary>
    public IReadOnlyList<Clearance> ClearanceHistory { get; init; } = [];

    /// <summary>Every controller handoff, oldest first.</summary>
    public IReadOnlyList<ControllerHandoff> HandoffHistory { get; init; } = [];

    /// <summary>The full conversation, oldest first.</summary>
    public IReadOnlyList<ConversationTurn> Conversation { get; init; } = [];

    /// <summary>Warnings currently raised, such as an unauthorised runway crossing.</summary>
    public IReadOnlyList<AtcWarning> ActiveWarnings { get; init; } = [];

    /// <summary>Runway assigned for departure.</summary>
    public RunwayIdentifier? AssignedDepartureRunway { get; init; }

    /// <summary>Runway assigned for arrival.</summary>
    public RunwayIdentifier? AssignedArrivalRunway { get; init; }

    /// <summary>Stand the aircraft departed from or is assigned on arrival.</summary>
    public string? AssignedStand { get; init; }

    /// <summary>Level the aircraft is currently cleared to.</summary>
    public Altitude? ClearedAltitude { get; init; }

    /// <summary>Heading the aircraft has been assigned, if any.</summary>
    public Heading? AssignedHeading { get; init; }

    /// <summary>Progress of any handoff in flight.</summary>
    public HandoffState HandoffState { get; init; } = HandoffState.None;

    /// <summary>When the session was completed. Null while it is still running.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// The language spoken on the radio. Every controller transmission in this session is worded
    /// with the matching profile from <see cref="AtcPhraseologies"/>.
    /// </summary>
    public PhraseologyLanguage Language { get; init; } = PhraseologyLanguage.English;

    /// <summary>
    /// The rules the flight is operated under. IFR until the pilot declares VFR; a VFR flight
    /// has no IFR clearance and stays with Tower while it flies the circuit.
    /// </summary>
    public FlightRules FlightRules { get; init; } = FlightRules.Ifr;

    /// <summary>The PAR or SRA approach the controller is talking the aircraft down, if any.</summary>
    public Approach.RadarApproachState? RadarApproach { get; init; }

    /// <summary>The leg of the aerodrome circuit the aircraft last reported.</summary>
    public CircuitLeg CircuitLeg { get; init; } = CircuitLeg.None;

    /// <summary>True when the pilot asked for a touch and go rather than a full-stop landing.</summary>
    public bool TouchAndGoRequested { get; init; }

    /// <summary>The option or low pass the pilot asked for, until the aircraft leaves the circuit.</summary>
    public CircuitManoeuvre RequestedManoeuvre { get; init; }

    /// <summary>The callsign in use, taken from the flight plan.</summary>
    public Callsign? Callsign => FlightPlan?.Callsign;

    /// <summary>True when the pilot owes a mandatory readback.</summary>
    public bool HasOutstandingMandatoryReadback =>
        PendingReadback is { IsMandatory: true };

    /// <summary>True when the session has reached its terminal state.</summary>
    public bool IsComplete => CompletedAt is not null || Phase == FlightPhase.Parked;

    /// <summary>Creates a new session in the pre-flight state.</summary>
    public static AtcSession Create(
        Guid sessionId,
        DateTimeOffset startedAt,
        SessionMode mode,
        AircraftState initialState) => new()
        {
            SessionId = sessionId,
            StartedAt = startedAt,
            Mode = mode,
            AircraftState = initialState,
            Phase = FlightPhase.Preflight,
            CurrentController = ControllerPosition.None,
        };

    // -----------------------------------------------------------------------------------
    // State transitions. Only the ATC engine should call these.
    // -----------------------------------------------------------------------------------

    /// <summary>Returns a session with a newer aircraft snapshot.</summary>
    public AtcSession WithAircraftState(AircraftState state) =>
        this with { AircraftState = state };

    /// <summary>Returns a session in a new flight phase.</summary>
    public AtcSession WithPhase(FlightPhase phase) => this with { Phase = phase };

    public AtcSession WithFlightRules(FlightRules rules) => this with { FlightRules = rules };

    public AtcSession WithCircuitLeg(CircuitLeg leg) => this with { CircuitLeg = leg };

    /// <summary>Starts, updates or (with null) ends the radar approach.</summary>
    public AtcSession WithRadarApproach(Approach.RadarApproachState? state) => this with { RadarApproach = state };

    public AtcSession WithArrivalRunway(RunwayIdentifier runway) => this with { AssignedArrivalRunway = runway };

    public AtcSession WithTouchAndGoRequested(bool requested) => this with { TouchAndGoRequested = requested };

    /// <summary>Records the option or low pass the pilot asked for.</summary>
    public AtcSession WithRequestedManoeuvre(CircuitManoeuvre manoeuvre) => this with { RequestedManoeuvre = manoeuvre };

    /// <summary>Returns a session with the flight plan attached.</summary>
    public AtcSession WithFlightPlan(FlightPlan plan) => this with { FlightPlan = plan };

    /// <summary>Returns a session working a different controller on a new frequency.</summary>
    public AtcSession WithController(ControllerPosition controller, Frequency? frequency) =>
        this with { CurrentController = controller, CurrentFrequency = frequency };

    /// <summary>Appends a clearance to the history and files it in the right slot.</summary>
    public AtcSession WithClearance(Clearance clearance)
    {
        ArgumentNullException.ThrowIfNull(clearance);

        var history = new List<Clearance>(ClearanceHistory) { clearance };

        AtcSession updated = this with { ClearanceHistory = history };

        return clearance.Type switch
        {
            ClearanceType.IfrClearance => updated with
            {
                IfrClearance = clearance,
                ClearedAltitude = clearance.InitialAltitude ?? ClearedAltitude,
                AssignedDepartureRunway = clearance.Runway ?? AssignedDepartureRunway,
            },
            // A taxi-to-stand clearance names the arrival stand, which is not the stand the
            // aircraft departed from. Recording it here is what lets the engine recognise
            // arrival at the correct one.
            ClearanceType.TaxiToStand => updated with
            {
                TaxiClearance = clearance,
                AssignedStand = clearance.TaxiDestination ?? AssignedStand,
            },
            ClearanceType.Taxi => updated with
            {
                TaxiClearance = clearance,
                AssignedDepartureRunway = clearance.Runway ?? AssignedDepartureRunway,
            },
            ClearanceType.Pushback
                or ClearanceType.RunwayCrossing => updated with { TaxiClearance = clearance },
            ClearanceType.LineUp or ClearanceType.Takeoff => updated with
            {
                DepartureClearance = clearance,
                AssignedDepartureRunway = clearance.Runway ?? AssignedDepartureRunway,
            },
            ClearanceType.ApproachClearance or ClearanceType.LandingClearance => updated with
            {
                ArrivalClearance = clearance,
                AssignedArrivalRunway = clearance.Runway ?? AssignedArrivalRunway,
            },
            ClearanceType.Climb or ClearanceType.Descent => updated with
            {
                ClearedAltitude = clearance.InitialAltitude ?? ClearedAltitude,
            },
            ClearanceType.Heading => updated with
            {
                AssignedHeading = clearance.AssignedHeading ?? AssignedHeading,
            },
            _ => updated,
        };
    }

    /// <summary>Returns a session with a readback outstanding.</summary>
    public AtcSession WithPendingReadback(PendingReadback? readback) =>
        this with { PendingReadback = readback };

    /// <summary>
    /// Clears the outstanding readback and marks the matching clearance acknowledged.
    /// </summary>
    public AtcSession WithReadbackAccepted(DateTimeOffset at)
    {
        if (PendingReadback is not PendingReadback pending)
        {
            return this;
        }

        Guid clearanceId = pending.ClearanceId;

        var history = new List<Clearance>(ClearanceHistory.Count);
        foreach (Clearance clearance in ClearanceHistory)
        {
            history.Add(clearance.ClearanceId == clearanceId ? clearance.Acknowledge(at) : clearance);
        }

        return this with
        {
            PendingReadback = null,
            ClearanceHistory = history,
            IfrClearance = AcknowledgeIfMatching(IfrClearance, clearanceId, at),
            TaxiClearance = AcknowledgeIfMatching(TaxiClearance, clearanceId, at),
            DepartureClearance = AcknowledgeIfMatching(DepartureClearance, clearanceId, at),
            ArrivalClearance = AcknowledgeIfMatching(ArrivalClearance, clearanceId, at),
        };

        static Clearance? AcknowledgeIfMatching(Clearance? clearance, Guid id, DateTimeOffset at) =>
            clearance is not null && clearance.ClearanceId == id ? clearance.Acknowledge(at) : clearance;
    }

    /// <summary>Appends a handoff and switches the working controller.</summary>
    public AtcSession WithHandoff(ControllerHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);

        var history = new List<ControllerHandoff>(HandoffHistory) { handoff };

        return this with
        {
            HandoffHistory = history,
            CurrentController = handoff.ToController,
            CurrentFrequency = handoff.ToFrequency,
            HandoffState = handoff.State,
        };
    }

    /// <summary>Appends a conversation turn.</summary>
    public AtcSession WithConversationTurn(ConversationTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var conversation = new List<ConversationTurn>(Conversation) { turn };
        return this with { Conversation = conversation };
    }

    /// <summary>Raises a warning.</summary>
    public AtcSession WithWarning(AtcWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);

        var warnings = new List<AtcWarning>(ActiveWarnings) { warning };
        return this with { ActiveWarnings = warnings };
    }

    /// <summary>Clears all active warnings.</summary>
    public AtcSession WithWarningsCleared() => this with { ActiveWarnings = [] };

    /// <summary>Returns a session with the simulator connection state updated.</summary>
    public AtcSession WithConnectionState(SimulatorConnectionState state) =>
        this with { ConnectionState = state };

    /// <summary>Marks the session complete.</summary>
    public AtcSession WithCompletion(DateTimeOffset at) =>
        this with { CompletedAt = at, Phase = FlightPhase.Parked };
}

/// <summary>A transfer of an aircraft from one controller to another.</summary>
public sealed record ControllerHandoff
{
    /// <summary>The controller releasing the aircraft.</summary>
    public required ControllerPosition FromController { get; init; }

    /// <summary>The controller taking the aircraft.</summary>
    public required ControllerPosition ToController { get; init; }

    /// <summary>The frequency the pilot is instructed to contact.</summary>
    public required Frequency ToFrequency { get; init; }

    /// <summary>When the handoff was instructed.</summary>
    public required DateTimeOffset InstructedAt { get; init; }

    /// <summary>How far the handoff has progressed.</summary>
    public HandoffState State { get; init; } = HandoffState.Instructed;

    /// <summary>Why the handoff happened, for the transcript and diagnostics.</summary>
    public string? Reason { get; init; }
}

/// <summary>One transmission in the session conversation.</summary>
public sealed record ConversationTurn
{
    /// <summary>Unique identifier.</summary>
    public required Guid TurnId { get; init; }

    /// <summary>When the transmission occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Who was speaking.</summary>
    public required ConversationSpeaker Speaker { get; init; }

    /// <summary>The controller position involved.</summary>
    public ControllerPosition Controller { get; init; } = ControllerPosition.None;

    /// <summary>The text as spoken or transcribed.</summary>
    public required string Text { get; init; }

    /// <summary>The normalised transcript, when it differs from <see cref="Text"/>.</summary>
    public string? NormalizedText { get; init; }

    /// <summary>The intent the parser assigned, for a pilot transmission.</summary>
    public PilotIntentType? Intent { get; init; }

    /// <summary>Correlation identifier linking this turn to its pipeline diagnostics.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Warnings attached to this turn.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// True when the controller text came from deterministic fallback phraseology rather than
    /// the language model — either because no model is configured or because validation failed.
    /// </summary>
    public bool UsedDeterministicFallback { get; init; }
}

/// <summary>Who produced a conversation turn.</summary>
public enum ConversationSpeaker
{
    Pilot = 0,
    Controller,
    System,
}

/// <summary>A warning raised by the ATC engine.</summary>
public sealed record AtcWarning
{
    /// <summary>Severity.</summary>
    public required AtcSeverity Severity { get; init; }

    /// <summary>Short machine-readable code, for example <c>HoldShortViolation</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable message shown in the UI.</summary>
    public required string Message { get; init; }

    /// <summary>When it was raised.</summary>
    public required DateTimeOffset RaisedAt { get; init; }
}
