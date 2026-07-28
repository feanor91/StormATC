using StormATC.Domain.Airports;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Tests;

/// <summary>
/// Taxi routing over the aerodrome graph.
/// </summary>
/// <remarks>
/// The route a controller reads out has to be a route the aircraft can actually follow, and it
/// must not send an aircraft across a runway without saying so. Both are structural properties
/// of the graph search rather than of any phrasing, so they are tested here.
/// </remarks>
public class TaxiRoutingTests
{
    private static TaxiGraph Graph => TestScenario.Departure_STMR.TaxiGraph!;

    [Fact]
    public void A_route_from_the_stand_to_the_holding_point_is_found()
    {
        TaxiRoute route = TaxiPathfinder.FindRoute(Graph, "S_A1", "H18");

        Assert.True(route.Found);
        Assert.Equal("S_A1", route.Nodes[0]);
        Assert.Equal("H18", route.Nodes[^1]);
        Assert.True(route.TotalLengthMeters > 0);
    }

    [Fact]
    public void Consecutive_edges_on_one_taxiway_are_spoken_once()
    {
        // "Alpha, Alpha, Charlie" is not how a clearance is read.
        TaxiRoute route = TaxiPathfinder.FindRoute(Graph, "S_A1", "H18");

        for (int i = 1; i < route.TaxiwayNames.Count; i++)
        {
            Assert.NotEqual(route.TaxiwayNames[i - 1], route.TaxiwayNames[i]);
        }
    }

    [Fact]
    public void The_route_to_runway_eighteen_uses_alpha_then_charlie()
    {
        // Charlie is the only link to the runway 18 holding point in this layout.
        TaxiRoute route = TaxiPathfinder.FindRoute(Graph, "S_A1", "H18");

        Assert.Equal(["A", "C"], route.TaxiwayNames);
    }

    [Fact]
    public void A_route_that_needs_no_crossing_does_not_report_one()
    {
        TaxiRoute route = TaxiPathfinder.FindRoute(Graph, "S_A1", "H18");

        Assert.False(route.RequiresRunwayCrossing);
        Assert.Empty(route.CrossedRunways);
    }

    [Fact]
    public void A_route_that_must_cross_a_runway_declares_it()
    {
        // The remote stand east of the runway is only reachable across runway 18.
        TaxiRoute route = TaxiPathfinder.FindRoute(Graph, "S_A1", "S_R1");

        Assert.True(route.Found);
        Assert.True(route.RequiresRunwayCrossing);
        Assert.Contains(RunwayIdentifier.Create("18"), route.CrossedRunways);
    }

    [Fact]
    public void The_search_avoids_a_runway_crossing_when_an_alternative_exists()
    {
        // Reaching the runway 36 holding point is possible without crossing, and the penalty
        // on crossing edges is what makes the search prefer the long way round.
        TaxiRoute route = TaxiPathfinder.FindRoute(Graph, "S_A1", "H36");

        Assert.True(route.Found);
        Assert.False(route.RequiresRunwayCrossing);
    }

    [Fact]
    public void An_unreachable_destination_returns_the_not_found_sentinel()
    {
        TaxiRoute route = TaxiPathfinder.FindRoute(Graph, "S_A1", "NOT_A_NODE");

        Assert.False(route.Found);
        Assert.Empty(route.Nodes);
        Assert.Empty(route.TaxiwayNames);
    }

    [Fact]
    public void A_route_to_the_same_node_is_trivially_found()
    {
        TaxiRoute route = TaxiPathfinder.FindRoute(Graph, "S_A1", "S_A1");

        Assert.True(route.Found);
        Assert.Equal(0, route.TotalLengthMeters);
    }

    [Fact]
    public void FindRouteToHoldingPoint_resolves_the_runway_from_the_aerodrome_data()
    {
        TaxiRoute route = TaxiPathfinder.FindRouteToHoldingPoint(
            TestScenario.Departure_STMR, "A1", RunwayIdentifier.Create("18"));

        Assert.True(route.Found);
        Assert.Equal("H18", route.Nodes[^1]);
    }

    [Fact]
    public void Position_aware_routing_starts_at_the_aircraft_not_the_first_airport_stand()
    {
        TaxiGraph graph = TestScenario.Departure_STMR.TaxiGraph!;
        GeoPosition aircraftPosition = graph.FindNode("S_A2")!.Position;

        TaxiRoute route = TaxiPathfinder.FindRouteFromPositionToHoldingPoint(
            TestScenario.Departure_STMR,
            aircraftPosition,
            RunwayIdentifier.Create("18"));

        Assert.True(route.Found);
        Assert.Equal("S_A2", route.Nodes[0]);
        Assert.NotEqual("S_A1", route.Nodes[0]);
        Assert.Equal("H18", route.Nodes[^1]);
    }

    [Fact]
    public void Position_aware_routing_refuses_to_snap_an_aircraft_far_from_the_airport()
    {
        TaxiRoute route = TaxiPathfinder.FindRouteFromPositionToHoldingPoint(
            TestScenario.Departure_STMR,
            GeoPosition.Create(0, 0),
            RunwayIdentifier.Create("18"));

        Assert.False(route.Found);
    }

    [Fact]
    public void A_stored_node_sequence_rebuilds_the_exact_taxi_route()
    {
        TaxiRoute original = TaxiPathfinder.FindRoute(Graph, "S_A1", "H18");

        TaxiRoute rebuilt = TaxiPathfinder.FromNodeSequence(Graph, original.Nodes);

        Assert.True(rebuilt.Found);
        Assert.Equal(original.Nodes, rebuilt.Nodes);
        Assert.Equal(original.TaxiwayNames, rebuilt.TaxiwayNames);
        Assert.Equal(original.TotalLengthMeters, rebuilt.TotalLengthMeters);
    }

    [Fact]
    public void Surface_guidance_reports_current_stand_next_taxiway_and_expected_route_sign()
    {
        TaxiRoute route = TaxiPathfinder.FindRoute(Graph, "S_A1", "H18");
        GeoPosition aircraftPosition = Graph.FindNode("S_A1")!.Position;

        TaxiRouteProgress progress = TaxiSurfaceAdvisor.Analyze(
            Graph,
            route,
            aircraftPosition);

        Assert.True(progress.Found);
        Assert.True(progress.IsOnRoute);
        Assert.Equal("A1", progress.SurfacePosition.StandName);
        Assert.Equal("A", progress.CurrentTaxiway);
        Assert.Equal("C", progress.NextTaxiway);
        Assert.Contains(
            progress.JunctionSignCues,
            cue => cue.TaxiwayName == "C" && cue.IsClearedRoute);
        Assert.True(progress.RemainingDistanceMeters > 0);
    }

    [Fact]
    public void FindRouteFromRunwayToStand_starts_at_a_published_exit()
    {
        TaxiRoute route = TaxiPathfinder.FindRouteFromRunwayToStand(
            TestScenario.Departure_STMR, RunwayIdentifier.Create("18"), "A1");

        Assert.True(route.Found);
        Assert.Contains(route.Nodes[0], new[] { "E18A", "E18B" });
        Assert.Equal("S_A1", route.Nodes[^1]);
    }

    [Fact]
    public void Routing_to_an_unknown_stand_fails_rather_than_guessing()
    {
        TaxiRoute route = TaxiPathfinder.FindRouteToHoldingPoint(
            TestScenario.Departure_STMR, "Z9", RunwayIdentifier.Create("18"));

        Assert.False(route.Found);
    }
}
