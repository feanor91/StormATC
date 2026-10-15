using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Tests;

/// <summary>
/// The wording variants of the French profile: they change courtesy words only, and every
/// variant stays a complete, correct transmission.
/// </summary>
public class FrenchVariantTests
{
    private static readonly Callsign Storm = Callsign.Create("STORM123");

    private sealed class FixedPicker(int index) : IVariantPicker
    {
        public int Pick(int count) => Math.Min(index, count - 1);
    }

    private static T WithVariant<T>(int index, Func<FrenchAtcPhraseology, T> speak)
    {
        FrenchAtcPhraseology phraseology = FrenchAtcPhraseology.Instance;
        IVariantPicker previous = phraseology.VariantPicker;
        phraseology.VariantPicker = new FixedPicker(index);
        try
        {
            return speak(phraseology);
        }
        finally
        {
            phraseology.VariantPicker = previous;
        }
    }

    [Fact]
    public void The_first_variant_is_the_reference_wording()
    {
        Assert.Equal("Reçu.", FrenchAtcPhraseology.Instance.RogerPlain);
        Assert.EndsWith(", reçu.", FrenchAtcPhraseology.Instance.Acknowledgement(Storm));
        Assert.EndsWith("au revoir.", FrenchAtcPhraseology.Instance.Handoff(
            Storm, ControllerPosition.Tower, Frequency.FromMegahertz(118.700m)));
    }

    [Fact]
    public void Variants_differ_but_keep_the_message()
    {
        string first = WithVariant(0, fr => fr.Acknowledgement(Storm));
        string second = WithVariant(1, fr => fr.Acknowledgement(Storm));

        Assert.NotEqual(first, second);
        Assert.Contains("reçu", second);
        Assert.StartsWith(FrenchAtcPhraseology.Instance.SpeakCallsign(Storm), second);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_handoff_always_names_the_controller_and_frequency(int variant)
    {
        string handoff = WithVariant(variant, fr =>
            fr.Handoff(Storm, ControllerPosition.Tower, Frequency.FromMegahertz(118.700m)));

        Assert.True(WithVariant(variant, fr => fr.MentionsContact(handoff, ControllerPosition.Tower)));
        Assert.Contains(FrenchAtcPhraseology.Instance.SpeakFrequency(Frequency.FromMegahertz(118.700m)), handoff);
        Assert.EndsWith(".", handoff);
        Assert.DoesNotContain(", ,", handoff);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_correct_readback_still_says_correct_and_passes_the_next_frequency(int variant)
    {
        string text = WithVariant(variant, fr => fr.ReadbackCorrect(
            Storm, Frequency.FromMegahertz(121.800m), ControllerPosition.Ground));

        Assert.Contains("correct", text);
        Assert.Contains("contactez Sol", text);
    }

    [Fact]
    public void Operational_instructions_never_vary()
    {
        RunwayIdentifier runway = RunwayIdentifier.Create("27L");

        string[] all = [.. Enumerable.Range(0, 3).Select(variant => WithVariant(variant, fr =>
            fr.TakeoffClearance(Storm, runway, null, null, null)))];

        Assert.Single(all.Distinct());
        Assert.Contains("autorisé décollage", all[0]);
    }

    [Fact]
    public void The_random_picker_stays_within_range()
    {
        for (int i = 0; i < 200; i++)
        {
            int pick = RandomVariantPicker.Instance.Pick(3);
            Assert.InRange(pick, 0, 2);
        }

        Assert.Equal(0, RandomVariantPicker.Instance.Pick(1));
    }
}
