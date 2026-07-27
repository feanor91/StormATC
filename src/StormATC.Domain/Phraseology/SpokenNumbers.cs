using System.Globalization;
using System.Text;

namespace StormATC.Domain.Phraseology;

/// <summary>
/// Deterministic conversion between numeric values and the way aviation radio speech renders
/// them, in both directions.
/// </summary>
/// <remarks>
/// <para>
/// Two directions matter, and they are not symmetric:
/// </para>
/// <list type="bullet">
/// <item><b>Outbound</b> — turning an approved numeric fact into spoken text for the controller
/// voice. This must be deterministic; the language model is never asked to format a number,
/// because a model that "helpfully" says "seven thousand five hundred" for 7000 has changed a
/// clearance.</item>
/// <item><b>Inbound</b> — folding a pilot's spoken numbers back into digits so a readback can
/// be compared. Speech-to-text output is messy and this only needs to be good enough to feed
/// an exact comparison afterwards.</item>
/// </list>
/// <para>
/// Aviation number pronunciation follows ICAO Annex 10 conventions: digits are spoken
/// individually, "decimal" separates a frequency's whole and fractional parts, and round
/// thousands/hundreds are spoken as words.
/// </para>
/// </remarks>
public static class SpokenNumbers
{
    private static readonly string[] DigitWords =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
    ];

    /// <summary>
    /// Single-token digit words recognised on the inbound path. "niner" and "tree" are the
    /// ICAO pronunciations; speech-to-text sometimes preserves them literally.
    /// </summary>
    private static readonly Dictionary<string, string> DigitWordLookup =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["zero"] = "0", ["oh"] = "0", ["o"] = "0", ["nought"] = "0", ["naught"] = "0",
            ["one"] = "1", ["won"] = "1",
            ["two"] = "2", ["to"] = "2", ["too"] = "2",
            ["three"] = "3", ["tree"] = "3",
            ["four"] = "4", ["fower"] = "4", ["for"] = "4",
            ["five"] = "5", ["fife"] = "5",
            ["six"] = "6",
            ["seven"] = "7",
            ["eight"] = "8", ["ait"] = "8", ["ate"] = "8",
            ["nine"] = "9", ["niner"] = "9",
        };

    /// <summary>Multi-word numbers that appear in altitudes and flight levels.</summary>
    private static readonly Dictionary<string, int> TeenAndTensLookup =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
            ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
            ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30,
            ["forty"] = 40, ["fourty"] = 40, ["fifty"] = 50, ["sixty"] = 60,
            ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
        };

    // ---------------------------------------------------------------------------------
    // Outbound: numeric value -> spoken text
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Speaks each character of a string individually. <c>"270"</c> becomes
    /// <c>"two seven zero"</c>; letters are rendered phonetically.
    /// </summary>
    public static string DigitsIndividually(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);

        var parts = new List<string>(digits.Length);
        foreach (char c in digits)
        {
            if (char.IsDigit(c))
            {
                parts.Add(DigitWords[c - '0']);
            }
            else if (char.IsLetter(c))
            {
                parts.Add(PhoneticAlphabet.ForLetter(c));
            }
            else if (c == '.')
            {
                parts.Add("decimal");
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Speaks an integer digit by digit, zero-padded to a fixed width.
    /// Used for squawk codes (always four digits) and headings (always three).
    /// </summary>
    public static string DigitsIndividually(int value, int width) =>
        DigitsIndividually(Math.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'));

    /// <summary>
    /// Renders an altitude in feet the way a controller says it.
    /// </summary>
    /// <example>
    /// <c>7000</c> becomes <c>"seven thousand"</c>; <c>3500</c> becomes
    /// <c>"three thousand five hundred"</c>; <c>500</c> becomes <c>"five hundred"</c>.
    /// </example>
    public static string Altitude(int feet)
    {
        int absolute = Math.Abs(feet);
        if (absolute == 0)
        {
            return "zero";
        }

        int thousands = absolute / 1000;
        int remainder = absolute % 1000;
        int hundreds = remainder / 100;
        int leftover = remainder % 100;

        var parts = new List<string>(4);

        if (thousands > 0)
        {
            // Thousands below ten are spoken as a single digit word; above that, digit by
            // digit ("one three thousand" for 13 000).
            parts.Add(thousands < 10
                ? DigitWords[thousands]
                : DigitsIndividually(thousands.ToString(CultureInfo.InvariantCulture)));
            parts.Add("thousand");
        }

        if (hundreds > 0)
        {
            parts.Add(DigitWords[hundreds]);
            parts.Add("hundred");
        }

        // Non-round altitudes are unusual in clearances but must not be silently truncated.
        if (leftover > 0)
        {
            parts.Add(DigitsIndividually(leftover.ToString(CultureInfo.InvariantCulture)));
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Renders a flight level. <c>120</c> becomes <c>"flight level one two zero"</c>.
    /// </summary>
    public static string FlightLevel(int level) =>
        $"flight level {DigitsIndividually(level, 3)}";

    /// <summary>
    /// Renders a VHF frequency in megahertz. <c>121.700</c> becomes
    /// <c>"one two one decimal seven"</c>.
    /// </summary>
    /// <remarks>
    /// Trailing zeros after the decimal point are dropped, matching real radio usage: 121.700
    /// is said "one two one decimal seven", not "…decimal seven zero zero". A frequency ending
    /// in a significant digit such as 119.125 keeps all three.
    /// </remarks>
    public static string Frequency(decimal megahertz)
    {
        // Round to the 25 kHz/8.33 kHz display resolution before stripping zeros so that
        // floating representation noise cannot leak into speech.
        decimal rounded = Math.Round(megahertz, 3, MidpointRounding.AwayFromZero);
        int whole = (int)Math.Truncate(rounded);
        decimal fractionalPart = rounded - whole;

        string fractionDigits = ((int)Math.Round(fractionalPart * 1000m, MidpointRounding.AwayFromZero))
            .ToString(CultureInfo.InvariantCulture)
            .PadLeft(3, '0')
            .TrimEnd('0');

        string wholeSpoken = DigitsIndividually(whole.ToString(CultureInfo.InvariantCulture));

        return fractionDigits.Length == 0
            ? wholeSpoken
            : $"{wholeSpoken} decimal {DigitsIndividually(fractionDigits)}";
    }

    // ---------------------------------------------------------------------------------
    // Inbound: spoken text -> digits
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Replaces spelled-out digit words in free text with the corresponding digits, so that
    /// "storm one two three" becomes "storm 1 2 3" and can then be normalised to STORM123.
    /// </summary>
    /// <remarks>
    /// Only unambiguous single-digit words are folded. Words such as "to" and "for" are
    /// homophones of digits and are folded only when they sit inside a run of other digit
    /// words — otherwise "cleared to land" would become "cleared 2 land".
    /// </remarks>
    public static string FoldDigitWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string[] tokens = Tokenize(text);
        if (tokens.Length == 0)
        {
            return text;
        }

        var mapped = new string?[tokens.Length];
        var isAmbiguous = new bool[tokens.Length];

        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (DigitWordLookup.TryGetValue(token, out string? digit))
            {
                mapped[i] = digit;
                isAmbiguous[i] = IsHomophone(token);
            }
        }

        // Second pass: keep ambiguous mappings only when a neighbour is an unambiguous digit.
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            bool neighbourIsDigit =
                (i > 0 && mapped[i - 1] is not null && !isAmbiguous[i - 1])
                || (i + 1 < tokens.Length && mapped[i + 1] is not null && !isAmbiguous[i + 1]);

            builder.Append(mapped[i] is not null && (!isAmbiguous[i] || neighbourIsDigit)
                ? mapped[i]
                : tokens[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Extracts a numeric altitude in feet from spoken text such as "seven thousand" or
    /// "three thousand five hundred". Returns null when no altitude phrase is present.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Number words are accumulated only while they are contiguous, and the scan stops at the
    /// first complete phrase — one that used "thousand" or "hundred", or a bare number of 100 or
    /// more.
    /// </para>
    /// <para>
    /// This scoping is what makes the method usable on a whole readback. A full clearance
    /// readback is one long utterance: "runway one eight, climb seven thousand, squawk four two
    /// seven one". Accumulating every number word in it would yield a nonsense altitude built
    /// from the runway and the transponder code; resetting at each non-numeric word yields the
    /// seven thousand the pilot actually said.
    /// </para>
    /// </remarks>
    public static int? ParseSpokenAltitude(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string[] tokens = Tokenize(text);

        // Some speech providers render a thousands separator as whitespace: "7 000" or
        // "12 000". Recover that written-number shape before the word-based scan.
        for (int index = 0; index + 1 < tokens.Length; index++)
        {
            string high = tokens[index];
            string low = tokens[index + 1];

            if (high.Length is >= 1 and <= 2
                && low.Length == 3
                && high.All(char.IsDigit)
                && low.All(char.IsDigit)
                && int.TryParse(
                    high + low,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int grouped)
                && grouped is >= 100 and <= 60_000)
            {
                return grouped;
            }
        }

        var digitRun = new List<int>(4);
        int wordValue = 0;
        int total = 0;
        bool sawScale = false;

        foreach (string token in tokens)
        {
            if (DigitWordLookup.TryGetValue(token, out string? digitText) && !IsHomophone(token))
            {
                digitRun.Add(int.Parse(digitText, CultureInfo.InvariantCulture));
            }
            else if (TeenAndTensLookup.TryGetValue(token, out int tens))
            {
                wordValue += tens;
            }
            else if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int literal))
            {
                // A bare number such as "7000" — accept it directly.
                if (literal >= 100)
                {
                    return literal;
                }

                if (literal <= 9)
                {
                    digitRun.Add(literal);
                }
                else
                {
                    wordValue += literal;
                }
            }
            else if (token.Equals("thousand", StringComparison.OrdinalIgnoreCase))
            {
                total += TakeMultiplier(digitRun, wordValue, maximum: MaximumFeetInThousands) * 1000;
                digitRun.Clear();
                wordValue = 0;
                sawScale = true;
            }
            else if (token.Equals("hundred", StringComparison.OrdinalIgnoreCase))
            {
                total += TakeMultiplier(digitRun, wordValue, maximum: 99) * 100;
                digitRun.Clear();
                wordValue = 0;
                sawScale = true;
            }
            else
            {
                // A word that is not part of a number ends the phrase. If a scale word has
                // already been seen the altitude is complete; otherwise the digits so far
                // belonged to something else — a runway, a squawk — and are discarded.
                if (sawScale)
                {
                    return total > 0 ? total : null;
                }

                digitRun.Clear();
                wordValue = 0;
            }
        }

        return sawScale && total > 0 ? total : null;
    }

    /// <summary>The largest multiplier "thousand" can legitimately take, from 60 000 ft.</summary>
    private const int MaximumFeetInThousands = 60;

    /// <summary>
    /// Picks the multiplier that precedes a scale word from the digits just spoken.
    /// </summary>
    /// <remarks>
    /// Takes the longest trailing run that is a plausible multiplier rather than the whole run.
    /// In "runway one eight seven thousand" the runway's digits sit immediately before the
    /// altitude with no word between them; "one eight seven" is not a multiplier anything could
    /// mean, but its trailing "seven" is exactly what the pilot said.
    /// </remarks>
    private static int TakeMultiplier(List<int> digitRun, int wordValue, int maximum)
    {
        // A teens or tens word ("twelve thousand") is unambiguous where it is valid.
        if (wordValue is > 0 && wordValue <= maximum)
        {
            return wordValue;
        }

        for (int length = Math.Min(digitRun.Count, 2); length >= 1; length--)
        {
            int value = 0;
            for (int i = digitRun.Count - length; i < digitRun.Count; i++)
            {
                value = (value * 10) + digitRun[i];
            }

            if (value >= 1 && value <= maximum)
            {
                return value;
            }
        }

        // "Thousand" with nothing usable in front of it means one thousand.
        return 1;
    }

    /// <summary>
    /// Extracts a run of digits from spoken text — the shape used by squawk codes, headings,
    /// and flight levels, where every digit is spoken individually.
    /// </summary>
    /// <param name="text">Text to scan.</param>
    /// <param name="expectedLength">Exact digit count required, or null to accept any run.</param>
    public static string? ParseSpokenDigitRun(string text, int? expectedLength = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        string folded = FoldDigitWords(text);
        var runs = new List<string>();
        var current = new StringBuilder();

        foreach (char c in folded)
        {
            if (char.IsDigit(c))
            {
                current.Append(c);
            }
            else if (c != ' ')
            {
                // Any non-space, non-digit character terminates the run. Spaces do not, so
                // "4 2 7 1" and "4271" both produce "4271".
                Flush(runs, current);
            }
        }

        Flush(runs, current);

        if (expectedLength is null)
        {
            return runs.Count > 0 ? runs[0] : null;
        }

        foreach (string run in runs)
        {
            if (run.Length == expectedLength)
            {
                return run;
            }
        }

        return null;

        static void Flush(List<string> target, StringBuilder buffer)
        {
            if (buffer.Length > 0)
            {
                target.Add(buffer.ToString());
                buffer.Clear();
            }
        }
    }

    /// <summary>
    /// Extracts a VHF frequency from spoken text such as "one two one decimal seven" or
    /// "121.7". Returns null when no plausible frequency is present.
    /// </summary>
    public static decimal? ParseSpokenFrequency(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Normalise the separator words first, then fold digits, then read the two sides.
        string prepared = text
            .Replace("decimal", ".", StringComparison.OrdinalIgnoreCase)
            .Replace("point", ".", StringComparison.OrdinalIgnoreCase)
            .Replace("comma", ".", StringComparison.OrdinalIgnoreCase);

        string folded = FoldDigitWords(prepared);

        var whole = new StringBuilder();
        var fraction = new StringBuilder();
        bool afterSeparator = false;
        bool started = false;

        foreach (char c in folded)
        {
            if (char.IsDigit(c))
            {
                started = true;
                (afterSeparator ? fraction : whole).Append(c);
            }
            else if (c == '.' && started && !afterSeparator)
            {
                afterSeparator = true;
            }
            else if (c != ' ' && started)
            {
                break;
            }
        }

        if (whole.Length is < 2 or > 3)
        {
            return null;
        }

        string combined = fraction.Length > 0
            ? $"{whole}.{fraction}"
            : whole.ToString();

        if (!decimal.TryParse(combined, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
        {
            return null;
        }

        return value;
    }

    /// <summary>Splits text into lowercase word tokens, discarding punctuation.</summary>
    internal static string[] Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = new List<string>();
        var current = new StringBuilder();
        bool? currentIsDigit = null;

        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                bool isDigit = char.IsDigit(c);

                // Streaming speech providers occasionally glue adjacent fields together, for
                // example "7000AndSquawk5217". Split at letter/digit boundaries so the literal
                // altitude and squawk remain independently parseable.
                if (current.Length > 0 && currentIsDigit == true && !isDigit)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                current.Append(char.ToLowerInvariant(c));
                currentIsDigit = isDigit;
            }
            else
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                currentIsDigit = null;

                // A decimal point between digits is meaningful; keep it as its own token.
                if (c is '.' or ',')
                {
                    tokens.Add(".");
                }
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return [.. tokens];
    }

    /// <summary>
    /// True for digit words that are also ordinary English words, which must not be folded
    /// unless surrounded by other digits.
    /// </summary>
    private static bool IsHomophone(string token) => token switch
    {
        "to" or "too" or "for" or "won" or "ate" or "o" or "oh" => true,
        _ => false,
    };
}
