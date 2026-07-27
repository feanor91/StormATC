using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Infrastructure.Audio;
using StormATC.Infrastructure.Security;

namespace StormATC.Infrastructure.Speech;

/// <summary>
/// Synthesises controller speech with Deepgram and plays it through the radio effect chain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overlap policy.</b> Two controllers must never speak at once — it sounds wrong and it makes
/// the clearance unreadable. StormATC's rule is: a new transmission cancels any in-progress
/// playback. That matches how a real controller behaves when a situation changes, and it keeps
/// the most current instruction audible rather than queueing a stale one behind it.
/// </para>
/// <para>
/// The last transmission's audio is retained in memory so the pilot can replay it without
/// spending another synthesis request.
/// </para>
/// </remarks>
public sealed class DeepgramTextToSpeechService(
    HttpClient httpClient,
    IOptionsMonitor<DeepgramOptions> deepgramOptions,
    IOptionsMonitor<AudioOptions> audioOptions,
    IOptionsMonitor<ApplicationOptions> applicationOptions,
    ISecretStore secretStore,
    ILogger<DeepgramTextToSpeechService> logger) : ITextToSpeechService
{
    /// <summary>Deepgram returns 24 kHz mono PCM for the linear16 container.</summary>
    private const int SynthesisSampleRate = 24000;

    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    private readonly IOptionsMonitor<DeepgramOptions> _deepgramOptions = deepgramOptions
        ?? throw new ArgumentNullException(nameof(deepgramOptions));

    private readonly IOptionsMonitor<AudioOptions> _audioOptions = audioOptions
        ?? throw new ArgumentNullException(nameof(audioOptions));

    private readonly IOptionsMonitor<ApplicationOptions> _applicationOptions = applicationOptions
        ?? throw new ArgumentNullException(nameof(applicationOptions));

    private readonly ISecretStore _secretStore =
        secretStore ?? throw new ArgumentNullException(nameof(secretStore));

    private readonly ILogger<DeepgramTextToSpeechService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly SemaphoreSlim _playbackLock = new(1, 1);

    private WaveOutEvent? _output;
    private CancellationTokenSource? _playbackCancellation;
    private byte[]? _lastAudio;
    private VoiceConfiguration _lastVoice = VoiceConfiguration.Default;
    private bool _disposed;

    /// <inheritdoc />
    public string ProviderName => "Deepgram";

    /// <inheritdoc />
    public bool IsConfigured { get; private set; }

    /// <inheritdoc />
    public bool IsSpeaking => _output?.PlaybackState == PlaybackState.Playing;

    /// <inheritdoc />
    public async Task SpeakAsync(
        string text,
        VoiceConfiguration voice,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(voice);

        if (string.IsNullOrWhiteSpace(text) || _audioOptions.CurrentValue.Muted)
        {
            return;
        }

        if (!_applicationOptions.CurrentValue.CloudServicesConsentGiven
            || !ProviderEndpointPolicy.IsAllowedDeepgramRest(
                _deepgramOptions.CurrentValue.BaseUrl))
        {
            IsConfigured = false;
            _logger.LogDebug(
                "Deepgram speech was skipped because cloud consent or endpoint validation failed.");
            return;
        }

        string? apiKey = await _secretStore
            .GetSecretAsync(SecretKeys.DeepgramApiKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            IsConfigured = false;
            _logger.LogDebug("No Deepgram key configured; the controller response is text only.");
            return;
        }

        IsConfigured = true;

        byte[]? audio = await SynthesizeAsync(text, voice, apiKey, cancellationToken)
            .ConfigureAwait(false);

        if (audio is null)
        {
            return;
        }

        _lastAudio = audio;
        _lastVoice = voice;

        await PlayAsync(audio, voice, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> SynthesizeAsync(
        string text,
        VoiceConfiguration voice,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (!ProviderEndpointPolicy.IsAllowedDeepgramRest(
                _deepgramOptions.CurrentValue.BaseUrl))
        {
            return null;
        }

        string voiceId = voice.VoiceId is { Length: > 0 } configured
            ? configured
            : _deepgramOptions.CurrentValue.Voice;

        var uri = new Uri(
            $"{_deepgramOptions.CurrentValue.BaseUrl.TrimEnd('/')}/v1/speak" +
            $"?model={Uri.EscapeDataString(voiceId)}" +
            $"&encoding=linear16&sample_rate={SynthesisSampleRate}&container=none");

        using var message = new HttpRequestMessage(HttpMethod.Post, uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey);
        message.Content = JsonContent.Create(new { text });

        try
        {
            using HttpResponseMessage response = await _httpClient
                .SendAsync(message, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Deepgram speech synthesis returned {StatusCode}. The response is text only.",
                    (int)response.StatusCode);

                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Deepgram speech synthesis failed. The response is text only.");
            return null;
        }
    }

    private async Task PlayAsync(
        byte[] audio,
        VoiceConfiguration voice,
        CancellationToken cancellationToken)
    {
        await _playbackLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Overlap policy: cancel whatever is playing. See the class remarks.
            await StopInternalAsync().ConfigureAwait(false);

            _playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var waveFormat = new WaveFormat(SynthesisSampleRate, bits: 16, channels: 1);
            var pcmStream = new RawSourceWaveStream(new MemoryStream(audio), waveFormat);
            ISampleProvider sampleProvider = pcmStream.ToSampleProvider();

            bool applyEffect = voice.ApplyRadioEffect
                && _audioOptions.CurrentValue.RadioEffectEnabled
                && !_audioOptions.CurrentValue.CleanAudioMode;

            if (applyEffect)
            {
                sampleProvider = new RadioEffectProcessor(sampleProvider, new RadioEffectSettings
                {
                    Enabled = true,
                    Strength = voice.RadioEffectStrength,
                    StaticLevel = voice.StaticLevel,
                });
            }

            // Clicks bracket the transmission, as a real squelch tail does.
            if (voice.IncludeClicks && _audioOptions.CurrentValue.TransmissionClicksEnabled
                && !_audioOptions.CurrentValue.CleanAudioMode)
            {
                sampleProvider = new ConcatenatingSampleProvider(
                [
                    new TransmissionClickProvider(SynthesisSampleRate, TimeSpan.FromMilliseconds(40)),
                    sampleProvider,
                    new TransmissionClickProvider(SynthesisSampleRate, TimeSpan.FromMilliseconds(60)),
                ]);
            }

            var volumeProvider = new VolumeSampleProvider(sampleProvider)
            {
                Volume = (float)Math.Clamp(
                    voice.Volume * _audioOptions.CurrentValue.ControllerVolume,
                    0,
                    1),
            };

            var output = new WaveOutEvent { DeviceNumber = ResolveOutputDevice() };
            output.Init(volumeProvider);
            output.Play();
            _output = output;

            // Wait for playback to finish so callers can sequence transmissions, but do not
            // block the caller's thread.
            await WaitForPlaybackAsync(output, pcmStream, _playbackCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer transmission, or the session ended.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Controller audio playback failed.");
        }
        finally
        {
            _playbackLock.Release();
        }
    }

    private static async Task WaitForPlaybackAsync(
        WaveOutEvent output,
        RawSourceWaveStream pcmStream,
        CancellationToken cancellationToken)
    {
        try
        {
            while (output.PlaybackState == PlaybackState.Playing)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            output.Dispose();
            pcmStream.Dispose();
        }
    }

    /// <summary>Resolves the configured output device, falling back to the system default.</summary>
    private int ResolveOutputDevice()
    {
        if (_deepgramOptions.CurrentValue.OutputDeviceName is not { Length: > 0 } wanted)
        {
            return -1;
        }

        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            if (WaveOut.GetCapabilities(i).ProductName
                .Contains(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        _logger.LogWarning("Output device '{Device}' was not found. Using the system default.", wanted);
        return -1;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _playbackLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _playbackLock.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        if (_playbackCancellation is not null)
        {
            await _playbackCancellation.CancelAsync().ConfigureAwait(false);
            _playbackCancellation.Dispose();
            _playbackCancellation = null;
        }

        if (_output is not null)
        {
            try
            {
                _output.Stop();
                _output.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring an error while stopping playback.");
            }

            _output = null;
        }
    }

    /// <inheritdoc />
    public async Task ReplayLastAsync(CancellationToken cancellationToken)
    {
        if (_lastAudio is byte[] audio)
        {
            await PlayAsync(audio, _lastVoice, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        if (!_applicationOptions.CurrentValue.CloudServicesConsentGiven)
        {
            return ProviderTestResult.Fail(
                "Cloud services are disabled. Enable consent in Settings before testing.");
        }

        if (!ProviderEndpointPolicy.IsAllowedDeepgramRest(
                _deepgramOptions.CurrentValue.BaseUrl))
        {
            return ProviderTestResult.Fail(
                "The configured Deepgram endpoint was blocked. Restore the official endpoint.");
        }

        string? apiKey = await _secretStore
            .GetSecretAsync(SecretKeys.DeepgramApiKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ProviderTestResult.Fail("No Deepgram API key is stored. Enter one and save first.");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        byte[]? audio = await SynthesizeAsync(
            "Storm ATC radio check.", VoiceConfiguration.Default, apiKey, cancellationToken)
            .ConfigureAwait(false);

        stopwatch.Stop();

        return audio is { Length: > 0 }
            ? ProviderTestResult.Ok(
                $"Connected. Voice '{_deepgramOptions.CurrentValue.Voice}'.",
                stopwatch.Elapsed)
            : ProviderTestResult.Fail("Deepgram did not return audio. Check the key and the voice name.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopInternalAsync().ConfigureAwait(false);
        _playbackLock.Dispose();
    }
}
