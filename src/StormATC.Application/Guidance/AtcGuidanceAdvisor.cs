using StormATC.Application.Localization;
using StormATC.Domain;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Guidance;

/// <summary>
/// Produces a non-authoritative training hint for the dashboard.
/// </summary>
/// <remarks>
/// This never changes session state and never grants a clearance. It only turns the state that
/// the deterministic engine already approved into a concise suggestion for the pilot's next
/// radio call.
/// </remarks>
public static class AtcGuidanceAdvisor
{
    /// <summary>Builds the most useful next-call hint for the current session.</summary>
    public static AtcGuidance ForSession(AtcSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.FlightPlan is null || session.Callsign is not Callsign callsign)
        {
            return new AtcGuidance(
                UiText.T("Import a flight plan"),
                UiText.T("Open Flight Plan and import your latest SimBrief OFP before calling ATC."),
                string.Empty);
        }

        // Titles and explanations are interface text; the example is what to say on the
        // radio, so it follows the radio language.
        bool french = session.Language == PhraseologyLanguage.French;
        IAtcPhraseology say = AtcPhraseologies.For(session.Language);
        string spokenCallsign = say.SpeakCallsign(callsign);
        string departureRunway = session.AssignedDepartureRunway is RunwayIdentifier runway
            ? say.SpeakRunway(runway)
            : french ? "piste assignée" : "runway assigned runway";
        string Example(string english, string inFrench) => french ? inFrench : english;

        if (session.CurrentController == ControllerPosition.Atis)
        {
            return new AtcGuidance(
                UiText.T("Listen to departure ATIS"),
                UiText.T("Copy the airport information, then tune the published Delivery frequency. "
                    + "ATIS is receive-only, so no pilot call is required."),
                string.Empty);
        }

        if (session.PendingReadback is PendingReadback pending)
        {
            ExpectedReadbackItem[] remaining =
            [
                .. pending.ExpectedItems.Where(
                    item => !pending.SatisfiedItems.Contains(item.Type)),
            ];

            string required = remaining.Length == 0
                ? UiText.T("Repeat the outstanding instruction.")
                : UiText.T("Still required: ") + string.Join(
                    ", ",
                    remaining.Select(item => FriendlyName(item.Type))) + ".";

            string example = string.Join(
                ", ",
                remaining
                    .OrderBy(item => item.Type == ReadbackItemType.Callsign ? 1 : 0)
                    .Select(item => item.SpokenValue));

            return new AtcGuidance(
                pending.AttemptCount > 0 ? UiText.T("Correct the readback") : UiText.T("Read back the clearance"),
                required,
                example);
        }

        if (session.FlightRules == FlightRules.Vfr
            && VfrGuidance(session, say, spokenCallsign, french) is AtcGuidance vfr)
        {
            return vfr;
        }

        return session.Phase switch
        {
            FlightPhase.Disconnected =>
                new(UiText.T("Wait for the simulator"), UiText.T("StormATC needs a live or mock aircraft state."), string.Empty),

            FlightPhase.Preflight or FlightPhase.ClearanceRequested when session.IfrClearance is null =>
                new(
                    UiText.T("Request IFR clearance"),
                    UiText.T("Call Delivery when you are ready to copy."),
                    Example(
                        $"{spokenCallsign}, request IFR clearance",
                        $"{spokenCallsign}, bonjour, demandons la clairance IFR")),

            FlightPhase.IFRClearanceIssued =>
                new(
                    UiText.T("Request pushback and start"),
                    UiText.T("Your IFR clearance is acknowledged. Remain on this frequency unless handed off."),
                    Example(
                        $"{spokenCallsign}, request pushback and start",
                        $"{spokenCallsign}, demandons repoussage et mise en route")),

            FlightPhase.Pushback =>
                new(
                    UiText.T("Request taxi"),
                    UiText.T("Call when pushback is complete and you are ready to taxi."),
                    Example(
                        $"{spokenCallsign}, ready for taxi",
                        $"{spokenCallsign}, prêts à rouler")),

            FlightPhase.TaxiOut =>
                new(
                    UiText.T("Continue to the holding point"),
                    UiText.T("Report holding short when you reach the assigned departure runway."),
                    Example(
                        $"{spokenCallsign}, holding short {departureRunway}",
                        $"{spokenCallsign}, au point d'attente {departureRunway}")),

            FlightPhase.HoldingShort =>
                new(
                    UiText.T("Report ready for departure"),
                    UiText.T("Tower will decide whether to line up, hold, or clear you for takeoff."),
                    Example(
                        $"{spokenCallsign}, holding short {departureRunway}, ready for departure",
                        $"{spokenCallsign}, au point d'attente {departureRunway}, prêts au départ")),

            FlightPhase.LineUp =>
                new(
                    UiText.T("Monitor Tower"),
                    UiText.T("You are lined up. Wait for takeoff clearance."),
                    string.Empty),

            FlightPhase.Takeoff =>
                new(
                    UiText.T("Fly the takeoff clearance"),
                    UiText.T("Report airborne after liftoff if Tower has not already handed you off."),
                    Example($"{spokenCallsign}, airborne", $"{spokenCallsign}, en l'air")),

            FlightPhase.InitialClimb =>
                new(
                    UiText.T("Check in with Departure"),
                    UiText.T("Use your current altitude and the altitude you are climbing to."),
                    Example(
                        $"Departure, {spokenCallsign}, passing "
                            + SpokenNumbers.Altitude(session.AircraftState.IndicatedAltitudeRounded)
                            + ", climbing "
                            + (session.ClearedAltitude?.ToSpoken() ?? "as cleared"),
                        $"Départ, bonjour, {spokenCallsign}, passant "
                            + FrenchSpokenNumbers.Cardinal(session.AircraftState.IndicatedAltitudeRounded)
                            + " pieds, en montée vers "
                            + (session.ClearedAltitude is Altitude cleared
                                ? say.SpeakAltitude(cleared)
                                : "le niveau autorisé"))),

            FlightPhase.Departure =>
                new(
                    UiText.T("Continue the departure"),
                    UiText.T("Request higher only when you need another level."),
                    Example($"{spokenCallsign}, request climb", $"{spokenCallsign}, demandons à monter")),

            FlightPhase.Enroute =>
                new(
                    UiText.T("Monitor Center"),
                    UiText.T("No call is required unless you need a change or receive a handoff."),
                    string.Empty),

            FlightPhase.Descent =>
                new(
                    UiText.T("Continue the descent"),
                    UiText.T("Call Approach after the handoff or request the filed approach."),
                    Example($"{spokenCallsign}, request approach", $"{spokenCallsign}, demandons l'approche")),

            FlightPhase.Arrival =>
                new(
                    UiText.T("Prepare for the approach"),
                    UiText.T("Check the arrival and request the approach you intend to fly."),
                    Example($"{spokenCallsign}, request approach", $"{spokenCallsign}, demandons l'approche")),

            FlightPhase.Approach =>
                new(
                    UiText.T("Report established"),
                    UiText.T("Report when established on the cleared approach."),
                    Example($"{spokenCallsign}, established", $"{spokenCallsign}, établis")),

            FlightPhase.Final =>
                new(
                    UiText.T("Continue final"),
                    UiText.T("Wait for landing clearance and report only if instructed."),
                    string.Empty),

            FlightPhase.Landed =>
                new(
                    UiText.T("Report runway vacated"),
                    UiText.T("Call Ground only after you are clear of the runway."),
                    Example($"{spokenCallsign}, runway vacated", $"{spokenCallsign}, piste dégagée")),

            FlightPhase.TaxiIn =>
                new(
                    UiText.T("Taxi to stand"),
                    UiText.T("Follow the issued route and report parked at your stand."),
                    string.Empty),

            FlightPhase.Parked =>
                new(UiText.T("Flight complete"), UiText.T("The aircraft is parked at the assigned stand."), string.Empty),

            FlightPhase.GoAround =>
                new(
                    UiText.T("Fly the missed approach"),
                    UiText.T("State your altitude when Approach asks or after the handoff."),
                    Example(
                        $"Approach, {spokenCallsign}, going around",
                        $"Approche, {spokenCallsign}, remise de gaz")),

            FlightPhase.Abnormal =>
                new(
                    UiText.T("State your intentions"),
                    UiText.T("Continue using MAYDAY or PAN PAN as appropriate for the simulated situation."),
                    Example($"{spokenCallsign}, request", $"{spokenCallsign}, demandons")),

            _ =>
                new(
                    UiText.T("Call the current controller"),
                    UiText.T("Use standard phraseology and include your callsign."),
                    spokenCallsign),
        };
    }

    /// <summary>
    /// The hint for a VFR flight, where the instrument-flight advice (IFR clearance, check in
    /// with Departure, request the approach) does not apply. Null leaves the general hint.
    /// </summary>
    private static AtcGuidance? VfrGuidance(
        AtcSession session,
        IAtcPhraseology say,
        string spokenCallsign,
        bool french)
    {
        string Example(string english, string inFrench) => french ? inFrench : english;
        string runway = session.AssignedArrivalRunway is RunwayIdentifier arrival
            ? say.SpeakRunway(arrival)
            : french ? "piste en service" : "runway in use";

        return session.Phase switch
        {
            FlightPhase.Preflight or FlightPhase.ClearanceRequested =>
                new(
                    UiText.T("Request taxi, VFR"),
                    UiText.T("A VFR flight needs no IFR clearance: call Ground, or Tower if there is no Ground."),
                    Example(
                        $"{spokenCallsign}, VFR local flight, request taxi",
                        $"{spokenCallsign}, vol local, demandons le roulage")),

            FlightPhase.InitialClimb or FlightPhase.Takeoff =>
                new(
                    UiText.T("Stay with the tower"),
                    UiText.T("Fly the circuit and report each leg, or report leaving the frequency."),
                    Example(
                        $"{spokenCallsign}, downwind runway {runway}",
                        $"{spokenCallsign}, vent arrière {runway}")),

            FlightPhase.Enroute or FlightPhase.Departure or FlightPhase.Descent or FlightPhase.Arrival
                when session.CircuitLeg == CircuitLeg.None =>
                new(
                    UiText.T("Call the tower"),
                    UiText.T("Tune the tower and give your type, origin, intention, altitude, estimate and ATIS letter."),
                    Example(
                        $"{spokenCallsign}, VFR from the north for a touch and go, 1500 feet, estimate the reporting point, information India",
                        $"{spokenCallsign}, VFR du nord pour un toucher, mille cinq cents pieds, estimé au point de report, information India")),

            FlightPhase.Circuit => session.CircuitLeg switch
            {
                CircuitLeg.Joining =>
                    new(
                        UiText.T("Report downwind"),
                        UiText.T("Join the circuit and report when you are on the downwind leg."),
                        Example(
                            $"{spokenCallsign}, downwind runway {runway}",
                            $"{spokenCallsign}, vent arrière {runway}")),
                CircuitLeg.Downwind =>
                    new(
                        UiText.T("Report base"),
                        UiText.T("Report when you turn onto the base leg."),
                        Example($"{spokenCallsign}, base runway {runway}", $"{spokenCallsign}, base {runway}")),
                CircuitLeg.Base =>
                    new(
                        UiText.T("Report final"),
                        UiText.T("Report when you are on final. Landing or touch-and-go clearance follows."),
                        Example($"{spokenCallsign}, final runway {runway}", $"{spokenCallsign}, finale {runway}")),
                _ => null,
            },

            _ => null,
        };
    }

    private static string FriendlyName(ReadbackItemType type) => type switch
    {
        ReadbackItemType.Callsign => UiText.T("callsign"),
        ReadbackItemType.Runway => UiText.T("runway"),
        ReadbackItemType.InitialAltitude => UiText.T("initial altitude"),
        ReadbackItemType.CruiseAltitude => UiText.T("cleared level"),
        ReadbackItemType.Squawk => UiText.T("squawk"),
        ReadbackItemType.Frequency => UiText.T("frequency"),
        ReadbackItemType.Heading => UiText.T("heading"),
        ReadbackItemType.HoldShortRunway => UiText.T("hold-short runway"),
        ReadbackItemType.TaxiRoute => UiText.T("taxi route"),
        ReadbackItemType.DepartureProcedure => UiText.T("departure"),
        ReadbackItemType.ApproachName => UiText.T("approach"),
        _ => type.ToString(),
    };
}

/// <summary>A dashboard training hint derived from the current deterministic session state.</summary>
public sealed record AtcGuidance(string Title, string Detail, string Example)
{
    /// <summary>True when there is an example transmission to display.</summary>
    public bool HasExample => !string.IsNullOrWhiteSpace(Example);
}
