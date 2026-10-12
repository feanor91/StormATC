using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;
using StormATC.Infrastructure.Speech;

namespace StormATC.Infrastructure.Tests;

/// <summary>
/// The local speech engines and the routers that choose between local and cloud speech.
/// </summary>
/// <remarks>
/// Whisper and Piper are external programs, so these tests cover everything StormATC itself
/// decides: the prompt, the transcript clean-up, the text sent to the voice, the language
/// handling for the cloud recognizer, and which engine a transmission goes to.
/// </remarks>
public class LocalVoiceTests
{
    // =====================================================================================
    // Whisper
    // =====================================================================================

    [Fact]
    public void The_Whisper_prompt_is_in_the_session_language_and_names_the_flight()
    {
        AtcSession french = Session(PhraseologyLanguage.French);
        AtcSession english = Session(PhraseologyLanguage.English);

        string frenchPrompt = WhisperSpeechRecognitionService.BuildPrompt(french, ["Lyon Saint-Exupéry"]);
        string englishPrompt = WhisperSpeechRecognitionService.BuildPrompt(english, []);

        Assert.Contains("point d'attente", frenchPrompt, StringComparison.Ordinal);
        Assert.Contains("AFR123", frenchPrompt, StringComparison.Ordinal);
        Assert.Contains("LFPG", frenchPrompt, StringComparison.Ordinal);
        Assert.Contains("LFLL", frenchPrompt, StringComparison.Ordinal);
        Assert.Contains("OKIPA", frenchPrompt, StringComparison.Ordinal);
        Assert.Contains("Lyon Saint-Exupéry", frenchPrompt, StringComparison.Ordinal);
        Assert.Contains("holding point", englishPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("point d'attente", englishPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" Roulons point d'attente piste 27 via Bravo.\n", "Roulons point d'attente piste 27 via Bravo")]
    [InlineData("[BLANK_AUDIO]\n", "")]
    [InlineData(" (musique)\n Transpondeur 4521.\n", "Transpondeur 4521")]
    [InlineData(" Sous-titres réalisés par la communauté d'Amara.org\n", "")]
    [InlineData(" Thanks for watching!\n", "")]
    [InlineData(" AFR123,\n montons niveau 120.\n", "AFR123, montons niveau 120")]
    public void Whisper_output_is_cleaned_of_annotations_and_silence_hallucinations(
        string output,
        string expected) =>
        Assert.Equal(expected, WhisperSpeechRecognitionService.CleanTranscript(output));

    // =====================================================================================
    // Piper
    // =====================================================================================

    [Fact]
    public void French_Piper_text_spells_short_acronyms_letter_by_letter()
    {
        string prepared = PiperTextToSpeechService.PrepareText(
            "AFR123, autorisé approche ILS piste 27,\nQNH mille treize.",
            PhraseologyLanguage.French);

        Assert.Equal("AFR123, autorisé approche I L S piste 27, Q N H mille treize.", prepared);
    }

    [Fact]
    public void English_Piper_text_is_left_as_written()
    {
        string prepared = PiperTextToSpeechService.PrepareText(
            "Storm one two three, cleared ILS approach.",
            PhraseologyLanguage.English);

        Assert.Equal("Storm one two three, cleared ILS approach.", prepared);
    }

    // =====================================================================================
    // Deepgram in a French session
    // =====================================================================================

    [Fact]
    public void A_French_session_is_recognised_in_French_without_English_keyterms()
    {
        var profile = new SpeechRecognitionProfile { Language = "en-GB", Keyterms = ["request IFR clearance"] };

        SpeechRecognitionProfile french = DeepgramRecognitionProfile.ForSessionLanguage(
            profile,
            PhraseologyLanguage.French);
        SpeechRecognitionProfile english = DeepgramRecognitionProfile.ForSessionLanguage(
            profile,
            PhraseologyLanguage.English);

        Assert.Equal("fr", french.Language);
        Assert.Equal("en-GB", english.Language);
        Assert.Empty(DeepgramRecognitionProfile.BuildKeyterms(profile, Session(PhraseologyLanguage.French)));
        Assert.NotEmpty(DeepgramRecognitionProfile.BuildKeyterms(profile, Session(PhraseologyLanguage.English)));
    }

    // =====================================================================================
    // Routers
    // =====================================================================================

    [Fact]
    public async Task Recognition_goes_to_the_engine_selected_when_the_key_was_pressed()
    {
        var options = new MutableOptions<LocalVoiceOptions>(new LocalVoiceOptions
        {
            SpeechToTextProvider = SpeechProviders.Whisper,
        });
        var cloud = new FakeRecognizer("Deepgram");
        var local = new FakeRecognizer("Whisper (local)");
        var router = new SpeechRecognitionRouter(cloud, local, options);
        string? heard = null;
        router.FinalTranscriptReceived += (_, e) => heard = e.Text;

        Assert.Equal("Whisper (local)", router.ProviderName);

        await router.StartTransmissionAsync(CancellationToken.None);

        // Changing the setting while the key is held must not split the transmission.
        options.Set(new LocalVoiceOptions { SpeechToTextProvider = SpeechProviders.Deepgram });
        await router.StopTransmissionAsync(CancellationToken.None);

        Assert.Equal(1, local.Starts);
        Assert.Equal(1, local.Stops);
        Assert.Equal(0, cloud.Starts);
        Assert.Equal("heard by Whisper (local)", heard);
        Assert.Equal("Deepgram", router.ProviderName);

        await router.StartTransmissionAsync(CancellationToken.None);
        await router.StopTransmissionAsync(CancellationToken.None);

        Assert.Equal(1, cloud.Starts);
        Assert.Equal(1, cloud.Stops);
    }

    [Fact]
    public void A_recognition_profile_reaches_both_engines()
    {
        var cloud = new FakeRecognizer("Deepgram");
        var local = new FakeRecognizer("Whisper (local)");
        var router = new SpeechRecognitionRouter(
            cloud,
            local,
            new MutableOptions<LocalVoiceOptions>(new LocalVoiceOptions()));

        router.ApplyRecognitionProfile(new SpeechRecognitionProfile { InputDeviceName = "Headset" });

        Assert.Equal("Headset", cloud.Profile?.InputDeviceName);
        Assert.Equal("Headset", local.Profile?.InputDeviceName);
    }

    [Fact]
    public async Task The_voice_follows_the_setting_and_replays_from_the_engine_that_spoke()
    {
        var options = new MutableOptions<LocalVoiceOptions>(new LocalVoiceOptions
        {
            TextToSpeechProvider = SpeechProviders.Piper,
        });
        var cloud = new FakeVoice("Deepgram");
        var local = new FakeVoice("Piper (local)");
        var router = new TextToSpeechRouter(cloud, local, options);

        await router.SpeakAsync("Storm un deux trois, reçu.", VoiceConfiguration.For(PhraseologyLanguage.French), CancellationToken.None);
        options.Set(new LocalVoiceOptions { TextToSpeechProvider = SpeechProviders.Deepgram });
        await router.ReplayLastAsync(CancellationToken.None);

        Assert.Equal(["Storm un deux trois, reçu."], local.Spoken);
        Assert.Equal(PhraseologyLanguage.French, local.LastVoice?.Language);
        Assert.Equal(1, local.Replays);
        Assert.Empty(cloud.Spoken);

        await router.SpeakAsync("Storm one two three, roger.", VoiceConfiguration.Default, CancellationToken.None);
        Assert.Equal(["Storm one two three, roger."], cloud.Spoken);
    }

    // =====================================================================================

    private static AtcSession Session(PhraseologyLanguage language)
    {
        var plan = new FlightPlan
        {
            FlightPlanId = "test",
            Callsign = Callsign.Create("AFR123"),
            DepartureIcao = "LFPG",
            DestinationIcao = "LFLL",
            DepartureRunway = RunwayIdentifier.Create("27L"),
            StandardInstrumentDeparture = "OKIPA1A",
            RouteLegs = [new RouteLeg { Identifier = "OKIPA" }],
            SourceIdentifier = "Test",
        };

        return AtcSession.Create(
                Guid.CreateVersion7(),
                DateTimeOffset.UtcNow,
                SessionMode.Mock,
                AircraftState.Unknown(DateTimeOffset.UtcNow))
            .WithFlightPlan(plan) with
        {
            Language = language,
        };
    }

    private sealed class MutableOptions<T>(T value) : IOptionsMonitor<T>
    {
        private T _value = value;

        public T CurrentValue => _value;

        public void Set(T value) => _value = value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeRecognizer(string name) : ISpeechRecognitionService
    {
        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public SpeechRecognitionProfile? Profile { get; private set; }

        public string ProviderName => name;

        public bool IsConfigured => true;

        public IReadOnlyList<string> InputDeviceNames => [];

        public event EventHandler<InterimTranscriptEventArgs>? InterimTranscriptReceived;

        public event EventHandler<FinalTranscriptEventArgs>? FinalTranscriptReceived;

        public event EventHandler<SpeechErrorEventArgs>? ErrorOccurred;

        public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

        public Task StartTransmissionAsync(CancellationToken cancellationToken)
        {
            Starts++;
            AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(0.5));
            return Task.CompletedTask;
        }

        public Task StopTransmissionAsync(CancellationToken cancellationToken)
        {
            Stops++;
            InterimTranscriptReceived?.Invoke(this, new InterimTranscriptEventArgs("…"));
            FinalTranscriptReceived?.Invoke(this, new FinalTranscriptEventArgs($"heard by {name}", 1));
            return Task.CompletedTask;
        }

        public Task<SpeechTrainingSample> CaptureTrainingSampleAsync(
            string? inputDeviceName,
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            ErrorOccurred?.Invoke(this, new SpeechErrorEventArgs("not used"));
            return Task.FromResult(new SpeechTrainingSample());
        }

        public void ApplyRecognitionProfile(SpeechRecognitionProfile profile) => Profile = profile;

        public Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ProviderTestResult.Ok(name, TimeSpan.Zero));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeVoice(string name) : ITextToSpeechService
    {
        public List<string> Spoken { get; } = [];

        public VoiceConfiguration? LastVoice { get; private set; }

        public int Replays { get; private set; }

        public string ProviderName => name;

        public bool IsConfigured => true;

        public bool IsSpeaking => false;

        public Task SpeakAsync(string text, VoiceConfiguration voice, CancellationToken cancellationToken)
        {
            Spoken.Add(text);
            LastVoice = voice;
            return Task.CompletedTask;
        }

        public Task<bool> SpeakCachedAsync(
            string cacheKey,
            string text,
            VoiceConfiguration voice,
            CancellationToken cancellationToken)
        {
            Spoken.Add(text);
            LastVoice = voice;
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReplayLastAsync(CancellationToken cancellationToken)
        {
            Replays++;
            return Task.CompletedTask;
        }

        public Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ProviderTestResult.Ok(name, TimeSpan.Zero));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
