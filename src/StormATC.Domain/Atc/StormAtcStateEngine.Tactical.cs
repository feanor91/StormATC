using StormATC.Domain.Aircraft;
using StormATC.Domain.Approach;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Atc;

// The tactical picture: a military pilot asks "situation" and the controller gives the nearest contacts of the
// simulator's traffic as bearing, range, altitude and aspect, seen from the aircraft. The contacts are what MSFS
// reports (AI aircraft, including the ones a tablet generates); the controller names no side, and "declares" nothing.
public sealed partial class StormAtcStateEngine
{
    /// <summary>The most contacts one picture names.</summary>
    private const int PictureContacts = 3;

    private static AtcDecision HandleTacticalPicture(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (!IsMilitaryCall(context, intent))
        {
            return AtcDecision.Reject(
                "The tactical picture is for military flights.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        AircraftState own = context.AircraftState;
        TacticalContact[] contacts =
        [
            .. context.Traffic
                .Where(contact => !contact.IsOnGround)
                .Select(contact => TacticalGeometry.Describe(own.Position, own.TrueHeadingDegrees, own.MagneticHeadingDegrees, contact))
                .OrderBy(contact => contact.RangeNauticalMiles)
                .Take(PictureContacts),
        ];

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["instruction"] = "tactical picture",
                ["contacts"] = contacts.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            ProhibitedFacts = ["any contact that is not in the picture", "clearance to engage"],
            AllowLanguageModelPhraseology = false,
            FallbackPhraseology = contacts.Length == 0
                ? Say(context).NoTacticalContact(callsign)
                : Say(context).TacticalPicture(callsign, contacts),
        };
    }
}
