using StormATC.Domain.Airports;
using StormATC.Domain.Approach;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Atc;

// Military radar approaches: PAR (azimuth and glidepath) and SRA (azimuth and a recommended altitude each mile).
// The pilot asks for one, the controller accepts it and then talks the aircraft down: the calls come from the
// aircraft's position against the extended runway centreline, not from what the pilot says (the pilot does not
// acknowledge). The geometry is RadarGeometry; the words are the phraseology's.
public sealed partial class StormAtcStateEngine
{
    /// <summary>The distance to the threshold at which the controller asks for the gear check, by approach.</summary>
    private static double GearCheckDistanceNauticalMiles(RadarApproachKind kind) => kind == RadarApproachKind.Par ? 2.0 : 3.0;

    /// <summary>The PAR is closed with the landing clearance at this distance; the SRA ends at 2 NM.</summary>
    private const double ParLandingDistanceNauticalMiles = 0.9;

    private const double SraEndDistanceNauticalMiles = 2.0;

    /// <summary>"Demande PAR piste 29": a military pilot asks for a radar approach.</summary>
    private static AtcDecision HandleRadarApproachRequest(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (intent.Entities.RadarApproach is not RadarApproachKind kind)
        {
            return HandleUnrecognized(context, intent);
        }

        if (context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "A radar approach is flown in the air.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        if (!IsMilitaryCall(context, intent))
        {
            return AtcDecision.Reject(
                "PAR and SRA are military radar approaches.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        AirportData? airport = context.DestinationAirport ?? context.DepartureAirport;
        RunwayIdentifier? runway = intent.Entities.Runway ?? ResolveVfrRunway(context);
        if (runway is not RunwayIdentifier chosen || airport?.FindRunway(chosen) is null)
        {
            return AtcDecision.Reject(
                "No runway is known for this aerodrome.",
                Say(context).Unable(callsign, UnableReason.NoArrivalRunwayAvailable));
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            NewPhase = FlightPhase.Approach,
            NewFlightRules = session.FlightRules.IsMilitary() ? null : intent.Entities.DeclaredRegime,
            NewArrivalRunway = chosen,
            NewRadarApproach = new RadarApproachState
            {
                Kind = kind,
                Runway = chosen,
                StartedAt = context.CurrentTime,
                LastCallAt = context.CurrentTime,
            },
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instruction"] = kind == RadarApproachKind.Par ? "PAR approach accepted" : "SRA approach accepted",
                ["runway"] = chosen.ToComparisonValue(),
            },
            ProhibitedFacts = ["landing clearance before the end of the approach"],
            AllowLanguageModelPhraseology = false,
            FallbackPhraseology = Say(context).RadarApproachStart(callsign, kind, chosen, QfeFor(context, airport)),
        };
    }

    /// <summary>
    /// The next call of the radar approach in progress, or null when it is not time to speak. Called with every
    /// state change; the pilot does not acknowledge, so the controller speaks on a timer and on the distance.
    /// </summary>
    private static AtcDecision? EvaluateRadarApproach(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;
        if (session.RadarApproach is not { Completed: false } approach || session.Callsign is not Callsign callsign)
        {
            return null;
        }

        // On the ground the approach is over, whatever happened: nothing to say.
        if (context.AircraftState.IsOnGround)
        {
            return new AtcDecision { Accepted = true, ResponseType = AtcResponseType.None, EndsRadarApproach = true, FallbackPhraseology = string.Empty };
        }

        AirportData? airport = context.DestinationAirport ?? context.DepartureAirport;
        if (airport?.FindRunway(approach.Runway) is not RunwayData runway)
        {
            return null;
        }

        RadarGeometry geometry = RadarGeometry.Compute(runway, airport.ElevationFeet, context.AircraftState);
        TimeSpan sinceLastCall = context.CurrentTime - approach.LastCallAt;

        if (!geometry.InWedge)
        {
            // Outside the wedge before contact: nothing to say yet. After contact: the approach is lost.
            return approach.Established
                ? RadarCall(approach with { Completed = true, LastCallAt = context.CurrentTime }, Say(context).RadarApproachEnd(callsign, approach.Kind))
                : null;
        }

        int distance = Math.Max(1, (int)Math.Round(geometry.AlongTrackNauticalMiles));

        if (!approach.Established)
        {
            return RadarCall(
                approach with { Established = true, LastCallAt = context.CurrentTime },
                Say(context).RadarContact(callsign, distance));
        }

        TimeSpan interval = TimeSpan.FromSeconds(geometry.AlongTrackNauticalMiles > 4 ? 8 : geometry.AlongTrackNauticalMiles > 2 ? 6 : 4);
        if (sinceLastCall < interval)
        {
            return null;
        }

        if (!approach.GearChecked && geometry.AlongTrackNauticalMiles <= GearCheckDistanceNauticalMiles(approach.Kind))
        {
            return RadarCall(
                approach with { GearChecked = true, LastCallAt = context.CurrentTime },
                Say(context).CheckGearDownAndLocked(callsign));
        }

        if (approach.Kind == RadarApproachKind.Par && geometry.AlongTrackNauticalMiles <= ParLandingDistanceNauticalMiles)
        {
            // The PAR ends with the landing clearance, given by the tower that holds the runway.
            AtcDecision landing = HandleLandingClearance(context with { Session = session.WithArrivalRunway(approach.Runway) }, callsign);
            return landing.Accepted
                ? landing with { EndsRadarApproach = true, NewCircuitLeg = CircuitLeg.Final }
                : null;
        }

        if (approach.Kind == RadarApproachKind.Sra && geometry.AlongTrackNauticalMiles <= SraEndDistanceNauticalMiles)
        {
            return RadarCall(
                approach with { Completed = true, LastCallAt = context.CurrentTime },
                Say(context).RadarApproachEnd(callsign, approach.Kind));
        }

        var call = new RadarCall(
            geometry.Lateral,
            approach.Kind == RadarApproachKind.Par ? geometry.Vertical : null,
            distance,
            geometry.CorrectionHeading,
            approach.Kind == RadarApproachKind.Sra ? geometry.RecommendedAltitudeFeet : null);
        return RadarCall(approach with { LastCallAt = context.CurrentTime }, Say(context).RadarGuidance(callsign, call));
    }

    private static AtcDecision RadarCall(RadarApproachState next, string words) => new()
    {
        Accepted = true,
        ResponseType = AtcResponseType.RadarGuidance,
        NewRadarApproach = next,
        ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instruction"] = next.Kind == RadarApproachKind.Par ? "PAR guidance" : "SRA guidance",
            ["runway"] = next.Runway.ToComparisonValue(),
        },
        ProhibitedFacts = ["landing clearance before the end of the approach"],
        AllowLanguageModelPhraseology = false,
        FallbackPhraseology = words,
    };
}
