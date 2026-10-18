using StormATC.Application.FlightPlanning;

namespace StormATC.Application.Tests;

/// <summary>The message the StormATC WASM module publishes in the simulator.</summary>
public class SimRouteMessageTests
{
    // Exactly what the module writes (msfs-wasm/src/route_json.h) for a VFR plan Lognes to Pontoise.
    private const string FromTheModule =
        """{"seq":3,"source":"atc","route":{"departureAirport":{"type":"A","region":"LF","airport":"LFPL","ident":"LFPL"},"departureRunway":{"number":"08","designator":""},"destinationAirport":{"type":"A","region":"LF","airport":"LFPT","ident":"LFPT"},"destinationRunway":{"number":"23","designator":"L"},"cruiseAltitude":{"isFlightLevel":false,"altitude":2500},"isVfr":true,"enroute":[{"fixIcao":{"type":"W","region":"LF","airport":"","ident":"MELUN"},"via":"","hasLatLon":false,"lat":0,"lon":0,"name":""},{"fixIcao":{"type":"","region":"","airport":"","ident":""},"via":"","hasLatLon":true,"lat":48.9,"lon":2.2,"name":""}]}}""";

    [Fact]
    public void The_modules_message_is_read()
    {
        Assert.True(SimRouteMessage.TryParse(FromTheModule, out SimRouteMessage? message));

        Assert.Equal(3, message.Sequence);
        Assert.Equal("atc", message.Source);
    }

    [Fact]
    public void The_route_in_the_message_is_the_one_the_route_parser_reads()
    {
        Assert.True(SimRouteMessage.TryParse(FromTheModule, out SimRouteMessage? message));

        Assert.True(PlannedRouteParser.TryParse(message.RouteJson, out SimFlightPlan? plan));

        Assert.Equal("LFPL", plan.DepartureIcao);
        Assert.Equal("LFPT", plan.DestinationIcao);
        Assert.Equal("08", plan.DepartureRunway);
        Assert.Equal("23L", plan.ArrivalRunway);
        Assert.Equal(2500, plan.CruisingAltitudeFeet);
        Assert.True(plan.IsVfr);
        Assert.Equal(["MELUN"], plan.Waypoints.Select(w => w.Identifier).Take(1));
        Assert.Equal(48.9, plan.Waypoints[1].Position!.Value.Latitude, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"seq":1,"source":"atc"}""")]
    [InlineData("""{"seq":1,"route":{}}""")]
    [InlineData("""{"seq":1,"source":5,"route":{}}""")]
    [InlineData("""{"seq":1,"source":"atc","route":5}""")]
    public void Anything_else_is_not_a_message(string? json) =>
        Assert.False(SimRouteMessage.TryParse(json, out _));

    [Fact]
    public void A_message_without_a_sequence_number_still_reads()
    {
        Assert.True(SimRouteMessage.TryParse("""{"source":"efb","route":{}}""", out SimRouteMessage? message));

        Assert.Equal(0, message.Sequence);
    }
}
