using StormATC.Domain.Intents;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Phraseology;

/// <summary>
/// Every sentence a controller can say, in one language.
/// </summary>
/// <remarks>
/// <para>
/// The engine decides; this interface only words the decision. Each member takes values the
/// engine already approved and renders them, so an implementation cannot add a clearance or a
/// number of its own. That is what lets a second language be added without touching the rules.
/// </para>
/// <para>
/// <see cref="EnglishAtcPhraseology"/> reproduces the original StormATC wording exactly;
/// <see cref="FrenchAtcPhraseology"/> follows the DSNA phraseology manual.
/// </para>
/// </remarks>
public interface IAtcPhraseology
{
    /// <summary>The language this profile speaks.</summary>
    PhraseologyLanguage Language { get; }

    // ---------------------------------------------------------------------------------
    // Values
    // ---------------------------------------------------------------------------------

    /// <summary>How an aircraft is addressed when its callsign is unknown.</summary>
    string StationCalling { get; }

    /// <summary>The callsign as spoken.</summary>
    string SpeakCallsign(Callsign callsign);

    /// <summary>A runway with its leading word: "runway two seven left".</summary>
    string SpeakRunway(RunwayIdentifier runway);

    /// <summary>A runway designator alone: "two seven left".</summary>
    string SpeakRunwayDesignator(RunwayIdentifier runway);

    /// <summary>An altitude or flight level as spoken in a clearance.</summary>
    string SpeakAltitude(Altitude altitude);

    /// <summary>A heading as spoken after its verb.</summary>
    string SpeakHeading(Heading heading);

    /// <summary>A radio frequency.</summary>
    string SpeakFrequency(Frequency frequency);

    /// <summary>A transponder code.</summary>
    string SpeakSquawk(SquawkCode squawk);

    /// <summary>An aerodrome ICAO code, spelled.</summary>
    string SpellIcao(string icao);

    /// <summary>A stand, waypoint, or other identifier, spelled.</summary>
    string SpellIdentifier(string identifier);

    /// <summary>A taxi route: "Alfa, Charlie".</summary>
    string Taxiways(IReadOnlyList<string> taxiways);

    /// <summary>The name a controller position is called by.</summary>
    string ControllerName(ControllerPosition position);

    // ---------------------------------------------------------------------------------
    // Clearances and instructions
    // ---------------------------------------------------------------------------------

    string IfrClearance(Callsign callsign, Clearance clearance);

    /// <summary>Sentence appended when the pilot asked for clearance and pushback together.</summary>
    string ClearanceThenPushbackNote { get; }

    string ReadbackCorrect(Callsign callsign, Frequency? nextFrequency, ControllerPosition? nextController);

    string ReadbackCorrectPlain(Callsign? callsign);

    string PushbackApproval(Callsign callsign, string? pushbackDirection);

    string TaxiClearance(Callsign callsign, Clearance clearance);

    string UnroutedTaxiClearance(Callsign callsign, RunwayIdentifier runway);

    string TaxiToStandClearance(Callsign callsign, Clearance clearance);

    string TaxiToStandNoRoute(Callsign callsign, string stand);

    /// <summary>The instruction part of progressive taxi guidance, without the callsign.</summary>
    string ProgressiveTaxiInstruction(ProgressiveTaxiGuidance guidance);

    string HoldPositionConfirm(Callsign callsign);

    string LineUpAndWait(Callsign callsign, RunwayIdentifier runway);

    string TakeoffClearance(
        Callsign callsign,
        RunwayIdentifier runway,
        Heading? windDirection,
        int? windSpeedKnots,
        Heading? initialHeading);

    string LandingClearance(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots);

    string ApproachClearance(Callsign callsign, string approachName, RunwayIdentifier runway);

    string Handoff(Callsign callsign, ControllerPosition toController, Frequency frequency);

    string CheckInAcknowledgement(Callsign callsign, ControllerPosition controller, Altitude? clearedAltitude, bool radarContact);

    string ClimbInstruction(Callsign callsign, Altitude altitude);

    string DescentInstruction(Callsign callsign, Altitude altitude);

    string HeadingInstruction(Callsign callsign, Heading heading);

    string DirectToApproval(Callsign callsign, string waypoint);

    string DeviationApproved(Callsign callsign);

    string GoAroundInstruction(Callsign callsign, Altitude? climbTo, Heading? heading);

    string VacateInstruction(Callsign callsign, string? exitTaxiway);

    string VacateWhenAbleNoCallsign { get; }

    // ---------------------------------------------------------------------------------
    // VFR circuit and arrival
    // ---------------------------------------------------------------------------------

    /// <summary>First answer to a military arrival: runway, wind, QFE and where to report.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="runway">The runway in use.</param>
    /// <param name="windDirection">Wind direction, when a weather source supplied it.</param>
    /// <param name="windSpeedKnots">Wind speed, when a weather source supplied it.</param>
    /// <param name="qfeHectopascals">The QFE, when it can be computed.</param>
    string MilitaryArrivalInstruction(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots, int? qfeHectopascals);

    /// <summary>Answer to the report at the initial point: QFE check, number in the sequence, report turning final.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="correctedQfe">The QFE to give when the pilot quoted a wrong one, else null.</param>
    string InitialPointInstruction(Callsign callsign, int? correctedQfe);

    /// <summary>Asks the pilot to check the gear before the landing clearance.</summary>
    /// <param name="callsign">The aircraft.</param>
    string CheckGearDownAndLocked(Callsign callsign);

    /// <summary>Accepts a PAR or SRA request: runway, QFE and "do not acknowledge".</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="kind">PAR or SRA.</param>
    /// <param name="runway">The runway.</param>
    /// <param name="qfeHectopascals">The QFE, when it can be computed.</param>
    string RadarApproachStart(Callsign callsign, Approach.RadarApproachKind kind, RunwayIdentifier runway, int? qfeHectopascals);

    /// <summary>Radar contact: the aircraft has entered the approach path.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="distanceNauticalMiles">Distance to the threshold.</param>
    string RadarContact(Callsign callsign, int distanceNauticalMiles);

    /// <summary>One call of the approach: position against the centreline and the path, distance.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="radarCall">What to say.</param>
    string RadarGuidance(Callsign callsign, Approach.RadarCall radarCall);

    /// <summary>The end of an SRA, or the loss of the approach: report the runway in sight or go around.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="kind">PAR or SRA.</param>
    string RadarApproachEnd(Callsign callsign, Approach.RadarApproachKind kind);

    /// <summary>The QFE as it is spoken.</summary>
    /// <param name="qfeHectopascals">The QFE in hectopascals.</param>
    string Qfe(int qfeHectopascals);

    /// <summary>Approves a MARSA departure or approach.</summary>
    /// <param name="callsign">The flight.</param>
    /// <param name="kind">Departure or approach.</param>
    /// <param name="runway">The runway.</param>
    /// <param name="initialHeading">The initial heading of a departure, magnetic.</param>
    string MarsaApproved(Callsign callsign, Approach.MarsaKind kind, RunwayIdentifier runway, int initialHeading);

    /// <summary>Confirms CAM Tango: a tactical flight, outside the air traffic service.</summary>
    /// <param name="callsign">The aircraft.</param>
    string TacticalRegime(Callsign callsign);

    /// <summary>Confirms a change of military regime (CAM Victor, CAM India).</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="regime">The spoken regime, such as "CAM Victor".</param>
    string RegimeChange(Callsign callsign, string regime);

    /// <summary>Acknowledges the arrival call and asks for a report at a reporting point.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="pointName">The spoken name of the reporting point, in the profile's language.</param>
    string ReportPointInstruction(Callsign callsign, string pointName);

    /// <summary>Tells a VFR aircraft to join the circuit on the downwind leg.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="runway">The runway in use.</param>
    /// <param name="side">The circuit direction, or null when the aerodrome does not publish one.</param>
    string CircuitJoinInstruction(Callsign callsign, RunwayIdentifier runway, CircuitSide? side);

    /// <summary>Asks the aircraft to report the next leg: base, then final.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="leg">The leg to report, <see cref="CircuitLeg.Base"/> or <see cref="CircuitLeg.Final"/>.</param>
    /// <param name="runway">The runway in use.</param>
    /// <param name="side">The circuit direction, or null when unknown.</param>
    string CircuitReportInstruction(Callsign callsign, CircuitLeg leg, RunwayIdentifier runway, CircuitSide? side);

    /// <summary>Clears the aircraft for the option: full stop, touch and go or stop-and-go, at the pilot's choice.</summary>
    string OptionClearance(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots);

    /// <summary>Clears the aircraft for a low pass over the runway.</summary>
    string LowPassClearance(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots);

    /// <summary>Clears the aircraft for a touch and go.</summary>
    string TouchAndGoClearance(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots);

    /// <summary>Acknowledges that the aircraft is leaving the circuit and the frequency.</summary>
    string LeaveFrequencyAcknowledgement(Callsign callsign);

    // ---------------------------------------------------------------------------------
    // Conversation
    // ---------------------------------------------------------------------------------

    string GoAhead(Callsign callsign, ControllerPosition controller);

    string Acknowledgement(Callsign callsign);

    /// <summary>A bare acknowledgement when no callsign is known.</summary>
    string RogerPlain { get; }

    string RogerSayIntentions(Callsign callsign);

    string RogerHoldShort(Callsign callsign);

    string SayAgain(Callsign? callsign);

    string SayRequestedLevel(Callsign callsign);

    string SayAgainWaypoint(Callsign callsign);

    string RadioCheckReply(Callsign callsign, ControllerPosition controller);

    string EmergencyAcknowledgement(Callsign callsign, bool isMayday);

    string Unable(Callsign callsign, UnableReason reason);

    string UnableMaintain(Callsign callsign, Altitude altitude);

    string ContactController(Callsign? callsign, ControllerPosition controller, Frequency? frequency = null);

    string FrequencyInformation(Callsign callsign, ControllerPosition controller, Frequency frequency);

    string NoSeparateFrequency(Callsign callsign, ControllerPosition controller);

    string FrequencyUnavailable(Callsign callsign, ControllerPosition controller);

    string WrongHoldingPoint(Callsign callsign, RunwayIdentifier assignedRunway);

    string HoldShortTrafficOnRunway(Callsign callsign, RunwayIdentifier runway);

    /// <summary>Clears the aircraft to cross a runway while taxiing.</summary>
    string RunwayCrossingClearance(Callsign callsign, RunwayIdentifier runway);

    /// <summary>Holds the aircraft at the holding point because another aircraft is on short final.</summary>
    string HoldPositionTrafficOnShortFinal(Callsign callsign, RunwayIdentifier runway);

    /// <summary>Tells an aircraft already lined up on the runway to stay put because traffic is landing.</summary>
    string HoldPositionLinedUp(Callsign callsign);

    string WrongApproachRunway(Callsign callsign, RunwayIdentifier clearedRunway);

    string ContinueApproachTrafficOnRunway(Callsign callsign);

    string HoldShortViolation(Callsign? callsign, RunwayIdentifier runway);

    string NoFlightPlanToBegin { get; }

    string NoFlightPlanFirst { get; }

    string ReadbackReminder(Callsign? callsign);

    // ---------------------------------------------------------------------------------
    // Readbacks
    // ---------------------------------------------------------------------------------

    /// <summary>How an initial altitude is restated in a readback correction.</summary>
    string ReadbackItemInitialAltitude(Altitude altitude);

    string ReadbackItemSquawk(SquawkCode squawk);

    string ReadbackItemDepartureFrequency(Frequency frequency);

    string ReadbackItemTaxiRoute(IReadOnlyList<string> taxiways);

    string ReadbackItemHoldShort(RunwayIdentifier runway);

    string ReadbackItemAltitudeChange(Altitude altitude, bool isClimb);

    string ReadbackItemApproach(string approachName);

    /// <summary>
    /// A targeted correction: "negative, squawk four two seven one" when a value was wrong,
    /// "readback incomplete, …" when one was missing.
    /// </summary>
    string ReadbackCorrection(Callsign? callsign, bool hasIncorrectValue, IReadOnlyList<string> parts);

    string SayAgainReadback(Callsign? callsign);

    string VerifyCallsign(Callsign? callsign);

    // ---------------------------------------------------------------------------------
    // Text helpers
    // ---------------------------------------------------------------------------------

    /// <summary>True when an earlier controller transmission told the pilot to contact that position.</summary>
    bool MentionsContact(string controllerText, ControllerPosition position);

    /// <summary>The on-screen form of a transmission: written callsign instead of spoken.</summary>
    string ToDisplayForm(string spokenText, Callsign callsign);
}

/// <summary>Why a controller turned a request down. Each language words these its own way.</summary>
public enum UnableReason
{
    AlreadyAirborne,
    ClearanceAlreadyIssued,
    NoDepartureRunwayAvailable,
    Airborne,
    AlreadyMoving,
    NotAtThisStageOfFlight,
    ReadBackClearanceFirst,
    NoDepartureRunwayAssigned,
    ProgressiveTaxiGroundOnly,
    NoActiveTaxiRoute,
    ContactTowerForDeparture,
    NotAtHoldingPoint,
    NotWithThisController,
    WaypointNotOnRoute,
    ContactApproach,
    DestinationDataUnavailable,
    NoArrivalRunwayAvailable,
    RunwayNotAvailable,
    NotClearedForApproach,
    ContactTower,
    NoLandingRunwayAssigned,
    NotLanded,
    AerodromeDataUnavailable,
    NoStandAvailable,
    NoRunwayCrossingPending,
}

/// <summary>What progressive taxi guidance tells the pilot to do next.</summary>
public enum ProgressiveTaxiStep
{
    /// <summary>Continue to the holding point of <see cref="ProgressiveTaxiGuidance.Runway"/>.</summary>
    ToHoldingPoint,

    /// <summary>Continue to <see cref="ProgressiveTaxiGuidance.Destination"/>, or the clearance limit.</summary>
    ToDestination,

    TurnLeft,

    TurnRight,

    Straight,
}

/// <summary>The facts behind one progressive taxi instruction.</summary>
public sealed record ProgressiveTaxiGuidance
{
    public required ProgressiveTaxiStep Step { get; init; }

    /// <summary>The taxiway the aircraft is on, or null when it is only known to be on the route.</summary>
    public string? CurrentTaxiway { get; init; }

    /// <summary>The taxiway to turn onto, for the turning and straight-ahead steps.</summary>
    public string? NextTaxiway { get; init; }

    public RunwayIdentifier? Runway { get; init; }

    /// <summary>The clearance limit as recorded on the clearance, when no runway applies.</summary>
    public string? Destination { get; init; }
}
