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

    /// <summary>
    /// French military air traffic regime CAM V (Victor, à vue): the military counterpart of VFR,
    /// handled by the base's tower and approach with military phraseology.
    /// </summary>
    CamV,

    /// <summary>
    /// French military air traffic regime CAM T (Tango, tactique): a tactical flight, outside the air traffic
    /// service: no clearance and no separation from the controller.
    /// </summary>
    CamT,

    /// <summary>
    /// French military air traffic regime CAM I (India, aux instruments): the military counterpart of IFR,
    /// handled by the base's approach and tower with military phraseology.
    /// </summary>
    CamI,
}

/// <summary>Groups the flight rules by how a flight is handled, whatever the civil or military regime.</summary>
public static class FlightRulesExtensions
{
    /// <summary>True for VFR and for CAM V: visual flight, no clearance, the circuit stays with Tower.</summary>
    public static bool IsVisual(this FlightRules rules) => rules is FlightRules.Vfr or FlightRules.CamV;

    /// <summary>True for IFR and for CAM I: instrument flight with a clearance and a departure handoff.</summary>
    public static bool IsInstrument(this FlightRules rules) => rules is FlightRules.Ifr or FlightRules.CamI;

    /// <summary>True for the French military regimes (CAM V, CAM I).</summary>
    public static bool IsMilitary(this FlightRules rules) => rules is FlightRules.CamV or FlightRules.CamI or FlightRules.CamT;

    /// <summary>
    /// The regime a flight plan gets: the simulator's planner knows only VFR and IFR, so a plan to or from a military
    /// base is flown in CAM V (a VFR plan) or CAM I (an IFR plan).
    /// </summary>
    /// <param name="visual">True for a VFR plan.</param>
    /// <param name="militaryBase">True when the plan departs from or arrives at a military base.</param>
    public static FlightRules ForPlan(bool visual, bool militaryBase) => militaryBase
        ? (visual ? FlightRules.CamV : FlightRules.CamI)
        : (visual ? FlightRules.Vfr : FlightRules.Ifr);

    /// <summary>
    /// True when two regimes handle a flight the same way (both visual or both instrument), or when the pilot chose
    /// CAM T, which a re-imported plan must not undo.
    /// </summary>
    /// <param name="chosen">The regime the session is in.</param>
    /// <param name="planned">The regime the plan gives.</param>
    public static bool SameHandling(FlightRules chosen, FlightRules planned) =>
        chosen == planned
        || chosen == FlightRules.CamT
        || (chosen.IsVisual() && planned.IsVisual())
        || (chosen.IsInstrument() && planned.IsInstrument());

    /// <summary>The label said on the radio: "CAM Victor" or "CAM India" (VFR and IFR for the civil rules).</summary>
    public static string ToSpokenLabel(this FlightRules rules) => rules switch
    {
        FlightRules.CamV => "CAM Victor",
        FlightRules.CamT => "CAM Tango",
        FlightRules.CamI => "CAM India",
        _ => rules.ToLabel(),
    };

    /// <summary>The short label shown to the pilot: VFR, IFR, CAM V or CAM I.</summary>
    public static string ToLabel(this FlightRules rules) => rules switch
    {
        FlightRules.Vfr => "VFR",
        FlightRules.CamV => "CAM V",
        FlightRules.CamI => "CAM I",
        FlightRules.CamT => "CAM T",
        _ => "IFR",
    };

    /// <summary>Reads a label produced by <see cref="ToLabel"/>; anything unknown is IFR.</summary>
    public static FlightRules FromLabel(string? label) => label?.Trim().ToUpperInvariant() switch
    {
        "VFR" => FlightRules.Vfr,
        "CAM V" => FlightRules.CamV,
        "CAM I" => FlightRules.CamI,
        "CAM T" => FlightRules.CamT,
        _ => FlightRules.Ifr,
    };
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

    /// <summary>A flight information service ("Paris Information"): the service a VFR flight asks for a flight following.</summary>
    Information,
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

    /// <summary>The pilot asks to change military regime (CAM Victor, CAM India).</summary>
    RequestRegimeChange,

    /// <summary>The pilot reports passing the initial point of a military arrival ("passant initiale, QFE ...").</summary>
    ReportInitial,

    /// <summary>The pilot of a flight asks for a MARSA departure or approach (the flight keeps its own separation).</summary>
    RequestMarsa,

    /// <summary>A military pilot asks for the tactical picture: the contacts around, by bearing and range.</summary>
    RequestTacticalPicture,

    /// <summary>A VFR pilot asks a flight information service for a flight following ("suivi VFR").</summary>
    RequestFlightFollowing,

    /// <summary>The pilot asks to backtrack the runway ("remontée de piste").</summary>
    RequestBacktrack,

    /// <summary>The pilot asks the exact time.</summary>
    RequestTimeCheck,

    /// <summary>The pilot squawks ident.</summary>
    ReportIdent,

    /// <summary>The pilot asks the controller to speak slower.</summary>
    SpeakSlowly,

    /// <summary>An IFR flight cancels its IFR flight plan and goes on VFR.</summary>
    CancelIfr,

    /// <summary>The pilot answers a traffic information: looking out, traffic in sight, or no contact.</summary>
    ReportTrafficLookout,

    /// <summary>The pilot asks for a special VFR clearance.</summary>
    RequestSpecialVfr,

    /// <summary>A VFR pilot asks to cross a controlled aerodrome's zone ("transit").</summary>
    RequestTransit,

    /// <summary>The pilot asks for a 360 turn.</summary>
    RequestOrbit,

    /// <summary>The pilot asks to extend the downwind leg.</summary>
    RequestExtendDownwind,

    /// <summary>The pilot asks to close the flight plan.</summary>
    RequestFlightPlanClosure,

    /// <summary>The pilot asks to change the route of the flight plan.</summary>
    RequestRouteChange,

    /// <summary>The pilot asks for a speed: to keep one, to reduce, or free speed.</summary>
    RequestSpeed,

    /// <summary>The pilot asks to hold, over a fix or at the present position.</summary>
    RequestHold,

    /// <summary>The pilot is ready to leave the hold.</summary>
    ReportReadyToLeaveHold,

    /// <summary>The pilot states whether the aircraft is approved for RVSM.</summary>
    ReportRvsm,

    /// <summary>A SELCAL check on the oceanic HF frequency.</summary>
    RequestSelcalCheck,

    /// <summary>The pilot logs on to CPDLC.</summary>
    RequestCpdlcLogon,

    /// <summary>A tug asks to tow the aircraft.</summary>
    RequestTow,

    /// <summary>A vehicle asks to enter the runway for an inspection.</summary>
    RequestRunwayInspection,

    /// <summary>An IFR flight reports its position over a point.</summary>
    ReportPosition,

    /// <summary>The pilot asks for a PAR or SRA radar approach.</summary>
    RequestRadarApproach,
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

    /// <summary>"Request to cross runway 27": asked at a hold-short line on the taxi route.</summary>
    RequestRunwayCrossing,
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

    /// <summary>A call of a radar approach: position against the path, distance, heading.</summary>
    RadarGuidance,

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

    /// <summary>Cleared to cross a runway while taxiing.</summary>
    RunwayCrossingClearance,
}
