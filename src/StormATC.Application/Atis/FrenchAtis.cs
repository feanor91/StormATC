using System.Globalization;
using StormATC.Domain.Atc;
using StormATC.Domain.Phraseology;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Atis;

/// <summary>
/// The French wording of a departure ATIS, built from the same normalised facts as the English
/// edition.
/// </summary>
/// <remarks>
/// Only the words differ: the information letter, the runway, the weather and therefore the
/// operational signature are exactly those of the English edition. Numbers follow the DSNA
/// manual (everyday cardinals, "Q N H mille treize", "vent deux cent soixante-dix degrés,
/// douze nœuds").
/// </remarks>
internal static class FrenchAtis
{
    /// <summary>Appends the French spoken and display lines of one ATIS edition.</summary>
    public static void Compose(
        string airportName,
        string airportDisplayName,
        string information,
        DateTimeOffset utc,
        string timeDigits,
        WeatherSnapshot? weather,
        RunwayIdentifier? runway,
        List<string> spoken,
        List<string> display)
    {
        spoken.Add($"Ici {airportName}, information {information}, enregistrée à {SpokenTime(utc)} UTC.");
        display.Add($"{airportDisplayName} information {information}.");
        display.Add($"{timeDigits}Z.");

        if (runway is RunwayIdentifier runwayValue)
        {
            spoken.Add($"Piste en service {FrenchAtcPhraseology.Instance.SpeakRunwayDesignator(runwayValue)}.");
            display.Add($"Piste en service {runwayValue.ToDisplay()}.");
        }
        else
        {
            spoken.Add("Information piste non disponible.");
            display.Add("Information piste non disponible.");
        }

        AddWind(weather, spoken, display);
        AddVisibility(weather, spoken, display);
        AddTemperature(weather, spoken, display);
        AddQnh(weather, spoken, display);

        spoken.Add($"Au premier contact, annoncez avoir reçu l'information {information}.");
        display.Add($"Au premier contact, annoncez l'information {information}.");
    }

    /// <summary>
    /// "quatorze heures trente", "une heure", "zéro heure cinq": hours are feminine in French,
    /// so "un" becomes "une".
    /// </summary>
    internal static string SpokenTime(DateTimeOffset utc)
    {
        int hours = utc.Hour;
        int minutes = utc.Minute;

        string hourWords = hours switch
        {
            0 => "zéro heure",
            1 => "une heure",
            21 => "vingt et une heures",
            _ => $"{FrenchSpokenNumbers.Cardinal(hours)} heures",
        };

        if (minutes == 0)
        {
            return hourWords;
        }

        string minuteWords = FrenchSpokenNumbers.Cardinal(minutes);
        if (minuteWords == "un" || minuteWords.EndsWith(" et un", StringComparison.Ordinal))
        {
            minuteWords += "e";
        }

        return $"{hourWords} {minuteWords}";
    }

    private static void AddWind(WeatherSnapshot? weather, List<string> spoken, List<string> display)
    {
        if (weather is not { HasWind: true, WindSpeedKnots: int speed })
        {
            return;
        }

        if (speed == 0)
        {
            spoken.Add("Vent calme.");
            display.Add("Vent calme.");
            return;
        }

        Heading direction = weather.WindDirection!.Value;
        spoken.Add(
            $"Vent {FrenchSpokenNumbers.Heading(direction.Degrees)} degrés, "
            + $"{FrenchSpokenNumbers.Cardinal(speed)} nœuds.");
        display.Add($"Vent {direction.ToDisplay()}° {speed.ToString(CultureInfo.InvariantCulture)} kt.");
    }

    private static void AddVisibility(WeatherSnapshot? weather, List<string> spoken, List<string> display)
    {
        if (weather?.VisibilityMeters is not int visibility)
        {
            return;
        }

        if (visibility >= 10_000)
        {
            spoken.Add("Visibilité supérieure à dix kilomètres.");
            display.Add("Visibilité 10 km ou plus.");
            return;
        }

        spoken.Add($"Visibilité {FrenchSpokenNumbers.Cardinal(visibility)} mètres.");
        display.Add($"Visibilité {visibility.ToString("N0", CultureInfo.InvariantCulture)} m.");
    }

    private static void AddTemperature(WeatherSnapshot? weather, List<string> spoken, List<string> display)
    {
        if (weather?.TemperatureCelsius is not int temperature)
        {
            return;
        }

        string words = FrenchSpokenNumbers.Cardinal(Math.Abs(temperature));
        if (temperature < 0)
        {
            words = $"moins {words}";
        }

        spoken.Add($"Température {words}.");
        display.Add($"Température {temperature.ToString(CultureInfo.InvariantCulture)}°C.");
    }

    private static void AddQnh(WeatherSnapshot? weather, List<string> spoken, List<string> display)
    {
        if (weather?.QnhHectopascals is not int qnh)
        {
            return;
        }

        spoken.Add($"Q N H {FrenchSpokenNumbers.Cardinal(qnh)}.");
        display.Add($"QNH {qnh.ToString(CultureInfo.InvariantCulture)} hPa.");
    }
}
