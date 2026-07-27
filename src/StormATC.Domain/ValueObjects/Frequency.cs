using System.Globalization;
using StormATC.Domain.Phraseology;

namespace StormATC.Domain.ValueObjects;

/// <summary>
/// A VHF communication frequency in megahertz.
/// </summary>
/// <remarks>
/// The civil VHF airband runs from 118.000 to 136.975 MHz. Anything outside that range that
/// arrives from a language model or a speech transcript is a parse failure, not a frequency —
/// rejecting it here is what stops a hallucinated "one one zero decimal five" from being
/// issued as a handoff.
/// </remarks>
public readonly record struct Frequency : IComparable<Frequency>
{
    /// <summary>Lowest frequency in the civil VHF airband.</summary>
    public const decimal MinimumMegahertz = 118.000m;

    /// <summary>Highest frequency in the civil VHF airband.</summary>
    public const decimal MaximumMegahertz = 136.975m;

    private Frequency(decimal megahertz) => Megahertz = megahertz;

    /// <summary>The frequency in megahertz, rounded to three decimal places.</summary>
    public decimal Megahertz { get; }

    /// <summary>Creates a frequency, rejecting values outside the civil VHF airband.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside the airband.</exception>
    public static Frequency FromMegahertz(decimal megahertz)
    {
        if (!TryFromMegahertz(megahertz, out Frequency frequency))
        {
            throw new ArgumentOutOfRangeException(
                nameof(megahertz),
                megahertz,
                $"Frequency must be between {MinimumMegahertz} and {MaximumMegahertz} MHz.");
        }

        return frequency;
    }

    /// <summary>Attempts to create a frequency without throwing.</summary>
    public static bool TryFromMegahertz(decimal megahertz, out Frequency frequency)
    {
        decimal rounded = Math.Round(megahertz, 3, MidpointRounding.AwayFromZero);
        if (rounded is < MinimumMegahertz or > MaximumMegahertz)
        {
            frequency = default;
            return false;
        }

        frequency = new Frequency(rounded);
        return true;
    }

    /// <summary>
    /// Parses a frequency from spoken text such as "one one nine decimal one" or "119.1".
    /// Returns null when the result would fall outside the airband.
    /// </summary>
    public static Frequency? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // A full readback often starts with a numeric callsign. Only inspect the part that
        // announces the channel when a radio marker is present, otherwise "ST123 ... departure
        // frequency 121.7" is incorrectly read as 123 MHz.
        string scope = ScopeAfterLastMarker(text, "frequency")
            ?? ScopeAfterLastMarker(text, "contact")
            ?? ScopeAfterLastMarker(text, "monitor")
            ?? text;

        decimal? value = SpokenNumbers.ParseSpokenFrequency(scope);
        return value is decimal mhz && TryFromMegahertz(mhz, out Frequency frequency)
            ? frequency
            : null;
    }

    private static string? ScopeAfterLastMarker(string text, string marker)
    {
        int index = text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? text[(index + marker.Length)..] : null;
    }

    /// <summary>
    /// Compares two frequencies for radio purposes, treating trailing zeros as equal so that
    /// 121.7 and 121.700 match.
    /// </summary>
    public bool IsSameChannel(Frequency other) => Megahertz == other.Megahertz;

    /// <summary>Renders the frequency as spoken: <c>"one two one decimal seven"</c>.</summary>
    public string ToSpoken() => SpokenNumbers.Frequency(Megahertz);

    /// <summary>Renders the frequency for display: <c>"121.700"</c>.</summary>
    public string ToDisplay() => Megahertz.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Canonical readback comparison value. Trailing zeros are stripped so that a pilot saying
    /// "one two one decimal seven" matches a stored 121.700.
    /// </summary>
    public string ToComparisonValue() =>
        Megahertz.ToString("0.###", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public int CompareTo(Frequency other) => Megahertz.CompareTo(other.Megahertz);

    /// <inheritdoc />
    public override string ToString() => ToDisplay();

    public static bool operator <(Frequency left, Frequency right) => left.Megahertz < right.Megahertz;
    public static bool operator >(Frequency left, Frequency right) => left.Megahertz > right.Megahertz;
    public static bool operator <=(Frequency left, Frequency right) => left.Megahertz <= right.Megahertz;
    public static bool operator >=(Frequency left, Frequency right) => left.Megahertz >= right.Megahertz;
}
