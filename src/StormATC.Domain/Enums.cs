namespace StormATC.Domain;

/// <summary>
/// Phase of flight as tracked by the deterministic ATC engine.
/// </summary>
/// <remarks>
/// This is StormATC's own operational state, not a simulator variable. The simulator reports
/// physical truth (on ground, airspeed, altitude); the engine decides which phase that truth
/// plus the clearance history adds up to. The two can legitimately disagree for a moment —
/// an aircraft can be physically airborne while still in <see cref="Takeoff"/> until the
/// engine observes a stable climb.
/// </remarks>
public enum FlightPhase
{
    Disconnected = 0,
    Preflight,
    ClearanceRequested,
    IFRClearanceIssued,
    Pushback,
    TaxiOut,
    HoldingShort,
    LineUp,
    Takeoff,
    InitialClimb,
    Departure,
    Enroute,
    Descent,
    Arrival,
    Approach,
    Final,
    Landed,
    TaxiIn,
    Parked,
    GoAround,
    Abnormal,

    /// <summary>Flying the aerodrome circuit under VFR: crosswind, downwind, base or final.</summary>
    Circuit,
}

/// <summary>The rules a flight is operated under.</summary>
public enum FlightRules
{
    /// <summary>Instrument flight rules: clearance, SID, squawk and departure handoff.</summary>
    Ifr = 0,

    /// <summary>
    /// Visual flight rules at a controlled aerodrome: no IFR clearance and no handoff to a
    /// departure controller; the aircraft stays with Tower while it flies the circuit.
    /// </summary>
    Vfr,
}

/// <summary>Where the aircraft is in the aerodrome circuit.</summary>
public enum CircuitLeg
{
    /// <summary>Not in the circuit.</summary>
    None = 0,

    /// <summary>Cleared to join or reported joining; downwind not yet reported.</summary>
    Joining,

    /// <summary>Downwind leg reported.</summary>
    Downwind,

    /// <summary>Base leg reported.</summary>
    Base,

    /// <summary>Final reported.</summary>
    Final,
}

/// <summary>
/// The controller position currently working the aircraft.
/// </summary>
public enum ControllerPosition
{
    None = 0,
    ClearanceDelivery,
    Ground,
    Tower,
    Departure,
    Center,
    Approach,
    Atis,
}

/// <summary>
/// Classified pilot intent. Produced by the deterministic parser, or by the language model
/// when deterministic confidence is too low. Never trusted without domain validation.
/// </summary>
public enum PilotIntentType
{
    Unknown = 0,
    InitialCall,
    RadioCheck,
    RequestIFRClearance,
    ClearanceReadback,
    RequestPushback,
    RequestTaxi,
    RequestProgressiveTaxi,
    TaxiReadback,
    ReportHoldingShort,
    ReadyForDeparture,
    TakeoffReadback,
    ReportAirborne,
    CheckIn,
    AltitudeReadback,
    HeadingReadback,
    FrequencyReadback,
    RequestClimb,
    RequestDescent,
    RequestDirect,
    RequestDeviation,
    RequestApproach,
    ReportEstablished,
    LandingReadback,
    ReportRunwayVacated,
    RequestTaxiToStand,
    GoAround,
    Mayday,
    PanPan,
    Unable,
    SayAgain,
    Correction,
    RequestFrequency,
    PilotAcknowledgement,

    /// <summary>"Lognes Tour, VFR from X for a touch and go, 1500 feet, estimate E at 05."</summary>
    VfrArrivalCall,

    /// <summary>Reporting a published VFR reporting point, such as "passing E".</summary>
    ReportAtPoint,

    /// <summary>Downwind leg of the circuit.</summary>
    ReportDownwind,

    /// <summary>Base leg of the circuit.</summary>
    ReportBase,

    /// <summary>Final, in the circuit (no instrument approach involved).</summary>
    ReportFinal,

    /// <summary>"Request touch and go."</summary>
    RequestTouchAndGo,

    /// <summary>Leaving the circuit and the frequency.</summary>
    LeaveFrequency,
}

/// <summary>
/// Category of clearance held or issued. Note that <see cref="ApproachClearance"/> and
/// <see cref="LandingClearance"/> are deliberately distinct: being cleared for the approach
/// does not authorise landing, and the engine enforces that separation.
/// </summary>
public enum ClearanceType
{
    None = 0,
    IfrClearance,
    Pushback,
    Taxi,
    LineUp,
    Takeoff,
    Climb,
    Descent,
    DirectTo,
    Heading,
    ApproachClearance,
    LandingClearance,
    TaxiToStand,
    RunwayCrossing,
}

/// <summary>
/// An individual value a pilot is required to read back.
/// </summary>
public enum ReadbackItemType
{
    Callsign = 0,
    Runway,
    InitialAltitude,
    CruiseAltitude,
    Squawk,
    Frequency,
    Heading,
    HoldShortRunway,
    TaxiRoute,
    DepartureProcedure,
    Route,
    ApproachName,
    Speed,
    QnhAltimeter,
}

/// <summary>Severity of an ATC-generated warning or event.</summary>
public enum AtcSeverity
{
    Information = 0,
    Advisory,
    Caution,
    Warning,
}

/// <summary>Generic connection state used by every external provider.</summary>
public enum ProviderConnectionState
{
    NotConfigured = 0,
    Configured,
    Connecting,
    Connected,
    Degraded,
    Unavailable,
    Error,
}

/// <summary>Simulator link state.</summary>
public enum SimulatorConnectionState
{
    Disconnected = 0,
    Connecting,
    Connected,
    Reconnecting,
    Unavailable,
    Error,
}

/// <summary>Push-to-talk / speech pipeline state surfaced to the UI.</summary>
public enum SpeechState
{
    Idle = 0,
    Connecting,
    Listening,
    Processing,
    ControllerResponding,
    PlayingAudio,
    Error,
}

/// <summary>Where a transmission originated.</summary>
public enum TransmissionSource
{
    /// <summary>Spoken by the pilot and transcribed by speech-to-text.</summary>
    Voice = 0,

    /// <summary>Typed by the user in developer mode. Bypasses STT only — nothing else.</summary>
    ManualText,

    /// <summary>Generated by the scripted demonstration runner.</summary>
    Demonstration,

    /// <summary>Produced by an automated test.</summary>
    Test,
}

/// <summary>Outcome of validating an intent, a readback, or a model response.</summary>
public enum ValidationResult
{
    Valid = 0,
    Incomplete,
    Invalid,
    Rejected,
}

/// <summary>Progress of a controller-to-controller handoff.</summary>
public enum HandoffState
{
    None = 0,

    /// <summary>The transferring controller has instructed a frequency change.</summary>
    Instructed,

    /// <summary>The pilot has read the new frequency back correctly.</summary>
    Acknowledged,

    /// <summary>The pilot has checked in with the receiving controller.</summary>
    CheckedIn,
}

/// <summary>Whether the session is driven by a real simulator or the built-in mock.</summary>
public enum SessionMode
{
    Mock = 0,
    Live,
}

/// <summary>Overall verdict of a readback comparison.</summary>
public enum ReadbackOutcome
{
    /// <summary>Every required item present and correct.</summary>
    Correct = 0,

    /// <summary>All present items correct, but at least one required item is missing.</summary>
    Incomplete,

    /// <summary>At least one item was read back with the wrong value.</summary>
    Incorrect,

    /// <summary>The transmission used a callsign belonging to another aircraft.</summary>
    WrongCallsign,

    /// <summary>Nothing readback-shaped was found in the transmission.</summary>
    NotAReadback,
}

/// <summary>The kind of response the engine has authorised the controller to make.</summary>
public enum AtcResponseType
{
    None = 0,
    Acknowledgement,
    IfrClearance,
    ClearanceReadbackCorrection,
    PushbackApproval,
    TaxiClearance,
    HoldShortInstruction,
    LineUpClearance,
    TakeoffClearance,
    Handoff,
    CheckInAcknowledgement,
    ClimbInstruction,
    DescentInstruction,
    DirectToApproval,
    HeadingInstruction,
    ApproachClearance,
    LandingClearance,
    GoAroundInstruction,
    VacateInstruction,
    TaxiToStandClearance,
    Rejection,
    SayAgain,
    Unable,
    EmergencyAcknowledgement,
    RadioCheckReply,

    /// <summary>Roger, report the reporting point.</summary>
    ReportPointInstruction,

    /// <summary>Join the circuit (downwind, base or straight in) and report.</summary>
    CircuitJoinInstruction,

    /// <summary>Report the next leg of the circuit.</summary>
    CircuitReportInstruction,

    /// <summary>Cleared for a touch and go.</summary>
    TouchAndGoClearance,

    /// <summary>Cleared for the option: full stop, touch and go or stop-and-go, at the pilot's choice.</summary>
    OptionClearance,

    /// <summary>Cleared for a low pass over the runway, without touching down.</summary>
    LowPassClearance,
}
