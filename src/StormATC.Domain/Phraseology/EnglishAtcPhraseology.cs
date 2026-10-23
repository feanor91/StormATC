using StormATC.Domain.Intents;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Phraseology;

/// <summary>
/// ICAO-style English. Reproduces StormATC's original wording word for word, so existing
/// behaviour and tests are unchanged by the introduction of language profiles.
/// </summary>
public sealed class EnglishAtcPhraseology : IAtcPhraseology
{
    /// <summary>The shared instance. The class is stateless.</summary>
    public static EnglishAtcPhraseology Instance { get; } = new();

    private EnglishAtcPhraseology()
    {
    }

    /// <inheritdoc />
    public PhraseologyLanguage Language => PhraseologyLanguage.English;

    /// <inheritdoc />
    public string StationCalling => "Station calling";

    /// <inheritdoc />
    public string SpeakCallsign(Callsign callsign) => callsign.ToSpoken();

    /// <inheritdoc />
    public string SpeakRunway(RunwayIdentifier runway) => runway.ToSpoken();

    /// <inheritdoc />
    public string SpeakRunwayDesignator(RunwayIdentifier runway) => runway.ToSpokenDesignator();

    /// <inheritdoc />
    public string SpeakAltitude(Altitude altitude) => altitude.ToSpoken();

    /// <inheritdoc />
    public string SpeakHeading(Heading heading) => heading.ToSpoken();

    /// <inheritdoc />
    public string SpeakFrequency(Frequency frequency) => frequency.ToSpoken();

    /// <inheritdoc />
    public string SpeakSquawk(SquawkCode squawk) => squawk.ToSpoken();

    /// <inheritdoc />
    public string SpellIcao(string icao) => DeterministicPhraseology.SpellIcao(icao);

    /// <inheritdoc />
    public string SpellIdentifier(string identifier) => PhoneticAlphabet.SpellOut(identifier);

    /// <inheritdoc />
    public string Taxiways(IReadOnlyList<string> taxiways) => DeterministicPhraseology.SpellTaxiways(taxiways);

    /// <inheritdoc />
    public string ControllerName(ControllerPosition position) =>
        DeterministicPhraseology.SpokenControllerName(position);

    /// <inheritdoc />
    public string IfrClearance(Callsign callsign, Clearance clearance) =>
        DeterministicPhraseology.IfrClearance(callsign, clearance);

    /// <inheritdoc />
    public string ClearanceThenPushbackNote => " Read back the clearance, then request pushback.";

    /// <inheritdoc />
    public string ReadbackCorrect(Callsign callsign, Frequency? nextFrequency, ControllerPosition? nextController) =>
        DeterministicPhraseology.ReadbackCorrect(callsign, nextFrequency, nextController);

    /// <inheritdoc />
    public string ReadbackCorrectPlain(Callsign? callsign) => callsign is null
        ? "Readback correct."
        : $"{callsign.ToSpoken()}, readback correct.";

    /// <inheritdoc />
    public string PushbackApproval(Callsign callsign, string? pushbackDirection) =>
        DeterministicPhraseology.PushbackApproval(callsign, pushbackDirection);

    /// <inheritdoc />
    public string TaxiClearance(Callsign callsign, Clearance clearance) =>
        DeterministicPhraseology.TaxiClearance(callsign, clearance);

    /// <inheritdoc />
    public string UnroutedTaxiClearance(Callsign callsign, RunwayIdentifier runway) =>
        $"{callsign.ToSpoken()}, taxi to holding point runway "
        + $"{runway.ToSpokenDesignator()}, detailed taxi routing unavailable, "
        + $"hold short runway {runway.ToSpokenDesignator()}.";

    /// <inheritdoc />
    public string TaxiToStandClearance(Callsign callsign, Clearance clearance) =>
        DeterministicPhraseology.TaxiToStandClearance(callsign, clearance);

    /// <inheritdoc />
    public string TaxiToStandNoRoute(Callsign callsign, string stand) =>
        $"{callsign.ToSpoken()}, taxi to stand {PhoneticAlphabet.SpellOut(stand)}, no published route available.";

    /// <inheritdoc />
    public string ProgressiveTaxiInstruction(ProgressiveTaxiGuidance guidance)
    {
        ArgumentNullException.ThrowIfNull(guidance);

        string current = guidance.CurrentTaxiway is { Length: > 0 } taxiway
            ? DeterministicPhraseology.SpellTaxiways([taxiway])
            : "the cleared route";
        string next = guidance.NextTaxiway is { Length: > 0 } nextTaxiway
            ? DeterministicPhraseology.SpellTaxiways([nextTaxiway])
            : string.Empty;

        return guidance.Step switch
        {
            ProgressiveTaxiStep.ToHoldingPoint when guidance.Runway is RunwayIdentifier runway =>
                $"continue on {current} to holding point {runway.ToSpoken()}, "
                + $"hold short {runway.ToSpoken()}",
            ProgressiveTaxiStep.TurnLeft =>
                $"continue on {current}, at the next junction turn left onto {next}",
            ProgressiveTaxiStep.TurnRight =>
                $"continue on {current}, at the next junction turn right onto {next}",
            ProgressiveTaxiStep.Straight =>
                $"continue on {current}, then continue straight onto {next}",
            _ => $"continue on {current} to {guidance.Destination ?? "the clearance limit"}",
        };
    }

    /// <inheritdoc />
    public string HoldPositionConfirm(Callsign callsign) =>
        $"{callsign.ToSpoken()}, hold position, confirm your position.";

    /// <inheritdoc />
    public string LineUpAndWait(Callsign callsign, RunwayIdentifier runway) =>
        DeterministicPhraseology.LineUpAndWait(callsign, runway);

    /// <inheritdoc />
    public string TakeoffClearance(
        Callsign callsign,
        RunwayIdentifier runway,
        Heading? windDirection,
        int? windSpeedKnots,
        Heading? initialHeading) =>
        DeterministicPhraseology.TakeoffClearance(callsign, runway, windDirection, windSpeedKnots, initialHeading);

    /// <inheritdoc />
    public string LandingClearance(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots) =>
        DeterministicPhraseology.LandingClearance(callsign, runway, windDirection, windSpeedKnots);

    /// <inheritdoc />
    public string ApproachClearance(Callsign callsign, string approachName, RunwayIdentifier runway) =>
        DeterministicPhraseology.ApproachClearance(callsign, approachName, runway);

    /// <inheritdoc />
    public string Handoff(Callsign callsign, ControllerPosition toController, Frequency frequency) =>
        DeterministicPhraseology.Handoff(callsign, toController, frequency);

    /// <inheritdoc />
    public string CheckInAcknowledgement(
        Callsign callsign,
        ControllerPosition controller,
        Altitude? clearedAltitude,
        bool radarContact) =>
        DeterministicPhraseology.CheckInAcknowledgement(callsign, controller, clearedAltitude, radarContact);

    /// <inheritdoc />
    public string ClimbInstruction(Callsign callsign, Altitude altitude) =>
        DeterministicPhraseology.ClimbInstruction(callsign, altitude);

    /// <inheritdoc />
    public string DescentInstruction(Callsign callsign, Altitude altitude) =>
        DeterministicPhraseology.DescentInstruction(callsign, altitude);

    /// <inheritdoc />
    public string HeadingInstruction(Callsign callsign, Heading heading) =>
        DeterministicPhraseology.HeadingInstruction(callsign, heading);

    /// <inheritdoc />
    public string DirectToApproval(Callsign callsign, string waypoint) =>
        DeterministicPhraseology.DirectToApproval(callsign, waypoint);

    /// <inheritdoc />
    public string DeviationApproved(Callsign callsign) =>
        $"{callsign.ToSpoken()}, deviation approved, advise when able to resume own navigation.";

    /// <inheritdoc />
    public string GoAroundInstruction(Callsign callsign, Altitude? climbTo, Heading? heading) =>
        DeterministicPhraseology.GoAroundInstruction(callsign, climbTo, heading);

    /// <inheritdoc />
    public string VacateInstruction(Callsign callsign, string? exitTaxiway) =>
        DeterministicPhraseology.VacateInstruction(callsign, exitTaxiway);

    /// <inheritdoc />
    public string VacateWhenAbleNoCallsign => "Vacate the runway when able.";

    /// <inheritdoc />
    public string GoAhead(Callsign callsign, ControllerPosition controller) =>
        $"{callsign.ToSpoken()}, {DeterministicPhraseology.SpokenControllerName(controller)}, go ahead.";

    /// <inheritdoc />
    public string Acknowledgement(Callsign callsign) => DeterministicPhraseology.Acknowledgement(callsign);

    /// <inheritdoc />
    public string MilitaryArrivalInstruction(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots, int? qfeHectopascals)
    {
        string wind = windDirection is Heading direction && windSpeedKnots is int speed
            ? $"wind {direction.ToSpoken()} degrees {SpokenNumbers.DigitsIndividually(speed.ToString(System.Globalization.CultureInfo.InvariantCulture))} knots, "
            : string.Empty;
        string qfe = qfeHectopascals is int value ? $"{Qfe(value)}, " : string.Empty;
        return $"{callsign.ToSpoken()}, {runway.ToSpoken()}, {wind}{qfe}report initial.";
    }

    /// <inheritdoc />
    public string InitialPointInstruction(Callsign callsign, int? correctedQfe) =>
        $"{callsign.ToSpoken()}, {(correctedQfe is int qfe ? $"negative, {Qfe(qfe)}" : "correct")}, number one, report turning final, gear down and locked.";

    /// <inheritdoc />
    public string CheckGearDownAndLocked(Callsign callsign) =>
        $"{callsign.ToSpoken()}, check gear down and locked.";

    /// <inheritdoc />
    public string RadarApproachStart(Callsign callsign, Approach.RadarApproachKind kind, RunwayIdentifier runway, int? qfeHectopascals) =>
        $"{callsign.ToSpoken()}, {(kind == Approach.RadarApproachKind.Par ? "PAR approach" : "surveillance radar approach")}, {runway.ToSpoken()}, "
        + (qfeHectopascals is int qfe ? $"{Qfe(qfe)}, " : string.Empty)
        + "do not acknowledge further transmissions unless requested.";

    /// <inheritdoc />
    public string RadarContact(Callsign callsign, int distanceNauticalMiles) =>
        $"{callsign.ToSpoken()}, radar contact, {Miles(distanceNauticalMiles)} from touchdown.";

    /// <inheritdoc />
    public string RadarGuidance(Callsign callsign, Approach.RadarCall radarCall)
    {
        List<string> parts =
        [
            radarCall.Lateral switch
            {
                Approach.RadarLateral.OnCentreline => "on centreline",
                Approach.RadarLateral.SlightlyLeft => "slightly left of centreline",
                Approach.RadarLateral.SlightlyRight => "slightly right of centreline",
                Approach.RadarLateral.Left => "left of centreline",
                _ => "right of centreline",
            },
        ];

        if (radarCall.CorrectionHeading is int heading)
        {
            parts.Add($"heading {SpokenNumbers.DigitsIndividually(heading, 3)}");
        }

        if (radarCall.Vertical is Approach.RadarVertical vertical)
        {
            parts.Add(vertical switch
            {
                Approach.RadarVertical.OnPath => "on glidepath",
                Approach.RadarVertical.SlightlyAbove => "slightly above glidepath",
                Approach.RadarVertical.SlightlyBelow => "slightly below glidepath",
                Approach.RadarVertical.Above => "above glidepath",
                _ => "below glidepath",
            });
        }

        parts.Add($"{Miles(radarCall.DistanceNauticalMiles)} from touchdown");
        if (radarCall.RecommendedAltitudeFeet is int altitude)
        {
            parts.Add($"recommended altitude {SpokenNumbers.Altitude(altitude)}");
        }

        return $"{callsign.ToSpoken()}, {string.Join(", ", parts)}.";
    }

    /// <inheritdoc />
    public string RadarApproachEnd(Callsign callsign, Approach.RadarApproachKind kind) =>
        $"{callsign.ToSpoken()}, {(kind == Approach.RadarApproachKind.Par ? "PAR approach" : "surveillance radar approach")} complete, report runway in sight or go around.";

    private static string Miles(int miles) => miles == 1 ? "one mile" : $"{SpokenNumbers.DigitsIndividually(miles.ToString(System.Globalization.CultureInfo.InvariantCulture))} miles";

    /// <inheritdoc />
    public string Qfe(int qfeHectopascals) =>
        $"QFE {SpokenNumbers.DigitsIndividually(qfeHectopascals.ToString(System.Globalization.CultureInfo.InvariantCulture))}";

    /// <inheritdoc />
    public string TacticalPicture(Callsign callsign, IReadOnlyList<Approach.TacticalContact> contacts)
    {
        List<string> parts = [contacts.Count == 1 ? "one contact" : $"{contacts.Count} contacts"];
        for (int i = 0; i < contacts.Count; i++)
        {
            Approach.TacticalContact contact = contacts[i];
            parts.Add(
                $"{(i == 0 ? "first" : i == 1 ? "second" : "third")}, bearing {SpokenNumbers.DigitsIndividually(contact.BearingMagnetic, 3)}, "
                + $"{Math.Max(1, contact.RangeNauticalMiles)} miles, {contact.AltitudeFeet} feet, "
                + contact.Aspect switch
                {
                    Approach.TacticalAspect.Hot => "hot",
                    Approach.TacticalAspect.Cold => "cold",
                    _ => "flank",
                });
        }

        return $"{callsign.ToSpoken()}, {string.Join(", ", parts)}.";
    }

    /// <inheritdoc />
    public string NoTacticalContact(Callsign callsign) => $"{callsign.ToSpoken()}, no contact.";

    /// <inheritdoc />
    public string MarsaApproved(Callsign callsign, Approach.MarsaKind kind, RunwayIdentifier runway, int initialHeading) =>
        kind == Approach.MarsaKind.Departure
            ? $"{callsign.ToSpoken()}, MARSA departure {runway.ToSpoken()} approved, initial heading {SpokenNumbers.DigitsIndividually(initialHeading, 3)}."
            : $"{callsign.ToSpoken()}, MARSA approach {runway.ToSpoken()} approved.";

    /// <inheritdoc />
    public string TacticalRegime(Callsign callsign) =>
        $"{callsign.ToSpoken()}, you are CAM Tango, tactical flight, outside the air traffic service, call to resume CAM Victor or CAM India.";

    /// <inheritdoc />
    public string RegimeChange(Callsign callsign, string regime) =>
        $"{callsign.ToSpoken()}, you are now {regime}.";

    /// <inheritdoc />
    public string ReportPointInstruction(Callsign callsign, string pointName) =>
        $"{callsign.ToSpoken()}, roger, report {pointName}.";

    /// <inheritdoc />
    public string CircuitJoinInstruction(Callsign callsign, RunwayIdentifier runway, CircuitSide? side) =>
        $"{callsign.ToSpoken()}, join {Hand(side)}downwind {runway.ToSpoken()}, report downwind.";

    /// <inheritdoc />
    public string CircuitReportInstruction(Callsign callsign, CircuitLeg leg, RunwayIdentifier runway, CircuitSide? side) =>
        leg == CircuitLeg.Final
            ? $"{callsign.ToSpoken()}, report final {runway.ToSpoken()}."
            : $"{callsign.ToSpoken()}, report {Hand(side)}base {runway.ToSpoken()}.";

    /// <inheritdoc />
    public string OptionClearance(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots) =>
        ManoeuvreClearance(callsign, runway, "cleared the option", windDirection, windSpeedKnots);

    /// <inheritdoc />
    public string LowPassClearance(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots) =>
        ManoeuvreClearance(callsign, runway, "cleared low pass", windDirection, windSpeedKnots);

    private static string ManoeuvreClearance(
        Callsign callsign,
        RunwayIdentifier runway,
        string clearance,
        Heading? windDirection,
        int? windSpeedKnots)
    {
        string wind = windDirection is Heading direction && windSpeedKnots is int speed
            ? $"wind {direction.ToSpoken()} degrees {SpokenNumbers.DigitsIndividually(speed.ToString(System.Globalization.CultureInfo.InvariantCulture))} knots, "
            : string.Empty;
        return $"{callsign.ToSpoken()}, {wind}{runway.ToSpoken()}, {clearance}.";
    }

    /// <inheritdoc />
    public string TouchAndGoClearance(Callsign callsign, RunwayIdentifier runway, Heading? windDirection, int? windSpeedKnots)
    {
        string wind = windDirection is Heading direction && windSpeedKnots is int speed
            ? $"wind {direction.ToSpoken()} degrees {SpokenNumbers.DigitsIndividually(speed.ToString(System.Globalization.CultureInfo.InvariantCulture))} knots, "
            : string.Empty;
        return $"{callsign.ToSpoken()}, {wind}{runway.ToSpoken()}, cleared touch and go.";
    }

    /// <inheritdoc />
    public string LeaveFrequencyAcknowledgement(Callsign callsign) =>
        $"{callsign.ToSpoken()}, roger, good day.";

    private static string Hand(CircuitSide? side) => side switch
    {
        CircuitSide.Left => "left-hand ",
        CircuitSide.Right => "right-hand ",
        _ => string.Empty,
    };

    /// <inheritdoc />
    public string RogerPlain => "Roger.";

    /// <inheritdoc />
    public string RogerSayIntentions(Callsign callsign) => $"{callsign.ToSpoken()}, roger, say intentions.";

    /// <inheritdoc />
    public string RogerHoldShort(Callsign callsign) => $"{callsign.ToSpoken()}, roger, hold short.";

    /// <inheritdoc />
    public string SayAgain(Callsign? callsign) => DeterministicPhraseology.SayAgain(callsign);

    /// <inheritdoc />
    public string SayRequestedLevel(Callsign callsign) => $"{callsign.ToSpoken()}, say requested level.";

    /// <inheritdoc />
    public string SayAgainWaypoint(Callsign callsign) =>
        $"{callsign.ToSpoken()}, say again the requested waypoint.";

    /// <inheritdoc />
    public string RadioCheckReply(Callsign callsign, ControllerPosition controller) =>
        DeterministicPhraseology.RadioCheckReply(callsign, controller);

    /// <inheritdoc />
    public string EmergencyAcknowledgement(Callsign callsign, bool isMayday) =>
        DeterministicPhraseology.EmergencyAcknowledgement(callsign, isMayday);

    /// <inheritdoc />
    public string Unable(Callsign callsign, UnableReason reason) =>
        DeterministicPhraseology.Unable(callsign, ReasonText(reason));

    /// <inheritdoc />
    public string UnableMaintain(Callsign callsign, Altitude altitude) =>
        DeterministicPhraseology.Unable(callsign, $"maintain {altitude.ToSpoken()}");

    /// <inheritdoc />
    public string ContactController(Callsign? callsign, ControllerPosition controller, Frequency? frequency = null) =>
        $"{callsign?.ToSpoken() ?? StationCalling}, contact {DeterministicPhraseology.SpokenControllerName(controller)}"
        + (frequency is Frequency f ? $" {f.ToSpoken()}." : ".");

    /// <inheritdoc />
    public string FrequencyInformation(Callsign callsign, ControllerPosition controller, Frequency frequency) =>
        $"{callsign.ToSpoken()}, {DeterministicPhraseology.SpokenControllerName(controller)} "
        + $"frequency {frequency.ToSpoken()}.";

    /// <inheritdoc />
    public string NoSeparateFrequency(Callsign callsign, ControllerPosition controller) =>
        $"{callsign.ToSpoken()}, no separate "
        + $"{DeterministicPhraseology.SpokenControllerName(controller)} frequency is available, "
        + "remain this frequency.";

    /// <inheritdoc />
    public string FrequencyUnavailable(Callsign callsign, ControllerPosition controller) =>
        $"{callsign.ToSpoken()}, {DeterministicPhraseology.SpokenControllerName(controller)} "
        + "frequency unavailable, remain this frequency.";

    /// <inheritdoc />
    public string WrongHoldingPoint(Callsign callsign, RunwayIdentifier assignedRunway) =>
        $"{callsign.ToSpoken()}, negative, your departure runway is {assignedRunway.ToSpokenDesignator()}, confirm position.";

    /// <inheritdoc />
    public string HoldShortTrafficOnRunway(Callsign callsign, RunwayIdentifier runway) =>
        $"{callsign.ToSpoken()}, hold short {runway.ToSpokenDesignator()}, traffic on the runway.";

    /// <inheritdoc />
    public string RunwayCrossingClearance(Callsign callsign, RunwayIdentifier runway) =>
        $"{callsign.ToSpoken()}, cross runway {runway.ToSpokenDesignator()}.";

    /// <inheritdoc />
    public string HoldPositionTrafficOnShortFinal(Callsign callsign, RunwayIdentifier runway) =>
        $"{callsign.ToSpoken()}, hold short {runway.ToSpokenDesignator()}, traffic on short final.";

    /// <inheritdoc />
    public string HoldPositionLinedUp(Callsign callsign) =>
        $"{callsign.ToSpoken()}, hold position, traffic on short final.";

    /// <inheritdoc />
    public string WrongApproachRunway(Callsign callsign, RunwayIdentifier clearedRunway) =>
        $"{callsign.ToSpoken()}, negative, you are cleared for the approach {clearedRunway.ToSpoken()}, confirm.";

    /// <inheritdoc />
    public string ContinueApproachTrafficOnRunway(Callsign callsign) =>
        $"{callsign.ToSpoken()}, continue approach, traffic on the runway.";

    /// <inheritdoc />
    public string HoldShortViolation(Callsign? callsign, RunwayIdentifier runway) =>
        $"{callsign?.ToSpoken() ?? "Aircraft"}, you were instructed to hold short {runway.ToSpokenDesignator()}, vacate the runway immediately.";

    /// <inheritdoc />
    public string NoFlightPlanToBegin =>
        "Station calling, no flight plan is loaded. Import a flight plan to begin.";

    /// <inheritdoc />
    public string NoFlightPlanFirst =>
        "Station calling, no flight plan is loaded. Import a flight plan first.";

    /// <inheritdoc />
    public string ReadbackReminder(Callsign? callsign) =>
        $"{callsign?.ToSpoken() ?? StationCalling}, standby, read back your clearance.";

    /// <inheritdoc />
    public string ReadbackItemInitialAltitude(Altitude altitude) => $"climb initially {altitude.ToSpoken()}";

    /// <inheritdoc />
    public string ReadbackItemSquawk(SquawkCode squawk) => $"squawk {squawk.ToSpoken()}";

    /// <inheritdoc />
    public string ReadbackItemDepartureFrequency(Frequency frequency) =>
        $"departure frequency {frequency.ToSpoken()}";

    /// <inheritdoc />
    public string ReadbackItemTaxiRoute(IReadOnlyList<string> taxiways) =>
        $"via {DeterministicPhraseology.SpellTaxiways(taxiways)}";

    /// <inheritdoc />
    public string ReadbackItemHoldShort(RunwayIdentifier runway) => $"hold short {runway.ToSpoken()}";

    /// <inheritdoc />
    public string ReadbackItemAltitudeChange(Altitude altitude, bool isClimb) =>
        $"{(isClimb ? "climb" : "descend")} {altitude.ToSpoken()}";

    /// <inheritdoc />
    public string ReadbackItemApproach(string approachName) => $"{approachName} approach";

    /// <inheritdoc />
    public string ReadbackCorrection(Callsign? callsign, bool hasIncorrectValue, IReadOnlyList<string> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        string spoken = callsign?.ToSpoken() ?? "Aircraft";
        if (parts.Count == 0)
        {
            return SayAgainReadback(callsign);
        }

        // "negative" for a wrong value, "readback incomplete" for an omission — the
        // distinction real controllers make.
        string lead = hasIncorrectValue ? "negative" : "readback incomplete";
        return $"{spoken}, {lead}, {string.Join(", ", parts)}.";
    }

    /// <inheritdoc />
    public string SayAgainReadback(Callsign? callsign) =>
        $"{callsign?.ToSpoken() ?? "Aircraft"}, say again your readback.";

    /// <inheritdoc />
    public string VerifyCallsign(Callsign? callsign) =>
        $"{callsign?.ToSpoken() ?? "Aircraft"}, verify callsign, say again.";

    /// <inheritdoc />
    public bool MentionsContact(string controllerText, ControllerPosition position) =>
        controllerText.Contains(
            $"contact {DeterministicPhraseology.SpokenControllerName(position)}",
            StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string ToDisplayForm(string spokenText, Callsign callsign) =>
        DeterministicPhraseology.ToDisplayForm(spokenText, callsign);

    private static string ReasonText(UnableReason reason) => reason switch
    {
        UnableReason.AlreadyAirborne => "you are already airborne",
        UnableReason.ClearanceAlreadyIssued => "clearance has already been issued",
        UnableReason.NoDepartureRunwayAvailable => "no departure runway is available",
        UnableReason.Airborne => "you are airborne",
        UnableReason.AlreadyMoving => "you are already moving",
        UnableReason.NotAtThisStageOfFlight => "not at this stage of flight",
        UnableReason.ReadBackClearanceFirst => "read back your clearance first",
        UnableReason.NoDepartureRunwayAssigned => "no departure runway is assigned",
        UnableReason.ProgressiveTaxiGroundOnly => "progressive taxi is only available on the ground",
        UnableReason.NoActiveTaxiRoute => "no active taxi route is available",
        UnableReason.NoRunwayCrossingPending => "no runway crossing is planned on your route",
        UnableReason.ContactTowerForDeparture => "contact Tower for departure",
        UnableReason.NotAtHoldingPoint => "you are not at the holding point",
        UnableReason.NotWithThisController => "not with this controller",
        UnableReason.WaypointNotOnRoute => "that waypoint is not on your route",
        UnableReason.ContactApproach => "contact Approach",
        UnableReason.DestinationDataUnavailable => "destination aerodrome data is unavailable",
        UnableReason.NoArrivalRunwayAvailable => "no arrival runway is available",
        UnableReason.RunwayNotAvailable => "that runway is not available",
        UnableReason.NotClearedForApproach => "you are not cleared for the approach",
        UnableReason.ContactTower => "contact Tower",
        UnableReason.NoLandingRunwayAssigned => "no landing runway is assigned",
        UnableReason.NotLanded => "you have not landed",
        UnableReason.AerodromeDataUnavailable => "aerodrome data is unavailable",
        UnableReason.NoStandAvailable => "no stand is available",
        _ => "unable to comply",
    };
}
