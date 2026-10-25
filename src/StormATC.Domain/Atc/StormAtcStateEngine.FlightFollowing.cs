using StormATC.Domain.Airports;
using StormATC.Domain.Approach;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Atc;

// A VFR flight following: the pilot tunes a flight information service ("Paris Information"), asks for a flight
// following, gets a transponder code, is passed to the next sector as the flight crosses the country, and is let go
// to the destination's approach or tower when the flight gets there.
public sealed partial class StormAtcStateEngine
{
    /// <summary>How close to the destination a followed flight is passed to the aerodrome, in nautical miles.</summary>
    private const double FlightFollowingEndRadiusNauticalMiles = 15;

    /// <summary>The shortest time between two sector changes, so that a flight along a limit is not passed back and forth.</summary>
    private static readonly TimeSpan FlightFollowingSectorChangeInterval = TimeSpan.FromMinutes(3);

    /// <summary>"Paris Information, F-HAST, VFR from Lognes to Pontoise, request flight following."</summary>
    private static AtcDecision HandleFlightFollowingRequest(AtcEvaluationContext context, PilotIntent intent)
    {
        AtcSession session = context.Session;

        if (session.Callsign is not Callsign callsign)
        {
            return AtcDecision.Silent();
        }

        if (!session.FlightRules.IsVisual() && !intent.Entities.DeclaresVfr)
        {
            return AtcDecision.Reject(
                "A flight following is for VFR flights.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        if (context.AircraftState.IsOnGround)
        {
            return AtcDecision.Reject(
                "A flight following is asked for in flight.",
                Say(context).Unable(callsign, UnableReason.NotAtThisStageOfFlight));
        }

        if (FlightInformationRegions.Find(context.AircraftState.Position) is not FlightInformationSector sector)
        {
            return AtcDecision.Reject(
                "No flight information service is published where the aircraft is.",
                Say(context).FlightFollowingUnavailable(callsign));
        }

        // The pilot has to be on a flight information frequency: whoever else he calls cannot take the flight.
        if (session.CurrentController != ControllerPosition.Information
            || session.CurrentFrequency is not Frequency current)
        {
            return AtcDecision.Reject(
                $"A flight following is taken by {sector.ServiceName}, not by {session.CurrentController}.",
                Say(context).FlightFollowingCallService(callsign, sector.ServiceName, sector.Frequency));
        }

        FlightInformationSector serving = FlightInformationRegions.FindByFrequency(current) ?? sector;
        SquawkCode squawk = session.FlightFollowing?.Squawk ?? ResolveSquawk(context, callsign);

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.CheckInAcknowledgement,
            NewPhase = FlightPhase.Enroute,
            NewFlightRules = VisualRules(context),
            NewFlightFollowing = new FlightFollowingState
            {
                ServiceName = serving.ServiceName,
                Frequency = current,
                Squawk = squawk,
                Since = context.CurrentTime,
            },
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["service"] = serving.ServiceName,
                ["squawk"] = squawk.Digits,
            },
            ProhibitedFacts = ["any clearance", "any altitude assignment", "any heading"],
            FallbackPhraseology = Say(context).FlightFollowingAccepted(callsign, serving.ServiceName, squawk),
        };
    }

    /// <summary>
    /// What the flight information service does by itself: passes the flight to the next sector when it crosses a
    /// limit, lets it go when it leaves the area, and hands it to the destination when it gets there.
    /// </summary>
    private static AtcDecision? EvaluateFlightFollowing(AtcEvaluationContext context)
    {
        AtcSession session = context.Session;
        if (session.FlightFollowing is not FlightFollowingState following
            || session.CurrentController != ControllerPosition.Information
            || context.AircraftState.IsOnGround
            || session.Callsign is not Callsign callsign)
        {
            return null;
        }

        // The flight is near its destination: end the following and pass it to the aerodrome.
        if (context.DestinationAirport is AirportData destination
            && context.AircraftState.Position.IsWithin(destination.ReferencePosition, FlightFollowingEndRadiusNauticalMiles))
        {
            ControllerPosition next = destination.Frequencies.Approach is not null
                ? ControllerPosition.Approach
                : ControllerPosition.Tower;
            Frequency? frequency = destination.Frequencies.For(next);
            return EndFlightFollowing(context, callsign, next, frequency);
        }

        if (context.CurrentTime - following.Since < FlightFollowingSectorChangeInterval)
        {
            return null;
        }

        FlightInformationSector? sector = FlightInformationRegions.Find(context.AircraftState.Position);
        if (sector is null)
        {
            return EndFlightFollowing(context, callsign, null, null);
        }

        if (sector.Frequency.IsSameChannel(following.Frequency))
        {
            return null;
        }

        var handoff = new ControllerHandoff
        {
            FromController = ControllerPosition.Information,
            ToController = ControllerPosition.Information,
            ToFrequency = sector.Frequency,
            InstructedAt = context.CurrentTime,
            State = HandoffState.Instructed,
            Reason = "The flight crossed into another flight information sector.",
        };

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Handoff,
            Handoff = handoff,
            NewFlightFollowing = following with
            {
                ServiceName = sector.ServiceName,
                Frequency = sector.Frequency,
                Since = context.CurrentTime,
            },
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nextController"] = sector.ServiceName,
                ["frequency"] = sector.Frequency.ToComparisonValue(),
            },
            ProhibitedFacts = ["any clearance", "any altitude change"],
            FallbackPhraseology = Say(context).FlightFollowingHandoff(callsign, sector.ServiceName, sector.Frequency),
        };
    }

    private static AtcDecision EndFlightFollowing(
        AtcEvaluationContext context,
        Callsign callsign,
        ControllerPosition? next,
        Frequency? frequency)
    {
        ControllerHandoff? handoff = next is ControllerPosition to && frequency is Frequency toFrequency
            ? new ControllerHandoff
            {
                FromController = ControllerPosition.Information,
                ToController = to,
                ToFrequency = toFrequency,
                InstructedAt = context.CurrentTime,
                State = HandoffState.Instructed,
                Reason = "The followed flight reached its destination.",
            }
            : null;
        string? name = handoff is null
            ? null
            : context.DestinationAirport is { } airport
                ? $"{airport.Name} {Say(context).ControllerName(handoff.ToController)}"
                : Say(context).ControllerName(handoff.ToController);

        return new AtcDecision
        {
            Accepted = true,
            ResponseType = handoff is null ? AtcResponseType.Acknowledgement : AtcResponseType.Handoff,
            EndsFlightFollowing = true,
            Handoff = handoff,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["acknowledgement"] = "flight following terminated",
            },
            ProhibitedFacts = ["any clearance"],
            FallbackPhraseology = Say(context).FlightFollowingEnded(callsign, name, frequency),
        };
    }
}
