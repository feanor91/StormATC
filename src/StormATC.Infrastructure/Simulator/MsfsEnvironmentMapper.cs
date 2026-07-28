using StormATC.Domain.Atc;
using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.Simulator;

/// <summary>Converts live simulator environment values into trusted domain observations.</summary>
internal static class MsfsEnvironmentMapper
{
    /// <summary>
    /// Builds an ambient weather observation at the user aircraft.
    /// </summary>
    internal static WeatherSnapshot? MapWeather(
        double windDirectionTrueDegrees,
        double windSpeedKnots,
        double seaLevelPressureHectopascals,
        double visibilityMeters,
        double temperatureCelsius,
        double trueHeadingDegrees,
        double magneticHeadingDegrees)
    {
        if (!double.IsFinite(windDirectionTrueDegrees)
            || !double.IsFinite(windSpeedKnots)
            || !double.IsFinite(seaLevelPressureHectopascals))
        {
            return null;
        }

        double magneticVariation = NormalizeSignedDegrees(
            trueHeadingDegrees - magneticHeadingDegrees);
        Heading magneticWind = Heading.FromDegrees(
            windDirectionTrueDegrees - magneticVariation);

        int speed = (int)Math.Round(
            Math.Clamp(windSpeedKnots, 0, 250),
            MidpointRounding.AwayFromZero);
        int qnh = (int)Math.Round(
            Math.Clamp(seaLevelPressureHectopascals, 850, 1100),
            MidpointRounding.AwayFromZero);
        int? visibility = double.IsFinite(visibilityMeters)
            ? (int)Math.Round(
                Math.Clamp(visibilityMeters, 0, 100_000),
                MidpointRounding.AwayFromZero)
            : null;
        int? temperature = double.IsFinite(temperatureCelsius)
            ? (int)Math.Round(
                Math.Clamp(temperatureCelsius, -100, 70),
                MidpointRounding.AwayFromZero)
            : null;

        return new WeatherSnapshot
        {
            WindDirection = magneticWind,
            WindSpeedKnots = speed,
            QnhHectopascals = qnh,
            VisibilityMeters = visibility,
            TemperatureCelsius = temperature,
            Source = "Microsoft Flight Simulator ambient conditions",
        };
    }

    private static double NormalizeSignedDegrees(double degrees)
    {
        double normalized = degrees % 360;
        if (normalized > 180)
        {
            normalized -= 360;
        }
        else if (normalized < -180)
        {
            normalized += 360;
        }

        return normalized;
    }
}
