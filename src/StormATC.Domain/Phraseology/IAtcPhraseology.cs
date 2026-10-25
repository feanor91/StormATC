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

    /// <summary>The nearest contacts: bearing, range, altitude and aspect of each.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="contacts">The contacts, nearest first (at most three).</param>
    string TacticalPicture(Callsign callsign, IReadOnlyList<Approach.TacticalContact> contacts);

    /// <summary>No contact in range.</summary>
    /// <param name="callsign">The aircraft.</param>
    string NoTacticalContact(Callsign callsign);

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

    /// <summary>The controller assigns a speed: reduce to it and keep it.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="knots">The speed.</param>
    string SpeedReduction(Callsign callsign, int knots);

    /// <summary>The readback of a speed.</summary>
    /// <param name="knots">The speed.</param>
    string ReadbackItemSpeed(int knots);

    /// <summary>The readback of a hold.</summary>
    /// <param name="fix">The fix, or null for the present position.</param>
    string ReadbackItemHold(string? fix);

    /// <summary>The readback of a direct.</summary>
    /// <param name="fix">The fix.</param>
    string ReadbackItemDirect(string fix);

    /// <summary>The new route is approved.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="fix">The fix named in the request, or null.</param>
    string RouteChangeApproved(Callsign callsign, string? fix);

    /// <summary>A speed instruction: keep a speed, a Mach number, or free speed.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="speedKnots">The speed in knots, or null.</param>
    /// <param name="mach">The Mach number, or null.</param>
    /// <param name="free">True for free speed.</param>
    string SpeedInstruction(Callsign callsign, int? speedKnots, decimal? mach, bool free);

    /// <summary>Hold over a fix, or at the present position, right-hand, one minute legs.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="fix">The fix, or null for the present position.</param>
    string HoldInstruction(Callsign callsign, string? fix);

    /// <summary>Leave the hold.</summary>
    /// <param name="callsign">The aircraft.</param>
    string LeaveHoldInstruction(Callsign callsign);

    /// <summary>Acknowledges the RVSM status.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="approved">True when the aircraft is approved.</param>
    string RvsmAcknowledgement(Callsign callsign, bool approved);

    /// <summary>Acknowledges a SELCAL check.</summary>
    /// <param name="callsign">The aircraft.</param>
    string SelcalCheckAcknowledgement(Callsign callsign);

    /// <summary>Acknowledges a CPDLC logon.</summary>
    /// <param name="callsign">The aircraft.</param>
    string CpdlcLogonAcknowledgement(Callsign callsign);

    /// <summary>Cleared to tow the aircraft.</summary>
    /// <param name="callsign">The aircraft or the tug.</param>
    string TowClearance(Callsign callsign);

    /// <summary>A vehicle may enter the runway to inspect it.</summary>
    /// <param name="callsign">The vehicle.</param>
    /// <param name="runway">The runway.</param>
    string RunwayInspectionClearance(Callsign callsign, RunwayIdentifier runway);

    /// <summary>The conditions an arriving aircraft without the ATIS needs: runway, wind and QNH.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="runway">The runway in use.</param>
    /// <param name="windDirection">The wind direction, or null.</param>
    /// <param name="windSpeedKnots">The wind speed, or null.</param>
    /// <param name="qnhHectopascals">The QNH, or null.</param>
    string ArrivalConditions(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots, int? qnhHectopascals);

    /// <summary>The place in the sequence: number two or more, behind the circuit traffic.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="number">The number in the sequence, 2 or more.</param>
    string SequenceNumber(Callsign callsign, int number);

    /// <summary>A caution about wake turbulence behind a heavier aircraft.</summary>
    /// <param name="aircraftType">The aircraft's type, or null.</param>
    string WakeTurbulenceCaution(string? aircraftType);

    /// <summary>Backtrack the runway: taxi along it against the traffic direction.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="runway">The runway to backtrack.</param>
    string BacktrackInstruction(Callsign callsign, RunwayIdentifier runway);

    /// <summary>The exact time, in UTC.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="utc">The time to give.</param>
    string TimeCheck(Callsign callsign, DateTimeOffset utc);

    /// <summary>Acknowledges an ident.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="airborne">True when the aircraft is flying, so that it can be identified.</param>
    string IdentAcknowledgement(Callsign callsign, bool airborne);

    /// <summary>Repeats the last transmission more slowly.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="lastTransmission">What was said last, or null when there is nothing to repeat.</param>
    string SpeakSlowly(Callsign callsign, string? lastTransmission);

    /// <summary>The IFR flight plan is cancelled and the flight goes on VFR.</summary>
    /// <param name="callsign">The aircraft.</param>
    string CancelIfrAcknowledgement(Callsign callsign);

    /// <summary>Cleared special VFR in the zone.</summary>
    /// <param name="callsign">The aircraft.</param>
    string SpecialVfrClearance(Callsign callsign);

    /// <summary>Special VFR cannot be given: the visibility is too low.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="visibilityMeters">The visibility.</param>
    string SpecialVfrRefused(Callsign callsign, int visibilityMeters);

    /// <summary>Cleared to cross the zone over the aerodrome, at an altitude.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="altitude">The altitude to keep over the aerodrome.</param>
    /// <param name="qnhHectopascals">The QNH, when known.</param>
    string TransitClearance(Callsign callsign, Altitude altitude, int? qnhHectopascals);

    /// <summary>The flight is over the aerodrome: carry on and report leaving the zone.</summary>
    /// <param name="callsign">The aircraft.</param>
    string TransitContinue(Callsign callsign);

    /// <summary>A 360 turn is approved.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="side">The side of the turn, or null for the pilot's choice.</param>
    string OrbitApproved(Callsign callsign, CircuitSide? side);

    /// <summary>The downwind leg may be extended.</summary>
    /// <param name="callsign">The aircraft.</param>
    string ExtendDownwindApproved(Callsign callsign);

    /// <summary>The flight plan is closed.</summary>
    /// <param name="callsign">The aircraft.</param>
    string FlightPlanClosed(Callsign callsign);

    /// <summary>Cleared for an immediate takeoff.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="runway">The runway.</param>
    string ImmediateTakeoffClearance(Callsign callsign, RunwayIdentifier runway);

    /// <summary>Traffic information: where another aircraft is, as a clock position, its distance and its altitude.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="clockPosition">The other aircraft's position on the clock face, 1 to 12.</param>
    /// <param name="miles">The distance in nautical miles.</param>
    /// <param name="relativeAltitudeFeet">The other aircraft's altitude relative to ours, in feet; positive is above.</param>
    string TrafficInformation(Callsign callsign, int clockPosition, int miles, int relativeAltitudeFeet);

    /// <summary>A flight information service takes a VFR flight: its transponder code and what to report.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="serviceName">The service, as named on the radio ("Paris Information").</param>
    /// <param name="squawk">The transponder code given.</param>
    string FlightFollowingAccepted(Callsign callsign, string serviceName, SquawkCode squawk);

    /// <summary>The flight is passed to another sector of the flight information service, still followed.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="serviceName">The next service.</param>
    /// <param name="frequency">Its frequency.</param>
    string FlightFollowingHandoff(Callsign callsign, string serviceName, Frequency frequency);

    /// <summary>The flight following ends, and the flight is passed on when there is a next controller.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="nextController">The next controller, as named on the radio, or null.</param>
    /// <param name="frequency">Its frequency, or null.</param>
    string FlightFollowingEnded(Callsign callsign, string? nextController, Frequency? frequency);

    /// <summary>No flight information service is published where the aircraft is.</summary>
    /// <param name="callsign">The aircraft.</param>
    string FlightFollowingUnavailable(Callsign callsign);

    /// <summary>The pilot asked for a flight following on a frequency that is not the service's: tell which to call.</summary>
    /// <param name="callsign">The aircraft.</param>
    /// <param name="serviceName">The service that covers the position.</param>
    /// <param name="frequency">Its frequency.</param>
    string FlightFollowingCallService(Callsign callsign, string serviceName, Frequency frequency);

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
