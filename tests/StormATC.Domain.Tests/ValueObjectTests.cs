using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Tests;

/// <summary>
/// Normalisation and formatting of the value objects the engine compares against.
/// </summary>
/// <remarks>
/// These matter more than they look. A readback is judged by comparing normalised strings, so a
/// bug here silently turns a correct readback into a rejected one, or worse, the reverse.
/// </remarks>
public class CallsignTests
{
    [Theory]
    [InlineData("STORM123", "STORM123")]
    [InlineData("storm 123", "STORM123")]
    [InlineData("Storm-123", "STORM123")]
    [InlineData("  STORM  123  ", "STORM123")]
    public void Normalize_strips_spacing_and_punctuation(string input, string expected) =>
        Assert.Equal(expected, Callsign.Create(input).Normalized);

    [Fact]
    public void Normalize_folds_spoken_digits()
    {
        Callsign callsign = Callsign.Create("Storm one two three");
        Assert.Equal("STORM123", callsign.Normalized);
    }

    [Fact]
    public void Numeric_suffix_is_extracted()
    {
        Callsign callsign = Callsign.Create("STORM123");
        Assert.Equal("123", callsign.NumericSuffix);
    }

    [Theory]
    [InlineData("storm one two three request ifr clearance")]
    [InlineData("storm 123 ready for pushback")]
    [InlineData("storm123 roger")]
    public void MatchesTranscript_accepts_the_full_callsign(string transcript) =>
        Assert.True(Callsign.Create("STORM123").MatchesTranscript(transcript));

    [Fact]
    public void MatchesTranscript_accepts_a_bare_numeric_suffix()
    {
        // Pilots routinely abbreviate once two-way communication is established.
        Assert.True(Callsign.Create("STORM123").MatchesTranscript("one two three descending"));
    }

    [Theory]
    [InlineData("speedbird four five six request taxi")]
    [InlineData("no callsign at all here")]
    public void MatchesTranscript_rejects_a_different_callsign(string transcript) =>
        Assert.False(Callsign.Create("STORM123").MatchesTranscript(transcript));

    [Fact]
    public void ToSpoken_expands_the_numeric_suffix()
    {
        Assert.Equal("Storm one two three", Callsign.Create("STORM123").ToSpoken());
    }

    [Fact]
    public void ToSpoken_spells_short_prefixes_phonetically()
    {
        Assert.Equal("Sierra Tango one two three", Callsign.Create("ST123").ToSpoken());
    }

    [Fact]
    public void Equality_uses_the_normalized_form()
    {
        Assert.Equal(Callsign.Create("storm 123"), Callsign.Create("STORM-123"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void TryCreate_rejects_unusable_input(string? input) =>
        Assert.False(Callsign.TryCreate(input, out _));
}

/// <summary>Altitude parsing, comparison, and the flight-level distinction.</summary>
public class AltitudeTests
{
    [Fact]
    public void Flight_level_and_feet_compare_equal_at_the_same_height()
    {
        // FL120 and 12,000 ft are the same height. The readback evaluator compares the
        // comparison value, so these must not read as different.
        Assert.Equal(
            Altitude.FromFlightLevel(120).ToComparisonValue(),
            Altitude.FromFeet(12_000).ToComparisonValue());
    }

    [Fact]
    public void Flight_level_and_feet_still_speak_differently()
    {
        Assert.Equal("flight level one two zero", Altitude.FromFlightLevel(120).ToSpoken());
        Assert.Equal("one two thousand", Altitude.FromFeet(12_000).ToSpoken());
        Assert.Equal("seven thousand", Altitude.FromFeet(7_000).ToSpoken());
        Assert.Equal("three thousand five hundred", Altitude.FromFeet(3_500).ToSpoken());
    }

    [Theory]
    [InlineData(7_000, "7,000 ft")]
    [InlineData(3_000, "3,000 ft")]
    public void Feet_display_with_a_thousands_separator(int feet, string expected) =>
        Assert.Equal(expected, Altitude.FromFeet(feet).ToDisplay());

    [Fact]
    public void Flight_level_displays_as_FL()
    {
        Assert.Equal("FL120", Altitude.FromFlightLevel(120).ToDisplay());
    }

    [Theory]
    [InlineData("flight level one two zero", 12_000, true)]
    [InlineData("seven thousand", 7_000, false)]
    [InlineData("three thousand feet", 3_000, false)]
    [InlineData("FL350", 35_000, true)]
    public void Parse_reads_both_spoken_and_written_forms(
        string text,
        int expectedFeet,
        bool expectedFlightLevel)
    {
        Altitude? parsed = Altitude.Parse(text);

        Assert.NotNull(parsed);
        Assert.Equal(expectedFeet, parsed.Value.Feet);
        Assert.Equal(expectedFlightLevel, parsed.Value.IsFlightLevel);
    }

    [Fact]
    public void Parse_recovers_a_literal_altitude_glued_to_the_next_field()
    {
        Altitude? parsed = Altitude.Parse("initially 7000AndSquawk5217");

        Assert.Equal(7_000, parsed?.Feet);
    }

    [Theory]
    [InlineData("climb 7 000 squawk five two one seven", 7_000)]
    [InlineData("climb 12 000", 12_000)]
    public void Parse_recovers_a_space_grouped_literal_altitude(string text, int expectedFeet)
    {
        Assert.Equal(expectedFeet, Altitude.Parse(text)?.Feet);
    }

    [Fact]
    public void A_callsign_number_is_not_mistaken_for_an_altitude()
    {
        Assert.Null(Altitude.Parse("ST123 request taxi"));
    }

    [Theory]
    [InlineData(70_000)]
    [InlineData(-5_000)]
    public void Out_of_range_altitudes_are_rejected(int feet) =>
        Assert.False(Altitude.TryFromFeet(feet, out _));

    [Fact]
    public void Ordering_follows_height()
    {
        Assert.True(Altitude.FromFeet(3_000) < Altitude.FromFeet(7_000));
        Assert.True(Altitude.FromFlightLevel(120) > Altitude.FromFeet(7_000));
    }
}

/// <summary>Heading wrapping and formatting.</summary>
public class HeadingTests
{
    [Theory]
    [InlineData(0, 360)]
    [InlineData(360, 360)]
    [InlineData(361, 1)]
    [InlineData(-10, 350)]
    [InlineData(720, 360)]
    public void Degrees_wrap_into_one_to_three_sixty(int input, int expected) =>
        Assert.Equal(expected, Heading.FromDegrees(input).Degrees);

    [Theory]
    [InlineData(90, "zero nine zero")]
    [InlineData(360, "three six zero")]
    [InlineData(5, "zero zero five")]
    public void ToSpoken_always_uses_three_digits(int degrees, string expected) =>
        Assert.Equal(expected, Heading.FromDegrees(degrees).ToSpoken());

    [Theory]
    [InlineData(90, "090°")]
    [InlineData(5, "005°")]
    public void ToDisplay_pads_to_three_digits(int degrees, string expected) =>
        Assert.Equal(expected, Heading.FromDegrees(degrees).ToDisplay());

    [Theory]
    [InlineData(350, 10, 20)]
    [InlineData(10, 350, 20)]
    [InlineData(90, 270, 180)]
    [InlineData(180, 180, 0)]
    public void DifferenceTo_takes_the_short_way_around(int from, int to, int expected) =>
        Assert.Equal(expected, Heading.FromDegrees(from).DifferenceTo(Heading.FromDegrees(to)));

    [Theory]
    [InlineData("heading zero nine zero", 90)]
    [InlineData("fly heading two seven zero", 270)]
    public void Parse_reads_a_spoken_heading(string text, int expected)
    {
        Heading? parsed = Heading.Parse(text);

        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed.Value.Degrees);
    }
}

/// <summary>Frequency range enforcement and spoken form.</summary>
public class FrequencyTests
{
    [Theory]
    [InlineData(118.000)]
    [InlineData(121.800)]
    [InlineData(136.975)]
    public void Frequencies_inside_the_aeronautical_band_are_accepted(double megahertz) =>
        Assert.True(Frequency.TryFromMegahertz((decimal)megahertz, out _));

    [Theory]
    [InlineData(117.999)]
    [InlineData(137.000)]
    [InlineData(0)]
    public void Frequencies_outside_the_band_are_rejected(double megahertz) =>
        Assert.False(Frequency.TryFromMegahertz((decimal)megahertz, out _));

    [Fact]
    public void ToDisplay_always_shows_three_decimals()
    {
        Assert.Equal("121.800", Frequency.FromMegahertz(121.8m).ToDisplay());
    }

    [Fact]
    public void ToComparisonValue_ignores_trailing_zeros()
    {
        // "one two one decimal eight" and "121.800" are the same channel, so a readback of
        // either must compare equal.
        Assert.Equal(
            Frequency.FromMegahertz(121.8m).ToComparisonValue(),
            Frequency.FromMegahertz(121.800m).ToComparisonValue());
    }

    [Fact]
    public void ToSpoken_uses_decimal_not_point()
    {
        Assert.Contains("decimal", Frequency.FromMegahertz(118.300m).ToSpoken(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("contact tower one one eight decimal three", 118.300)]
    [InlineData("119.100", 119.100)]
    [InlineData(
        "SierraTango123 departure frequency one two one decimal seven",
        121.700)]
    public void Parse_reads_spoken_and_written_frequencies(string text, double expected)
    {
        Frequency? parsed = Frequency.Parse(text);

        Assert.NotNull(parsed);
        Assert.Equal((decimal)expected, parsed.Value.Megahertz);
    }
}

/// <summary>Transponder code validation.</summary>
public class SquawkCodeTests
{
    [Theory]
    [InlineData("4271")]
    [InlineData("0000")]
    [InlineData("7777")]
    public void Four_octal_digits_are_accepted(string digits) =>
        Assert.True(SquawkCode.TryCreate(digits, out _));

    [Theory]
    [InlineData("4281")]  // 8 is not an octal digit
    [InlineData("429")]   // too short
    [InlineData("42711")] // too long
    [InlineData("abcd")]
    [InlineData("")]
    public void Anything_else_is_rejected(string digits) =>
        Assert.False(SquawkCode.TryCreate(digits, out _));

    [Fact]
    public void ToSpoken_reads_the_digits_individually()
    {
        Assert.Equal("four two seven one", SquawkCode.Create("4271").ToSpoken());
    }

    [Fact]
    public void Parse_scopes_to_squawk_when_an_altitude_comes_first()
    {
        Assert.True(
            SquawkCode.TryParse(
                "runway 26 left initially 7000AndSquawk5217",
                out SquawkCode? parsed));
        Assert.Equal(SquawkCode.Create("5217"), parsed);
    }

    [Fact]
    public void Parse_recovers_when_speech_recognition_hears_one_as_point()
    {
        Assert.True(
            SquawkCode.TryParse(
                "squawk five two point seven",
                out SquawkCode? parsed));
        Assert.Equal(SquawkCode.Create("5217"), parsed);
    }

    [Theory]
    [InlineData("7700")]
    [InlineData("7600")]
    [InlineData("7500")]
    public void Emergency_codes_are_recognised(string digits) =>
        Assert.True(SquawkCode.Create(digits).IsEmergencyCode);

    [Fact]
    public void An_ordinary_code_is_not_an_emergency()
    {
        Assert.False(SquawkCode.Create("4271").IsEmergencyCode);
    }
}

/// <summary>Runway designator parsing, including the side suffix.</summary>
public class RunwayIdentifierTests
{
    [Theory]
    [InlineData("18", 18, RunwaySide.None)]
    [InlineData("36", 36, RunwaySide.None)]
    [InlineData("09L", 9, RunwaySide.Left)]
    [InlineData("27R", 27, RunwaySide.Right)]
    [InlineData("06C", 6, RunwaySide.Center)]
    [InlineData("6", 6, RunwaySide.None)]
    public void Designators_parse_with_and_without_a_side(string input, int number, RunwaySide side)
    {
        Assert.True(RunwayIdentifier.TryCreate(input, out RunwayIdentifier runway));
        Assert.Equal(number, runway.Number);
        Assert.Equal(side, runway.Side);
    }

    [Theory]
    [InlineData("00")]
    [InlineData("37")]
    [InlineData("18X")]
    [InlineData("")]
    public void Invalid_designators_are_rejected(string input) =>
        Assert.False(RunwayIdentifier.TryCreate(input, out _));

    [Fact]
    public void ToDisplay_pads_a_single_digit()
    {
        Assert.Equal("06L", RunwayIdentifier.Create("6L").ToDisplay());
    }

    [Fact]
    public void ToSpoken_reads_the_side_in_full()
    {
        Assert.Equal("runway zero nine left", RunwayIdentifier.Create("09L").ToSpoken());
    }

    [Fact]
    public void Reciprocal_flips_by_one_eighty_and_swaps_the_side()
    {
        RunwayIdentifier reciprocal = RunwayIdentifier.Create("09L").Reciprocal();

        Assert.Equal(27, reciprocal.Number);
        Assert.Equal(RunwaySide.Right, reciprocal.Side);
    }

    [Fact]
    public void Parse_scopes_the_search_to_the_word_runway()
    {
        // "squawk four two seven one, runway one eight" must find 18, not 42.
        RunwayIdentifier? parsed = RunwayIdentifier.Parse("squawk four two seven one runway one eight");

        Assert.NotNull(parsed);
        Assert.Equal(18, parsed.Value.Number);
    }

    [Fact]
    public void Parse_returns_null_when_no_runway_is_mentioned()
    {
        Assert.Null(RunwayIdentifier.Parse("storm one two three request pushback"));
    }

    [Fact]
    public void Centerline_heading_follows_the_designator()
    {
        Assert.Equal(180, RunwayIdentifier.Create("18").CenterlineHeading.Degrees);
        Assert.Equal(360, RunwayIdentifier.Create("36").CenterlineHeading.Degrees);
    }
}

/// <summary>Geodesic helpers used by the incursion and proximity checks.</summary>
public class GeoPositionTests
{
    [Fact]
    public void Distance_to_self_is_zero()
    {
        GeoPosition position = GeoPosition.Create(52.3, 4.7);
        Assert.Equal(0, position.DistanceNauticalMilesTo(position), precision: 6);
    }

    [Fact]
    public void One_minute_of_latitude_is_about_one_nautical_mile()
    {
        GeoPosition south = GeoPosition.Create(52.0, 4.7);
        GeoPosition north = GeoPosition.Create(52.0 + (1.0 / 60.0), 4.7);

        Assert.Equal(1.0, south.DistanceNauticalMilesTo(north), precision: 2);
    }

    [Fact]
    public void Bearing_due_north_is_three_sixty()
    {
        GeoPosition south = GeoPosition.Create(52.0, 4.7);
        GeoPosition north = GeoPosition.Create(52.1, 4.7);

        Assert.Equal(360, south.TrueBearingTo(north).Degrees);
    }

    [Fact]
    public void Project_then_measure_returns_the_original_distance()
    {
        GeoPosition start = GeoPosition.Create(52.0, 4.7);
        GeoPosition projected = start.Project(Heading.FromDegrees(90), distanceNauticalMiles: 5);

        Assert.Equal(5.0, start.DistanceNauticalMilesTo(projected), precision: 2);
    }

    [Theory]
    [InlineData(0.5, true)]
    [InlineData(0.05, false)]
    public void IsWithin_respects_the_radius(double radius, bool expected)
    {
        GeoPosition a = GeoPosition.Create(52.0, 4.7);
        GeoPosition b = a.Project(Heading.FromDegrees(90), distanceNauticalMiles: 0.1);

        Assert.Equal(expected, a.IsWithin(b, radius));
    }

    [Theory]
    [InlineData(91.0, 0)]
    [InlineData(0, 181.0)]
    public void Out_of_range_coordinates_are_rejected(double latitude, double longitude) =>
        Assert.False(GeoPosition.TryCreate(latitude, longitude, out _));
}
