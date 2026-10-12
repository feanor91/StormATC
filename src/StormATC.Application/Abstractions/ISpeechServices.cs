using StormATC.Domain.Phraseology;

namespace StormATC.Application.Abstractions;

/// <summary>
/// Streaming speech-to-text driven by push-to-talk.
/// </summary>
/// <remarks>
/// Implementations must capture audio only between <see cref="StartTransmissionAsync"/> and
/// <see cref="StopTransmissionAsync"/>. StormATC never opens a continuously listening
/// microphone, and never writes raw audio to disk by default — see
/// <c>docs/privacy-and-api-keys.md</c>.
/// </remarks>
public interface ISpeechRecognitionService : IAsyncDisposable
{
    /// <summary>A stable identifier for this service, shown on the Diagnostics page.</summary>
    string ProviderName { get; }

    /// <summary>True when the service has the configuration it needs.</summary>
    bool IsConfigured { get; }

    /// <summary>Raised repeatedly while the pilot is speaking, for on-screen feedback only.</summary>
    event EventHandler<InterimTranscriptEventArgs>? InterimTranscriptReceived;

    /// <summary>
    /// Raised once per transmission with the text that will be sent to the ATC pipeline.
    /// Interim results never reach the engine.
    /// </summary>
    event EventHandler<FinalTranscriptEventArgs>? FinalTranscriptReceived;

    /// <summary>Raised when capture or streaming fails.</summary>
    event EventHandler<SpeechErrorEventArgs>? ErrorOccurred;

    /// <summary>Raised with the current input level, for the audio meter.</summary>
    event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

    /// <summary>
    /// Recording devices visible to the speech provider.
    /// </summary>
    /// <remarks>
    /// The value is refreshed whenever it is read so a headset connected after application
    /// startup can be selected without restarting StormATC.
    /// </remarks>
    IReadOnlyList<string> InputDeviceNames { get; }

    /// <summary>Opens the microphone and the streaming connection.</summary>
    Task StartTransmissionAsync(CancellationToken cancellationToken);

    /// <summary>Closes the microphone and waits for the final transcript.</summary>
    Task StopTransmissionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records a short calibration phrase without sending it to the ATC engine.
    /// </summary>
    /// <param name="inputDeviceName">Selected recording device, or null for the Windows default.</param>
    /// <param name="duration">Length of the guided recording window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SpeechTrainingSample> CaptureTrainingSampleAsync(
        string? inputDeviceName,
        TimeSpan duration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies a personal recognition profile immediately for subsequent transmissions.
    /// </summary>
    void ApplyRecognitionProfile(SpeechRecognitionProfile profile);

    /// <summary>Verifies credentials without starting a transmission.</summary>
    Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>
/// User-controlled speech-recognition adaptation.
/// </summary>
/// <remarks>
/// This is local configuration, not a claim that a third-party model has been fine-tuned. Expected
/// phrases are sent as Deepgram keyterms, and explicit heard-to-expected corrections are applied
/// after transcription.
/// </remarks>
public sealed record SpeechRecognitionProfile
{
    /// <summary>Recording device name, or null to follow the Windows default.</summary>
    public string? InputDeviceName { get; init; }

    /// <summary>Deepgram language hint. Plain <c>en</c> is English across accents.</summary>
    public string Language { get; init; } = "en";

    /// <summary>Terms and phrases Deepgram should expect to hear.</summary>
    public IReadOnlyList<string> Keyterms { get; init; } = [];

    /// <summary>Exact transcription corrections learned through the guided trainer.</summary>
    public IReadOnlyDictionary<string, string> TranscriptCorrections { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Result of one guided voice-training sample.</summary>
public sealed record SpeechTrainingSample
{
    /// <summary>True when a non-empty final transcript was received.</summary>
    public bool Success { get; init; }

    /// <summary>What the provider heard, before a new correction is applied.</summary>
    public string Transcript { get; init; } = string.Empty;

    /// <summary>Average provider confidence for the final transcript segments.</summary>
    public double Confidence { get; init; }

    /// <summary>Actionable result text for the trainer UI.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>A partial transcript, for display only.</summary>
public sealed class InterimTranscriptEventArgs(string text) : EventArgs
{
    /// <summary>The partial text.</summary>
    public string Text { get; } = text;
}

/// <summary>The final transcript of one transmission.</summary>
public sealed class FinalTranscriptEventArgs(string text, double confidence) : EventArgs
{
    /// <summary>The transcript.</summary>
    public string Text { get; } = text;

    /// <summary>Provider confidence, 0 to 1.</summary>
    public double Confidence { get; } = confidence;
}

/// <summary>A speech pipeline failure.</summary>
public sealed class SpeechErrorEventArgs(string message, string? suggestedAction = null) : EventArgs
{
    /// <summary>What went wrong.</summary>
    public string Message { get; } = message;

    /// <summary>One concrete thing the user can do about it.</summary>
    public string? SuggestedAction { get; } = suggestedAction;
}

/// <summary>The current microphone input level.</summary>
public sealed class AudioLevelEventArgs(double level) : EventArgs
{
    /// <summary>Normalised peak level, 0 to 1.</summary>
    public double Level { get; } = level;
}

/// <summary>Text-to-speech for the controller voice.</summary>
public interface ITextToSpeechService : IAsyncDisposable
{
    /// <summary>A stable identifier for this service.</summary>
    string ProviderName { get; }

    /// <summary>True when the service has the configuration it needs.</summary>
    bool IsConfigured { get; }

    /// <summary>True while controller audio is playing.</summary>
    bool IsSpeaking { get; }

    /// <summary>
    /// Synthesises and plays a controller transmission.
    /// </summary>
    /// <remarks>
    /// Implementations must never overlap two controller voices. The documented policy is to
    /// cancel any in-progress non-critical playback when a newer transmission supersedes it.
    /// </remarks>
    Task SpeakAsync(string text, VoiceConfiguration voice, CancellationToken cancellationToken);

    /// <summary>
    /// Plays a named transmission, reusing its in-memory synthesized audio while the text and
    /// provider voice remain unchanged.
    /// </summary>
    /// <remarks>
    /// This is intended for repeating broadcasts such as ATIS. Implementations return false when
    /// no audio could be synthesized or played. A changed text value invalidates only the named
    /// entry and may require one new provider request; an unchanged value must not make another
    /// synthesis request.
    /// </remarks>
    Task<bool> SpeakCachedAsync(
        string cacheKey,
        string text,
        VoiceConfiguration voice,
        CancellationToken cancellationToken);

    /// <summary>Stops playback immediately.</summary>
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>Replays the most recent controller transmission.</summary>
    Task ReplayLastAsync(CancellationToken cancellationToken);

    /// <summary>Verifies credentials without speaking.</summary>
    Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>Voice and audio-treatment settings for one transmission.</summary>
public sealed record VoiceConfiguration
{
    /// <summary>A neutral default used when no per-controller voice is configured.</summary>
    public static VoiceConfiguration Default { get; } = new();

    /// <summary>The default voice for a controller speaking <paramref name="language"/>.</summary>
    public static VoiceConfiguration For(PhraseologyLanguage language) =>
        language == PhraseologyLanguage.English ? Default : new() { Language = language };

    /// <summary>
    /// The language of the text. Providers pick a voice that speaks it when
    /// <see cref="VoiceId"/> is empty.
    /// </summary>
    public PhraseologyLanguage Language { get; init; } = PhraseologyLanguage.English;

    /// <summary>The provider voice identifier.</summary>
    public string VoiceId { get; init; } = string.Empty;

    /// <summary>Playback volume, 0 to 1.</summary>
    public double Volume { get; init; } = 0.85;

    /// <summary>Whether to apply the radio effect.</summary>
    public bool ApplyRadioEffect { get; init; } = true;

    /// <summary>Radio-effect intensity, 0 to 1.</summary>
    public double RadioEffectStrength { get; init; } = 0.5;

    /// <summary>Background static level, 0 to 1.</summary>
    public double StaticLevel { get; init; } = 0.08;

    /// <summary>Whether to play the transmission-start and -end clicks.</summary>
    public bool IncludeClicks { get; init; } = true;
}
