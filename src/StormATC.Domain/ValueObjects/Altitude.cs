using System.Globalization;
using StormATC.Domain.Phraseology;

namespace StormATC.Domain.ValueObjects;

/// <summary>
/// A vertical position expressed either as an altitude in feet or as a flight level.
/// </summary>
/// <remarks>
/// <para>
/// Altitudes and flight levels are not interchangeable: an altitude is referenced to a local
/// pressure setting, a flight level to the standard 1013.25 hPa datum. Which one a controller
/// uses depends on whether the aircraft is above the transition altitude. Storing them in one
/// type with an explicit discriminator keeps the distinction visible everywhere instead of
/// letting a bare <c>int</c> quietly mean two different things.
/// </para>
/// <para>
/// The comparison used for readback checking is on <see cref="Feet"/>, so "flight level one
/// two zero" and "twelve thousand" compare equal in feet — deliberate, because a pilot reading
/// back the wrong reference but the right number is a phraseology slip, not a level bust.
/// </para>
/// </remarks>
public readonly record struct Altitude : IComparable<Altitude>
{
    /// <summary>Highest value StormATC will accept. Above this the input is a parse error.</summary>
    public const int MaximumFeet = 60_000;

    /// <summary>Lowest value StormATC will accept, allowing for below-sea-level aerodromes.</summary>
    public const int MinimumFeet = -1_500;

    private Altitude(int feet, bool isFlightLevel)
    {
        Feet = feet;
        IsFlightLevel = isFlightLevel;
    }

    /// <summary>The vertical position in feet. Always populated, including for flight levels.</summary>
    public int Feet { get; }

    /// <summary>True when this value should be spoken and displayed as a flight level.</summary>
    public bool IsFlightLevel { get; }

    /// <summary>The flight level number, for example 120 for FL120.</summary>
    public int FlightLevelNumber => Feet / 100;

    /// <summary>Creates an altitude in feet.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside the accepted range.</exception>
    public static Altitude FromFeet(int feet)
    {
        if (feet is < MinimumFeet or > MaximumFeet)
        {
            throw new ArgumentOutOfRangeException(
                nameof(feet),
                feet,
                $"Altitude must be between {MinimumFeet} and {MaximumFeet} feet.");
        }

        return new Altitude(feet, isFlightLevel: false);
    }

    /// <summary>Creates a flight level. <c>FromFlightLevel(120)</c> is FL120, i.e. 12 000 ft.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside the accepted range.</exception>
    public static Altitude FromFlightLevel(int level)
    {
        if (level is < 0 or > MaximumFeet / 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                $"Flight level must be between 0 and {MaximumFeet / 100}.");
        }

        return new Altitude(level * 100, isFlightLevel: true);
    }

    /// <summary>Attempts to create an altitude in feet without throwing.</summary>
    public static bool TryFromFeet(int feet, out Altitude altitude)
    {
        if (feet is < MinimumFeet or > MaximumFeet)
        {
            altitude = default;
            return false;
        }

        altitude = new Altitude(feet, isFlightLevel: false);
        return true;
    }

    /// <summary>
    /// Reinterprets this value as a flight level or as an altitude, keeping the same feet.
    /// </summary>
    public Altitude AsFlightLevel() => new(Feet, isFlightLevel: true);

    /// <summary>Reinterprets this value as an altitude in feet.</summary>
    public Altitude AsAltitude() => new(Feet, isFlightLevel: false);

    /// <summary>
    /// Parses spoken or written text such as "seven thousand", "FL120", "flight level one two
    /// zero", or "7000". Returns null when nothing altitude-shaped is found.
    /// </summary>
    /// <remarks>
    /// A flight level is read from the text <em>after</em> the words that announce it. Searching
    /// the whole transmission would find the callsign first: "Storm one two three request flight
    /// level one two zero" contains two three-digit runs, and the wrong one is the callsign.
    /// </remarks>
    public static Altitude? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (ScopeAfterFlightLevelMarker(text) is string scope)
        {
            // "FL120" and "flight level one two zero" both reduce to a three-digit run.
            string? digits = SpokenNumbers.ParseSpokenDigitRun(scope, expectedLength: 3)
                ?? SpokenNumbers.ParseSpokenDigitRun(scope, expectedLength: 2);

            if (digits is not null
                && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)
                && level is >= 0 and <= MaximumFeet / 100)
            {
                return FromFlightLevel(level);
            }
        }

        // A written callsign can contain a three-digit literal before the actual altitude:
        // "Sierra Tango 123, climb 7000". Scope to the altitude instruction when one is
        // present so the flight number cannot be mistaken for the cleared level.
        string altitudeScope = ScopeAfterAltitudeMarker(text) ?? text;
        int? feet = SpokenNumbers.ParseSpokenAltitude(altitudeScope);
        if (feet is int value && value is >= MinimumFeet and <= MaximumFeet)
        {
            return FromFeet(value);
        }

        return null;
    }

    private static string? ScopeAfterAltitudeMarker(string text)
    {
        string[] markers =
        [
            "climb",
            "descend",
            "maintain",
            "initially",
            "initial",
            "altitude",
            "passing",
            "leaving",
        ];

        int bestIndex = -1;
        int bestLength = 0;

        foreach (string marker in markers)
        {
            int index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (bestIndex < 0 || index < bestIndex))
            {
                bestIndex = index;
                bestLength = marker.Length;
            }
        }

        return bestIndex < 0 ? null : text[(bestIndex + bestLength)..];
    }

    /// <summary>
    /// Returns the text following a flight-level announcement, or null when there is none.
    /// </summary>
    private static string? ScopeAfterFlightLevelMarker(string text)
    {
        const string SpokenMarker = "flight level";

        int index = text.IndexOf(SpokenMarker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return text[(index + SpokenMarker.Length)..];
        }

        // The written form is "FL" immediately followed by digits, so a bare "FL" elsewhere in
        // a sentence is not a flight level.
        for (int i = 0; i < text.Length - 2; i++)
        {
            if (text[i] is 'F' && text[i + 1] is 'L' && char.IsAsciiDigit(text[i + 2]))
            {
                return text[(i + 2)..];
            }
        }

        return null;
    }

    /// <summary>Renders the value as a controller would speak it.</summary>
    public string ToSpoken() => IsFlightLevel
        ? SpokenNumbers.FlightLevel(FlightLevelNumber)
        : SpokenNumbers.Altitude(Feet);

    /// <summary>Renders the value for on-screen display: <c>FL120</c> or <c>7000 ft</c>.</summary>
    public string ToDisplay() => IsFlightLevel
        ? $"FL{FlightLevelNumber.ToString(CultureInfo.InvariantCulture).PadLeft(3, '0')}"
        : $"{Feet.ToString("N0", CultureInfo.InvariantCulture)} ft";

    /// <summary>
    /// The canonical string used for readback comparison — always feet, never the display form,
    /// so that FL120 and 12 000 ft compare equal.
    /// </summary>
    public string ToComparisonValue() => Feet.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public int CompareTo(Altitude other) => Feet.CompareTo(other.Feet);

    /// <inheritdoc />
    public override string ToString() => ToDisplay();

    public static bool operator <(Altitude left, Altitude right) => left.Feet < right.Feet;
    public static bool operator >(Altitude left, Altitude right) => left.Feet > right.Feet;
    public static bool operator <=(Altitude left, Altitude right) => left.Feet <= right.Feet;
    public static bool operator >=(Altitude left, Altitude right) => left.Feet >= right.Feet;
}
