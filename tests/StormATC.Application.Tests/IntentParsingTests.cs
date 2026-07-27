using StormATC.Application.Intent;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Intents;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>Transcript clean-up before anything tries to understand it.</summary>
public class TranscriptNormalizerTests
{
    [Theory]
    [InlineData("storm one two three request  ifr   clearance", "storm one two three request ifr clearance")]
    [InlineData("  leading and trailing  ", "leading and trailing")]
    public void Whitespace_is_collapsed(string input, string expected) =>
        Assert.Equal(expected, TranscriptNormalizer.Normalize(input));

    [Theory]
    [InlineData("squak four two seven one", "squawk")]
    [InlineData("holding shot of runway one eight", "hold")]
    public void Common_mis_transcriptions_are_repaired(string input, string expectedFragment) =>
        Assert.Contains(expectedFragment, TranscriptNormalizer.Normalize(input), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Filler_words_are_removed()
    {
        string normalized = TranscriptNormalizer.Normalize("uh storm one two three um request taxi");

        Assert.DoesNotContain(" uh ", $" {normalized} ", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("request taxi", normalized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Text_that_needs_nothing_is_left_alone()
    {
        const string clean = "storm one two three request taxi";

        Assert.Equal(clean, TranscriptNormalizer.Normalize(clean));
        Assert.False(TranscriptNormalizer.DiffersMeaningfully(clean, clean));
    }

    [Fact]
    public void An_empty_transcript_normalises_to_empty()
    {
        Assert.Equal(string.Empty, TranscriptNormalizer.Normalize("   "));
    }

    [Fact]
    public void A_thousands_separator_is_not_turned_into_two_numbers()
    {
        string normalized = TranscriptNormalizer.Normalize(
            "Runway 26 left, climb 7,000, squawk five two one seven.");

        Assert.Contains("climb 7000 squawk", normalized, StringComparison.Ordinal);
    }
}

/// <summary>
/// The deterministic intent parser.
/// </summary>
/// <remarks>
/// This runs on every transmission and the language model runs on almost none, so its accuracy
/// decides how the application feels. Rule ordering is load-bearing and is pinned here.
/// </remarks>
public class DeterministicPilotIntentParserTests
{
    [Theory]
    [InlineData("storm one two three request ifr clearance", PilotIntentType.RequestIFRClearance)]
    [InlineData("storm one two three request pushback", PilotIntentType.RequestPushback)]
    [InlineData("sorry what s what s the frequency", PilotIntentType.RequestFrequency)]
    [InlineData("ground frequency please", PilotIntentType.RequestFrequency)]
    [InlineData("roger storm one two three", PilotIntentType.PilotAcknowledgement)]
    [InlineData("storm one two three thank you", PilotIntentType.PilotAcknowledgement)]
    [InlineData("storm one two three ready for taxi", PilotIntentType.RequestTaxi)]
    [InlineData("storm one two three holding short runway one eight", PilotIntentType.ReportHoldingShort)]
    [InlineData("storm one two three ready for departure", PilotIntentType.ReadyForDeparture)]
    [InlineData("storm one two three request descent", PilotIntentType.RequestDescent)]
    [InlineData("storm one two three runway vacated", PilotIntentType.ReportRunwayVacated)]
    [InlineData("storm one two three taxi to stand", PilotIntentType.RequestTaxiToStand)]
    public void Standard_phraseology_is_recognised(string transcript, PilotIntentType expected)
    {
        PilotIntent intent = DeterministicPilotIntentParser.Parse(transcript, Fixture.Session());

        Assert.Equal(expected, intent.Type);
        Assert.Equal(IntentParserSource.Deterministic, intent.Source);
    }

    [Theory]
    [InlineData("mayday mayday mayday storm one two three engine failure", PilotIntentType.Mayday)]
    [InlineData("pan pan pan pan storm one two three medical", PilotIntentType.PanPan)]
    public void Emergencies_outrank_everything_else(string transcript, PilotIntentType expected)
    {
        PilotIntent intent = DeterministicPilotIntentParser.Parse(transcript, Fixture.Session());

        Assert.Equal(expected, intent.Type);
        Assert.True(intent.IsEmergency);
    }

    [Fact]
    public void A_go_around_is_not_read_as_an_approach_report()
    {
        // Rule ordering: "going around" contains nothing approach-like, but a transmission
        // during an approach easily could. This is the ordering that matters most.
        PilotIntent intent = DeterministicPilotIntentParser.Parse(
            "storm one two three going around", Fixture.Session());

        Assert.Equal(PilotIntentType.GoAround, intent.Type);
    }

    [Fact]
    public void Ready_for_departure_is_not_read_as_a_taxi_request()
    {
        PilotIntent intent = DeterministicPilotIntentParser.Parse(
            "storm one two three ready for departure", Fixture.Session());

        Assert.Equal(PilotIntentType.ReadyForDeparture, intent.Type);
    }

    [Fact]
    public void The_callsign_is_extracted()
    {
        PilotIntent intent = DeterministicPilotIntentParser.Parse(
            "storm one two three request taxi", Fixture.Session());

        Assert.Equal(Callsign.Create("STORM123"), intent.Callsign);
    }

    [Fact]
    public void Nothing_recognisable_produces_a_low_confidence_unknown()
    {
        PilotIntent intent = DeterministicPilotIntentParser.Parse(
            "the weather is lovely today", Fixture.Session());

        Assert.Equal(PilotIntentType.Unknown, intent.Type);
        Assert.True(intent.Confidence < 0.5);
    }

    // --- Entity extraction ---------------------------------------------------------------

    [Fact]
    public void A_squawk_is_extracted()
    {
        PilotIntent intent = DeterministicPilotIntentParser.Parse(
            "squawk four two seven one storm one two three", Fixture.Session());

        Assert.Equal(SquawkCode.Create("4271"), intent.Entities.Squawk);
    }

    [Fact]
    public void Altitude_and_squawk_are_both_extracted_from_a_full_numeric_readback()
    {
        string transcript = TranscriptNormalizer.Normalize(
            "Clear to Echo November Victor Alfa. FRAN1M departure, Runway 26 left, "
            + "climb 7,000, squawk five two one seven, Sierra Tango one two three.");

        PilotIntent intent = DeterministicPilotIntentParser.Parse(
            transcript,
            Fixture.SessionAwaitingClearanceReadback());

        Assert.Equal(Altitude.FromFeet(7_000), intent.Entities.Altitude);
        Assert.Equal(SquawkCode.Create("5217"), intent.Entities.Squawk);
        Assert.Equal(RunwayIdentifier.Create("26L"), intent.Entities.Runway);
    }

    [Fact]
    public void A_heading_is_only_extracted_after_the_word_heading()
    {
        PilotIntent withHeading = DeterministicPilotIntentParser.Parse(
            "storm one two three heading two seven zero", Fixture.Session());

        PilotIntent withoutHeading = DeterministicPilotIntentParser.Parse(
            "storm one two three squawk four two seven one", Fixture.Session());

        Assert.Equal(Heading.FromDegrees(270), withHeading.Entities.Heading);
        Assert.Null(withoutHeading.Entities.Heading);
    }

    [Fact]
    public void Taxiways_are_read_only_after_via_and_stop_at_hold()
    {
        PilotIntent intent = DeterministicPilotIntentParser.Parse(
            "taxi via alfa charlie hold short runway one eight storm one two three",
            Fixture.Session());

        Assert.Equal(["A", "C"], intent.Entities.Taxiways);
    }

    [Fact]
    public void A_hold_short_instruction_is_recognised()
    {
        PilotIntent intent = DeterministicPilotIntentParser.Parse(
            "taxi via alfa hold short runway one eight storm one two three",
            Fixture.Session());

        Assert.True(intent.Entities.MentionsHoldShort);
        Assert.Equal(RunwayIdentifier.Create("18"), intent.Entities.HoldShortRunway);
    }

    // --- Session context -------------------------------------------------------------------

    [Fact]
    public void An_ambiguous_readback_is_resolved_by_the_pending_clearance()
    {
        // "Runway one eight, seven thousand, four two seven one" is not obviously anything
        // until you know a clearance readback is outstanding.
        AtcSession withPending = Fixture.SessionAwaitingClearanceReadback();

        PilotIntent intent = DeterministicPilotIntentParser.Parse(
            "runway one eight seven thousand four two seven one storm one two three",
            withPending);

        Assert.Equal(PilotIntentType.ClearanceReadback, intent.Type);
    }

    [Fact]
    public void The_same_words_are_not_a_readback_with_nothing_outstanding()
    {
        PilotIntent intent = DeterministicPilotIntentParser.Parse(
            "runway one eight seven thousand four two seven one storm one two three",
            Fixture.Session());

        Assert.NotEqual(PilotIntentType.ClearanceReadback, intent.Type);
    }
}

/// <summary>The escalation policy between the local parser and the language model.</summary>
public class EscalatingPilotIntentParserTests
{
    [Fact]
    public async Task A_confident_local_result_never_reaches_the_model()
    {
        var model = new RecordingLanguageModelParser();
        var parser = new EscalatingPilotIntentParser(new DeterministicPilotIntentParser(), model);

        PilotIntent intent = await parser.ParseAsync(
            "storm one two three request ifr clearance", Fixture.Session(), CancellationToken.None);

        Assert.Equal(PilotIntentType.RequestIFRClearance, intent.Type);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task An_unsure_local_result_escalates()
    {
        var model = new RecordingLanguageModelParser
        {
            Result = new PilotIntent
            {
                Type = PilotIntentType.RequestTaxi,
                Confidence = 0.9,
                Source = IntentParserSource.LanguageModel,
                SourceTranscript = "we would like to head out to the runway now",
            },
        };

        var parser = new EscalatingPilotIntentParser(new DeterministicPilotIntentParser(), model);

        PilotIntent intent = await parser.ParseAsync(
            "we would like to head out to the runway now", Fixture.Session(), CancellationToken.None);

        Assert.Equal(1, model.CallCount);
        Assert.Equal(PilotIntentType.RequestTaxi, intent.Type);
        Assert.Equal(IntentParserSource.LanguageModel, intent.Source);
    }

    [Fact]
    public async Task An_unavailable_model_leaves_the_deterministic_result_standing()
    {
        var model = new RecordingLanguageModelParser { Available = false };
        var parser = new EscalatingPilotIntentParser(new DeterministicPilotIntentParser(), model);

        PilotIntent intent = await parser.ParseAsync(
            "the weather is lovely today", Fixture.Session(), CancellationToken.None);

        Assert.Equal(0, model.CallCount);
        Assert.Equal(PilotIntentType.Unknown, intent.Type);
    }

    [Fact]
    public async Task A_model_that_fails_leaves_the_deterministic_result_standing()
    {
        var model = new RecordingLanguageModelParser { Result = null };
        var parser = new EscalatingPilotIntentParser(new DeterministicPilotIntentParser(), model);

        PilotIntent intent = await parser.ParseAsync(
            "the weather is lovely today", Fixture.Session(), CancellationToken.None);

        Assert.Equal(1, model.CallCount);
        Assert.Equal(PilotIntentType.Unknown, intent.Type);

        // The source records that a model was tried and rejected, so the Transcript page can
        // show why the answer is the degraded one.
        Assert.Equal(IntentParserSource.DeterministicAfterModelRejection, intent.Source);
    }

    [Fact]
    public async Task No_model_at_all_is_a_supported_configuration()
    {
        var parser = new EscalatingPilotIntentParser(new DeterministicPilotIntentParser(), languageModelParser: null);

        PilotIntent intent = await parser.ParseAsync(
            "storm one two three request taxi", Fixture.Session(), CancellationToken.None);

        Assert.Equal(PilotIntentType.RequestTaxi, intent.Type);
    }

    /// <summary>A stand-in model parser that records whether it was consulted.</summary>
    private sealed class RecordingLanguageModelParser : ILanguageModelIntentParser
    {
        public bool Available { get; init; } = true;

        public PilotIntent? Result { get; init; }

        public int CallCount { get; private set; }

        public bool IsAvailable => Available;

        public Task<PilotIntent?> ParseAsync(
            string normalizedTranscript,
            AtcSession session,
            PilotIntent deterministicHypothesis,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }
}

/// <summary>Shared sessions for the application-layer tests.</summary>
internal static class Fixture
{
    internal static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    internal static AtcSession Session() => AtcSession
        .Create(Guid.CreateVersion7(), Now, SessionMode.Mock, AircraftState.Unknown(Now))
        with
    {
        FlightPlan = new Domain.FlightPlanning.FlightPlan
        {
            FlightPlanId = "test",
            Callsign = Callsign.Create("STORM123"),
            DepartureIcao = "STMR",
            DestinationIcao = "STMD",
            DepartureRunway = RunwayIdentifier.Create("18"),
            CruiseAltitude = Altitude.FromFlightLevel(120),
            SourceIdentifier = "Test",
        },
        CurrentController = ControllerPosition.ClearanceDelivery,
        CurrentFrequency = Frequency.FromMegahertz(121.800m),
    };

    /// <summary>A session with an IFR clearance readback outstanding.</summary>
    internal static AtcSession SessionAwaitingClearanceReadback() => Session().WithPendingReadback(
        new PendingReadback
        {
            ClearanceId = Guid.CreateVersion7(),
            ClearanceType = ClearanceType.IfrClearance,
            IssuedAt = Now,
            ExpectedItems =
            [
                new ExpectedReadbackItem
                {
                    Type = ReadbackItemType.Runway,
                    ExpectedValue = "18",
                    SpokenValue = "runway one eight",
                },
                new ExpectedReadbackItem
                {
                    Type = ReadbackItemType.InitialAltitude,
                    ExpectedValue = "7000",
                    SpokenValue = "climb initially seven thousand",
                },
                new ExpectedReadbackItem
                {
                    Type = ReadbackItemType.Squawk,
                    ExpectedValue = "4271",
                    SpokenValue = "squawk four two seven one",
                },
            ],
        });
}
