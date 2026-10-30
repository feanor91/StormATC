using System.Globalization;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Weather;

namespace StormATC.Application.Weather;

/// <summary>One line of a decoded METAR.</summary>
/// <param name="Label">What the line is about: wind, visibility, and so on.</param>
/// <param name="Value">What the report says about it.</param>
public sealed record MetarLine(string Label, string Value);

/// <summary>Turns a <see cref="DecodedMetar"/> into plain sentences, in English or French.</summary>
public static class MetarDescriber
{
    private static readonly (string Code, string English, string French)[] Phenomena =
    [
        ("DZ", "drizzle", "bruine"),
        ("RA", "rain", "pluie"),
        ("SN", "snow", "neige"),
        ("SG", "snow grains", "neige en grains"),
        ("IC", "ice crystals", "cristaux de glace"),
        ("PL", "ice pellets", "granules de glace"),
        ("GR", "hail", "grêle"),
        ("GS", "small hail", "grésil"),
        ("UP", "unknown precipitation", "précipitations inconnues"),
        ("BR", "mist", "brume"),
        ("FG", "fog", "brouillard"),
        ("FU", "smoke", "fumée"),
        ("VA", "volcanic ash", "cendres volcaniques"),
        ("DU", "dust", "poussière"),
        ("SA", "sand", "sable"),
        ("HZ", "haze", "brume sèche"),
        ("PY", "spray", "embruns"),
        ("PO", "dust whirls", "tourbillons de poussière"),
        ("SQ", "squalls", "grains"),
        ("FC", "funnel cloud", "nuage en entonnoir"),
        ("SS", "sandstorm", "tempête de sable"),
        ("DS", "duststorm", "tempête de poussière"),
    ];

    private static readonly (string Code, string English, string French)[] Descriptors =
    [
        ("MI", "shallow", "mince"),
        ("PR", "partial", "partiel"),
        ("BC", "patches of", "bancs de"),
        ("DR", "low drifting", "chasse basse de"),
        ("BL", "blowing", "chasse haute de"),
        ("SH", "showers of", "averses de"),
        ("TS", "thunderstorm with", "orage avec"),
        ("FZ", "freezing", "se congelant :"),
    ];

    /// <summary>Describes a report line by line.</summary>
    /// <param name="metar">The decoded report.</param>
    /// <param name="language">The language of the sentences.</param>
    public static IReadOnlyList<MetarLine> Describe(DecodedMetar metar, PhraseologyLanguage language)
    {
        ArgumentNullException.ThrowIfNull(metar);
        bool fr = language == PhraseologyLanguage.French;
        var lines = new List<MetarLine>
        {
            new("Observation", DescribeTime(metar, fr)),
            new(fr ? "Vent" : "Wind", DescribeWind(metar, fr)),
            new(fr ? "Visibilité" : "Visibility", DescribeVisibility(metar, fr)),
        };

        if (metar.Phenomena.Count > 0)
        {
            lines.Add(new(
                fr ? "Temps présent" : "Weather",
                string.Join(", ", metar.Phenomena.Select(code => DescribePhenomenon(code, fr)))));
        }

        lines.Add(new(fr ? "Nuages" : "Clouds", DescribeClouds(metar, fr)));

        if (metar.TemperatureCelsius is int temperature)
        {
            string dew = metar.DewPointCelsius is int dewPoint
                ? fr ? $", point de rosée {dewPoint} °C" : $", dew point {dewPoint} °C"
                : string.Empty;
            lines.Add(new(fr ? "Température" : "Temperature", $"{temperature} °C{dew}"));
        }

        if (metar.QnhHectopascals is int qnh)
        {
            lines.Add(new("QNH", $"{qnh.ToString(CultureInfo.InvariantCulture)} hPa"));
        }

        if (metar.Trend is { Length: > 0 } trend)
        {
            lines.Add(new(fr ? "Tendance" : "Trend", DescribeTrend(trend, fr)));
        }

        if (metar.Remarks is { Length: > 0 } remarks)
        {
            lines.Add(new(fr ? "Remarques" : "Remarks", remarks));
        }

        return lines;
    }

    private static string DescribeTime(DecodedMetar metar, bool fr)
    {
        if (metar is not { Day: int day, Hour: int hour, Minute: int minute })
        {
            return fr ? "heure non indiquée" : "time not stated";
        }

        string when = fr
            ? $"le {day}, à {hour:00}:{minute:00} UTC"
            : $"day {day}, at {hour:00}:{minute:00} UTC";
        string kind = metar.IsAutomatic ? fr ? " (station automatique)" : " (automatic station)" : string.Empty;
        string correction = metar.IsCorrection ? fr ? " (rectificatif)" : " (correction)" : string.Empty;
        return when + kind + correction;
    }

    private static string DescribeWind(DecodedMetar metar, bool fr)
    {
        if (metar.WindSpeedKnots is not int speed)
        {
            return fr ? "non communiqué" : "not reported";
        }

        if (speed == 0)
        {
            return fr ? "calme" : "calm";
        }

        string direction = metar.WindIsVariable
            ? fr ? "variable" : "variable"
            : metar.WindDirectionDegrees is int degrees
                ? $"{degrees:000}°"
                : fr ? "direction non communiquée" : "direction not reported";
        string text = fr ? $"{direction}, {speed} kt" : $"{direction} at {speed} kt";

        if (metar.GustKnots is int gust)
        {
            text += fr ? $", rafales {gust} kt" : $", gusting {gust} kt";
        }

        if (metar is { WindVariationFromDegrees: int from, WindVariationToDegrees: int to })
        {
            text += fr ? $", variant de {from:000}° à {to:000}°" : $", varying from {from:000}° to {to:000}°";
        }

        return text;
    }

    private static string DescribeVisibility(DecodedMetar metar, bool fr)
    {
        if (metar.IsCavok)
        {
            return fr
                ? "CAVOK : 10 km ou plus, pas de nuage significatif, pas de phénomène significatif"
                : "CAVOK: 10 km or more, no significant cloud, no significant weather";
        }

        return metar.VisibilityMeters switch
        {
            null => fr ? "non communiquée" : "not reported",
            >= 9999 => fr ? "10 km ou plus" : "10 km or more",
            >= 5000 and int meters => $"{(meters / 1000d).ToString("0.#", CultureInfo.InvariantCulture)} km",
            int meters => $"{meters.ToString(CultureInfo.InvariantCulture)} m",
        };
    }

    private static string DescribePhenomenon(string code, bool fr)
    {
        string rest = code;
        string intensity = string.Empty;
        if (rest.StartsWith('-'))
        {
            intensity = fr ? "faible " : "light ";
            rest = rest[1..];
        }
        else if (rest.StartsWith('+'))
        {
            intensity = fr ? "fort " : "heavy ";
            rest = rest[1..];
        }

        string vicinity = string.Empty;
        if (rest.StartsWith("VC", StringComparison.Ordinal))
        {
            vicinity = fr ? " à proximité" : " in the vicinity";
            rest = rest[2..];
        }

        var words = new List<string>();
        foreach ((string descriptor, string english, string french) in Descriptors)
        {
            if (rest.StartsWith(descriptor, StringComparison.Ordinal))
            {
                words.Add(fr ? french : english);
                rest = rest[descriptor.Length..];
                break;
            }
        }

        while (rest.Length >= 2)
        {
            string part = rest[..2];
            (string Code, string English, string French) match =
                Array.Find(Phenomena, p => p.Code == part);
            words.Add(match.Code is null ? part : fr ? match.French : match.English);
            rest = rest[2..];
        }

        return intensity + string.Join(' ', words) + vicinity;
    }

    private static string DescribeClouds(DecodedMetar metar, bool fr)
    {
        if (metar.IsCavok)
        {
            return fr ? "aucun nuage significatif" : "no significant cloud";
        }

        if (metar.Clouds.Count == 0)
        {
            return metar.NoSignificantCloud
                ? fr ? "aucun nuage significatif" : "no significant cloud"
                : fr ? "non communiqués" : "not reported";
        }

        return string.Join(
            "; ",
            metar.Clouds.Select(layer =>
            {
                string coverage = layer.Coverage switch
                {
                    "FEW" => fr ? "quelques nuages (1 à 2 octas)" : "few (1-2 oktas)",
                    "SCT" => fr ? "épars (3 à 4 octas)" : "scattered (3-4 oktas)",
                    "BKN" => fr ? "fragmentés (5 à 7 octas)" : "broken (5-7 oktas)",
                    "OVC" => fr ? "couvert (8 octas)" : "overcast (8 oktas)",
                    "VV" => fr ? "ciel invisible, visibilité verticale" : "sky obscured, vertical visibility",
                    _ => layer.Coverage,
                };
                string height = layer.BaseFeet is int feet
                    ? fr
                        ? $" à {feet.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ')} ft"
                        : $" at {feet.ToString("N0", CultureInfo.InvariantCulture)} ft"
                    : string.Empty;
                string type = layer.Type switch
                {
                    "CB" => fr ? ", cumulonimbus" : ", cumulonimbus",
                    "TCU" => fr ? ", cumulus bourgeonnants" : ", towering cumulus",
                    _ => string.Empty,
                };
                return coverage + height + type;
            }));
    }

    private static string DescribeTrend(string trend, bool fr) =>
        trend.StartsWith("NOSIG", StringComparison.Ordinal)
            ? fr ? "aucun changement significatif prévu (NOSIG)" : "no significant change expected (NOSIG)"
            : trend;
}
