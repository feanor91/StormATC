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
                "Import a flight plan",
                "Open Flight Plan and import your latest SimBrief OFP before calling ATC.",
                string.Empty);
        }

        string spokenCallsign = callsign.ToSpoken();

        if (session.PendingReadback is PendingReadback pending)
        {
            ExpectedReadbackItem[] remaining =
            [
                .. pending.ExpectedItems.Where(
                    item => !pending.SatisfiedItems.Contains(item.Type)),
            ];

            string required = remaining.Length == 0
                ? "Repeat the outstanding instruction."
                : "Still required: " + string.Join(
                    ", ",
                    remaining.Select(item => FriendlyName(item.Type))) + ".";

            string example = string.Join(
                ", ",
                remaining
                    .OrderBy(item => item.Type == ReadbackItemType.Callsign ? 1 : 0)
                    .Select(item => item.SpokenValue));

            return new AtcGuidance(
                pending.AttemptCount > 0 ? "Correct the readback" : "Read back the clearance",
                required,
                example);
        }

        return session.Phase switch
        {
            FlightPhase.Disconnected =>
                new("Wait for the simulator", "StormATC needs a live or mock aircraft state.", string.Empty),

            FlightPhase.Preflight or FlightPhase.ClearanceRequested when session.IfrClearance is null =>
                new(
                    "Request IFR clearance",
                    "Call Delivery when you are ready to copy.",
                    $"{spokenCallsign}, request IFR clearance"),

            FlightPhase.IFRClearanceIssued =>
                new(
                    "Request pushback and start",
                    "Your IFR clearance is acknowledged. Remain on this frequency unless handed off.",
                    $"{spokenCallsign}, request pushback and start"),

            FlightPhase.Pushback =>
                new(
                    "Request taxi",
                    "Call when pushback is complete and you are ready to taxi.",
                    $"{spokenCallsign}, ready for taxi"),

            FlightPhase.TaxiOut =>
                new(
                    "Continue to the holding point",
                    "Report holding short when you reach the assigned departure runway.",
                    $"{spokenCallsign}, holding short runway "
                        + (session.AssignedDepartureRunway?.ToSpokenDesignator() ?? "assigned runway")),

            FlightPhase.HoldingShort =>
                new(
                    "Report ready for departure",
                    "Tower will decide whether to line up, hold, or clear you for takeoff.",
                    $"{spokenCallsign}, holding short runway "
                        + (session.AssignedDepartureRunway?.ToSpokenDesignator() ?? "assigned runway")
                        + ", ready for departure"),

            FlightPhase.LineUp =>
                new(
                    "Monitor Tower",
                    "You are lined up. Wait for takeoff clearance.",
                    string.Empty),

            FlightPhase.Takeoff =>
                new(
                    "Fly the takeoff clearance",
                    "Report airborne after liftoff if Tower has not already handed you off.",
                    $"{spokenCallsign}, airborne"),

            FlightPhase.InitialClimb =>
                new(
                    "Check in with Departure",
                    "Use your current altitude and the altitude you are climbing to.",
                    $"Departure, {spokenCallsign}, passing "
                        + SpokenNumbers.Altitude(session.AircraftState.IndicatedAltitudeRounded)
                        + ", climbing "
                        + (session.ClearedAltitude?.ToSpoken() ?? "as cleared")),

            FlightPhase.Departure =>
                new(
                    "Continue the departure",
                    "Request higher only when you need another level.",
                    $"{spokenCallsign}, request climb"),

            FlightPhase.Enroute =>
                new(
                    "Monitor Center",
                    "No call is required unless you need a change or receive a handoff.",
                    string.Empty),

            FlightPhase.Descent =>
                new(
                    "Continue the descent",
                    "Call Approach after the handoff or request the filed approach.",
                    $"{spokenCallsign}, request approach"),

            FlightPhase.Arrival =>
                new(
                    "Prepare for the approach",
                    "Check the arrival and request the approach you intend to fly.",
                    $"{spokenCallsign}, request approach"),

            FlightPhase.Approach =>
                new(
                    "Report established",
                    "Report when established on the cleared approach.",
                    $"{spokenCallsign}, established"),

            FlightPhase.Final =>
                new(
                    "Continue final",
                    "Wait for landing clearance and report only if instructed.",
                    string.Empty),

            FlightPhase.Landed =>
                new(
                    "Report runway vacated",
                    "Call Ground only after you are clear of the runway.",
                    $"{spokenCallsign}, runway vacated"),

            FlightPhase.TaxiIn =>
                new(
                    "Taxi to stand",
                    "Follow the issued route and report parked at your stand.",
                    string.Empty),

            FlightPhase.Parked =>
                new("Flight complete", "The aircraft is parked at the assigned stand.", string.Empty),

            FlightPhase.GoAround =>
                new(
                    "Fly the missed approach",
                    "State your altitude when Approach asks or after the handoff.",
                    $"Approach, {spokenCallsign}, going around"),

            FlightPhase.Abnormal =>
                new(
                    "State your intentions",
                    "Continue using MAYDAY or PAN PAN as appropriate for the simulated situation.",
                    $"{spokenCallsign}, request"),

            _ =>
                new(
                    "Call the current controller",
                    "Use standard phraseology and include your callsign.",
                    spokenCallsign),
        };
    }

    private static string FriendlyName(ReadbackItemType type) => type switch
    {
        ReadbackItemType.Callsign => "callsign",
        ReadbackItemType.Runway => "runway",
        ReadbackItemType.InitialAltitude => "initial altitude",
        ReadbackItemType.CruiseAltitude => "cleared level",
        ReadbackItemType.Squawk => "squawk",
        ReadbackItemType.Frequency => "frequency",
        ReadbackItemType.Heading => "heading",
        ReadbackItemType.HoldShortRunway => "hold-short runway",
        ReadbackItemType.TaxiRoute => "taxi route",
        ReadbackItemType.DepartureProcedure => "departure",
        ReadbackItemType.ApproachName => "approach",
        _ => type.ToString(),
    };
}

/// <summary>A dashboard training hint derived from the current deterministic session state.</summary>
public sealed record AtcGuidance(string Title, string Detail, string Example)
{
    /// <summary>True when there is an example transmission to display.</summary>
    public bool HasExample => !string.IsNullOrWhiteSpace(Example);
}
