using System.Globalization;
using StormATC.Domain.Phraseology;

namespace StormATC.Domain.ValueObjects;

/// <summary>
/// A compass heading in degrees, normalised to the 1–360 range used on the radio.
/// </summary>
/// <remarks>
/// Controllers say "three six zero", never "zero zero zero" — north is 360 in a heading
/// instruction. Normalisation therefore maps 0 to 360, which is why this type exists rather
/// than a raw <c>int</c> that would let 0 leak into a spoken clearance.
/// </remarks>
/// <remarks>
/// Deliberately not <see cref="IComparable{T}"/>: headings are circular, so "less than" has no
/// meaning — 350 is 20 degrees from 010, not 340 degrees away. Use <see cref="DifferenceTo"/>
/// when you need angular separation.
/// </remarks>
public readonly record struct Heading
{
    private Heading(int degrees) => Degrees = degrees;

    /// <summary>The heading in degrees, always between 1 and 360 inclusive.</summary>
    public int Degrees { get; }

    /// <summary>
    /// Creates a heading, wrapping any input into the valid range. 0 becomes 360, 370 becomes 10.
    /// </summary>
    public static Heading FromDegrees(int degrees)
    {
        int wrapped = degrees % 360;
        if (wrapped <= 0)
        {
            wrapped += 360;
        }

        return new Heading(wrapped);
    }

    /// <summary>Creates a heading from a floating-point simulator value.</summary>
    public static Heading FromDegrees(double degrees) =>
        FromDegrees((int)Math.Round(degrees, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Validates a heading that arrived from an untrusted source such as a language model.
    /// Unlike <see cref="FromDegrees(int)"/> this rejects out-of-range values rather than
    /// wrapping them, because a model emitting 450 has made an error worth surfacing.
    /// </summary>
    public static bool TryValidate(int degrees, out Heading heading)
    {
        if (degrees is < 0 or > 360)
        {
            heading = default;
            return false;
        }

        heading = FromDegrees(degrees);
        return true;
    }

    /// <summary>
    /// Parses a heading from spoken text such as "two seven zero" or "heading 270".
    /// Returns null when no three-digit heading is present.
    /// </summary>
    public static Heading? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string? digits = SpokenNumbers.ParseSpokenDigitRun(text, expectedLength: 3);
        if (digits is null
            || !int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            || value > 360)
        {
            return null;
        }

        return FromDegrees(value);
    }

    /// <summary>Absolute angular difference to another heading, 0–180 degrees.</summary>
    public int DifferenceTo(Heading other)
    {
        int raw = Math.Abs(Degrees - other.Degrees);
        return Math.Min(raw, 360 - raw);
    }

    /// <summary>Renders the heading as spoken: <c>"two seven zero"</c>.</summary>
    public string ToSpoken() => SpokenNumbers.DigitsIndividually(Degrees, width: 3);

    /// <summary>Renders the heading for display: <c>"270°"</c>.</summary>
    public string ToDisplay() =>
        $"{Degrees.ToString(CultureInfo.InvariantCulture).PadLeft(3, '0')}°";

    /// <summary>Canonical readback comparison value — a zero-padded three-digit string.</summary>
    public string ToComparisonValue() =>
        Degrees.ToString(CultureInfo.InvariantCulture).PadLeft(3, '0');

    /// <inheritdoc />
    public override string ToString() => ToDisplay();
}
