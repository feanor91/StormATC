using System.Diagnostics.CodeAnalysis;
using StormATC.Domain.Phraseology;

namespace StormATC.Domain.ValueObjects;

/// <summary>
/// A four-digit transponder code.
/// </summary>
/// <remarks>
/// Squawk codes are octal: each of the four digits is 0–7, so 4271 is valid and 4281 is not.
/// The type also knows the reserved emergency codes, which the engine must never assign as an
/// ordinary discrete code.
/// </remarks>
public readonly record struct SquawkCode
{
    /// <summary>General emergency.</summary>
    public static readonly SquawkCode Emergency = new("7700");

    /// <summary>Radio communication failure.</summary>
    public static readonly SquawkCode RadioFailure = new("7600");

    /// <summary>Unlawful interference.</summary>
    public static readonly SquawkCode Hijack = new("7500");

    /// <summary>Conspicuity code used when no discrete code has been assigned.</summary>
    public static readonly SquawkCode Conspicuity = new("7000");

    private SquawkCode(string digits) => Digits = digits;

    /// <summary>The four octal digits, for example <c>"4271"</c>.</summary>
    public string Digits { get; }

    /// <summary>True when this is one of the reserved emergency codes.</summary>
    public bool IsEmergencyCode =>
        Digits is "7500" or "7600" or "7700";

    /// <summary>Creates a squawk code.</summary>
    /// <exception cref="ArgumentException">The value is not four octal digits.</exception>
    public static SquawkCode Create(string value)
    {
        if (!TryCreate(value, out SquawkCode code))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid squawk code. Expected four digits, each 0-7.",
                nameof(value));
        }

        return code;
    }

    /// <summary>Attempts to create a squawk code without throwing.</summary>
    public static bool TryCreate(string? value, out SquawkCode code)
    {
        code = default;
        if (value is null)
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.Length != 4)
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            // Octal: transponder digits cannot be 8 or 9.
            if (c is < '0' or > '7')
            {
                return false;
            }
        }

        code = new SquawkCode(trimmed);
        return true;
    }

    /// <summary>
    /// Parses a squawk from spoken text such as "squawk four two seven one".
    /// </summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out SquawkCode? code)
    {
        code = null;

        // A clearance readback often contains an altitude before the transponder code:
        // "initially 7000 and squawk 5217". Scanning the whole sentence would select 7000
        // simply because it is the first four-digit run. When the marker is present, only the
        // text after "squawk" can be the transponder code.
        int marker = text.IndexOf("squawk", StringComparison.OrdinalIgnoreCase);
        string scope = marker >= 0 ? text[(marker + "squawk".Length)..] : text;

        // Deepgram can hear the ICAO digit "one" as "point" inside a digit-by-digit code
        // ("five two point seven"). A squawk is always four octal digits and can never contain
        // a decimal separator, so this recovery is unambiguous when confined to squawk scope.
        // Do not apply it globally: "point" is meaningful in a frequency elsewhere.
        scope = scope.Replace("point", "one", StringComparison.OrdinalIgnoreCase);

        string? digits = SpokenNumbers.ParseSpokenDigitRun(scope, expectedLength: 4);
        if (digits is not null && TryCreate(digits, out SquawkCode parsed))
        {
            code = parsed;
            return true;
        }

        return false;
    }

    /// <summary>Renders the code as spoken: <c>"four two seven one"</c>.</summary>
    public string ToSpoken() => SpokenNumbers.DigitsIndividually(Digits);

    /// <summary>Renders the code for display: <c>"4271"</c>.</summary>
    public string ToDisplay() => Digits;

    /// <summary>Canonical readback comparison value.</summary>
    public string ToComparisonValue() => Digits;

    /// <inheritdoc />
    public override string ToString() => Digits;
}
