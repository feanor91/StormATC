using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Sessions;
using StormATC.Domain;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>What loading a flight plan does to the controller and frequency of the session.</summary>
public class FlightPlanLoadingTests
{
    private readonly AtcSessionContext _context = new(
        new StubAirportDataProvider(),
        new StubTrafficProvider(),
        new StubWeatherProvider(),
        new FixedClock(Fixture.Now),
        NullLogger<AtcSessionContext>.Instance);

    private static FlightPlan Plan(string departure) => new()
    {
        FlightPlanId = "plan-" + departure,
        Callsign = Callsign.Create("STORM123"),
        DepartureIcao = departure,
        DestinationIcao = departure,
        SourceIdentifier = "Test",
    };

    [Fact]
    public async Task A_plan_from_another_aerodrome_starts_a_fresh_flight_on_that_aerodromes_frequency()
    {
        _context.UpdateSession(AtcSession.Create(
            Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now)));

        // The first aerodrome has an ATIS: the session starts by listening to it.
        AtcSession first = await _context.LoadFlightPlanAsync(Plan("STMR"), CancellationToken.None);
        Assert.Equal(ControllerPosition.Atis, first.CurrentController);
        Assert.Equal(Frequency.FromMegahertz(132.150m), first.CurrentFrequency);

        // The second has neither Delivery nor ATIS: Ground answers first, on its own frequency.
        AtcSession second = await _context.LoadFlightPlanAsync(Plan("STMD"), CancellationToken.None);

        Assert.Equal(ControllerPosition.Ground, second.CurrentController);
        Assert.Equal(Frequency.FromMegahertz(121.900m), second.CurrentFrequency);
        Assert.Equal(FlightPhase.Preflight, second.Phase);
        Assert.Null(second.PendingReadback);
    }

    [Fact]
    public async Task Loading_the_same_aerodromes_plan_again_keeps_the_flight_where_it_is()
    {
        _context.UpdateSession(AtcSession.Create(
            Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now)));
        await _context.LoadFlightPlanAsync(Plan("STMR"), CancellationToken.None);
        _context.Mutate(session => session with
        {
            CurrentController = ControllerPosition.Tower,
            CurrentFrequency = Frequency.FromMegahertz(118.300m),
            Phase = FlightPhase.HoldingShort,
        });

        AtcSession reloaded = await _context.LoadFlightPlanAsync(Plan("STMR"), CancellationToken.None);

        Assert.Equal(ControllerPosition.Tower, reloaded.CurrentController);
        Assert.Equal(Frequency.FromMegahertz(118.300m), reloaded.CurrentFrequency);
        Assert.Equal(FlightPhase.HoldingShort, reloaded.Phase);
    }
}
