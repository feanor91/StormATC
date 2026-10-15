using StormATC.Application.FlightPlanning;
using StormATC.Domain.FlightPlanning;

namespace StormATC.Application.Tests;

/// <summary>The minimal plan of a local VFR flight.</summary>
public class VfrLocalFlightTests
{
    [Theory]
    [InlineData("LFPL", "F-GABC")]
    [InlineData(" lfpl ", "fgabc")]
    [InlineData("LFPN", "STORM123")]
    public void A_valid_aerodrome_and_callsign_make_a_local_plan(string aerodrome, string callsign)
    {
        bool created = VfrLocalFlight.TryCreatePlan(aerodrome, callsign, Fixture.Now, out FlightPlan? plan, out string? problem);

        Assert.True(created);
        Assert.Null(problem);
        Assert.Equal(aerodrome.Trim().ToUpperInvariant(), plan!.DepartureIcao);
        Assert.Equal(plan.DepartureIcao, plan.DestinationIcao);
        Assert.Equal("VFR local", plan.SourceIdentifier);
    }

    [Theory]
    [InlineData(null, "F-GABC", "aerodrome")]
    [InlineData("", "F-GABC", "aerodrome")]
    [InlineData("LFP", "F-GABC", "aerodrome")]
    [InlineData("LFPLX", "F-GABC", "aerodrome")]
    [InlineData("LF-L", "F-GABC", "aerodrome")]
    [InlineData("LFPL", null, "callsign")]
    [InlineData("LFPL", "  ", "callsign")]
    public void A_bad_input_is_named(string? aerodrome, string? callsign, string expectedProblem)
    {
        bool created = VfrLocalFlight.TryCreatePlan(aerodrome, callsign, Fixture.Now, out FlightPlan? plan, out string? problem);

        Assert.False(created);
        Assert.Null(plan);
        Assert.Equal(expectedProblem, problem);
    }
}
