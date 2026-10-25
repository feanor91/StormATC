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

    private static PilotIntent Parse(string french, FlightRules rules = FlightRules.Vfr, bool transit = false, bool airborne = false)
    {
        string normalized = TranscriptNormalizer.Normalize(FrenchTranscriptCanonicalizer.Canonicalize(french));
        AtcSession session = Session(rules, transit);
        if (airborne)
        {
            session = session with { AircraftState = session.AircraftState with { IsOnGround = false } };
        }

        return DeterministicPilotIntentParser.Parse(normalized, session);
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

    [Theory]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, demande une nouvelle route via KILOX", PilotIntentType.RequestRouteChange)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, demande vitesse libre", PilotIntentType.RequestSpeed)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, demande une attente sur KILOX", PilotIntentType.RequestHold)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, prêt à quitter l'attente", PilotIntentType.ReportReadyToLeaveHold)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, RVSM approuvé", PilotIntentType.ReportRvsm)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, négatif RVSM", PilotIntentType.ReportRvsm)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, contrôle SELCAL", PilotIntentType.RequestSelcalCheck)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, logon CPDLC", PilotIntentType.RequestCpdlcLogon)]
    [InlineData("Sol, Hôtel Alpha Sierra Tango, demandons le tractage", PilotIntentType.RequestTow)]
    [InlineData("Tour, véhicule de piste, demande à pénétrer sur la piste pour inspection", PilotIntentType.RequestRunwayInspection)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, position KILOX niveau cent vingt", PilotIntentType.ReportPosition)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, demande un décalage de deux nautiques à droite", PilotIntentType.RequestOffset)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, demandons une route parallèle", PilotIntentType.RequestOffset)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, de retour sur la route", PilotIntentType.ReportBackOnRoute)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, panne CPDLC", PilotIntentType.ReportCpdlcFailure)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, CPDLC en panne", PilotIntentType.ReportCpdlcFailure)]
    [InlineData("Contrôle, Hôtel Alpha Sierra Tango, confirmez par phonie", PilotIntentType.ReportCpdlcFailure)]
    public void An_ifr_request_of_the_manual_is_recognised(string french, PilotIntentType expected) =>
        Assert.Equal(expected, Parse(french, FlightRules.Ifr, airborne: true).Type);

    [Fact]
    public void A_speed_a_fix_and_an_approach_name_are_taken_from_the_request()
    {
        Assert.Equal(250, Parse("Contrôle, Hôtel Alpha Sierra Tango, demande vitesse deux cent cinquante", FlightRules.Ifr, airborne: true).Entities.SpeedKnots);
        Assert.True(Parse("Contrôle, Hôtel Alpha Sierra Tango, demande vitesse libre", FlightRules.Ifr, airborne: true).Entities.FreeSpeed);

        PilotIntent rnp = Parse("Approche, Hôtel Alpha Sierra Tango, demande l'approche RNP piste zéro sept", FlightRules.Ifr, airborne: true);
        Assert.Equal(PilotIntentType.RequestApproach, rnp.Type);
        Assert.Equal("RNP", rnp.Entities.ApproachName);
    }

    private static PilotIntent ParseWithPending(string french, ClearanceType type, ReadbackItemType item, string value)
    {
        AtcSession session = Session(FlightRules.Ifr);
        session = session with
        {
            AircraftState = session.AircraftState with { IsOnGround = false },
            PendingReadback = new PendingReadback
            {
                ClearanceId = Guid.CreateVersion7(),
                ClearanceType = type,
                IssuedAt = Fixture.Now,
                IsMandatory = false,
                ExpectedItems = [new ExpectedReadbackItem { Type = item, ExpectedValue = value, SpokenValue = value, IsCritical = true }],
            },
        };
        string normalized = TranscriptNormalizer.Normalize(FrenchTranscriptCanonicalizer.Canonicalize(french));
        return DeterministicPilotIntentParser.Parse(normalized, session);
    }

    [Fact]
    public void A_french_readback_of_a_speed_a_hold_and_a_direct_is_a_readback_with_its_value()
    {
        PilotIntent speed = ParseWithPending(
            "Contrôle, Hôtel Alpha Sierra Tango, réduisons vitesse deux deux zéro nœuds", ClearanceType.SpeedInstruction, ReadbackItemType.Speed, "220");
        Assert.Equal(PilotIntentType.InstructionReadback, speed.Type);
        Assert.Contains(speed.ReadbackItems, item => item.Type == ReadbackItemType.Speed && item.NormalizedValue == "220");

        PilotIntent hold = ParseWithPending(
            "Approche, Hôtel Alpha Sierra Tango, attente sur KILOX", ClearanceType.HoldInstruction, ReadbackItemType.HoldFix, "KILOX");
        Assert.Equal(PilotIntentType.InstructionReadback, hold.Type);
        Assert.Contains(hold.ReadbackItems, item => item.Type == ReadbackItemType.HoldFix && item.NormalizedValue == "KILOX");

        PilotIntent direct = ParseWithPending(
            "Contrôle, Hôtel Alpha Sierra Tango, direct NOVAR", ClearanceType.DirectTo, ReadbackItemType.RouteFix, "NOVAR");
        Assert.Equal(PilotIntentType.InstructionReadback, direct.Type);
        Assert.Contains(direct.ReadbackItems, item => item.Type == ReadbackItemType.RouteFix && item.NormalizedValue == "NOVAR");
    }

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
