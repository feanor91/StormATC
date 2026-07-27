using System.Text;
using System.Text.RegularExpressions;

namespace StormATC.Application.Intent;

/// <summary>
/// Cleans up a raw speech-to-text transcript before parsing.
/// </summary>
/// <remarks>
/// <para>
/// Speech-to-text output has predictable defects for aviation speech: aviation words get
/// mapped to common English homophones ("squawk" becomes "squak" or "squad"), filler words
/// creep in, and punctuation is inconsistent. Normalising here means the deterministic parser
/// downstream can use straightforward patterns instead of every rule carrying its own fuzzy
/// matching.
/// </para>
/// <para>
/// Normalisation is lossy and is never persisted over the original: the transcript page shows
/// both, so a user can see what was heard and what it was interpreted as.
/// </para>
/// </remarks>
public static partial class TranscriptNormalizer
{
    /// <summary>
    /// Aviation terms that speech-to-text commonly mangles, mapped to the canonical spelling.
    /// </summary>
    /// <remarks>
    /// Kept deliberately conservative. Every entry here is a substitution observed in practice
    /// for aviation phraseology, not a general spelling correction — over-eager substitution
    /// would corrupt correct transcripts.
    /// </remarks>
    private static readonly (string Pattern, string Replacement)[] Substitutions =
    [
        ("squak", "squawk"),
        ("squack", "squawk"),
        ("squad", "squawk"),
        ("swak", "squawk"),
        ("hold in short", "holding short"),
        ("holdin short", "holding short"),
        ("push back", "pushback"),
        ("taxy", "taxi"),
        ("taxing", "taxiing"),
        ("run way", "runway"),
        ("fight level", "flight level"),
        ("flight lever", "flight level"),
        ("ready to cop", "ready to copy"),
        ("clearence", "clearance"),
        ("clarence", "clearance"),
        ("i f r", "ifr"),
        ("v f r", "vfr"),
        ("i l s", "ils"),
        ("may day", "mayday"),
        ("pan pan", "panpan"),
        ("go round", "go around"),
        ("goaround", "go around"),
        ("wilco", "will comply"),
        ("decimal point", "decimal"),
    ];

    /// <summary>Filler words removed before parsing.</summary>
    private static readonly string[] FillerWords =
    [
        "um", "uh", "erm", "ah", "eh", "like", "you know",
    ];

    /// <summary>
    /// Normalises a transcript: lowercase, collapse whitespace, strip filler, and apply the
    /// aviation substitution table.
    /// </summary>
    public static string Normalize(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        if (transcript.Length == 0)
        {
            return string.Empty;
        }

        string working = transcript.ToLowerInvariant();

        // Strip everything except letters, digits, spaces, and the decimal point, which is
        // meaningful inside frequencies.
        var builder = new StringBuilder(working.Length);
        for (int index = 0; index < working.Length; index++)
        {
            char c = working[index];
            bool betweenDigits =
                index > 0
                && index + 1 < working.Length
                && char.IsDigit(working[index - 1])
                && char.IsDigit(working[index + 1]);

            if (char.IsLetterOrDigit(c) || c == ' ')
            {
                builder.Append(c);
            }
            else if (c == '.' && betweenDigits)
            {
                // Decimal separator in a radio frequency.
                builder.Append(c);
            }
            else if (c == ',' && betweenDigits)
            {
                // Thousands separator: "7,000" must remain the literal altitude 7000, not
                // become two unrelated digit runs "7 000".
            }
            else
            {
                // Punctuation between fields or sentences is just a word boundary.
                builder.Append(' ');
            }
        }

        working = builder.ToString();
        working = WhitespaceRegex().Replace(working, " ").Trim();

        foreach ((string pattern, string replacement) in Substitutions)
        {
            working = working.Replace(pattern, replacement, StringComparison.Ordinal);
        }

        working = RemoveFillerWords(working);

        return WhitespaceRegex().Replace(working, " ").Trim();
    }

    /// <summary>
    /// True when normalisation changed the text enough to be worth showing both forms in the
    /// transcript view.
    /// </summary>
    public static bool DiffersMeaningfully(string original, string normalized)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(normalized);

        // Compare with case and punctuation already discounted, so a purely cosmetic change
        // does not clutter the UI.
        string comparable = new(original.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray());

        comparable = WhitespaceRegex().Replace(comparable, " ").Trim();
        string comparableNormalized = new(normalized.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
        comparableNormalized = WhitespaceRegex().Replace(comparableNormalized, " ").Trim();

        return !string.Equals(comparable, comparableNormalized, StringComparison.Ordinal);
    }

    private static string RemoveFillerWords(string text)
    {
        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(tokens.Length);

        foreach (string token in tokens)
        {
            bool isFiller = false;
            foreach (string filler in FillerWords)
            {
                if (string.Equals(token, filler, StringComparison.Ordinal))
                {
                    isFiller = true;
                    break;
                }
            }

            if (!isFiller)
            {
                kept.Add(token);
            }
        }

        return string.Join(' ', kept);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
