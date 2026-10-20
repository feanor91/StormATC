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

    [Theory]
    [InlineData("Contactez Tour cent dix-huit sept cinq", "118 decimal 7 5")]
    [InlineData("Contactez Paris cent trente-deux zéro zéro", "132 decimal 0 0")]
    [InlineData("Contactez Sol cent vingt et un neuf", "121 decimal 9")]
    [InlineData("Contactez Tour cent dix-huit décimale sept", "118 decimal 7")]
    [InlineData("Piste deux sept L", "runway 2 7 left")]
    [InlineData("Piste zéro neuf R", "runway 0 9 right")]
    [InlineData("Piste zéro neuf C", "runway 0 9 center")]
    [InlineData("Piste vingt-sept gauche", "runway 27 left")]
    public void Frequencies_and_runway_sides_are_read_the_way_pilots_say_them(string spoken, string expected) =>
        Assert.Contains(expected, FrenchTranscriptCanonicalizer.Canonicalize(spoken), StringComparison.Ordinal);

    [Fact]
    public void Digits_after_a_heading_are_not_taken_for_frequency_decimals() =>
        Assert.DoesNotContain(
            "decimal",
            FrenchTranscriptCanonicalizer.Canonicalize("Cap un deux zéro, contactez le sol"),
            StringComparison.Ordinal);

    [Fact]
    public void A_taxi_instruction_with_listening_watch_is_not_taken_for_go_ahead() =>
        Assert.DoesNotContain(
            "go ahead",
            FrenchTranscriptCanonicalizer.Canonicalize("Roulons à l'écoute et quittons au parc"),
            StringComparison.Ordinal);
}
