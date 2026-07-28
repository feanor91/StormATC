using StormATC.Domain.Airports;
using StormATC.Domain.ValueObjects;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Infrastructure.Tests;

public sealed class MsfsAirportFacilityMapperTests
{
    [Fact]
    public void Facility_snapshot_builds_a_routable_airport_graph()
    {
        MsfsAirportFacilitySnapshot snapshot = BuildSnapshot();

        AirportData airport = Assert.IsType<AirportData>(
            MsfsAirportFacilityMapper.Map(snapshot));

        Assert.Equal("TEST", airport.Icao);
        Assert.False(airport.IsFictional);
        Assert.True(airport.SupportsTaxiRouting);
        Assert.Equal(2, airport.Runways.Count);
        Assert.All(airport.Runways, runway => Assert.NotEmpty(runway.HoldingPointNodes));
        Assert.Single(airport.Stands);
        Assert.Equal("A12", airport.Stands[0].Name);
        Assert.Equal(
            Frequency.FromMegahertz(121.700m),
            airport.Frequencies.Ground);

        TaxiRoute route = TaxiPathfinder.FindRouteToHoldingPoint(
            airport,
            "A12",
            RunwayIdentifier.Create("18"));

        Assert.True(route.Found);
        Assert.Equal(["apron", "A"], route.TaxiwayNames);

        TaxiRoute reciprocalRoute = TaxiPathfinder.FindRouteToHoldingPoint(
            airport,
            "A12",
            RunwayIdentifier.Create("36"));
        Assert.True(reciprocalRoute.Found);
    }

    [Fact]
    public void Closed_and_vehicle_paths_are_not_exposed_as_aircraft_routes()
    {
        MsfsAirportFacilitySnapshot snapshot = BuildSnapshot();
        snapshot.TaxiPaths[2] = snapshot.TaxiPaths[2] with { Type = 5 };
        snapshot.TaxiPaths[4] = new MsfsTaxiPathFacility(
            4,
            Type: 6,
            WidthMeters: 5,
            WeightLimitPounds: 0,
            RunwayNumber: 0,
            RunwayDesignator: 0,
            StartIndex: 1,
            EndIndex: 2,
            NameIndex: 0);

        AirportData airport = Assert.IsType<AirportData>(
            MsfsAirportFacilityMapper.Map(snapshot));
        TaxiRoute route = TaxiPathfinder.FindRouteToHoldingPoint(
            airport,
            "A12",
            RunwayIdentifier.Create("18"));

        Assert.False(route.Found);
    }

    private static MsfsAirportFacilitySnapshot BuildSnapshot()
    {
        var snapshot = new MsfsAirportFacilitySnapshot("TEST")
        {
            Airport = new MsfsAirportFacility(
                "TEST",
                "Test International",
                52.0,
                4.0,
                10,
                2),
        };

        snapshot.Runways[0] = new MsfsRunwayFacility(
            0,
            52.0,
            4.0,
            HeadingDegrees: 180,
            LengthMeters: 2_000,
            WidthMeters: 45,
            Surface: 4,
            PrimaryNumber: 18,
            PrimaryDesignator: 0,
            SecondaryNumber: 36,
            SecondaryDesignator: 0);
        snapshot.Frequencies[0] = new MsfsFrequencyFacility(0, 5, 121_700_000);
        snapshot.TaxiNames[0] = new MsfsTaxiNameFacility(0, "A");

        snapshot.TaxiParkings[100] = new MsfsTaxiParkingFacility(
            100,
            Type: 9,
            TaxiPointType: 1,
            Name: 12,
            Suffix: 0,
            Number: 12,
            Orientation: 0,
            HeadingDegrees: 90,
            RadiusMeters: 25,
            BiasXMeters: -500,
            BiasZMeters: 500);
        snapshot.TaxiPoints[1] = new MsfsTaxiPointFacility(
            1,
            Type: 1,
            Orientation: 0,
            BiasXMeters: -250,
            BiasZMeters: 700);
        snapshot.TaxiPoints[2] = new MsfsTaxiPointFacility(
            2,
            Type: 2,
            Orientation: 0,
            BiasXMeters: -50,
            BiasZMeters: 950);
        snapshot.TaxiPoints[3] = new MsfsTaxiPointFacility(
            3,
            Type: 1,
            Orientation: 0,
            BiasXMeters: -50,
            BiasZMeters: -950);

        snapshot.TaxiPaths[1] = new MsfsTaxiPathFacility(
            1,
            Type: 3,
            WidthMeters: 20,
            WeightLimitPounds: 0,
            RunwayNumber: 0,
            RunwayDesignator: 0,
            StartIndex: 100,
            EndIndex: 1,
            NameIndex: 99);
        snapshot.TaxiPaths[2] = new MsfsTaxiPathFacility(
            2,
            Type: 1,
            WidthMeters: 20,
            WeightLimitPounds: 0,
            RunwayNumber: 0,
            RunwayDesignator: 0,
            StartIndex: 1,
            EndIndex: 2,
            NameIndex: 0);
        snapshot.TaxiPaths[3] = new MsfsTaxiPathFacility(
            3,
            Type: 1,
            WidthMeters: 20,
            WeightLimitPounds: 0,
            RunwayNumber: 0,
            RunwayDesignator: 0,
            StartIndex: 1,
            EndIndex: 3,
            NameIndex: 0);

        return snapshot;
    }
}
