using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Phraseology;
using StormATC.Application.Sessions;
using StormATC.Application.Transmissions;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>
/// The engine noticing what the aircraft does by itself — lifting off — without anyone pressing
/// a button, and the monitor keeping those checks to one every couple of seconds.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The coordinator is IAsyncDisposable and is disposed in IAsyncLifetime.DisposeAsync.")]
public class StateChangeMonitorTests : IAsyncLifetime
{
    private readonly FixedClock _clock = new(Fixture.Now);
    private AtcSessionContext _context = null!;
    private PilotTransmissionCoordinator _coordinator = null!;
    private StateChangeMonitor _monitor = null!;

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        _context = new AtcSessionContext(
            new StubAirportDataProvider(),
            new StubTrafficProvider(),
            new StubWeatherProvider(),
            _clock,
            NullLogger<AtcSessionContext>.Instance);

        _coordinator = new PilotTransmissionCoordinator(
            new StormAtcStateEngine(new DeterministicReadbackEvaluator()),
            new EscalatingPilotIntentParser(new DeterministicPilotIntentParser(), languageModelParser: null),
            new DeterministicPhraseologyRenderer(),
            _context,
            _clock,
            NullLogger<PilotTransmissionCoordinator>.Instance,
            new RecordingSessionRepository(),
            languageModelRenderer: null,
            new PhraseologyValidator());

        _monitor = new StateChangeMonitor(_coordinator, _context, _clock, NullLogger<StateChangeMonitor>.Instance);

        _context.UpdateSession(AtcSession.Create(
            Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _coordinator.DisposeAsync();

    [Fact]
    public async Task Nothing_is_checked_before_a_callsign_is_known() =>
        Assert.False(await _monitor.EvaluateAsync());

    [Fact]
    public async Task Checks_are_spaced_by_the_minimum_interval()
    {
        await LoadFlightPlanAsync();

        Assert.True(await _monitor.EvaluateAsync());
        Assert.False(await _monitor.EvaluateAsync());

        _clock.Advance(StateChangeMonitor.MinimumInterval - TimeSpan.FromMilliseconds(1));
        Assert.False(await _monitor.EvaluateAsync());

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(await _monitor.EvaluateAsync());
    }

    [Fact]
    public async Task An_aircraft_that_lifts_off_moves_the_session_into_the_climb()
    {
        await LoadFlightPlanAsync();
        _context.UpdateSession(_context.CurrentSession with { Phase = FlightPhase.Takeoff });
        _context.UpdateAircraftState(TestAerodromes.AtStand(Fixture.Now) with
        {
            IndicatedAltitudeFeet = 1_200,
            PressureAltitudeFeet = 1_200,
            GroundSpeedKnots = 120,
            IsOnGround = false,
            ParkingBrakeSet = false,
            EnginesRunning = true,
        });

        Assert.True(await _monitor.EvaluateAsync());

        Assert.Equal(FlightPhase.InitialClimb, _context.CurrentSession.Phase);
    }

    private Task LoadFlightPlanAsync() =>
        _context.LoadFlightPlanAsync(
            new FlightPlan
            {
                FlightPlanId = "monitor",
                Callsign = Callsign.Create("STORM123"),
                DepartureIcao = "STMR",
                DestinationIcao = "STMD",
                SourceIdentifier = "Test",
            },
            CancellationToken.None);
}
