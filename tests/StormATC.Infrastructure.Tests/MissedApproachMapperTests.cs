using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.FlightPlanning;
using StormATC.Domain.FlightPlanning;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Infrastructure.Tests;

/// <summary>Choosing an approach in the simulator's navigation data and naming its missed approach.</summary>
public sealed class MissedApproachMapperTests
{
    // Nice (LFMN) as the simulator gave it: the ILS Y of runway 04R and another approach of the same runway.
    private static readonly List<MsfsSimConnectConnector.ApproachFacility> Approaches =
    [
        new(10, 4, 2, 'Z', [new(7, "ZZ401", null)]),
        new(4, 4, 2, 'Y',
        [
            new(18, "D221A", null),
            new(5, string.Empty, null),
            new(18, "NERAS", 3000),
            new(18, "AZR35", null),
            new(18, "NERAS", 3000),
            new(14, "NERAS", 3000),
        ]),
        new(4, 22, 1, 'Y', [new(18, "OTHER", null)]),
    ];

    [Fact]
    public void The_missed_approach_of_the_planned_approach_is_named_point_by_point()
    {
        IReadOnlyList<MissedApproachPoint>? points = MissedApproachMapper.Find(
            Approaches, new SimApproach("ILS", "Y", "04R"), NullLogger.Instance);

        Assert.NotNull(points);
        Assert.Equal(
            ["D221A", "INTRCPT", "NERAS", "AZR35", "NERAS", "HOLD NERAS"],
            points.Select(p => p.Identifier));
        Assert.Equal([null, null, 3000, null, 3000, 3000], points.Select(p => p.AltitudeFeet));
    }

    [Fact]
    public void An_approach_the_simulator_does_not_have_gives_nothing()
    {
        Assert.Null(MissedApproachMapper.Find(Approaches, new SimApproach("VOR", string.Empty, "22L"), NullLogger.Instance));
    }

    [Fact]
    public void A_suffix_the_simulator_does_not_have_falls_back_to_the_approach_of_that_runway()
    {
        IReadOnlyList<MissedApproachPoint>? points = MissedApproachMapper.Find(
            Approaches, new SimApproach("ILS", string.Empty, "04R"), NullLogger.Instance);

        Assert.NotNull(points);
        Assert.Equal("D221A", points[0].Identifier);
    }

    [Theory]
    [InlineData("ILS", 4)]
    [InlineData("rnav", 10)]
    [InlineData("VOR DME", 8)]
    [InlineData("LOCALIZER", 5)]
    [InlineData("unknown", -1)]
    public void An_approach_type_is_the_simulators_number(string type, int expected) =>
        Assert.Equal(expected, MissedApproachMapper.TypeCode(type));

    [Theory]
    [InlineData("04R", 4, 2)]
    [InlineData("22L", 22, 1)]
    [InlineData("33", 33, 0)]
    [InlineData("08C", 8, 3)]
    [InlineData("", 0, 0)]
    public void A_runway_is_the_simulators_number_and_designator(string runway, int number, int designator) =>
        Assert.Equal((number, designator), MissedApproachMapper.Runway(runway));
}
