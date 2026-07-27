using System.Globalization;
using StormATC.Domain.Phraseology;

namespace StormATC.Domain.ValueObjects;

/// <summary>
/// A runway designator: a magnetic-bearing number 01–36 with an optional L/C/R suffix.
/// </summary>
/// <remarks>
/// <para>
/// Runway identifiers are written zero-padded ("01L", "36") but spoken digit by digit
/// ("runway zero one left", "runway three six"). The two forms differ enough that comparing
/// raw strings from a transcript against airport data fails constantly — "1L", "01L", "zero
/// one left" and "one left" are all the same runway. Normalising on construction is what makes
/// the readback comparison reliable.
/// </para>
/// </remarks>
public readonly record struct RunwayIdentifier
{
    private RunwayIdentifier(int number, RunwaySide side)
    {
        Number = number;
        Side = side;
    }

    /// <summary>The runway number, 1–36.</summary>
    public int Number { get; }

    /// <summary>The parallel-runway suffix, where one exists.</summary>
    public RunwaySide Side { get; }

    /// <summary>Creates a runway identifier from text such as "01L", "1l", or "36".</summary>
    /// <exception cref="ArgumentException">The value is not a valid runway designator.</exception>
    public static RunwayIdentifier Create(string value)
    {
        if (!TryCreate(value, out RunwayIdentifier runway))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid runway identifier. Expected 01-36 with an optional L, C, or R.",
                nameof(value));
        }

        return runway;
    }

    /// <summary>Attempts to create a runway identifier without throwing.</summary>
    public static bool TryCreate(string? value, out RunwayIdentifier runway)
    {
        runway = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim().ToUpperInvariant();

        // Strip a leading "RWY" or "RUNWAY" prefix if present.
        if (trimmed.StartsWith("RUNWAY", StringComparison.Ordinal))
        {
            trimmed = trimmed[6..].Trim();
        }
        else if (trimmed.StartsWith("RWY", StringComparison.Ordinal))
        {
            trimmed = trimmed[3..].Trim();
        }

        var digits = new System.Text.StringBuilder(2);
        RunwaySide side = RunwaySide.None;

        // The whole designator must be consumed. Stopping early at unrecognised input would
        // silently turn a typo such as "18X" into runway 18, hiding a broken data file rather
        // than reporting it. Free-form text is the job of Parse, not of this method.
        bool sideSeen = false;

        foreach (char c in trimmed)
        {
            if (c is ' ' or '-' or '/')
            {
                continue;
            }

            if (char.IsAsciiDigit(c) && digits.Length < 2 && !sideSeen)
            {
                digits.Append(c);
                continue;
            }

            if (c is 'L' or 'C' or 'R' && digits.Length > 0 && !sideSeen)
            {
                side = c switch
                {
                    'L' => RunwaySide.Left,
                    'C' => RunwaySide.Center,
                    _ => RunwaySide.Right,
                };

                sideSeen = true;
                continue;
            }

            return false;
        }

        if (digits.Length == 0
            || !int.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            || number is < 1 or > 36)
        {
            return false;
        }

        runway = new RunwayIdentifier(number, side);
        return true;
    }

    /// <summary>
    /// Parses a runway from spoken text such as "runway zero one left" or "holding short one eight".
    /// </summary>
    /// <remarks>
    /// Digit words are folded first, then side words ("left", "centre", "right") are detected.
    /// A one- or two-digit run is accepted, so both "one eight" and "eighteen" work.
    /// </remarks>
    public static RunwayIdentifier? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        RunwaySide side = RunwaySide.None;
        if (text.Contains("left", StringComparison.OrdinalIgnoreCase))
        {
            side = RunwaySide.Left;
        }
        else if (text.Contains("right", StringComparison.OrdinalIgnoreCase))
        {
            side = RunwaySide.Right;
        }
        else if (text.Contains("center", StringComparison.OrdinalIgnoreCase)
            || text.Contains("centre", StringComparison.OrdinalIgnoreCase))
        {
            side = RunwaySide.Center;
        }

        // Look for the runway number after the word "runway" when it is present, because a
        // transmission can contain several numbers ("runway one eight, wind two seven zero").
        string scope = text;
        int runwayWordIndex = text.IndexOf("runway", StringComparison.OrdinalIgnoreCase);
        if (runwayWordIndex >= 0)
        {
            scope = text[(runwayWordIndex + "runway".Length)..];
        }

        string? digits = SpokenNumbers.ParseSpokenDigitRun(scope, expectedLength: 2)
            ?? SpokenNumbers.ParseSpokenDigitRun(scope, expectedLength: 1);

        if (digits is null)
        {
            // Fall back to a spelled-out number such as "eighteen".
            int? spoken = SpokenNumbers.ParseSpokenAltitude(scope);
            if (spoken is >= 1 and <= 36)
            {
                return new RunwayIdentifier(spoken.Value, side);
            }

            return null;
        }

        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            || number is < 1 or > 36)
        {
            return null;
        }

        return new RunwayIdentifier(number, side);
    }

    /// <summary>The reciprocal runway — 18 becomes 36, and L/R swap.</summary>
    public RunwayIdentifier Reciprocal()
    {
        int opposite = Number > 18 ? Number - 18 : Number + 18;
        RunwaySide flipped = Side switch
        {
            RunwaySide.Left => RunwaySide.Right,
            RunwaySide.Right => RunwaySide.Left,
            _ => Side,
        };

        return new RunwayIdentifier(opposite, flipped);
    }

    /// <summary>Approximate magnetic heading of the runway centreline.</summary>
    public Heading CenterlineHeading => Heading.FromDegrees(Number * 10);

    /// <summary>Renders as spoken: <c>"runway zero one left"</c>.</summary>
    public string ToSpoken()
    {
        string digits = SpokenNumbers.DigitsIndividually(Number, width: 2);
        string sideWord = Side switch
        {
            RunwaySide.Left => " left",
            RunwaySide.Center => " center",
            RunwaySide.Right => " right",
            _ => string.Empty,
        };

        return $"runway {digits}{sideWord}";
    }

    /// <summary>Renders the bare designator as spoken, without the word "runway".</summary>
    public string ToSpokenDesignator() => ToSpoken()["runway ".Length..];

    /// <summary>Renders for display: <c>"01L"</c>.</summary>
    public string ToDisplay()
    {
        string digits = Number.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
        string sideLetter = Side switch
        {
            RunwaySide.Left => "L",
            RunwaySide.Center => "C",
            RunwaySide.Right => "R",
            _ => string.Empty,
        };

        return digits + sideLetter;
    }

    /// <summary>Canonical readback comparison value, identical to the display form.</summary>
    public string ToComparisonValue() => ToDisplay();

    /// <inheritdoc />
    public override string ToString() => ToDisplay();
}

/// <summary>Parallel-runway suffix.</summary>
public enum RunwaySide
{
    None = 0,
    Left,
    Center,
    Right,
}
