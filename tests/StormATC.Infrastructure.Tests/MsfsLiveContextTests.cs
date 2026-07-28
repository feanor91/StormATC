using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.ValueObjects;
using StormATC.Application.Options;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Infrastructure.Tests;

public sealed class MsfsLiveContextTests
{
    [Fact]
    public void Traffic_and_all_credit_using_features_default_off()
    {
        var options = new TrafficOptions();

        Assert.False(options.EnableLiveTraffic);
        Assert.False(options.EnableRadioChatter);
        Assert.False(options.UseLanguageModelForChatter);
        Assert.False(options.SpeakChatter);
        Assert.True(options.PauseChatterWhenOnlineAtcClientDetected);
        Assert.Equal(6, options.MaximumExchangesPerHour);
        Assert.Equal(TimeSpan.FromMinutes(2), options.MinimumInterval);
    }

    [Theory]
    [InlineData("FSLTL A320 CFM easyJet", true)]
    [InlineData("fslTl_B738_Ryanair", true)]
    [InlineData("Asobo A320 Neo", false)]
    public void FslTl_models_are_classified_from_the_simulator_title(
        string modelTitle,
        bool expected)
    {
        var traffic = new MsfsTrafficSnapshot
        {
            ObjectId = 55,
            Callsign = "TEST55",
            ModelTitle = modelTitle,
            Position = GeoPosition.Create(52.0, 4.0),
            AltitudeFeet = 1_500,
            GroundSpeedKnots = 120,
            IsOnGround = false,
        };

        Assert.Equal(expected, traffic.IsFslTlModel);
    }

    [Fact]
    public void Ambient_weather_is_converted_from_true_to_magnetic_wind()
    {
        var weather = Assert.IsType<WeatherSnapshot>(
            MsfsEnvironmentMapper.MapWeather(
                windDirectionTrueDegrees: 180,
                windSpeedKnots: 12.4,
                seaLevelPressureHectopascals: 1012.6,
                visibilityMeters: 9_600,
                temperatureCelsius: 17.6,
                trueHeadingDegrees: 100,
                magneticHeadingDegrees: 90));

        Assert.Equal(170, weather.WindDirection?.Degrees);
        Assert.Equal(12, weather.WindSpeedKnots);
        Assert.Equal(1013, weather.QnhHectopascals);
        Assert.Equal(9_600, weather.VisibilityMeters);
        Assert.Equal(18, weather.TemperatureCelsius);
        Assert.Contains("Microsoft Flight Simulator", weather.Source);
    }

    [Fact]
    public void Nearby_ground_traffic_is_matched_to_loaded_runway_geometry()
    {
        AirportData airport = BuildAirport();
        var traffic = new MsfsTrafficSnapshot
        {
            ObjectId = 42,
            Callsign = "TEST42",
            Position = GeoPosition.Create(52.00005, 4.0),
            AltitudeFeet = 20,
            GroundSpeedKnots = 8,
            IsOnGround = true,
        };

        RunwayIdentifier? runway = MsfsTrafficProvider.FindOccupiedRunway(
            traffic,
            [airport]);

        Assert.Equal(RunwayIdentifier.Create("18"), runway);
    }

    [Fact]
    public void Airborne_traffic_is_not_reported_as_runway_occupancy()
    {
        AirportData airport = BuildAirport();
        var traffic = new MsfsTrafficSnapshot
        {
            ObjectId = 43,
            Callsign = "TEST43",
            Position = GeoPosition.Create(52.00005, 4.0),
            AltitudeFeet = 2_000,
            GroundSpeedKnots = 140,
            IsOnGround = false,
        };

        Assert.Null(MsfsTrafficProvider.FindOccupiedRunway(traffic, [airport]));
    }

    private static AirportData BuildAirport() => new()
    {
        Icao = "TEST",
        Name = "Test Airport",
        ReferencePosition = GeoPosition.Create(52.0, 4.0),
        ElevationFeet = 20,
        IsFictional = false,
        Frequencies = new AirportFrequencies(),
        Runways =
        [
            new RunwayData
            {
                Identifier = RunwayIdentifier.Create("18"),
                ThresholdPosition = GeoPosition.Create(52.005, 4.0),
                EndPosition = GeoPosition.Create(51.995, 4.0),
                LengthFeet = 3_650,
                WidthFeet = 148,
            },
        ],
    };
}
