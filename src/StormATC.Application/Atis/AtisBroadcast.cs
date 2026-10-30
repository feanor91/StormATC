using System.Globalization;
using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Atis;

/// <summary>A deterministic departure ATIS edition ready for display and speech.</summary>
public sealed record AtisBroadcast
{
    /// <summary>The aerodrome ICAO code.</summary>
    public required string AirportIcao { get; init; }

    /// <summary>The alphabetic edition identifier, for example <c>Alfa</c>.</summary>
    public required string InformationCode { get; init; }

    /// <summary>The UTC half-hour slot that produced this edition.</summary>
    public required long EditionNumber { get; init; }

    /// <summary>
    /// Canonical operational values, excluding edition letter and observation time.
    /// </summary>
    /// <remarks>
    /// The ATIS service compares this value locally to decide whether a new edition is needed.
    /// It is not produced or interpreted by a language model.
    /// </remarks>
    public required string OperationalSignature { get; init; }

    /// <summary>The frequency on which this edition is available.</summary>
    public required Frequency Frequency { get; init; }

    /// <summary>Natural aviation phraseology sent to text-to-speech.</summary>
    public required string SpokenText { get; init; }

    /// <summary>Compact values shown on the Radio page.</summary>
    public required string DisplayText { get; init; }

    /// <summary>The weather source, where the provider supplied one.</summary>
    public string? WeatherSource { get; init; }

    /// <summary>The language the edition is worded in.</summary>
    public PhraseologyLanguage Language { get; init; } = PhraseologyLanguage.English;
}

/// <summary>Builds an ATIS only from airport, flight-plan, and observed-weather facts.</summary>
/// <remarks>
/// Missing facts are explicitly omitted or labelled unavailable. The builder never asks a
/// language model to compose operational information and never guesses weather or a runway.
/// </remarks>
public static class AtisBroadcastBuilder
{
    private const int EditionMinutes = 30;

    /// <summary>Builds the current departure ATIS edition, or null before a plan/frequency exists.</summary>
    public static AtisBroadcast? Build(
        AtcSession session,
        AirportData? airport,
        WeatherSnapshot? weather,
        DateTimeOffset now) =>
        Build(
            session,
            airport,
            weather,
            now,
            GetEditionNumber(now));

    /// <summary>
    /// Builds an edition with an explicit sequential number. The always-on ATIS service uses
    /// this overload so its information letter changes only when operational facts change.
    /// </summary>
    public static AtisBroadcast? Build(
        AtcSession session,
        AirportData? airport,
        WeatherSnapshot? weather,
        DateTimeOffset now,
        long edition)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.FlightPlan is not FlightPlan plan
            || session.CurrentFrequency is not Frequency frequency)
        {
            return null;
        }

        return BuildCore(
            plan.DepartureIcao,
            frequency,
            session.AssignedDepartureRunway ?? plan.DepartureRunway,
            session.Language,
            airport,
            weather,
            now,
            edition);
    }

    /// <summary>
    /// Builds an edition for any aerodrome, with no flight plan or session behind it: the Radio page uses
    /// it to let the pilot listen to the ATIS of an aerodrome from the simulator's menus.
    /// </summary>
    /// <param name="icao">The aerodrome.</param>
    /// <param name="frequency">The frequency typed in, or null when none was.</param>
    /// <param name="language">The language of the wording.</param>
    /// <param name="airport">The aerodrome's data, when known: its name and runways.</param>
    /// <param name="weather">The observation the edition is made of.</param>
    /// <param name="now">The current time.</param>
    /// <param name="runway">The runway to announce, when the caller knows better than the aerodrome data.</param>
    public static AtisBroadcast BuildForAerodrome(
        string icao,
        Frequency? frequency,
        PhraseologyLanguage language,
        AirportData? airport,
        WeatherSnapshot? weather,
        DateTimeOffset now,
        RunwayIdentifier? runway = null) =>
        BuildCore(
            icao,
            (frequency ?? airport?.Frequencies.Atis).GetValueOrDefault(),
            runway,
            language,
            airport,
            weather,
            now,
            GetEditionNumber(now));

    private static AtisBroadcast BuildCore(
        string icao,
        Frequency frequency,
        RunwayIdentifier? plannedRunway,
        PhraseologyLanguage language,
        AirportData? airport,
        WeatherSnapshot? weather,
        DateTimeOffset now,
        long edition)
    {
        DateTimeOffset utc = now.ToUniversalTime();
        long normalizedEdition = Math.Abs(edition % 26);
        char editionLetter = (char)('A' + normalizedEdition);
        string information = PhoneticAlphabet.ForLetter(editionLetter);
        string timeDigits = utc.ToString("HHmm", CultureInfo.InvariantCulture);
        string airportName = airport?.Name is { Length: > 0 } publishedName
            ? publishedName
            : PhoneticAlphabet.SpellOut(icao);
        string airportDisplayName = airport?.Name is { Length: > 0 } displayName
            ? displayName
            : icao;

        WeatherSnapshot? atisWeather = NormalizeWeather(weather);
        RunwayIdentifier? runway =
            plannedRunway
            ?? airport?.SelectActiveRunway(atisWeather?.WindDirection)?.Identifier;

        var spoken = new List<string>();
        var display = new List<string>();

        if (language == PhraseologyLanguage.French)
        {
            FrenchAtis.Compose(
                airport?.Name is { Length: > 0 } ? airport.Name : FrenchSpokenNumbers.SpellOut(icao),
                airportDisplayName,
                information,
                utc,
                timeDigits,
                atisWeather,
                runway,
                spoken,
                display);
        }
        else
        {
            ComposeEnglish(
                airportName,
                airportDisplayName,
                information,
                timeDigits,
                atisWeather,
                runway,
                spoken,
                display);
        }

        return new AtisBroadcast
        {
            AirportIcao = icao,
            InformationCode = information,
            EditionNumber = edition,
            OperationalSignature = BuildOperationalSignature(
                icao,
                airport,
                frequency,
                runway,
                atisWeather),
            Frequency = frequency,
            SpokenText = string.Join(' ', spoken),
            DisplayText = string.Join(' ', display),
            WeatherSource = weather?.Source,
            Language = language,
        };
    }

    private static void ComposeEnglish(
        string airportName,
        string airportDisplayName,
        string information,
        string timeDigits,
        WeatherSnapshot? atisWeather,
        RunwayIdentifier? runway,
        List<string> spoken,
        List<string> display)
    {
        spoken.Add($"{airportName} departure information {information}.");
        spoken.Add($"{SpokenNumbers.DigitsIndividually(timeDigits)} Zulu.");

        display.Add($"{airportDisplayName} information {information}.");
        display.Add($"{timeDigits}Z.");

        AddWind(atisWeather, spoken, display);
        AddVisibility(atisWeather, spoken, display);

        if (runway is RunwayIdentifier runwayValue)
        {
            spoken.Add($"Runway in use {runwayValue.ToSpoken()}.");
            display.Add($"Runway in use {runwayValue.ToDisplay()}.");
        }
        else
        {
            spoken.Add("Runway information unavailable.");
            display.Add("Runway information unavailable.");
        }

        AddTemperature(atisWeather, spoken, display);
        AddQnh(atisWeather, spoken, display);

        spoken.Add($"On first contact, advise you have information {information}.");
        display.Add($"On first contact, report information {information}.");
    }

    /// <summary>Returns the current edition number for playback de-duplication.</summary>
    public static long GetEditionNumber(DateTimeOffset now) =>
        now.ToUniversalTime().UtcDateTime.Ticks
        / TimeSpan.TicksPerMinute
        / EditionMinutes;

    private static WeatherSnapshot? NormalizeWeather(WeatherSnapshot? weather)
    {
        if (weather is null)
        {
            return null;
        }

        int? windSpeed = weather.WindSpeedKnots;
        Heading? windDirection = weather.WindDirection;
        if (windSpeed != 0 && windDirection is Heading direction)
        {
            int roundedDegrees = (int)Math.Round(
                direction.Degrees / 10d,
                MidpointRounding.AwayFromZero) * 10;
            windDirection = Heading.FromDegrees(roundedDegrees);
        }

        int? visibility = weather.VisibilityMeters is int meters
            ? NormalizeVisibility(meters)
            : null;

        return weather with
        {
            WindDirection = windDirection,
            WindSpeedKnots = windSpeed,
            VisibilityMeters = visibility,
        };
    }

    private static int NormalizeVisibility(int meters)
    {
        int clamped = Math.Clamp(meters, 0, 100_000);
        if (clamped >= 10_000)
        {
            return 10_000;
        }

        int increment = clamped switch
        {
            >= 5_000 => 1_000,
            >= 800 => 100,
            _ => 50,
        };

        return (int)Math.Round(
            clamped / (double)increment,
            MidpointRounding.AwayFromZero) * increment;
    }

    private static string BuildOperationalSignature(
        string icao,
        AirportData? airport,
        Frequency frequency,
        RunwayIdentifier? runway,
        WeatherSnapshot? weather) =>
        string.Join(
            '|',
            icao.ToUpperInvariant(),
            airport?.Name ?? string.Empty,
            frequency.ToDisplay(),
            runway?.ToDisplay() ?? string.Empty,
            weather?.WindSpeedKnots == 0
                ? string.Empty
                : weather?.WindDirection?.ToComparisonValue() ?? string.Empty,
            weather?.WindSpeedKnots?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            weather?.VisibilityMeters?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            weather?.TemperatureCelsius?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            weather?.QnhHectopascals?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static void AddWind(
        WeatherSnapshot? weather,
        List<string> spoken,
        List<string> display)
    {
        if (weather is not { HasWind: true, WindSpeedKnots: int speed })
        {
            return;
        }

        if (speed == 0)
        {
            spoken.Add("Wind calm.");
            display.Add("Wind calm.");
            return;
        }

        Heading direction = weather.WindDirection!.Value;
        spoken.Add(
            $"Wind {direction.ToSpoken()} degrees, "
            + $"{SpokenNumbers.DigitsIndividually(speed.ToString(CultureInfo.InvariantCulture))} knots.");
        display.Add($"Wind {direction.ToDisplay()} at {speed.ToString(CultureInfo.InvariantCulture)} kt.");
    }

    private static void AddVisibility(
        WeatherSnapshot? weather,
        List<string> spoken,
        List<string> display)
    {
        if (weather?.VisibilityMeters is not int visibility)
        {
            return;
        }

        if (visibility >= 10_000)
        {
            spoken.Add("Visibility one zero kilometres or more.");
            display.Add("Visibility 10 km or more.");
            return;
        }

        spoken.Add($"Visibility {SpokenNumbers.Altitude(visibility)} metres.");
        display.Add($"Visibility {visibility.ToString("N0", CultureInfo.InvariantCulture)} m.");
    }

    private static void AddTemperature(
        WeatherSnapshot? weather,
        List<string> spoken,
        List<string> display)
    {
        if (weather?.TemperatureCelsius is not int temperature)
        {
            return;
        }

        string spokenTemperature = SpokenNumbers.DigitsIndividually(
            Math.Abs(temperature).ToString(CultureInfo.InvariantCulture));
        if (temperature < 0)
        {
            spokenTemperature = $"minus {spokenTemperature}";
        }

        spoken.Add($"Temperature {spokenTemperature}.");
        display.Add($"Temperature {temperature.ToString(CultureInfo.InvariantCulture)}°C.");
    }

    private static void AddQnh(
        WeatherSnapshot? weather,
        List<string> spoken,
        List<string> display)
    {
        if (weather?.QnhHectopascals is not int qnh)
        {
            return;
        }

        spoken.Add(
            $"Q N H {SpokenNumbers.DigitsIndividually(qnh.ToString(CultureInfo.InvariantCulture))}.");
        display.Add($"QNH {qnh.ToString(CultureInfo.InvariantCulture)} hPa.");
    }
}
