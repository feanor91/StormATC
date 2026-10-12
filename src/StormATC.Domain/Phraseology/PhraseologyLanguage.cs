namespace StormATC.Domain.Phraseology;

/// <summary>
/// The language a controller speaks on the radio, and the phraseology profile that goes with it.
/// </summary>
/// <remarks>
/// The language changes the words only. Every decision — which clearance is available, which
/// values it carries, whether a readback is correct — is made by the engine before any wording
/// is chosen, so switching language can never change what is permitted.
/// </remarks>
public enum PhraseologyLanguage
{
    /// <summary>ICAO-style English, the default.</summary>
    English = 0,

    /// <summary>French, following the DSNA phraseology manual for general air traffic.</summary>
    French,
}

/// <summary>Conversions between <see cref="PhraseologyLanguage"/> and configuration codes.</summary>
public static class PhraseologyLanguages
{
    /// <summary>
    /// Parses a configuration value such as <c>en</c>, <c>fr</c>, <c>French</c> or
    /// <c>fr-FR</c>. Anything unrecognised is English, so a typo can never silence the radio.
    /// </summary>
    public static PhraseologyLanguage Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PhraseologyLanguage.English;
        }

        string trimmed = value.Trim();
        return trimmed.StartsWith("fr", StringComparison.OrdinalIgnoreCase)
            ? PhraseologyLanguage.French
            : PhraseologyLanguage.English;
    }

    /// <summary>The two-letter code used by speech providers: <c>en</c> or <c>fr</c>.</summary>
    public static string ToCode(this PhraseologyLanguage language) => language switch
    {
        PhraseologyLanguage.French => "fr",
        _ => "en",
    };
}

/// <summary>Finds the phraseology profile for a language.</summary>
public static class AtcPhraseologies
{
    /// <summary>The profile that speaks <paramref name="language"/>.</summary>
    public static IAtcPhraseology For(PhraseologyLanguage language) => language switch
    {
        PhraseologyLanguage.French => FrenchAtcPhraseology.Instance,
        _ => EnglishAtcPhraseology.Instance,
    };
}
