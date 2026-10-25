using System.Diagnostics.CodeAnalysis;
using System.Text;
using StormATC.Domain.Phraseology;

namespace StormATC.Domain.ValueObjects;

/// <summary>
/// An aircraft callsign, stored in a normalised comparison form alongside the text the user
/// or flight plan originally supplied.
/// </summary>
/// <remarks>
/// <para>
/// Callsign matching is the single most error-prone comparison in the whole pipeline, because
/// speech-to-text renders the same callsign many different ways. "STORM123" may arrive as
/// "storm one two three", "storm 123", "Storm123", or "storm one twenty three". Normalisation
/// therefore strips whitespace and punctuation, uppercases, and folds spelled-out digits.
/// </para>
/// <para>
/// The <see cref="Normalized"/> form is the only value that should ever be compared.
/// </para>
/// </remarks>
public sealed class Callsign : IEquatable<Callsign>
{
    private Callsign(string original, string normalized, string? telephony, string? numericSuffix)
    {
        Original = original;
        Normalized = normalized;
        Telephony = telephony;
        NumericSuffix = numericSuffix;
    }

    /// <summary>The callsign exactly as supplied.</summary>
    public string Original { get; }

    /// <summary>Uppercase, alphanumeric-only comparison form. For example <c>STORM123</c>.</summary>
    public string Normalized { get; }

    /// <summary>
    /// The alphabetic prefix, where one exists — the airline telephony designator part.
    /// <c>STORM</c> for <c>STORM123</c>.
    /// </summary>
    public string? Telephony { get; }

    /// <summary>The trailing flight number, where one exists. <c>123</c> for <c>STORM123</c>.</summary>
    public string? NumericSuffix { get; }

    /// <summary>
    /// Creates a callsign, throwing when the input cannot yield any usable identifier.
    /// </summary>
    /// <exception cref="ArgumentException">The value is empty or has no alphanumeric content.</exception>
    public static Callsign Create(string value)
    {
        if (!TryCreate(value, out Callsign? callsign))
        {
            throw new ArgumentException(
                $"'{value}' is not a usable callsign. A callsign needs at least one letter or digit.",
                nameof(value));
        }

        return callsign;
    }

    /// <summary>Attempts to create a callsign. Returns false rather than throwing.</summary>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out Callsign? callsign)
    {
        callsign = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = Normalize(value);
        if (normalized.Length == 0)
        {
            return false;
        }

        // Split into a leading alphabetic run and a trailing alphanumeric run. Real callsigns
        // are either all-alphanumeric registrations (G-ABCD -> GABCD) or telephony + number
        // (STORM123). Both shapes are preserved; only the split differs.
        int firstDigit = -1;
        for (int i = 0; i < normalized.Length; i++)
        {
            if (char.IsDigit(normalized[i]))
            {
                firstDigit = i;
                break;
            }
        }

        string? telephony = firstDigit > 0 ? normalized[..firstDigit] : null;
        string? suffix = firstDigit >= 0 ? normalized[firstDigit..] : null;

        callsign = new Callsign(value.Trim(), normalized, telephony, suffix);
        return true;
    }

    /// <summary>
    /// Reduces a raw string to the canonical comparison form: uppercase alphanumerics only,
    /// with spelled-out digit words folded into digits.
    /// </summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Fold digit words first, while token boundaries still exist.
        string folded = SpokenNumbers.FoldDigitWords(value);

        var builder = new StringBuilder(folded.Length);
        foreach (char c in folded)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Determines whether a transcript plausibly addresses this callsign.
    /// </summary>
    /// <remarks>
    /// Speech-to-text frequently drops or mangles the telephony prefix while keeping the flight
    /// number intact ("one two three, ready to copy"). A bare numeric-suffix match is therefore
    /// accepted, but only when the suffix is at least two characters, to avoid matching stray
    /// digits such as an altitude. This is a recognition aid, never a clearance authority.
    /// </remarks>
    public bool MatchesTranscript(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        string normalizedTranscript = Normalize(transcript);
        if (normalizedTranscript.Contains(Normalized, StringComparison.Ordinal))
        {
            return true;
        }

        if (NumericSuffix is { Length: >= 2 }
            && normalizedTranscript.Contains(NumericSuffix, StringComparison.Ordinal))
        {
            return true;
        }

        return MatchesSpelledRegistration(transcript);
    }

    /// <summary>
    /// A registration said in the phonetic alphabet: "Fox Golf Tango Mike Papa" for F-GTMP, or the
    /// abbreviated form "Fox Mike Papa" (first letter and last two) once the controller has used it,
    /// or only its last three letters ("Tango Mike Papa"), or its last two ("Mike Papa").
    /// Only a registration made of letters is concerned; an airline callsign has its own rules.
    /// </summary>
    private bool MatchesSpelledRegistration(string transcript)
    {
        if (Telephony is not null || Normalized.Length < 3)
        {
            return false;
        }

        var run = new StringBuilder();
        foreach (string token in SpokenNumbers.Tokenize(transcript).Append(string.Empty))
        {
            if (PhoneticAlphabet.TryGetLetter(token, out char letter))
            {
                run.Append(char.ToUpperInvariant(letter));
                continue;
            }

            string spelled = run.ToString();
            run.Clear();
            if (spelled.Length >= 3
                && (spelled.Contains(Normalized, StringComparison.Ordinal)
                    || spelled.Contains($"{Normalized[0]}{Normalized[^2..]}", StringComparison.Ordinal)
                    || SpelledWithoutNationality(spelled)
                    || SpelledLastLetters(spelled)))
            {
                return true;
            }

            // Two letters alone ("Sierra Tango") name the aircraft only as the whole of what was spelled.
            if (spelled.Length == 2 && Normalized.Length >= 4 && spelled == Normalized[^2..])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The last three letters of a registration, the shortest form a controller needs to tell the aircraft.</summary>
    private bool SpelledLastLetters(string spelled) =>
        Normalized.Length >= 4
        && Normalized.All(char.IsLetter)
        && spelled.Contains(Normalized[^3..], StringComparison.Ordinal);

    /// <summary>
    /// French pilots often drop the nationality letter: "Golf Tango Mike Papa" for F-GTMP, or the
    /// short form "Golf Mike Papa". A five-letter registration is matched on its last four letters
    /// (or the first of those four and the last two).
    /// </summary>
    private bool SpelledWithoutNationality(string spelled) =>
        Normalized.Length == 5
        && Normalized.All(char.IsLetter)
        && (spelled.Contains(Normalized[1..], StringComparison.Ordinal)
            || spelled.Contains($"{Normalized[1]}{Normalized[^2..]}", StringComparison.Ordinal));

    /// <summary>
    /// Renders the callsign the way a controller would say it: telephony as a word, flight
    /// number digit-by-digit. <c>STORM123</c> becomes <c>Storm one two three</c>.
    /// </summary>
    public string ToSpoken()
    {
        if (Telephony is null)
        {
            // A registration-style callsign is spelled phonetically: G-ABCD -> Golf Alfa Bravo…
            return PhoneticAlphabet.SpellOut(Normalized);
        }

        // One- and two-letter prefixes are normally registrations or ad-hoc identifiers rather
        // than an ICAO airline telephony word. Spell them phonetically so ST123 is spoken as
        // "Sierra Tango one two three", not the ambiguous word "St".
        string telephonyWord = Telephony.Length <= 2
            ? PhoneticAlphabet.SpellOut(Telephony)
            : string.Concat(
                char.ToUpperInvariant(Telephony[0]),
                Telephony[1..].ToLowerInvariant());

        return NumericSuffix is null
            ? telephonyWord
            : $"{telephonyWord} {SpokenNumbers.DigitsIndividually(NumericSuffix)}";
    }

    /// <inheritdoc />
    public bool Equals(Callsign? other) =>
        other is not null && string.Equals(Normalized, other.Normalized, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Callsign);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Normalized);

    /// <inheritdoc />
    public override string ToString() => Normalized;

    public static bool operator ==(Callsign? left, Callsign? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Callsign? left, Callsign? right) => !(left == right);
}
