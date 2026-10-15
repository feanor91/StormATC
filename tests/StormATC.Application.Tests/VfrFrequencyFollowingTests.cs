using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Sessions;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>A VFR session follows the published frequency the pilot tunes in the aircraft.</summary>
public class VfrFrequencyFollowingTests
{
    private readonly AtcSessionContext _context = new(
        new StubAirportDataProvider(),
        new StubTrafficProvider(),
        new StubWeatherProvider(),
        new FixedClock(Fixture.Now),
        NullLogger<AtcSessionContext>.Instance);

    private static AircraftState Tuned(decimal megahertz) => TestAerodromes.AtStand(Fixture.Now) with
    {
        Radio = RadioState.Default with
        {
            Com1Active = Frequency.FromMegahertz(megahertz),
            TransmitOnCom1 = true,
        },
    };

    private async Task StartAsync(FlightRules rules)
    {
        _context.UpdateSession(AtcSession.Create(
            Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, Tuned(121.800m)));
        await _context.LoadFlightPlanAsync(
            new FlightPlan
            {
                FlightPlanId = "local",
                Callsign = Callsign.Create("STORM123"),
                DepartureIcao = "STMR",
                DestinationIcao = "STMR",
                SourceIdentifier = "Test",
            },
            CancellationToken.None);
        _context.Mutate(session => session.WithFlightRules(rules));
    }

    [Fact]
    public async Task Tuning_each_published_frequency_in_turn_moves_the_session_to_that_controller()
    {
        await StartAsync(FlightRules.Vfr);
        Assert.Equal(ControllerPosition.Atis, _context.CurrentSession.CurrentController);

        // Straight to Tower: no ATIS first, no Delivery, no handoff.
        AtcSession tower = _context.UpdateAircraftState(Tuned(118.300m));
        Assert.Equal(ControllerPosition.Tower, tower.CurrentController);
        Assert.Equal(Frequency.FromMegahertz(118.300m), tower.CurrentFrequency);

        AtcSession ground = _context.UpdateAircraftState(Tuned(121.700m));
        Assert.Equal(ControllerPosition.Ground, ground.CurrentController);

        AtcSession atis = _context.UpdateAircraftState(Tuned(132.150m));
        Assert.Equal(ControllerPosition.Atis, atis.CurrentController);
    }

    [Fact]
    public async Task A_frequency_the_aerodrome_does_not_publish_changes_nothing()
    {
        await StartAsync(FlightRules.Vfr);
        AtcSession before = _context.UpdateAircraftState(Tuned(118.300m));

        AtcSession after = _context.UpdateAircraftState(Tuned(124.850m));

        Assert.Equal(before.CurrentController, after.CurrentController);
        Assert.Equal(before.CurrentFrequency, after.CurrentFrequency);
    }

    [Fact]
    public async Task An_ifr_session_keeps_its_fixed_order_of_frequencies()
    {
        await StartAsync(FlightRules.Ifr);

        AtcSession session = _context.UpdateAircraftState(Tuned(118.300m));

        Assert.Equal(ControllerPosition.Atis, session.CurrentController);
    }
}
