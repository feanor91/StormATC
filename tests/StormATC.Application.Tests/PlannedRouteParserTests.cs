using StormATC.Application.FlightPlanning;

namespace StormATC.Application.Tests;

/// <summary>Reading the route the simulator hands to JavaScript (the EFB's flight plan).</summary>
public class PlannedRouteParserTests
{
    // The shape of JS_FlightPlanRoute in the simulator's own JavaScript library.
    private const string VfrRoute = """
        {
          "__Type": "JS_FlightPlanRoute",
          "departureAirport": { "type": "A", "region": "", "airport": "LFPL", "ident": "LFPL" },
          "departureRunway": { "__Type": "JS_RunwayIdentifier", "number": "26", "designator": "L" },
          "destinationAirport": { "type": "A", "region": "", "airport": "LFPT", "ident": "LFPT" },
          "destinationRunway": { "__Type": "JS_RunwayIdentifier", "number": "", "designator": "" },
          "enroute": [
            { "__Type": "JS_EnrouteLeg", "fixIcao": { "type": "W", "region": "LF", "airport": "", "ident": "MELUN" }, "via": "", "hasLatLon": false, "lat": 0, "lon": 0 },
            { "__Type": "JS_EnrouteLeg", "fixIcao": { "type": "", "region": "", "airport": "", "ident": "" }, "via": "", "hasLatLon": true, "lat": 48.9, "lon": 2.2, "name": "USER1" }
          ],
          "departure": "",
          "cruiseAltitude": { "isFlightLevel": false, "altitude": 2500 },
          "isVfr": true
        }
        """;

    [Fact]
    public void A_vfr_route_gives_its_aerodromes_runway_altitude_and_legs()
    {
        Assert.True(PlannedRouteParser.TryParse(VfrRoute, out SimFlightPlan? plan));

        Assert.Equal("LFPL", plan.DepartureIcao);
        Assert.Equal("LFPT", plan.DestinationIcao);
        Assert.True(plan.IsVfr);
        Assert.Equal("26L", plan.DepartureRunway);
        Assert.Equal(2500, plan.CruisingAltitudeFeet);
        Assert.Equal(["MELUN", "USER1"], plan.Waypoints.Select(w => w.Identifier));
        Assert.Null(plan.Waypoints[0].Position);
        Assert.Equal(48.9, plan.Waypoints[1].Position!.Value.Latitude, 3);
    }

    [Fact]
    public void A_flight_level_is_in_hundreds_of_feet()
    {
        string route = VfrRoute
            .Replace("\"isFlightLevel\": false, \"altitude\": 2500", "\"isFlightLevel\": true, \"altitude\": 100")
            .Replace("\"isVfr\": true", "\"isVfr\": false");

        Assert.True(PlannedRouteParser.TryParse(route, out SimFlightPlan? plan));

        Assert.Equal(10000, plan.CruisingAltitudeFeet);
        Assert.False(plan.IsVfr);
    }

    [Theory]
    [InlineData("L", "08L")]
    [InlineData("LEFT", "08L")]
    [InlineData("R", "08R")]
    [InlineData("RIGHT", "08R")]
    [InlineData("C", "08C")]
    [InlineData("", "08")]
    [InlineData("1", "08L")]
    [InlineData("2", "08R")]
    public void The_runway_designator_is_understood_in_its_usual_forms(string designator, string expected)
    {
        string route = VfrRoute.Replace("\"number\": \"26\", \"designator\": \"L\"", $"\"number\": \"08\", \"designator\": \"{designator}\"");

        Assert.True(PlannedRouteParser.TryParse(route, out SimFlightPlan? plan));

        Assert.Equal(expected, plan.DepartureRunway);
    }

    [Fact]
    public void An_airport_whose_ident_is_empty_is_read_from_its_airport_field()
    {
        string route = """
            { "departureAirport": { "type": "A", "region": "", "airport": "LFPL", "ident": "" }, "isVfr": true }
            """;

        Assert.True(PlannedRouteParser.TryParse(route, out SimFlightPlan? plan));

        Assert.Equal("LFPL", plan.DepartureIcao);
        Assert.Equal("LFPL", plan.DestinationIcao);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "departureAirport": { "type": "", "region": "", "airport": "", "ident": "" }, "isVfr": true }""")]
    public void A_route_with_no_departure_is_an_empty_route(string route) =>
        Assert.False(PlannedRouteParser.TryParse(route, out _));
}
