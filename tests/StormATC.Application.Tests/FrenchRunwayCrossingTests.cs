using StormATC.Application.Intent;
using StormATC.Domain;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>Asking to cross a runway, in French and in English, and the clearance read back.</summary>
public class FrenchRunwayCrossingTests
{
    private static AtcSession Session() =>
        AtcSession.Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now))
        with
        {
            FlightPlan = new FlightPlan
            {
                FlightPlanId = "crossing",
                Callsign = Callsign.Create("FHAST"),
                DepartureIcao = "LFPL",
                DestinationIcao = "LFPL",
                SourceIdentifier = "Test",
            },
            FlightRules = FlightRules.Vfr,
            Phase = FlightPhase.TaxiIn,
        };

    private static PilotIntentType Understand(string french)
    {
        string canonical = TranscriptNormalizer.Normalize(FrenchTranscriptCanonicalizer.Canonicalize(french));
        return DeterministicPilotIntentParser.Parse(canonical, Session()).Type;
    }

    [Theory]
    [InlineData("F-HAST, au point d'attente piste un huit, demandons à traverser")]
    [InlineData("F-HAST, demande la traversée de la piste dix-huit")]
    [InlineData("F-HAST, prêts à traverser la piste un huit")]
    [InlineData("F-HAST, demandons à traverser")]
    public void A_pilot_asking_to_cross_a_runway_in_French_is_understood(string spoken) =>
        Assert.Equal(PilotIntentType.RequestRunwayCrossing, Understand(spoken));

    [Fact]
    public void A_pilot_asking_to_cross_a_runway_in_English_is_understood() =>
        Assert.Equal(
            PilotIntentType.RequestRunwayCrossing,
            DeterministicPilotIntentParser.Parse("fhast holding short runway 18 request crossing", Session()).Type);

    [Fact]
    public void A_pilot_reading_back_a_crossing_clearance_is_not_asking_for_one() =>
        Assert.NotEqual(PilotIntentType.RequestRunwayCrossing, Understand("Traversons piste un huit, F-HAST"));

    [Theory]
    [InlineData("Alignons-nous piste un huit et attendons, F-HAST")]
    [InlineData("Alignement piste un huit, on attend, F-HAST")]
    public void Reading_back_a_line_up_and_wait_is_taken_as_its_readback(string spoken)
    {
        AtcSession session = Session() with
        {
            Phase = FlightPhase.HoldingShort,
            PendingReadback = new PendingReadback
            {
                ClearanceId = Guid.CreateVersion7(),
                ClearanceType = ClearanceType.LineUp,
                ExpectedItems =
                [
                    new ExpectedReadbackItem
                    {
                        Type = ReadbackItemType.Runway,
                        ExpectedValue = RunwayIdentifier.Create("18").ToComparisonValue(),
                        SpokenValue = "un huit",
                        IsCritical = true,
                    },
                ],
                IssuedAt = Fixture.Now,
                IsMandatory = true,
            },
        };
        string canonical = TranscriptNormalizer.Normalize(FrenchTranscriptCanonicalizer.Canonicalize(spoken));

        Assert.Equal(PilotIntentType.TakeoffReadback, DeterministicPilotIntentParser.Parse(canonical, session).Type);
    }
}
