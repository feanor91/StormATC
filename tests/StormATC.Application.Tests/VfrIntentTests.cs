using StormATC.Application.Intent;
using StormATC.Domain;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>
/// Recognising the transmissions of a VFR flight at a controlled aerodrome, in French (through
/// the canonicaliser) and in English.
/// </summary>
public class VfrIntentTests
{
    private static AtcSession Session(FlightRules rules) => AtcSession
        .Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now))
        with
    {
        FlightPlan = new FlightPlan
        {
            FlightPlanId = "vfr",
            Callsign = Callsign.Create("FGABC"),
            DepartureIcao = "LFPL",
            DestinationIcao = "LFPL",
            SourceIdentifier = "Test",
        },
        FlightRules = rules,
    };

    private static PilotIntent ParseFrench(string french, FlightRules rules = FlightRules.Vfr)
    {
        string normalized = TranscriptNormalizer.Normalize(FrenchTranscriptCanonicalizer.Canonicalize(french));
        return DeterministicPilotIntentParser.Parse(normalized, Session(rules));
    }

    private static PilotIntent ParseEnglish(string english, FlightRules rules = FlightRules.Vfr) =>
        DeterministicPilotIntentParser.Parse(TranscriptNormalizer.Normalize(english), Session(rules));

    // =====================================================================================
    // The phrases of the DSNA circuit chapter
    // =====================================================================================

    [Theory]
    [InlineData("Lognes Tour, F-GABC, en vent arrière main gauche piste zéro huit", PilotIntentType.ReportDownwind)]
    [InlineData("Lognes Tour, F-GABC, vent arrière piste zéro huit", PilotIntentType.ReportDownwind)]
    [InlineData("Lognes Tour, F-GABC, base main gauche piste zéro huit", PilotIntentType.ReportBase)]
    [InlineData("Lognes Tour, F-GABC, en base piste zéro huit", PilotIntentType.ReportBase)]
    [InlineData("Lognes Tour, F-GABC, en finale piste zéro huit", PilotIntentType.ReportFinal)]
    [InlineData("Lognes Tour, F-GABC, finale piste zéro huit", PilotIntentType.ReportFinal)]
    [InlineData("Lognes Tour, F-GABC, demande toucher", PilotIntentType.RequestTouchAndGo)]
    [InlineData("Lognes Tour, F-GABC, passe Écho", PilotIntentType.ReportAtPoint)]
    [InlineData("Lognes Tour, F-GABC, à la verticale d'Écho", PilotIntentType.ReportAtPoint)]
    [InlineData("F-GABC, sortie de circuit, je quitte la fréquence", PilotIntentType.LeaveFrequency)]
    public void French_circuit_reports_are_recognised(string french, PilotIntentType expected) =>
        Assert.Equal(expected, ParseFrench(french).Type);

    [Theory]
    [InlineData("Lognes Tour, F-GABC, downwind left hand runway 08", PilotIntentType.ReportDownwind)]
    [InlineData("Lognes Tower, F-GABC, base left hand runway 08", PilotIntentType.ReportBase)]
    [InlineData("Lognes Tower, F-GABC, final runway 08", PilotIntentType.ReportFinal)]
    [InlineData("Lognes Tower, F-GABC, request touch and go", PilotIntentType.RequestTouchAndGo)]
    [InlineData("Lognes Tower, F-GABC, passing echo", PilotIntentType.ReportAtPoint)]
    [InlineData("F-GABC, leaving the circuit and frequency", PilotIntentType.LeaveFrequency)]
    public void English_circuit_reports_are_recognised(string english, PilotIntentType expected) =>
        Assert.Equal(expected, ParseEnglish(english).Type);

    // =====================================================================================
    // The arrival call and what it carries
    // =====================================================================================

    [Fact]
    public void A_French_arrival_call_carries_the_point_the_letter_and_touch_and_go()
    {
        PilotIntent intent = ParseFrench(
            "Lognes Tour, bonjour, F-GABC, PA 28, VFR de Melun pour un toucher, "
            + "mille deux cents pieds, estimé Écho à zéro cinq, information India, "
            + "demande consignes d'intégration",
            FlightRules.Ifr);

        Assert.Equal(PilotIntentType.VfrArrivalCall, intent.Type);
        Assert.True(intent.Entities.DeclaresVfr);
        Assert.True(intent.Entities.TouchAndGo);
        Assert.Equal("E", intent.Entities.Waypoint);
        Assert.Equal("I", intent.Entities.InformationLetter);
    }

    [Fact]
    public void An_English_arrival_call_is_recognised_from_an_ifr_session_because_it_says_vfr()
    {
        PilotIntent intent = ParseEnglish(
            "Lognes Tower, F-GABC, VFR from Melun for touch and go, 1200 feet, estimate echo, information india, request joining instructions",
            FlightRules.Ifr);

        Assert.Equal(PilotIntentType.VfrArrivalCall, intent.Type);
        Assert.Equal("E", intent.Entities.Waypoint);
        Assert.Equal("I", intent.Entities.InformationLetter);
    }

    // =====================================================================================
    // Readbacks and the entities of a report
    // =====================================================================================

    [Theory]
    [InlineData("Je rappelle Écho, F-GABC")]
    [InlineData("Je rappelle vent arrière main gauche piste zéro huit, F-GABC")]
    [InlineData("J'entre vent arrière main gauche piste zéro huit, F-GABC")]
    [InlineData("Je rappelle finale piste zéro huit, F-GABC")]
    public void Repeating_a_circuit_instruction_is_not_a_report(string french) =>
        Assert.Equal(PilotIntentType.PilotAcknowledgement, ParseFrench(french).Type);

    [Fact]
    public void A_downwind_report_names_the_hand_and_the_runway()
    {
        PilotIntent intent = ParseFrench("Lognes Tour, F-GABC, vent arrière main droite piste deux six");

        Assert.Equal(PilotIntentType.ReportDownwind, intent.Type);
        Assert.Equal(CircuitSide.Right, intent.Entities.CircuitSide);
        Assert.Equal(RunwayIdentifier.Create("26"), intent.Entities.Runway);
    }

    [Fact]
    public void A_local_flight_taxi_request_declares_vfr_and_stays_a_taxi_request()
    {
        PilotIntent intent = ParseFrench(
            "Lognes Sol, F-GABC, DR 400, parking Alpha deux, information India, vol local, demande roulage",
            FlightRules.Ifr);

        Assert.Equal(PilotIntentType.RequestTaxi, intent.Type);
        Assert.True(intent.Entities.DeclaresVfr);
        Assert.Equal("I", intent.Entities.InformationLetter);
    }

    [Fact]
    public void A_taxi_request_for_circuits_is_not_mistaken_for_an_arrival_call()
    {
        PilotIntent intent = ParseEnglish(
            "Lognes Ground, F-GABC, VFR local, request taxi for touch and go circuits",
            FlightRules.Ifr);

        Assert.Equal(PilotIntentType.RequestTaxi, intent.Type);
    }

    // =====================================================================================
    // IFR is untouched
    // =====================================================================================

    [Theory]
    [InlineData("Storm 123, on final runway 18, established ILS", PilotIntentType.ReportEstablished)]
    [InlineData("Storm 123, passing 2000 feet", PilotIntentType.CheckIn)]
    [InlineData("Storm 123, leaving 3000 feet", PilotIntentType.AltitudeReadback)]
    public void An_ifr_flight_keeps_the_instrument_rules(string english, PilotIntentType expected) =>
        Assert.Equal(expected, ParseEnglish(english, FlightRules.Ifr).Type);
}
