using System.Text.RegularExpressions;

namespace StormATC.Domain.Phraseology;

/// <summary>Splits the name of a departure or arrival procedure for speech.</summary>
/// <remarks>
/// A procedure such as <c>PILU2F</c> is the name of a fix (<c>PILU</c>, said as a word), the
/// revision number (<c>2</c>) and the letter of the variant (<c>F</c>); the number and the letter
/// are said digit by digit and phonetically: "PILU two Foxtrot".
/// </remarks>
internal static partial class ProcedureNames
{
    [GeneratedRegex(@"^(?<word>[A-Z]{2,})(?<digits>\d+)(?<letters>[A-Z]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Splits a name; false when it does not have that shape (it is then said as it is written).</summary>
    public static bool TrySplit(string name, out string word, out string rest)
    {
        Match match = Pattern().Match(name.Trim().ToUpperInvariant());
        word = match.Success ? match.Groups["word"].Value : string.Empty;
        rest = match.Success ? match.Groups["digits"].Value + match.Groups["letters"].Value : string.Empty;
        return match.Success;
    }
}
