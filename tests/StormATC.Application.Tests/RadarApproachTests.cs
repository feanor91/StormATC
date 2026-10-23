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

/// <summary>
/// PAR and SRA: the pilot asks, the controller accepts and then talks the aircraft down from its position against
/// the extended centreline (runway 18 of the test aerodrome, approached from the north), in French.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The coordinator is IAsyncDisposable and is disposed in IAsyncLifetime.DisposeAsync.")]
public sealed class RadarApproachTests : IAsyncLifetime
{
    private static readonly Frequency Tower = Frequency.FromMegahertz(118.300m);
    private static readonly GeoPosition Threshold = GeoPosition.Create(52.31200, 4.70000);

    private readonly FixedClock _clock = new(Fixture.Now);
    private AtcSessionContext _context = null!;
    private PilotTransmissionCoordinator _coordinator = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _context = new AtcSessionContext(
            new StubAirportDataProvider(),
            new StubTrafficProvider(),
            new StubWeatherProvider { Snapshot = new WeatherSnapshot { QnhHectopascals = 1020 } },
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

        _context.UpdateSession(AtcSession.Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, At(8, 0, 2600)));
        await _context.LoadFlightPlanAsync(
            new FlightPlan
            {
                FlightPlanId = "par",
                Callsign = Callsign.Create("STORM123"),
                DepartureIcao = "STMR",
                DestinationIcao = "STMR",
                DepartureRunway = RunwayIdentifier.Create("18"),
                ArrivalRunway = RunwayIdentifier.Create("18"),
                SourceIdentifier = "Test",
            },
            CancellationToken.None);

        _context.SetLanguage(PhraseologyLanguage.French);
        _context.UpdateSession(_context.CurrentSession with { FlightRules = FlightRules.CamV });
        _context.UpdateAircraftState(At(8, 0, 2600));
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _coordinator.DisposeAsync();

    [Fact]
    public async Task A_par_is_accepted_then_talks_the_aircraft_down_and_ends_with_the_landing_clearance()
    {
        TransmissionProcessingResult request = await SendAsync("Storm un deux trois, demande PAR piste dix-huit");
        Assert.Equal(PilotIntentType.RequestRadarApproach, request.Intent?.Type);
        Assert.Contains("radar PAR", request.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Contains("ne répondez plus", request.ControllerSpokenText!, StringComparison.Ordinal);
        Assert.Equal(RadarApproachKind.Par, _context.CurrentSession.RadarApproach?.Kind);

        // Inside the wedge: radar contact.
        string contact = await NextCallAsync(secondsLater: 5, along: 8, crossEast: 0, height: HeightOnPath(8));
        Assert.Contains("contact radar", contact, StringComparison.Ordinal);

        // On the centreline and on the glidepath.
        string onPath = await NextCallAsync(secondsLater: 9, along: 6, crossEast: 0, height: HeightOnPath(6));
        Assert.Contains("sur l'axe", onPath, StringComparison.Ordinal);
        Assert.Contains("sur la pente", onPath, StringComparison.Ordinal);
        Assert.Contains("distance six milles", onPath, StringComparison.Ordinal);

        // East of the centreline (to the left of an aircraft flying south), and low: a heading and "sous la pente".
        string off = await NextCallAsync(secondsLater: 9, along: 4, crossEast: 500, height: HeightOnPath(4) - 300);
        Assert.Contains("à gauche de l'axe", off, StringComparison.Ordinal);
        Assert.Contains("route", off, StringComparison.Ordinal);
        Assert.Contains("sous la pente", off, StringComparison.Ordinal);

        // At 2 NM the gear is checked, once.
        string gear = await NextCallAsync(secondsLater: 5, along: 1.9, crossEast: 0, height: HeightOnPath(1.9));
        Assert.Contains("vérifiez train sorti verrouillé", gear, StringComparison.Ordinal);
        Assert.True(_context.CurrentSession.RadarApproach?.GearChecked);

        // The PAR ends with the landing clearance at the threshold.
        string landing = await NextCallAsync(secondsLater: 5, along: 0.8, crossEast: 0, height: HeightOnPath(0.8));
        Assert.Contains("autorisé atterrissage", landing, StringComparison.Ordinal);
        Assert.Null(_context.CurrentSession.RadarApproach);
    }

    [Fact]
    public async Task An_sra_gives_a_recommended_altitude_and_ends_at_two_miles()
    {
        await SendAsync("Storm un deux trois, demande SRA piste dix-huit");
        Assert.Equal(RadarApproachKind.Sra, _context.CurrentSession.RadarApproach?.Kind);

        await NextCallAsync(secondsLater: 5, along: 8, crossEast: 0, height: HeightOnPath(8));
        string call = await NextCallAsync(secondsLater: 9, along: 5, crossEast: 0, height: HeightOnPath(5));
        Assert.Contains("altitude recommandée", call, StringComparison.Ordinal);
        Assert.DoesNotContain("pente", call, StringComparison.Ordinal);

        string gear = await NextCallAsync(secondsLater: 7, along: 2.9, crossEast: 0, height: HeightOnPath(2.9));
        Assert.Contains("vérifiez train sorti verrouillé", gear, StringComparison.Ordinal);

        string end = await NextCallAsync(secondsLater: 5, along: 1.9, crossEast: 0, height: HeightOnPath(1.9));
        Assert.Contains("fin d'approche de surveillance", end, StringComparison.Ordinal);
        Assert.True(_context.CurrentSession.RadarApproach?.Completed);

        // "Piste en vue": cleared to land at once, the gear having been checked.
        TransmissionProcessingResult inSight = await SendAsync("Storm un deux trois, piste en vue");
        Assert.Equal(AtcResponseType.LandingClearance, inSight.Decision?.ResponseType);
        Assert.Null(_context.CurrentSession.RadarApproach);
    }

    [Fact]
    public async Task A_civil_pilot_asking_for_a_par_is_refused()
    {
        _context.UpdateSession(_context.CurrentSession with { FlightRules = FlightRules.Vfr });

        TransmissionProcessingResult request = await SendAsync("Storm un deux trois, demande PAR piste dix-huit");

        Assert.Null(_context.CurrentSession.RadarApproach);
        Assert.NotEqual(PilotIntentType.RequestRadarApproach, request.Intent?.Type);
    }

    [Fact]
    public async Task The_controller_says_nothing_while_the_aircraft_is_outside_the_wedge()
    {
        await SendAsync("Storm un deux trois, demande PAR piste dix-huit");

        // 8 NM out but 6 NM to the side: not inside the approach path.
        string silence = await NextCallAsync(secondsLater: 9, along: 8, crossEast: 11_000, height: HeightOnPath(8));

        Assert.Equal(string.Empty, silence);
        Assert.False(_context.CurrentSession.RadarApproach?.Established);
    }

    private static double HeightOnPath(double alongNauticalMiles) =>
        RadarGeometry.ThresholdCrossingHeightFeet + (alongNauticalMiles * 6076.12 * Math.Tan(RadarGeometry.GlideAngleDegrees * Math.PI / 180));

    /// <summary>Moves the clock and the aircraft, runs the state-change check, and returns what the controller said.</summary>
    private async Task<string> NextCallAsync(int secondsLater, double along, double crossEast, double height)
    {
        _clock.Advance(TimeSpan.FromSeconds(secondsLater));
        _context.UpdateAircraftState(At(along, crossEast, height));
        int turns = _context.CurrentSession.Conversation.Count;
        await _coordinator.ProcessStateChangeAsync(CancellationToken.None);
        return _context.CurrentSession.Conversation.Count > turns
            ? _context.CurrentSession.Conversation[^1].Text
            : string.Empty;
    }

    private Task<TransmissionProcessingResult> SendAsync(string text) =>
        _coordinator.ProcessAsync(
            PilotTransmission.Create(
                _context.CurrentSession.SessionId, text, TransmissionSource.ManualText, _clock.UtcNow),
            CancellationToken.None);

    /// <summary>An aircraft on the approach to runway 18 (heading south), `along` NM north of the threshold, `crossEast` m east of the axis.</summary>
    private static AircraftState At(double along, double crossEast, double height)
    {
        GeoPosition onAxis = Threshold.Project(Heading.FromDegrees(0), along);
        GeoPosition position = crossEast == 0 ? onAxis : onAxis.Project(Heading.FromDegrees(90), crossEast / 1852.0);
        return TestAerodromes.AtStand(Fixture.Now) with
        {
            Position = position,
            IndicatedAltitudeFeet = 20 + height,
            PressureAltitudeFeet = 20 + height,
            AltitudeAboveGroundFeet = height,
            GroundSpeedKnots = 160,
            IndicatedAirspeedKnots = 160,
            TrueHeadingDegrees = 180,
            MagneticHeadingDegrees = 180,
            VerticalSpeedFeetPerMinute = -700,
            IsOnGround = false,
            ParkingBrakeSet = false,
            EnginesRunning = true,
            Radio = RadioState.Default with { Com1Active = Tower, TransmitOnCom1 = true },
        };
    }
}
