using StormATC.Domain.Airports;
using StormATC.Domain.ValueObjects;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Infrastructure.Tests;

/// <summary>Which traffic a pilot hears on the frequency he is tuned to.</summary>
public class TrafficFrequencyFilterTests
{
    private static readonly Frequency Approach = Frequency.FromMegahertz(119.250m);
    private static readonly Frequency Tower = Frequency.FromMegahertz(118.700m);
    private static readonly Frequency Elsewhere = Frequency.FromMegahertz(121.050m);

    private static readonly AirportData Airport = new()
    {
        Icao = "LFPL",
        Name = "Test",
        ReferencePosition = GeoPosition.Create(48.8, 2.4),
        ElevationFeet = 100,
        Frequencies = new AirportFrequencies { Approach = Approach, Tower = Tower },
        Runways = [],
        Stands = [],
    };

    private static MsfsTrafficSnapshot Contact(GeoPosition position, bool onGround) => new()
    {
        ObjectId = 1,
        Callsign = "TEST",
        Position = position,
        AltitudeFeet = onGround ? 100 : 4_000,
        GroundSpeedKnots = 120,
        IsOnGround = onGround,
    };

    [Fact]
    public void The_approach_frequency_hears_the_airborne_traffic_near_its_aerodrome_only()
    {
        Func<MsfsTrafficSnapshot, bool> filter = TrafficRadioService.FrequencyFilter(Approach, [Airport]);

        Assert.True(filter(Contact(Airport.ReferencePosition.Project(Heading.FromDegrees(0), 20), onGround: false)));
        Assert.False(filter(Contact(Airport.ReferencePosition.Project(Heading.FromDegrees(0), 80), onGround: false)));
        Assert.False(filter(Contact(Airport.ReferencePosition, onGround: true)));
    }

    [Fact]
    public void The_tower_frequency_hears_the_surface_traffic_of_its_aerodrome_only()
    {
        Func<MsfsTrafficSnapshot, bool> filter = TrafficRadioService.FrequencyFilter(Tower, [Airport]);

        Assert.True(filter(Contact(Airport.ReferencePosition.Project(Heading.FromDegrees(90), 1), onGround: true)));
        Assert.False(filter(Contact(Airport.ReferencePosition.Project(Heading.FromDegrees(90), 20), onGround: true)));
        Assert.False(filter(Contact(Airport.ReferencePosition.Project(Heading.FromDegrees(90), 3), onGround: false)));
    }

    [Fact]
    public void A_frequency_of_another_aerodrome_hears_nobody()
    {
        Func<MsfsTrafficSnapshot, bool> filter = TrafficRadioService.FrequencyFilter(Elsewhere, [Airport]);

        Assert.False(filter(Contact(Airport.ReferencePosition, onGround: false)));
        Assert.False(filter(Contact(Airport.ReferencePosition, onGround: true)));
    }

    [Fact]
    public void A_flight_information_frequency_hears_the_airborne_traffic_of_its_sector()
    {
        GeoPosition inParisNorth = GeoPosition.Create(49.5, 2.5);
        FlightInformationSector? sector = FlightInformationRegions.Find(inParisNorth);
        Assert.NotNull(sector);

        Func<MsfsTrafficSnapshot, bool> filter = TrafficRadioService.FrequencyFilter(sector.Frequency, []);

        Assert.True(filter(Contact(inParisNorth, onGround: false)));
        Assert.False(filter(Contact(inParisNorth, onGround: true)));
        Assert.False(filter(Contact(GeoPosition.Create(52.0, 20.0), onGround: false)));
    }
}
