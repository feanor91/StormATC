using StormATC.Application.FlightPlanning;
using StormATC.Domain.Airports;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>Which stand a flight that starts at the parking is given.</summary>
public class StartStandSelectorTests
{
    private static readonly GeoPosition Origin = GeoPosition.Create(48.0, 2.0);

    private static GeoPosition East(double meters) => Origin.Project(Heading.FromDegrees(90), meters / 1852.0);

    private static StandData Stand(string name, double eastMeters, int type = 2, double radius = 12, double? heading = 90) => new()
    {
        Name = name,
        Position = East(eastMeters),
        TaxiNodeName = "N" + name,
        ParkingType = type,
        RadiusMeters = radius,
        HeadingDegrees = heading,
    };

    private static AirportData Airport(params StandData[] stands) => new()
    {
        Icao = "TEST",
        Name = "Test",
        ReferencePosition = Origin,
        ElevationFeet = 100,
        Frequencies = new AirportFrequencies(),
        Stands = stands,
    };

    [Fact]
    public void The_nearest_free_stand_of_the_right_kind_is_chosen()
    {
        AirportData airport = Airport(Stand("far", 900), Stand("near", 100), Stand("middle", 400));

        StandData? stand = StartStandSelector.Choose(airport, Origin, 10, [], militaryBase: false);

        Assert.Equal("near", stand!.Name);
    }

    [Fact]
    public void A_stand_with_an_aircraft_on_it_is_skipped()
    {
        AirportData airport = Airport(Stand("near", 100), Stand("middle", 400));

        StandData? stand = StartStandSelector.Choose(airport, Origin, 10, [East(105)], militaryBase: false);

        Assert.Equal("middle", stand!.Name);
    }

    [Fact]
    public void A_stand_too_small_for_the_wingspan_is_skipped()
    {
        AirportData airport = Airport(Stand("small", 100, radius: 8), Stand("big", 500, type: 4, radius: 30));

        StandData? stand = StartStandSelector.Choose(airport, Origin, 36, [], militaryBase: false);

        Assert.Equal("big", stand!.Name);
    }

    [Fact]
    public void An_airliner_takes_a_gate_and_a_light_aircraft_a_ramp()
    {
        AirportData airport = Airport(Stand("ramp", 100, type: 2, radius: 25), Stand("gate", 600, type: 9, radius: 25));

        Assert.Equal("gate", StartStandSelector.Choose(airport, Origin, 34, [], militaryBase: false)!.Name);
        Assert.Equal("ramp", StartStandSelector.Choose(airport, Origin, 11, [], militaryBase: false)!.Name);
    }

    [Fact]
    public void A_military_base_gives_a_military_apron()
    {
        AirportData airport = Airport(Stand("ramp", 100, type: 2), Stand("apron", 700, type: 7));

        Assert.Equal("apron", StartStandSelector.Choose(airport, Origin, 11, [], militaryBase: true)!.Name);
    }

    [Fact]
    public void Fuel_and_vehicle_spots_and_stands_with_no_heading_are_never_chosen()
    {
        AirportData airport = Airport(
            Stand("fuel", 50, type: 12),
            Stand("vehicle", 60, type: 13),
            Stand("no-heading", 70, heading: null));

        Assert.Null(StartStandSelector.Choose(airport, Origin, 10, [], militaryBase: false));
    }
}
