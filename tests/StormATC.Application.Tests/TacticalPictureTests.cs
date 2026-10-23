using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Phraseology;
using StormATC.Application.Sessions;
using StormATC.Application.Transmissions;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Approach;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>The tactical picture of a CAM T flight: the simulator's traffic as bearing, range, altitude and aspect.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The coordinator is IAsyncDisposable and is disposed in IAsyncLifetime.DisposeAsync.")]
public sealed class TacticalPictureTests : IAsyncLifetime
{
    private static readonly Frequency Tower = Frequency.FromMegahertz(118.300m);
    private static readonly GeoPosition Own = GeoPosition.Create(48.0, 3.0);

    private readonly FixedClock _clock = new(Fixture.Now);
    private readonly StubTrafficProvider _traffic = new();
    private AtcSessionContext _context = null!;
    private PilotTransmissionCoordinator _coordinator = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _context = new AtcSessionContext(
            new StubAirportDataProvider(),
            _traffic,
            new StubWeatherProvider(),
            _clock,
            NullLogger<AtcSessionContext>.Instance,
            vfrProfileProvider: null);

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

        _context.UpdateSession(AtcSession.Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, Airborne()));
        await _context.LoadFlightPlanAsync(
            new FlightPlan
            {
                FlightPlanId = "tactical",
                Callsign = Callsign.Create("STORM123"),
                DepartureIcao = "STMR",
                DestinationIcao = "STMR",
                SourceIdentifier = "Test",
            },
            CancellationToken.None);

        _context.SetLanguage(PhraseologyLanguage.French);
        _context.UpdateSession(_context.CurrentSession with { FlightRules = FlightRules.CamT });
        _context.UpdateAircraftState(Airborne());
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _coordinator.DisposeAsync();

    [Fact]
    public void A_contact_is_seen_by_bearing_range_altitude_and_aspect()
    {
        // 40 NM due east of the aircraft, flying west (toward it): hot.
        TrafficContact east = Contact(Own.Project(Heading.FromDegrees(90), 40), 25_030, headingMagnetic: 270);
        TacticalContact seen = TacticalGeometry.Describe(Own, 90, 90, east);

        Assert.InRange(seen.BearingMagnetic, 88, 92);
        Assert.Equal(40, seen.RangeNauticalMiles);
        Assert.Equal(25_000, seen.AltitudeFeet);
        Assert.Equal(TacticalAspect.Hot, seen.Aspect);

        // The same contact flying east (away): cold; flying north (across): flank.
        Assert.Equal(TacticalAspect.Cold, TacticalGeometry.Describe(Own, 90, 90, east with { HeadingDegrees = 90 }).Aspect);
        Assert.Equal(TacticalAspect.Flank, TacticalGeometry.Describe(Own, 90, 90, east with { HeadingDegrees = 0 }).Aspect);
    }

    [Fact]
    public void The_bearing_is_magnetic()
    {
        // The aircraft's true heading is 2 degrees more than its magnetic one: a variation of 2 degrees east.
        TrafficContact north = Contact(Own.Project(Heading.FromDegrees(0), 10), 5_000, headingMagnetic: 0);

        TacticalContact seen = TacticalGeometry.Describe(Own, ownTrueHeadingDegrees: 92, ownMagneticHeadingDegrees: 90, contact: north);

        Assert.InRange(seen.BearingMagnetic, 357, 359);
    }

    [Fact]
    public async Task The_controller_gives_the_nearest_contacts_first_and_ignores_the_ones_on_the_ground()
    {
        _traffic.Traffic.Add(Contact(Own.Project(Heading.FromDegrees(90), 60), 20_000, 270));
        _traffic.Traffic.Add(Contact(Own.Project(Heading.FromDegrees(45), 30), 15_000, 90));
        _traffic.Traffic.Add(Contact(Own.Project(Heading.FromDegrees(180), 5), 0, 0) with { IsOnGround = true });

        TransmissionProcessingResult result = await SendAsync("Storm un deux trois, demande la situation");

        Assert.Equal(PilotIntentType.RequestTacticalPicture, result.Intent?.Type);
        string text = result.ControllerSpokenText!;
        Assert.Contains("deux contacts", text, StringComparison.Ordinal);
        Assert.Contains("gisement", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("trente milles", StringComparison.Ordinal) < text.IndexOf("soixante milles", StringComparison.Ordinal),
            text);
    }

    [Fact]
    public async Task Without_any_contact_the_controller_says_so()
    {
        TransmissionProcessingResult result = await SendAsync("Storm un deux trois, situation");

        Assert.Contains("aucun contact", result.ControllerSpokenText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_civil_pilot_gets_no_picture()
    {
        _context.UpdateSession(_context.CurrentSession with { FlightRules = FlightRules.Vfr });
        _traffic.Traffic.Add(Contact(Own.Project(Heading.FromDegrees(90), 30), 15_000, 90));

        TransmissionProcessingResult result = await SendAsync("Storm un deux trois, demande la situation");

        Assert.DoesNotContain("gisement", result.ControllerSpokenText ?? string.Empty, StringComparison.Ordinal);
    }

    private static TrafficContact Contact(GeoPosition position, double altitude, double headingMagnetic) => new()
    {
        Callsign = "TARGET",
        Position = position,
        AltitudeFeet = altitude,
        GroundSpeedKnots = 400,
        HeadingDegrees = headingMagnetic,
        ModelTitle = "Test Target",
    };

    private Task<TransmissionProcessingResult> SendAsync(string text) =>
        _coordinator.ProcessAsync(
            PilotTransmission.Create(
                _context.CurrentSession.SessionId, text, TransmissionSource.ManualText, _clock.UtcNow),
            CancellationToken.None);

    private static AircraftState Airborne() => TestAerodromes.AtStand(Fixture.Now) with
    {
        Position = Own,
        IndicatedAltitudeFeet = 20_000,
        PressureAltitudeFeet = 20_000,
        GroundSpeedKnots = 400,
        IndicatedAirspeedKnots = 300,
        TrueHeadingDegrees = 90,
        MagneticHeadingDegrees = 90,
        VerticalSpeedFeetPerMinute = 0,
        IsOnGround = false,
        ParkingBrakeSet = false,
        EnginesRunning = true,
        Radio = RadioState.Default with { Com1Active = Tower, TransmitOnCom1 = true },
    };
}
