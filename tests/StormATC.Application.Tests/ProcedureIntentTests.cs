using StormATC.Application.Intent;
using StormATC.Domain;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>The everyday requests of the phraseology manual, as a French pilot says them.</summary>
public class ProcedureIntentTests
{
    private static AtcSession Session(FlightRules rules = FlightRules.Vfr, bool transit = false) => AtcSession
        .Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now))
        with
    {
        FlightRules = rules,
        VfrTransit = transit,
        Language = PhraseologyLanguage.French,
        FlightPlan = new FlightPlan
        {
            FlightPlanId = "p",
            Callsign = Callsign.Create("FHAST"),
            DepartureIcao = "LFPL",
            DestinationIcao = "LFPN",
            SourceIdentifier = "Test",
        },
    };

    private static PilotIntent Parse(string french, FlightRules rules = FlightRules.Vfr, bool transit = false)
    {
        string normalized = TranscriptNormalizer.Normalize(FrenchTranscriptCanonicalizer.Canonicalize(french));
        return DeterministicPilotIntentParser.Parse(normalized, Session(rules, transit));
    }

    [Theory]
    [InlineData("Sol, Fox-trot Hôtel Alpha Sierra Tango, demandons la remontée de piste zéro sept", PilotIntentType.RequestBacktrack)]
    [InlineData("Tour, Hôtel Alpha Sierra Tango, demande l'heure exacte", PilotIntentType.RequestTimeCheck)]
    [InlineData("Fox-trot Hôtel Alpha Sierra Tango, ident", PilotIntentType.ReportIdent)]
    [InlineData("Paris Information, Hôtel Alpha Sierra Tango, parlez plus lentement s'il vous plaît", PilotIntentType.SpeakSlowly)]
    [InlineData("Lognes Tour, Hôtel Alpha Sierra Tango, demande VFR spécial", PilotIntentType.RequestSpecialVfr)]
    [InlineData("Lognes Tour, Hôtel Alpha Sierra Tango, demande transit par la verticale", PilotIntentType.RequestTransit)]
    [InlineData("Tour, Hôtel Alpha Sierra Tango, demande un 360 par la droite", PilotIntentType.RequestOrbit)]
    [InlineData("Tour, Hôtel Alpha Sierra Tango, demande d'allonger la vent arrière", PilotIntentType.RequestExtendDownwind)]
    [InlineData("Hôtel Alpha Sierra Tango, demande la clôture du plan de vol", PilotIntentType.RequestFlightPlanClosure)]
    [InlineData("Hôtel Alpha Sierra Tango, je surveille le trafic", PilotIntentType.ReportTrafficLookout)]
    [InlineData("Hôtel Alpha Sierra Tango, trafic en vue", PilotIntentType.ReportTrafficLookout)]
    [InlineData("Hôtel Alpha Sierra Tango, pas de contact visuel", PilotIntentType.ReportTrafficLookout)]
    public void A_french_request_of_the_manual_is_recognised(string french, PilotIntentType expected) =>
        Assert.Equal(expected, Parse(french).Type);

    [Fact]
    public void Cancelling_ifr_is_recognised()
    {
        Assert.Equal(PilotIntentType.CancelIfr, Parse("Contrôle, Hôtel Alpha Sierra Tango, annulons l'IFR", FlightRules.Ifr).Type);
        Assert.Equal(PilotIntentType.CancelIfr, Parse("Contrôle, Hôtel Alpha Sierra Tango, annulation du plan de vol IFR", FlightRules.Ifr).Type);
    }

    [Fact]
    public void An_orbit_to_the_right_carries_its_side()
    {
        PilotIntent intent = Parse("Tour, Hôtel Alpha Sierra Tango, demande un 360 par la droite");

        Assert.Equal(CircuitSide.Right, intent.Entities.CircuitSide);
    }

    [Fact]
    public void A_ready_for_departure_that_says_immediate_carries_it()
    {
        PilotIntent intent = Parse("Tour, Hôtel Alpha Sierra Tango, prêt immédiat", FlightRules.Ifr);

        Assert.Equal(PilotIntentType.ReadyForDeparture, intent.Type);
        Assert.True(intent.Entities.ReadyImmediate);
    }

    [Fact]
    public void Overhead_on_a_transit_is_a_report_at_the_point_and_not_a_circuit_call()
    {
        PilotIntent intent = Parse("Tour, Hôtel Alpha Sierra Tango, à la verticale", transit: true);

        Assert.Equal(PilotIntentType.ReportAtPoint, intent.Type);
    }

    [Theory]
    [InlineData("Hôtel Alpha Sierra Tango, cap trois cent soixante")]
    [InlineData("Hôtel Alpha Sierra Tango, identifié radar, merci")]
    public void Ordinary_words_are_not_taken_for_a_request(string french)
    {
        PilotIntentType type = Parse(french, FlightRules.Ifr).Type;

        Assert.NotEqual(PilotIntentType.RequestOrbit, type);
        Assert.NotEqual(PilotIntentType.ReportIdent, type);
    }
}
