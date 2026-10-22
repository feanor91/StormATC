using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Application.Atis;
using StormATC.Application.Sessions;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

public sealed class AtisBroadcastTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 07, 28, 12, 30, 00, TimeSpan.Zero);

    [Fact]
    public void Builder_includes_only_known_operational_facts()
    {
        AtcSession session = SessionOnAtis();
        var weather = new WeatherSnapshot
        {
            WindDirection = Heading.FromDegrees(270),
            WindSpeedKnots = 12,
            VisibilityMeters = 10_000,
            TemperatureCelsius = -5,
            QnhHectopascals = 1013,
            Source = "MSFS live aircraft environment",
        };

        AtisBroadcast broadcast = Assert.IsType<AtisBroadcast>(
            AtisBroadcastBuilder.Build(session, TestAerodromes.Departure, weather, Now));

        Assert.Equal("STMR", broadcast.AirportIcao);
        Assert.Equal(Frequency.FromMegahertz(132.150m), broadcast.Frequency);
        Assert.Contains("one two three zero Zulu", broadcast.SpokenText);
        Assert.Contains("Wind two seven zero degrees, one two knots", broadcast.SpokenText);
        Assert.Contains("Visibility one zero kilometres or more", broadcast.SpokenText);
        Assert.Contains("Runway in use runway one eight", broadcast.SpokenText);
        Assert.Contains("Temperature minus five", broadcast.SpokenText);
        Assert.Contains("Q N H one zero one three", broadcast.SpokenText);
        Assert.Contains(
            $"information {broadcast.InformationCode}",
            broadcast.SpokenText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Wind 270° at 12 kt", broadcast.DisplayText);
        Assert.Equal("MSFS live aircraft environment", broadcast.WeatherSource);
    }

    [Fact]
    public void Builder_labels_an_unknown_runway_and_does_not_invent_weather()
    {
        AtcSession session = SessionOnAtis() with
        {
            FlightPlan = Plan() with { DepartureRunway = null },
            AssignedDepartureRunway = null,
        };

        AtisBroadcast broadcast = Assert.IsType<AtisBroadcast>(
            AtisBroadcastBuilder.Build(
                session,
                TestAerodromes.Departure with { Runways = [] },
                weather: null,
                Now));

        Assert.Contains("Runway information unavailable", broadcast.SpokenText);
        Assert.DoesNotContain("Wind", broadcast.SpokenText);
        Assert.DoesNotContain("Visibility", broadcast.SpokenText);
        Assert.DoesNotContain("Q N H", broadcast.SpokenText);
    }

    [Fact]
    public void Edition_identifier_changes_on_the_half_hour()
    {
        AtisBroadcast first = Assert.IsType<AtisBroadcast>(
            AtisBroadcastBuilder.Build(SessionOnAtis(), TestAerodromes.Departure, null, Now));
        AtisBroadcast second = Assert.IsType<AtisBroadcast>(
            AtisBroadcastBuilder.Build(
                SessionOnAtis(),
                TestAerodromes.Departure,
                null,
                Now.AddMinutes(30)));

        Assert.NotEqual(first.EditionNumber, second.EditionNumber);
        Assert.NotEqual(first.InformationCode, second.InformationCode);
    }

    [Fact]
    public void Operational_signature_ignores_clock_and_small_weather_jitter()
    {
        var firstWeather = new WeatherSnapshot
        {
            WindDirection = Heading.FromDegrees(181),
            WindSpeedKnots = 8,
            VisibilityMeters = 10_050,
            TemperatureCelsius = 18,
            QnhHectopascals = 1013,
        };
        var secondWeather = firstWeather with
        {
            WindDirection = Heading.FromDegrees(184),
            VisibilityMeters = 12_000,
        };

        AtisBroadcast first = Assert.IsType<AtisBroadcast>(
            AtisBroadcastBuilder.Build(
                SessionOnAtis(),
                TestAerodromes.Departure,
                firstWeather,
                Now,
                edition: 0));
        AtisBroadcast second = Assert.IsType<AtisBroadcast>(
            AtisBroadcastBuilder.Build(
                SessionOnAtis(),
                TestAerodromes.Departure,
                secondWeather,
                Now.AddHours(1),
                edition: 0));

        Assert.Equal(first.OperationalSignature, second.OperationalSignature);
    }

    [Fact]
    public async Task Service_speaks_once_only_after_the_selected_radio_reaches_atis()
    {
        var clock = new FixedClock(Now);
        var weather = new StubWeatherProvider
        {
            Snapshot = new WeatherSnapshot
            {
                WindDirection = Heading.FromDegrees(180),
                WindSpeedKnots = 8,
                QnhHectopascals = 1015,
                Source = "test weather",
            },
        };
        var context = new AtcSessionContext(
            new StubAirportDataProvider(),
            new StubTrafficProvider(),
            weather,
            clock,
            NullLogger<AtcSessionContext>.Instance);
        var speech = new RecordingTextToSpeechService();
        using var service = new AtisBroadcastService(
            context,
            weather,
            speech,
            clock,
            NullLogger<AtisBroadcastService>.Instance);

        service.Start();
        context.StartNewSession(SessionMode.Mock, TestAerodromes.AtStand(Now));
        await context.LoadFlightPlanAsync(Plan(), CancellationToken.None);

        Assert.Equal(0, speech.SpeakCount);

        AircraftState tuned = context.CurrentSession.AircraftState with
        {
            Radio = context.CurrentSession.AircraftState.Radio with
            {
                Com1Active = Frequency.FromMegahertz(132.150m),
                TransmitOnCom1 = true,
            },
        };

        context.UpdateAircraftState(tuned);
        await speech.FirstTransmission.WaitAsync(TimeSpan.FromSeconds(2));
        context.UpdateAircraftState(tuned);

        Assert.Equal(1, speech.SpeakCount);
        Assert.NotNull(service.CurrentBroadcast);
        Assert.Contains("information", speech.LastText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_atis_waits_until_the_pilot_is_in_the_aircraft()
    {
        var clock = new FixedClock(Now);
        var weather = new StubWeatherProvider
        {
            Snapshot = new WeatherSnapshot
            {
                WindDirection = Heading.FromDegrees(180),
                WindSpeedKnots = 8,
                QnhHectopascals = 1015,
                Source = "test weather",
            },
        };
        var context = new AtcSessionContext(
            new StubAirportDataProvider(),
            new StubTrafficProvider(),
            weather,
            clock,
            NullLogger<AtcSessionContext>.Instance);
        var speech = new RecordingTextToSpeechService();
        using var service = new AtisBroadcastService(
            context,
            weather,
            speech,
            clock,
            NullLogger<AtisBroadcastService>.Instance);

        service.Start();
        context.StartNewSession(SessionMode.Mock, TestAerodromes.AtStand(Now));
        await context.LoadFlightPlanAsync(Plan(), CancellationToken.None);

        // The radio is on the ATIS, but the simulator is not running or is in its menus.
        AircraftState tuned = context.CurrentSession.AircraftState with
        {
            IsUserInAircraft = false,
            Radio = context.CurrentSession.AircraftState.Radio with
            {
                Com1Active = Frequency.FromMegahertz(132.150m),
                TransmitOnCom1 = true,
            },
        };
        context.UpdateAircraftState(tuned);
        await Task.Delay(150);

        Assert.Equal(0, speech.SpeakCount);
        Assert.Null(service.CurrentBroadcast);
        Assert.Contains("in the aircraft", service.StatusMessage, StringComparison.Ordinal);

        // The pilot gets into the aircraft: the ATIS is read.
        context.UpdateAircraftState(tuned with { IsUserInAircraft = true });
        await speech.FirstTransmission.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, speech.SpeakCount);
        Assert.NotNull(service.CurrentBroadcast);
    }

    [Fact]
    public async Task Service_loops_cached_audio_and_updates_only_after_operational_change()
    {
        var clock = new FixedClock(Now);
        var weather = new StubWeatherProvider
        {
            Snapshot = new WeatherSnapshot
            {
                WindDirection = Heading.FromDegrees(180),
                WindSpeedKnots = 8,
                VisibilityMeters = 10_000,
                TemperatureCelsius = 18,
                QnhHectopascals = 1015,
                Source = "test weather",
            },
        };
        var context = new AtcSessionContext(
            new StubAirportDataProvider(),
            new StubTrafficProvider(),
            weather,
            clock,
            NullLogger<AtcSessionContext>.Instance);
        var speech = new RecordingTextToSpeechService();
        using var service = new AtisBroadcastService(
            context,
            weather,
            speech,
            clock,
            NullLogger<AtisBroadcastService>.Instance,
            repeatDelay: TimeSpan.FromMilliseconds(5),
            unavailableRetryDelay: TimeSpan.FromMilliseconds(5));

        service.Start();
        context.StartNewSession(SessionMode.Mock, TestAerodromes.AtStand(Now));
        await context.LoadFlightPlanAsync(Plan(), CancellationToken.None);
        context.UpdateAircraftState(context.CurrentSession.AircraftState with
        {
            Radio = context.CurrentSession.AircraftState.Radio with
            {
                Com1Active = Frequency.FromMegahertz(132.150m),
                TransmitOnCom1 = true,
            },
        });

        await WaitUntilAsync(() => speech.PlaybackCount >= 2);
        string firstInformation = Assert.IsType<AtisBroadcast>(
            service.CurrentBroadcast).InformationCode;

        Assert.Equal(1, speech.SynthesisCount);

        // Time passing and tiny simulator jitter do not create a new ATIS edition.
        clock.Advance(TimeSpan.FromHours(1));
        weather.Snapshot = weather.Snapshot with
        {
            WindDirection = Heading.FromDegrees(184),
            VisibilityMeters = 12_000,
        };
        int playbackBeforeJitter = speech.PlaybackCount;
        await WaitUntilAsync(() => speech.PlaybackCount > playbackBeforeJitter);

        Assert.Equal(1, speech.SynthesisCount);
        Assert.Equal(
            firstInformation,
            Assert.IsType<AtisBroadcast>(service.CurrentBroadcast).InformationCode);

        // A material wind change creates one new edition and one new synthesis request.
        weather.Snapshot = weather.Snapshot with
        {
            WindDirection = Heading.FromDegrees(230),
        };
        await WaitUntilAsync(() => speech.SynthesisCount >= 2);

        Assert.NotEqual(
            firstInformation,
            Assert.IsType<AtisBroadcast>(service.CurrentBroadcast).InformationCode);
        Assert.Equal(2, speech.SynthesisCount);
    }

    [Fact]
    public async Task Returning_to_atis_after_delivery_replays_cache_without_reopening_atis_control()
    {
        var clock = new FixedClock(Now);
        var weather = new StubWeatherProvider
        {
            Snapshot = new WeatherSnapshot
            {
                WindDirection = Heading.FromDegrees(180),
                WindSpeedKnots = 8,
                QnhHectopascals = 1015,
            },
        };
        var context = new AtcSessionContext(
            new StubAirportDataProvider(),
            new StubTrafficProvider(),
            weather,
            clock,
            NullLogger<AtcSessionContext>.Instance);
        var speech = new RecordingTextToSpeechService();
        using var service = new AtisBroadcastService(
            context,
            weather,
            speech,
            clock,
            NullLogger<AtisBroadcastService>.Instance,
            repeatDelay: TimeSpan.FromHours(1));

        service.Start();
        context.StartNewSession(SessionMode.Mock, TestAerodromes.AtStand(Now));
        await context.LoadFlightPlanAsync(Plan(), CancellationToken.None);

        AircraftState atisState = context.CurrentSession.AircraftState with
        {
            Radio = context.CurrentSession.AircraftState.Radio with
            {
                Com1Active = Frequency.FromMegahertz(132.150m),
                TransmitOnCom1 = true,
            },
        };
        context.UpdateAircraftState(atisState);
        await WaitUntilAsync(() => speech.PlaybackCount >= 1);

        context.UpdateAircraftState(atisState with
        {
            Radio = atisState.Radio with
            {
                Com1Active = Frequency.FromMegahertz(121.800m),
            },
        });
        Assert.Equal(ControllerPosition.ClearanceDelivery, context.CurrentSession.CurrentController);

        context.UpdateAircraftState(atisState);
        await WaitUntilAsync(() => speech.PlaybackCount >= 2);

        Assert.Equal(ControllerPosition.ClearanceDelivery, context.CurrentSession.CurrentController);
        Assert.Equal(1, speech.SynthesisCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(5, cancellation.Token);
        }
    }

    private static AtcSession SessionOnAtis() =>
        AtcSession.Create(
            Guid.Parse("1149ec33-129d-4700-bfcc-897705026902"),
            Now,
            SessionMode.Mock,
            TestAerodromes.AtStand(Now) with
            {
                Radio = RadioState.Default with
                {
                    Com1Active = Frequency.FromMegahertz(132.150m),
                    TransmitOnCom1 = true,
                },
            })
        with
        {
            FlightPlan = Plan(),
            AssignedDepartureRunway = RunwayIdentifier.Create("18"),
            CurrentController = ControllerPosition.Atis,
            CurrentFrequency = Frequency.FromMegahertz(132.150m),
        };

    private static FlightPlan Plan() => new()
    {
        FlightPlanId = "atis-test",
        Callsign = Callsign.Create("STORM123"),
        DepartureIcao = "STMR",
        DestinationIcao = "STMD",
        DepartureRunway = RunwayIdentifier.Create("18"),
        SourceIdentifier = "Test",
    };

    private sealed class RecordingTextToSpeechService : ITextToSpeechService
    {
        private readonly TaskCompletionSource _firstTransmission =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

        public string ProviderName => "Recording";

        public bool IsConfigured => true;

        public bool IsSpeaking => false;

        public int SpeakCount { get; private set; }

        public int PlaybackCount { get; private set; }

        public int SynthesisCount { get; private set; }

        public string LastText { get; private set; } = string.Empty;

        public Task FirstTransmission => _firstTransmission.Task;

        public Task SpeakAsync(
            string text,
            VoiceConfiguration voice,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SpeakCount++;
            PlaybackCount++;
            SynthesisCount++;
            LastText = text;
            _firstTransmission.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<bool> SpeakCachedAsync(
            string cacheKey,
            string text,
            VoiceConfiguration voice,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_cache.TryGetValue(cacheKey, out string? cachedText)
                || !string.Equals(cachedText, text, StringComparison.Ordinal))
            {
                _cache[cacheKey] = text;
                SynthesisCount++;
            }

            SpeakCount++;
            PlaybackCount++;
            LastText = text;
            _firstTransmission.TrySetResult();
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReplayLastAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ProviderTestResult.Ok("Recording provider", TimeSpan.Zero));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
