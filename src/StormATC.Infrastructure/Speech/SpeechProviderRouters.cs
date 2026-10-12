using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;

namespace StormATC.Infrastructure.Speech;

/// <summary>
/// Sends push-to-talk to the recognition engine chosen in Settings — the cloud service or
/// Whisper on this computer — and can switch between them while the application runs.
/// </summary>
/// <remarks>
/// The choice is read when a transmission starts, and that transmission is finished by the same
/// engine even if the setting changes while the key is held.
/// </remarks>
public sealed class SpeechRecognitionRouter : ISpeechRecognitionService
{
    private readonly ISpeechRecognitionService _cloud;
    private readonly ISpeechRecognitionService _local;
    private readonly IOptionsMonitor<LocalVoiceOptions> _options;
    private readonly Lock _gate = new();
    private ISpeechRecognitionService? _transmitting;

    /// <summary>Creates the router over the cloud (or offline mock) and local engines.</summary>
    public SpeechRecognitionRouter(
        ISpeechRecognitionService cloud,
        ISpeechRecognitionService local,
        IOptionsMonitor<LocalVoiceOptions> options)
    {
        _cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        foreach (ISpeechRecognitionService engine in (ISpeechRecognitionService[])[_cloud, _local])
        {
            engine.InterimTranscriptReceived += (_, e) => InterimTranscriptReceived?.Invoke(this, e);
            engine.FinalTranscriptReceived += (_, e) => FinalTranscriptReceived?.Invoke(this, e);
            engine.ErrorOccurred += (_, e) => ErrorOccurred?.Invoke(this, e);
            engine.AudioLevelChanged += (_, e) => AudioLevelChanged?.Invoke(this, e);
        }
    }

    /// <summary>The engine the current setting selects.</summary>
    public ISpeechRecognitionService Selected =>
        SpeechProviders.Is(_options.CurrentValue.SpeechToTextProvider, SpeechProviders.Whisper)
            ? _local
            : _cloud;

    /// <inheritdoc />
    public string ProviderName => Selected.ProviderName;

    /// <inheritdoc />
    public bool IsConfigured => Selected.IsConfigured;

    /// <inheritdoc />
    public event EventHandler<InterimTranscriptEventArgs>? InterimTranscriptReceived;

    /// <inheritdoc />
    public event EventHandler<FinalTranscriptEventArgs>? FinalTranscriptReceived;

    /// <inheritdoc />
    public event EventHandler<SpeechErrorEventArgs>? ErrorOccurred;

    /// <inheritdoc />
    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

    /// <inheritdoc />
    public IReadOnlyList<string> InputDeviceNames => Selected.InputDeviceNames;

    /// <inheritdoc />
    public Task StartTransmissionAsync(CancellationToken cancellationToken)
    {
        ISpeechRecognitionService engine;
        lock (_gate)
        {
            _transmitting ??= Selected;
            engine = _transmitting;
        }

        return engine.StartTransmissionAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task StopTransmissionAsync(CancellationToken cancellationToken)
    {
        ISpeechRecognitionService engine;
        lock (_gate)
        {
            engine = _transmitting ?? Selected;
            _transmitting = null;
        }

        await engine.StopTransmissionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<SpeechTrainingSample> CaptureTrainingSampleAsync(
        string? inputDeviceName,
        TimeSpan duration,
        CancellationToken cancellationToken) =>
        Selected.CaptureTrainingSampleAsync(inputDeviceName, duration, cancellationToken);

    /// <inheritdoc />
    public void ApplyRecognitionProfile(SpeechRecognitionProfile profile)
    {
        _cloud.ApplyRecognitionProfile(profile);
        _local.ApplyRecognitionProfile(profile);
    }

    /// <inheritdoc />
    public Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
        Selected.TestConnectionAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>The engines are container singletons and are disposed by the container.</remarks>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Speaks through the voice engine chosen in Settings — the cloud voice or Piper on this
/// computer — and can switch between them while the application runs.
/// </summary>
public sealed class TextToSpeechRouter : ITextToSpeechService
{
    private readonly ITextToSpeechService _cloud;
    private readonly ITextToSpeechService _local;
    private readonly IOptionsMonitor<LocalVoiceOptions> _options;
    private ITextToSpeechService? _lastSpeaker;

    /// <summary>Creates the router over the cloud (or offline mock) and local voices.</summary>
    public TextToSpeechRouter(
        ITextToSpeechService cloud,
        ITextToSpeechService local,
        IOptionsMonitor<LocalVoiceOptions> options)
    {
        _cloud = cloud ?? throw new ArgumentNullException(nameof(cloud));
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The voice the current setting selects.</summary>
    public ITextToSpeechService Selected =>
        SpeechProviders.Is(_options.CurrentValue.TextToSpeechProvider, SpeechProviders.Piper)
            ? _local
            : _cloud;

    /// <inheritdoc />
    public string ProviderName => Selected.ProviderName;

    /// <inheritdoc />
    public bool IsConfigured => Selected.IsConfigured;

    /// <inheritdoc />
    public bool IsSpeaking => _cloud.IsSpeaking || _local.IsSpeaking;

    /// <inheritdoc />
    public async Task SpeakAsync(
        string text,
        VoiceConfiguration voice,
        CancellationToken cancellationToken)
    {
        ITextToSpeechService engine = Selected;
        await StopOtherAsync(engine, cancellationToken).ConfigureAwait(false);
        _lastSpeaker = engine;
        await engine.SpeakAsync(text, voice, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SpeakCachedAsync(
        string cacheKey,
        string text,
        VoiceConfiguration voice,
        CancellationToken cancellationToken)
    {
        ITextToSpeechService engine = Selected;
        await StopOtherAsync(engine, cancellationToken).ConfigureAwait(false);
        _lastSpeaker = engine;
        return await engine.SpeakCachedAsync(cacheKey, text, voice, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cloud.StopAsync(cancellationToken).ConfigureAwait(false);
        await _local.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task ReplayLastAsync(CancellationToken cancellationToken) =>
        (_lastSpeaker ?? Selected).ReplayLastAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
        Selected.TestConnectionAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>The voices are container singletons and are disposed by the container.</remarks>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Two voices must never overlap, even across a provider switch.</summary>
    private Task StopOtherAsync(ITextToSpeechService engine, CancellationToken cancellationToken)
    {
        ITextToSpeechService other = ReferenceEquals(engine, _cloud) ? _local : _cloud;
        return other.IsSpeaking ? other.StopAsync(cancellationToken) : Task.CompletedTask;
    }
}
