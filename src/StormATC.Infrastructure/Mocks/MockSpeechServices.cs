using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;

namespace StormATC.Infrastructure.Mocks;

/// <summary>
/// A speech-to-text service that captures nothing and transcribes nothing.
/// </summary>
/// <remarks>
/// <para>
/// Registered in offline mode, where the pilot types instead of speaking, via the developer text
/// input. It deliberately reports an error rather than inventing a transcript — a fake transcript
/// would make the push-to-talk button appear to work when it does not.
/// </para>
/// <para>
/// No audio device is ever opened by this implementation.
/// </para>
/// </remarks>
public sealed class MockSpeechRecognitionService(
    ILogger<MockSpeechRecognitionService> logger) : ISpeechRecognitionService
{
    private readonly ILogger<MockSpeechRecognitionService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ProviderName => "Mock (no speech recognition)";

    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public event EventHandler<InterimTranscriptEventArgs>? InterimTranscriptReceived;

    /// <inheritdoc />
    public event EventHandler<FinalTranscriptEventArgs>? FinalTranscriptReceived;

    /// <inheritdoc />
    public event EventHandler<SpeechErrorEventArgs>? ErrorOccurred;

    /// <inheritdoc />
    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

    /// <inheritdoc />
    public IReadOnlyList<string> InputDeviceNames => [];

    /// <inheritdoc />
    public Task StartTransmissionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Push-to-talk pressed, but no speech provider is configured.");

        ErrorOccurred?.Invoke(this, new SpeechErrorEventArgs(
            "Speech recognition is not configured.",
            "Add a Deepgram API key in Settings, or use the text input on the Dashboard in developer mode."));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopTransmissionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SpeechTrainingSample> CaptureTrainingSampleAsync(
        string? inputDeviceName,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SpeechTrainingSample
        {
            Message = "Voice training is unavailable while Offline mode is enabled.",
        });
    }

    /// <inheritdoc />
    public void ApplyRecognitionProfile(SpeechRecognitionProfile profile) =>
        ArgumentNullException.ThrowIfNull(profile);

    /// <inheritdoc />
    public Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(ProviderTestResult.Fail(
            "Offline mode is on, so no speech service is available. Turn it off in Settings and "
            + "add a Deepgram API key to enable speech recognition."));
    }

    /// <summary>
    /// Raises the transcript events as if the pilot had spoken. Used by the developer text
    /// input so the UI takes exactly the same path as a real voice transmission.
    /// </summary>
    public void SimulateTransmission(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        InterimTranscriptReceived?.Invoke(this, new InterimTranscriptEventArgs(text));
        AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(0));
        FinalTranscriptReceived?.Invoke(this, new FinalTranscriptEventArgs(text, 1.0));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A text-to-speech service that produces no audio.
/// </summary>
/// <remarks>
/// The controller's words still appear in the transcript and on the dashboard; only the audio
/// is absent. This keeps offline mode honest about what is and is not working, while leaving the
/// entire ATC pipeline fully usable.
/// </remarks>
public sealed class MockTextToSpeechService(
    ILogger<MockTextToSpeechService> logger) : ITextToSpeechService
{
    private readonly ILogger<MockTextToSpeechService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private string? _lastSpokenText;

    /// <inheritdoc />
    public string ProviderName => "Mock (no speech synthesis)";

    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public bool IsSpeaking => false;

    /// <summary>Raised when a transmission would have been spoken. Used by the UI for feedback.</summary>
    public event EventHandler<string>? TransmissionSpoken;

    /// <inheritdoc />
    public Task SpeakAsync(string text, VoiceConfiguration voice, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _lastSpokenText = text;
        _logger.LogInformation("Controller (silent, no TTS configured): {Text}", text);
        TransmissionSpoken?.Invoke(this, text);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<bool> SpeakCachedAsync(
        string cacheKey,
        string text,
        VoiceConfiguration voice,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        await SpeakAsync(text, voice, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task ReplayLastAsync(CancellationToken cancellationToken)
    {
        if (_lastSpokenText is { Length: > 0 } text)
        {
            TransmissionSpoken?.Invoke(this, text);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(ProviderTestResult.Fail(
            "Offline mode is on, so no controller voice is available. Turn it off in Settings and "
            + "add a Deepgram API key to enable it."));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
