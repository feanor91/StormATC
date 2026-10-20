using System.Text;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Phraseology;

/// <summary>
/// Builds correct ICAO-style radio phraseology from approved clearance facts, with no language
/// model involved.
/// </summary>
/// <remarks>
/// <para>
/// This class is the safety net that makes the whole LLM-assisted design acceptable. Every
/// controller response StormATC can produce has a deterministic rendering here. The language
/// model is an optional layer on top that makes wording sound more natural; if it is absent,
/// slow, wrong, or hallucinating, the session continues on this output with no loss of
/// correctness — only of variety.
/// </para>
/// <para>
/// Because these strings are generated from validated value objects, they cannot contain a
/// number the engine did not approve.
/// </para>
/// </remarks>
public static class DeterministicPhraseology
{
    /// <summary>Renders an IFR clearance.</summary>
    public static string IfrClearance(Callsign callsign, Clearance clearance)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentNullException.ThrowIfNull(clearance);

        var builder = new PhraseBuilder(callsign);

        builder.Add(clearance.ClearedToDestination is { Length: > 0 } destination
            ? $"cleared to {SpellIcao(destination)}"
            : "cleared as filed");

        if (clearance.DepartureProcedure is { Length: > 0 } sid)
        {
            builder.Add($"{SpeakProcedure(sid)} departure");
        }
        else if (clearance.ClearedRoute is { Length: > 0 })
        {
            builder.Add("via flight planned route");
        }

        if (clearance.Runway is RunwayIdentifier runway)
        {
            builder.Add(runway.ToSpoken());
        }

        if (clearance.InitialAltitude is Altitude initial)
        {
            builder.Add($"climb initially {initial.ToSpoken()}");
        }

        if (clearance.Squawk is SquawkCode squawk)
        {
            builder.Add($"squawk {squawk.ToSpoken()}");
        }

        if (clearance.NextFrequency is Frequency frequency)
        {
            builder.Add($"departure frequency {frequency.ToSpoken()}");
        }

        return builder.Build();
    }

    /// <summary>Renders acceptance of a readback, optionally with the next frequency.</summary>
    public static string ReadbackCorrect(Callsign callsign, Frequency? nextFrequency, ControllerPosition? nextController)
    {
        var builder = new PhraseBuilder(callsign);
        builder.Add("readback correct");

        if (nextFrequency is Frequency frequency)
        {
            string position = nextController is ControllerPosition controller
                ? SpokenControllerName(controller)
                : "next frequency";
            builder.Add($"contact {position} {frequency.ToSpoken()}");
        }

        return builder.Build();
    }

    /// <summary>Renders pushback approval.</summary>
    public static string PushbackApproval(Callsign callsign, string? pushbackDirection = null)
    {
        var builder = new PhraseBuilder(callsign);
        builder.Add("pushback approved");

        if (pushbackDirection is { Length: > 0 } direction)
        {
            builder.Add(direction);
        }

        return builder.Build();
    }

    /// <summary>Renders an outbound taxi clearance to a holding point.</summary>
    public static string TaxiClearance(Callsign callsign, Clearance clearance)
    {
        ArgumentNullException.ThrowIfNull(clearance);

        var builder = new PhraseBuilder(callsign);

        string destination = clearance.Runway is RunwayIdentifier runway
            ? $"holding point {runway.ToSpoken()}"
            : clearance.TaxiDestination ?? "holding point";

        builder.Add($"taxi {destination}");

        if (clearance.TaxiRoute.Count > 0)
        {
            builder.Add($"via {SpellTaxiways(clearance.TaxiRoute)}");
        }

        if (clearance.HoldShortOfRunway is RunwayIdentifier holdShort)
        {
            builder.Add($"hold short {holdShort.ToSpoken()}");
        }

        return builder.Build();
    }

    /// <summary>Renders an inbound taxi clearance to a stand.</summary>
    public static string TaxiToStandClearance(Callsign callsign, Clearance clearance)
    {
        ArgumentNullException.ThrowIfNull(clearance);

        var builder = new PhraseBuilder(callsign);

        string stand = clearance.TaxiDestination is { Length: > 0 } name
            ? $"stand {PhoneticAlphabet.SpellOut(name)}"
            : "stand";

        builder.Add($"taxi to {stand}");

        if (clearance.TaxiRoute.Count > 0)
        {
            builder.Add($"via {SpellTaxiways(clearance.TaxiRoute)}");
        }

        if (clearance.HoldShortOfRunway is RunwayIdentifier holdShort)
        {
            builder.Add($"hold short {holdShort.ToSpoken()}");
        }

        return builder.Build();
    }

    /// <summary>Renders a line-up-and-wait instruction.</summary>
    public static string LineUpAndWait(Callsign callsign, RunwayIdentifier runway)
    {
        var builder = new PhraseBuilder(callsign);
        builder.Add(runway.ToSpoken());
        builder.Add("line up and wait");
        return builder.Build();
    }

    /// <summary>Renders a takeoff clearance.</summary>
    public static string TakeoffClearance(
        Callsign callsign,
        RunwayIdentifier runway,
        Heading? windDirection,
        int? windSpeedKnots,
        Heading? initialHeading)
    {
        var builder = new PhraseBuilder(callsign);

        // Wind is only spoken when a weather source actually supplied it. Never invented.
        if (windDirection is Heading direction && windSpeedKnots is int speed)
        {
            builder.Add($"wind {direction.ToSpoken()} degrees {SpokenNumbers.DigitsIndividually(speed.ToString(System.Globalization.CultureInfo.InvariantCulture))} knots");
        }

        if (initialHeading is Heading heading)
        {
            builder.Add($"fly heading {heading.ToSpoken()}");
        }

        builder.Add(runway.ToSpoken());
        builder.Add("cleared for takeoff");

        return builder.Build();
    }

    /// <summary>Renders a landing clearance.</summary>
    public static string LandingClearance(
        Callsign callsign,
        RunwayIdentifier runway,
        Heading? windDirection,
        int? windSpeedKnots)
    {
        var builder = new PhraseBuilder(callsign);

        if (windDirection is Heading direction && windSpeedKnots is int speed)
        {
            builder.Add($"wind {direction.ToSpoken()} degrees {SpokenNumbers.DigitsIndividually(speed.ToString(System.Globalization.CultureInfo.InvariantCulture))} knots");
        }

        builder.Add(runway.ToSpoken());
        builder.Add("cleared to land");

        return builder.Build();
    }

    /// <summary>Renders an approach clearance.</summary>
    public static string ApproachClearance(Callsign callsign, string approachName, RunwayIdentifier runway)
    {
        var builder = new PhraseBuilder(callsign);
        builder.Add($"cleared {approachName} approach {runway.ToSpoken()}");
        return builder.Build();
    }

    /// <summary>Renders a frequency handoff.</summary>
    public static string Handoff(Callsign callsign, ControllerPosition toController, Frequency frequency)
    {
        var builder = new PhraseBuilder(callsign);
        builder.Add($"contact {SpokenControllerName(toController)} {frequency.ToSpoken()}");
        return builder.Build();
    }

    /// <summary>Renders acknowledgement of a check-in.</summary>
    public static string CheckInAcknowledgement(
        Callsign callsign,
        ControllerPosition controller,
        Altitude? clearedAltitude,
        bool radarContact)
    {
        var builder = new PhraseBuilder(callsign);
        builder.Add($"{SpokenControllerName(controller)}, good day");

        if (radarContact)
        {
            builder.Add("radar contact");
        }

        if (clearedAltitude is Altitude altitude)
        {
            builder.Add($"climb {altitude.ToSpoken()}");
        }

        return builder.Build();
    }

    /// <summary>Renders a climb instruction.</summary>
    public static string ClimbInstruction(Callsign callsign, Altitude altitude)
    {
        var builder = new PhraseBuilder(callsign);
        builder.Add($"climb {altitude.ToSpoken()}");
        return builder.Build();
    }

    /// <summary>Renders a descent instruction.</summary>
    public static string DescentInstruction(Callsign callsign, Altitude altitude)
    {
        var builder = new PhraseBuilder(callsign);
        builder.Add($"descend {altitude.ToSpoken()}");
        return builder.Build();
    }

    /// <summary>Renders a heading instruction.</summary>
    public static string HeadingInstruction(Callsign callsign, Heading heading)
    {
        var builder = new PhraseBuilder(callsign);
        builder.Add($"fly heading {heading.ToSpoken()}");
        return builder.Build();
    }

    /// <summary>Renders approval of a direct-to request.</summary>
    public static string DirectToApproval(Callsign callsign, string waypoint)
    {
        var builder = new PhraseBuilder(callsign);
        builder.Add($"cleared direct {PhoneticAlphabet.SpellOut(waypoint)}");
        return builder.Build();
    }

    /// <summary>Renders a go-around instruction.</summary>
    public static string GoAroundInstruction(Callsign callsign, Altitude? climbTo, Heading? heading)
    {
        var builder = new PhraseBuilder(callsign);
        builder.Add("go around");

        if (climbTo is Altitude altitude)
        {
            builder.Add($"climb {altitude.ToSpoken()}");
        }

        if (heading is Heading turn)
        {
            builder.Add($"fly heading {turn.ToSpoken()}");
        }

        return builder.Build();
    }

    /// <summary>Renders an instruction to vacate the runway.</summary>
    public static string VacateInstruction(Callsign callsign, string? exitTaxiway)
    {
        var builder = new PhraseBuilder(callsign);

        builder.Add(exitTaxiway is { Length: > 0 } exit
            ? $"vacate via {PhoneticAlphabet.SpellOut(exit)}"
            : "vacate when able");

        return builder.Build();
    }

    /// <summary>Renders a plain acknowledgement.</summary>
    public static string Acknowledgement(Callsign callsign) =>
        new PhraseBuilder(callsign).Add("roger").Build();

    /// <summary>Renders a "say again" request.</summary>
    public static string SayAgain(Callsign? callsign) => callsign is null
        ? "Station calling, say again."
        : new PhraseBuilder(callsign).Add("say again").Build();

    /// <summary>Renders a refusal with a plain-language reason.</summary>
    public static string Unable(Callsign? callsign, string reason)
    {
        if (callsign is null)
        {
            return $"Unable, {reason}.";
        }

        return new PhraseBuilder(callsign).Add("unable").Add(reason).Build();
    }

    /// <summary>Renders a radio check reply.</summary>
    public static string RadioCheckReply(Callsign callsign, ControllerPosition controller)
    {
        var builder = new PhraseBuilder(callsign);
        builder.Add($"{SpokenControllerName(controller)}, reading you five");
        return builder.Build();
    }

    /// <summary>
    /// Renders acknowledgement of a declared emergency.
    /// </summary>
    /// <remarks>
    /// Deliberately measured and non-committal. StormATC is entertainment software: it must not
    /// imply that any real assistance, coordination, or emergency service is being provided.
    /// </remarks>
    public static string EmergencyAcknowledgement(Callsign callsign, bool isMayday)
    {
        var builder = new PhraseBuilder(callsign);
        builder.Add(isMayday ? "mayday acknowledged" : "pan pan acknowledged");
        builder.Add("say intentions");
        return builder.Build();
    }

    /// <summary>Renders an aerodrome ICAO code phonetically: <c>STMD</c> becomes the four letters.</summary>
    public static string SpellIcao(string icao) => PhoneticAlphabet.SpellOut(icao);

    /// <summary>Renders the name of a departure or arrival procedure: <c>PILU2F</c> becomes "PILU two Foxtrot".</summary>
    public static string SpeakProcedure(string procedure)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        return ProcedureNames.TrySplit(procedure, out string word, out string rest)
            ? $"{word} {PhoneticAlphabet.SpellOut(rest)}"
            : procedure;
    }

    /// <summary>Renders a taxiway list as spoken: <c>["A","C"]</c> becomes "Alfa, Charlie".</summary>
    public static string SpellTaxiways(IReadOnlyList<string> taxiways)
    {
        ArgumentNullException.ThrowIfNull(taxiways);

        var parts = new List<string>(taxiways.Count);
        foreach (string taxiway in taxiways)
        {
            // Letter and alphanumeric designators are spelled phonetically, including identifiers
            // such as A6 and A10. Named surfaces such as "apron" and "inner" are spoken as words.
            bool isDesignator =
                taxiway.Length == 1
                || taxiway.Any(char.IsDigit)
                    && taxiway.All(char.IsLetterOrDigit);
            parts.Add(isDesignator ? PhoneticAlphabet.SpellOut(taxiway) : taxiway);
        }

        return string.Join(", ", parts);
    }

    /// <summary>The name a controller position is addressed by on the radio.</summary>
    public static string SpokenControllerName(ControllerPosition position) => position switch
    {
        ControllerPosition.Atis => "ATIS",
        ControllerPosition.ClearanceDelivery => "Delivery",
        ControllerPosition.Ground => "Ground",
        ControllerPosition.Tower => "Tower",
        ControllerPosition.Departure => "Departure",
        ControllerPosition.Center => "Center",
        ControllerPosition.Approach => "Approach",
        _ => "Controller",
    };

    /// <summary>
    /// Builds the display form of a controller transmission — abbreviated, uppercase callsign,
    /// numerals rather than spoken words. Shown on screen alongside the spoken version.
    /// </summary>
    public static string ToDisplayForm(string spokenText, Callsign callsign)
    {
        ArgumentNullException.ThrowIfNull(spokenText);
        ArgumentNullException.ThrowIfNull(callsign);

        // Replace the spoken callsign with the written one; leave the rest intact. A fuller
        // spoken-to-written transformation is not worth the ambiguity it would introduce.
        string spokenCallsign = callsign.ToSpoken();
        return spokenText.Replace(spokenCallsign, callsign.Normalized, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Assembles a transmission from a callsign and comma-separated phrases, then applies the
    /// sentence casing and terminal full stop consistently.
    /// </summary>
    private sealed class PhraseBuilder(Callsign callsign)
    {
        private readonly List<string> _parts = [];

        public PhraseBuilder Add(string phrase)
        {
            if (!string.IsNullOrWhiteSpace(phrase))
            {
                _parts.Add(phrase.Trim());
            }

            return this;
        }

        public string Build()
        {
            var builder = new StringBuilder();
            builder.Append(callsign.ToSpoken());

            foreach (string part in _parts)
            {
                builder.Append(", ").Append(part);
            }

            builder.Append('.');
            return builder.ToString();
        }
    }
}
