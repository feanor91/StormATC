using StormATC.Domain.Atc;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Weather;

/// <summary>One layer of cloud in a METAR.</summary>
/// <param name="Coverage">FEW, SCT, BKN, OVC, or VV for a sky that cannot be seen.</param>
/// <param name="BaseFeet">The height of the base above the aerodrome, when reported.</param>
/// <param name="Type">CB or TCU, when the layer is that kind of cloud.</param>
public sealed record MetarCloudLayer(string Coverage, int? BaseFeet, string? Type);

/// <summary>A METAR read into its parts.</summary>
/// <remarks>
/// Only what the report states is filled in. A group that is absent or not understood leaves its
/// value null: nothing is inferred.
/// </remarks>
public sealed record DecodedMetar
{
    /// <summary>The report as received.</summary>
    public required string Raw { get; init; }

    /// <summary>The ICAO code of the aerodrome that made the observation.</summary>
    public required string Station { get; init; }

    /// <summary>The day of the month of the observation (UTC).</summary>
    public int? Day { get; init; }

    /// <summary>The hour of the observation (UTC).</summary>
    public int? Hour { get; init; }

    /// <summary>The minute of the observation (UTC).</summary>
    public int? Minute { get; init; }

    /// <summary>True when the report was made by an automatic station.</summary>
    public bool IsAutomatic { get; init; }

    /// <summary>True when the report corrects an earlier one.</summary>
    public bool IsCorrection { get; init; }

    /// <summary>The wind direction in degrees, or null when it is variable or not reported.</summary>
    public int? WindDirectionDegrees { get; init; }

    /// <summary>True when the wind is reported as variable (VRB).</summary>
    public bool WindIsVariable { get; init; }

    /// <summary>The wind speed in knots.</summary>
    public int? WindSpeedKnots { get; init; }

    /// <summary>The gust speed in knots, when gusts are reported.</summary>
    public int? GustKnots { get; init; }

    /// <summary>The start of the sector the wind direction varies through, when reported.</summary>
    public int? WindVariationFromDegrees { get; init; }

    /// <summary>The end of the sector the wind direction varies through, when reported.</summary>
    public int? WindVariationToDegrees { get; init; }

    /// <summary>The prevailing visibility in metres (9999 stands for 10 km or more).</summary>
    public int? VisibilityMeters { get; init; }

    /// <summary>True for CAVOK: visibility 10 km or more, no significant cloud, no significant weather.</summary>
    public bool IsCavok { get; init; }

    /// <summary>The weather groups as written, for example <c>-RA</c> or <c>BR</c>.</summary>
    public IReadOnlyList<string> Phenomena { get; init; } = [];

    /// <summary>The cloud layers, lowest first as reported.</summary>
    public IReadOnlyList<MetarCloudLayer> Clouds { get; init; } = [];

    /// <summary>True when the report states there is no cloud of significance (NSC, NCD, SKC, CLR).</summary>
    public bool NoSignificantCloud { get; init; }

    /// <summary>The air temperature in degrees Celsius.</summary>
    public int? TemperatureCelsius { get; init; }

    /// <summary>The dew point in degrees Celsius.</summary>
    public int? DewPointCelsius { get; init; }

    /// <summary>The altimeter setting in hectopascals (an inches-of-mercury value is converted).</summary>
    public int? QnhHectopascals { get; init; }

    /// <summary>The landing forecast that follows the observation: NOSIG, BECMG or TEMPO with its groups.</summary>
    public string? Trend { get; init; }

    /// <summary>What follows RMK, as written.</summary>
    public string? Remarks { get; init; }

    /// <summary>The observation in the form the ATIS is made from.</summary>
    /// <param name="source">Where the report came from.</param>
    public WeatherSnapshot ToWeatherSnapshot(string? source) =>
        new()
        {
            WindDirection = WindDirectionDegrees is int degrees ? Heading.FromDegrees(degrees) : null,
            WindSpeedKnots = WindSpeedKnots,
            QnhHectopascals = QnhHectopascals,
            VisibilityMeters = IsCavok ? 10_000 : VisibilityMeters,
            TemperatureCelsius = TemperatureCelsius,
            Source = source,
        };
}
