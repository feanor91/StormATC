using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Phraseology;
using StormATC.Application.Sessions;
using StormATC.Application.Transmissions;
using StormATC.Domain;
using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>An aerodrome with no air traffic service: nobody to call, nothing invented.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The coordinator is IAsyncDisposable and is disposed in IAsyncLifetime.DisposeAsync.")]
public sealed class AerodromeWithoutAtcTests : IAsyncLifetime
{
    private readonly FixedClock _clock = new(Fixture.Now);
    private readonly RecordingSessionRepository _repository = new();
    private readonly FakeProfiles _profiles = new();
    private AtcSessionContext _context = null!;
    private PilotTransmissionCoordinator _coordinator = null!;

    private sealed class FakeProfiles : IVfrProfileProvider
    {
        public VfrAerodromeProfile? Profile { get; set; }

        public Task<VfrAerodromeProfile?> GetProfileAsync(string icao, CancellationToken cancellationToken) =>
            Task.FromResult(Profile is { } profile && profile.Icao == icao ? profile : null);
    }

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        _context = new AtcSessionContext(
            new StubAirportDataProvider(),
            new StubTrafficProvider(),
            new StubWeatherProvider(),
            _clock,
            NullLogger<AtcSessionContext>.Instance,
            _profiles);

        _coordinator = new PilotTransmissionCoordinator(
            new StormAtcStateEngine(new DeterministicReadbackEvaluator()),
            new EscalatingPilotIntentParser(new DeterministicPilotIntentParser(), languageModelParser: null),
            new DeterministicPhraseologyRenderer(),
            _context,
            _clock,
            NullLogger<PilotTransmissionCoordinator>.Instance,
            _repository,
            languageModelRenderer: null,
            new PhraseologyValidator());

        _context.UpdateSession(AtcSession.Create(
            Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _coordinator.DisposeAsync();

    private static VfrAerodromeProfile Profile(string? kind, decimal? airAir) => new()
    {
        Icao = "STMR",
        Name = "Storm International",
        ServiceKind = kind,
        AirAirFrequency = airAir is decimal mhz ? Frequency.FromMegahertz(mhz) : null,
    };

    private static FlightPlan Plan() => new()
    {
        FlightPlanId = "vfr",
        Callsign = Callsign.Create("STORM123"),
        DepartureIcao = "STMR",
        DestinationIcao = "STMR",
        SourceIdentifier = "Test",
    };

    [Theory]
    [InlineData("None", true)]
    [InlineData("none", true)]
    [InlineData("Afis", false)]
    [InlineData("Tower", false)]
    [InlineData(null, false)]
    public void Only_a_profile_that_says_none_has_no_air_traffic_service(string? kind, bool expected) =>
        Assert.Equal(expected, Profile(kind, null).HasNoAtc);

    [Fact]
    public async Task Loading_a_plan_at_such_an_aerodrome_tells_the_pilot_in_the_conversation()
    {
        _profiles.Profile = Profile("None", 119.205m);

        await _context.LoadFlightPlanAsync(Plan(), CancellationToken.None);

        ConversationTurn turn = Assert.Single(_context.CurrentSession.Conversation);
        Assert.Equal(ConversationSpeaker.System, turn.Speaker);
        Assert.Contains("119.205", turn.Text, StringComparison.Ordinal);
        Assert.Contains("Storm International (STMR)", turn.Text, StringComparison.Ordinal);

        // The same plan again does not repeat it.
        await _context.LoadFlightPlanAsync(Plan(), CancellationToken.None);
        Assert.Single(_context.CurrentSession.Conversation);
    }

    [Fact]
    public async Task A_controlled_aerodrome_gets_no_such_message()
    {
        _profiles.Profile = Profile("Tower", 118.3m);

        await _context.LoadFlightPlanAsync(Plan(), CancellationToken.None);

        Assert.Empty(_context.CurrentSession.Conversation);
    }

    [Fact]
    public async Task A_call_at_such_an_aerodrome_gets_no_controller_and_no_engine_decision()
    {
        _profiles.Profile = Profile("None", 119.205m);
        await _context.LoadFlightPlanAsync(Plan(), CancellationToken.None);

        TransmissionProcessingResult result = await _coordinator.ProcessAsync(
            PilotTransmission.Create(
                _context.CurrentSession.SessionId,
                "Storm one two three, request taxi",
                TransmissionSource.ManualText,
                _clock.UtcNow),
            CancellationToken.None);

        Assert.True(result.WasAerodromeWithoutAtc);
        Assert.False(result.Processed);
        Assert.Equal(Frequency.FromMegahertz(119.205m), result.ExpectedFrequency);
        Assert.Null(result.Decision);
        Assert.Null(result.ControllerSpokenText);
    }

    [Fact]
    public async Task A_call_at_a_controlled_aerodrome_is_processed_as_before()
    {
        _profiles.Profile = Profile("TowerAndGround", 118.3m);
        await _context.LoadFlightPlanAsync(Plan(), CancellationToken.None);

        TransmissionProcessingResult result = await _coordinator.ProcessAsync(
            PilotTransmission.Create(
                _context.CurrentSession.SessionId,
                "Storm one two three, request taxi",
                TransmissionSource.ManualText,
                _clock.UtcNow),
            CancellationToken.None);

        Assert.False(result.WasAerodromeWithoutAtc);
    }
}
