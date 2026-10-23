using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Panel;
using StormATC.Application.Phraseology;
using StormATC.Application.Sessions;
using StormATC.Application.Transmissions;
using StormATC.Domain;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>The French military regimes: CAM Victor (à vue) and CAM India (aux instruments).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The coordinator is IAsyncDisposable and is disposed in IAsyncLifetime.DisposeAsync.")]
public sealed class MilitaryRegimeTests : IAsyncLifetime
{
    private readonly FixedClock _clock = new(Fixture.Now);
    private AtcSessionContext _context = null!;
    private PilotTransmissionCoordinator _coordinator = null!;

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        _context = new AtcSessionContext(
            new StubAirportDataProvider(),
            new StubTrafficProvider(),
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

        _context.UpdateSession(AtcSession.Create(
            Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now)));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _coordinator.DisposeAsync();

    private static AtcSession Session(FlightRules rules) => AtcSession
        .Create(Guid.CreateVersion7(), Fixture.Now, SessionMode.Mock, TestAerodromes.AtStand(Fixture.Now))
        with
    {
        FlightRules = rules,
    };

    private static PilotIntent ParseFrench(string french, FlightRules rules = FlightRules.CamV)
    {
        string normalized = TranscriptNormalizer.Normalize(FrenchTranscriptCanonicalizer.Canonicalize(french));
        return DeterministicPilotIntentParser.Parse(normalized, Session(rules));
    }

    [Theory]
    [InlineData(FlightRules.Vfr, true, false, false, "VFR")]
    [InlineData(FlightRules.Ifr, false, true, false, "IFR")]
    [InlineData(FlightRules.CamV, true, false, true, "CAM V")]
    [InlineData(FlightRules.CamI, false, true, true, "CAM I")]
    public void The_rules_group_by_how_the_flight_is_handled(
        FlightRules rules, bool visual, bool instrument, bool military, string label)
    {
        Assert.Equal(visual, rules.IsVisual());
        Assert.Equal(instrument, rules.IsInstrument());
        Assert.Equal(military, rules.IsMilitary());
        Assert.Equal(label, rules.ToLabel());
        Assert.Equal(rules, FlightRulesExtensions.FromLabel(label));
    }

    [Theory]
    [InlineData("Robinson Tour, Rafale douze, demande à passer en CAM India", FlightRules.CamI)]
    [InlineData("Rafale douze, demandons CAM Victor", FlightRules.CamV)]
    public void A_request_to_change_regime_is_recognised(string french, FlightRules expected)
    {
        PilotIntent intent = ParseFrench(french, FlightRules.CamV);

        Assert.Equal(PilotIntentType.RequestRegimeChange, intent.Type);
        Assert.Equal(expected, intent.Entities.DeclaredRegime);
    }

    [Fact]
    public void A_taxi_call_that_states_the_regime_stays_a_taxi_call_and_declares_it()
    {
        PilotIntent intent = ParseFrench("Sol, Rafale douze, CAM Victor, parking, demandons le roulage", FlightRules.Ifr);

        Assert.Equal(PilotIntentType.RequestTaxi, intent.Type);
        Assert.Equal(FlightRules.CamV, intent.Entities.DeclaredRegime);
    }

    [Fact]
    public async Task Changing_regime_updates_the_session_and_the_controller_confirms()
    {
        await _context.LoadFlightPlanAsync(
            new FlightPlan
            {
                FlightPlanId = "cam",
                Callsign = Callsign.Create("STORM123"),
                DepartureIcao = "STMR",
                DestinationIcao = "STMR",
                SourceIdentifier = "Test",
            },
            CancellationToken.None);

        foreach (decimal megahertz in new[] { 132.150m, 121.800m })
        {
            _context.UpdateAircraftState(_context.CurrentSession.AircraftState with
            {
                Radio = _context.CurrentSession.AircraftState.Radio with
                {
                    Com1Active = Frequency.FromMegahertz(megahertz),
                    TransmitOnCom1 = true,
                },
            });
        }

        TransmissionProcessingResult result = await _coordinator.ProcessAsync(
            PilotTransmission.Create(
                _context.CurrentSession.SessionId,
                "Storm one two three, request CAM India",
                TransmissionSource.ManualText,
                _clock.UtcNow),
            CancellationToken.None);

        Assert.Equal(FlightRules.CamI, _context.CurrentSession.FlightRules);
        Assert.Contains("CAM India", result.ControllerSpokenText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_adds_the_military_calls_only_in_a_military_regime()
    {
        PhraseologyGuide civil = PhraseologyCatalog.For(
            PhraseologyStage.VfrCircuit, PhraseologyLanguage.French, "FOXTROT ALFA", FlightRules.Vfr);
        PhraseologyGuide military = PhraseologyCatalog.For(
            PhraseologyStage.VfrCircuit, PhraseologyLanguage.French, "FOXTROT ALFA", FlightRules.CamV);

        Assert.Contains(military.Examples, example => example.Pilot.Contains("train sorti verrouillé", StringComparison.Ordinal));
        Assert.DoesNotContain(civil.Examples, example => example.Pilot.Contains("train sorti", StringComparison.Ordinal));
        Assert.Contains(
            PhraseologyCatalog.Always(PhraseologyLanguage.French, "FOXTROT ALFA", FlightRules.CamV),
            example => example.Pilot.Contains("CAM India", StringComparison.Ordinal));
    }

    [Fact]
    public void A_break_arrival_call_is_an_arrival_call_in_cam_v()
    {
        PilotIntent intent = ParseFrench("Storm un deux trois, vingt nautiques au sud, demande le break", FlightRules.CamV);

        Assert.Equal(PilotIntentType.VfrArrivalCall, intent.Type);
        Assert.True(intent.Entities.RequestsBreak);
    }
}
