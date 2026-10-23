using StormATC.Domain.Approach;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Atc;

// MARSA (Military Assumes Responsibility for Separation of Aircraft), as Instruction 1950 DSAE/DIRCAM words it:
// the leader of a flight asks for a MARSA departure or approach; the controller approves it and then treats the
// flight as one, the aircraft keeping their own separation from each other.
public sealed partial class StormAtcStateEngine
{
    /// <summary>"Flight of two, request MARSA departure / approach runway 29": approved for a military flight.</summary>
    private static AtcDecision HandleMarsaRequest(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (!IsMilitaryCall(context, intent))
        {
            return AtcDecision.Reject(
                "MARSA is a military procedure.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        MarsaKind kind = intent.Entities.MarsaApproach ? MarsaKind.Approach : MarsaKind.Departure;
        bool onGround = context.AircraftState.IsOnGround;

        // A departure is asked for on the ground, an approach in the air.
        if (onGround == (kind == MarsaKind.Approach))
        {
            return AtcDecision.Reject(
                kind == MarsaKind.Departure ? "A MARSA departure is asked for on the ground." : "A MARSA approach is asked for in flight.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        // A formation has at least two aircraft: a request for a single aircraft is not a MARSA.
        if (intent.Entities.FlightSize is not int size || size < 2)
        {
            return AtcDecision.Reject(
                "A MARSA is asked for by a flight of at least two aircraft.",
                Say(context).SayAgain(callsign));
        }

        if (ResolveVfrRunway(context) is not RunwayIdentifier runway)
        {
            return AtcDecision.Reject(
                "No runway is known for this aerodrome.",
                Say(context).Unable(callsign, UnableReason.NoArrivalRunwayAvailable));
        }

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            NewMarsa = new MarsaState
            {
                Kind = kind,
                FlightSize = size,
                TrailMiles = intent.Entities.TrailMiles,
                Since = context.CurrentTime,
            },
            NewArrivalRunway = kind == MarsaKind.Approach ? runway : null,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instruction"] = kind == MarsaKind.Departure ? "MARSA departure approved" : "MARSA approach approved",
                ["runway"] = runway.ToComparisonValue(),
            },
            ProhibitedFacts = ["landing clearance", "takeoff clearance"],
            AllowLanguageModelPhraseology = false,
            FallbackPhraseology = Say(context).MarsaApproved(callsign, kind, runway, runway.CenterlineHeading.Degrees),
        };
    }
}
