using System.Globalization;

namespace StormATC.Domain.Phraseology;

/// <summary>
/// French radiotelephony numbers, as the DSNA phraseology manual prescribes them.
/// </summary>
/// <remarks>
/// <para>
/// French airspace keeps the everyday way of saying numbers (the national derogation to
/// SERA.14035): an altitude of 3 500 ft is "trois mille cinq cents", a heading of 180 is
/// "cent quatre-vingts", QNH 1022 is "mille vingt-deux". Transponder codes are read in pairs
/// ("trente-deux cinquante-quatre"), frequencies as a number, "décimale", then each digit, and
/// runways below ten keep their leading zero ("zéro cinq").
/// </para>
/// <para>
/// Callsign digits, and any number the controller wants to make unambiguous, are read one digit
/// at a time.
/// </para>
/// </remarks>
public static class FrenchSpokenNumbers
{
    private static readonly string[] Units =
        ["zéro", "un", "deux", "trois", "quatre", "cinq", "six", "sept", "huit", "neuf"];

    private static readonly string[] Teens =
        ["dix", "onze", "douze", "treize", "quatorze", "quinze", "seize", "dix-sept", "dix-huit", "dix-neuf"];

    private static readonly string[] Tens =
        ["", "", "vingt", "trente", "quarante", "cinquante", "soixante"];

    /// <summary>
    /// The everyday French cardinal, from zero to 999 999. <c>3500</c> becomes
    /// <c>"trois mille cinq cents"</c>; <c>180</c> becomes <c>"cent quatre-vingts"</c>.
    /// </summary>
    public static string Cardinal(int value)
    {
        int absolute = Math.Abs(value);
        if (absolute >= 1_000_000)
        {
            return DigitsIndividually(absolute.ToString(CultureInfo.InvariantCulture));
        }

        if (absolute < 1000)
        {
            return BelowThousand(absolute, isFinal: true);
        }

        int thousands = absolute / 1000;
        int remainder = absolute % 1000;

        // "quatre-vingts" and "deux cents" lose their plural "s" before "mille".
        string thousandsPart = thousands == 1
            ? "mille"
            : $"{BelowThousand(thousands, isFinal: false)} mille";

        return remainder == 0
            ? thousandsPart
            : $"{thousandsPart} {BelowThousand(remainder, isFinal: true)}";
    }

    /// <summary>
    /// Reads each character individually: <c>"270"</c> becomes <c>"deux sept zéro"</c>. Letters
    /// use the ICAO spelling alphabet, which French radiotelephony shares.
    /// </summary>
    public static string DigitsIndividually(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var parts = new List<string>(text.Length);
        foreach (char c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                parts.Add(Units[c - '0']);
            }
            else if (char.IsLetter(c))
            {
                parts.Add(PhoneticAlphabet.ForLetter(c));
            }
            else if (c is '.' or ',')
            {
                parts.Add("décimale");
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>Spells an identifier: letters in the ICAO alphabet, digits in French.</summary>
    public static string SpellOut(string value) => DigitsIndividually(value);

    /// <summary>
    /// A frequency: <c>133.375</c> becomes <c>"cent trente-trois décimale trois sept cinq"</c>.
    /// Trailing zeros are dropped except one, as in the manual's "Paris 132,0".
    /// </summary>
    public static string Frequency(decimal megahertz)
    {
        decimal rounded = Math.Round(megahertz, 3, MidpointRounding.AwayFromZero);
        int whole = (int)Math.Truncate(rounded);
        decimal fractionalPart = rounded - whole;

        string fractionDigits = ((int)Math.Round(fractionalPart * 1000m, MidpointRounding.AwayFromZero))
            .ToString(CultureInfo.InvariantCulture)
            .PadLeft(3, '0')
            .TrimEnd('0');

        if (fractionDigits.Length == 0)
        {
            fractionDigits = "0";
        }

        return $"{Cardinal(whole)} décimale {DigitsIndividually(fractionDigits)}";
    }

    /// <summary>
    /// A transponder code, read in pairs: <c>"3254"</c> becomes
    /// <c>"trente-deux cinquante-quatre"</c>. A pair starting with zero is read digit by digit.
    /// </summary>
    public static string Squawk(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (code.Length != 4 || !code.All(char.IsAsciiDigit))
        {
            return DigitsIndividually(code);
        }

        return $"{Pair(code[..2])} {Pair(code[2..])}";

        static string Pair(string pair) => pair[0] == '0'
            ? DigitsIndividually(pair)
            : Cardinal(int.Parse(pair, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A heading or wind direction: <c>180</c> becomes <c>"cent quatre-vingts"</c>; headings below
    /// one hundred keep their leading zero so they cannot be mistaken for a speed.
    /// </summary>
    public static string Heading(int degrees)
    {
        int normalized = degrees % 360 == 0 && degrees != 0 ? 360 : ((degrees % 360) + 360) % 360;

        return normalized switch
        {
            < 10 => $"zéro zéro {Units[normalized]}",
            < 100 => $"zéro {Cardinal(normalized)}",
            _ => Cardinal(normalized),
        };
    }

    /// <summary>A runway number: <c>5</c> becomes <c>"zéro cinq"</c>, <c>27</c> becomes <c>"vingt-sept"</c>.</summary>
    public static string RunwayNumber(int number) => number < 10
        ? $"zéro {Units[Math.Max(0, number)]}"
        : Cardinal(number);

    private static string BelowThousand(int value, bool isFinal)
    {
        int hundreds = value / 100;
        int rest = value % 100;

        if (hundreds == 0)
        {
            return BelowHundred(rest, isFinal);
        }

        string hundredsPart = hundreds == 1 ? "cent" : $"{Units[hundreds]} cent";

        if (rest == 0)
        {
            // "deux cents", but "deux cent mille".
            return hundreds > 1 && isFinal ? hundredsPart + "s" : hundredsPart;
        }

        return $"{hundredsPart} {BelowHundred(rest, isFinal: true)}";
    }

    private static string BelowHundred(int value, bool isFinal)
    {
        switch (value)
        {
            case < 10:
                return Units[value];
            case < 20:
                return Teens[value - 10];
            case < 70:
            {
                int tens = value / 10;
                int units = value % 10;
                return units switch
                {
                    0 => Tens[tens],
                    1 => $"{Tens[tens]} et un",
                    _ => $"{Tens[tens]}-{Units[units]}",
                };
            }

            case < 80:
                return value == 71 ? "soixante et onze" : $"soixante-{Teens[value - 70]}";
            case 80:
                return isFinal ? "quatre-vingts" : "quatre-vingt";
            case < 90:
                return $"quatre-vingt-{Units[value - 80]}";
            default:
                return $"quatre-vingt-{Teens[value - 90]}";
        }
    }
}
