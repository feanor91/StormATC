using StormATC.Application.Intent;

namespace StormATC.Application.Tests;

/// <summary>
/// Wording taken from the French phraseology manuals (ACTMP, IVAO, Pilotez) that the pilot may
/// use and the parser must understand.
/// </summary>
public class FrenchOfficialWordingTests
{
    [Theory]
    [InlineData("Piste 15, autorisé option", "cleared to option")]
    [InlineData("Finale piste 15, pour une option", "leg runway 15 , option")]
    [InlineData("Piste 15, autorisé passage bas", "cleared to low pass")]
    [InlineData("Demandons un passage bas", "low pass")]
    [InlineData("Finale piste 15, pour un complet", "full stop")]
    [InlineData("Finale piste 15, pour un touché", "touch and go")]
    [InlineData("Au point d'arrêt piste 33, prêt au départ", "ready for departure")]
    [InlineData("Je maintiens le point d'arrêt", "maintaining holding point")]
    [InlineData("Rogger", "roger")]
    [InlineData("Wilco", "will comply")]
    [InlineData("Affichez sept zéro trois unité au transpondeur", "7 0 3 1 squawk")]
    [InlineData("Passons DN deux mille pieds pour quitter", "leaving frequency")]
    [InlineData("J'écoute", "go ahead")]
    public void The_manual_wording_becomes_the_matching_canonical_words(string spoken, string expected) =>
        Assert.Contains(expected, FrenchTranscriptCanonicalizer.Canonicalize(spoken), StringComparison.Ordinal);

    [Fact]
    public void A_taxi_instruction_with_listening_watch_is_not_taken_for_go_ahead() =>
        Assert.DoesNotContain(
            "go ahead",
            FrenchTranscriptCanonicalizer.Canonicalize("Roulons à l'écoute et quittons au parc"),
            StringComparison.Ordinal);
}
