using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;

namespace StormATC.Infrastructure.Audio;

/// <summary>
/// Plays synthesised controller speech through the radio effect chain, one transmission at a
/// time.
/// </summary>
/// <remarks>
/// Same overlap policy as the Deepgram voice: a new transmission cancels the one playing, so two
/// controllers never speak at once and the newest instruction is the one heard.
/// </remarks>
internal sealed class RadioVoicePlayer : IAsyncDisposable
{
    private readonly Func<AudioOptions> _audioOptions;
    private readonly Func<string?> _outputDeviceName;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _playbackLock = new(1, 1);

    private WaveOutEvent? _output;
    private CancellationTokenSource? _playbackCancellation;

    public RadioVoicePlayer(
        Func<AudioOptions> audioOptions,
        Func<string?> outputDeviceName,
        ILogger logger)
    {
        _audioOptions = audioOptions ?? throw new ArgumentNullException(nameof(audioOptions));
        _outputDeviceName = outputDeviceName ?? throw new ArgumentNullException(nameof(outputDeviceName));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>True while a transmission is playing.</summary>
    public bool IsSpeaking => _output?.PlaybackState == PlaybackState.Playing;

    /// <summary>
    /// Plays a complete WAV file (any PCM sample rate, mono or stereo) and returns when it ends
    /// or is superseded.
    /// </summary>
    public async Task PlayWavAsync(
        byte[] wav,
        VoiceConfiguration voice,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wav);
        ArgumentNullException.ThrowIfNull(voice);

        await _playbackLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        WaveFileReader? reader = null;
        try
        {
            await StopInternalAsync().ConfigureAwait(false);

            _playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            reader = new WaveFileReader(new MemoryStream(wav, writable: false));
            ISampleProvider sampleProvider = reader.ToSampleProvider();
            if (sampleProvider.WaveFormat.Channels == 2)
            {
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider);
            }

            int sampleRate = sampleProvider.WaveFormat.SampleRate;
            AudioOptions audio = _audioOptions();

            if (voice.ApplyRadioEffect && audio.RadioEffectEnabled && !audio.CleanAudioMode)
            {
                sampleProvider = new RadioEffectProcessor(sampleProvider, new RadioEffectSettings
                {
                    Enabled = true,
                    Strength = voice.RadioEffectStrength,
                    StaticLevel = voice.StaticLevel,
                });
            }

            // Clicks bracket the transmission, as a real squelch tail does.
            if (voice.IncludeClicks && audio.TransmissionClicksEnabled && !audio.CleanAudioMode)
            {
                sampleProvider = new ConcatenatingSampleProvider(
                [
                    new TransmissionClickProvider(sampleRate, TimeSpan.FromMilliseconds(40)),
                    sampleProvider,
                    new TransmissionClickProvider(sampleRate, TimeSpan.FromMilliseconds(60)),
                ]);
            }

            var volumeProvider = new VolumeSampleProvider(sampleProvider)
            {
                Volume = (float)Math.Clamp(voice.Volume * audio.ControllerVolume, 0, 1),
            };

            var output = new WaveOutEvent { DeviceNumber = ResolveOutputDevice() };
            output.Init(volumeProvider);
            output.Play();
            _output = output;

            try
            {
                while (output.PlaybackState == PlaybackState.Playing)
                {
                    await Task.Delay(50, _playbackCancellation.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                output.Dispose();
                if (ReferenceEquals(_output, output))
                {
                    _output = null;
                }
            }
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
            reader?.Dispose();
            _playbackLock.Release();
        }
    }

    /// <summary>Stops playback immediately.</summary>
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

    /// <summary>Resolves the configured output device, falling back to the system default.</summary>
    private int ResolveOutputDevice()
    {
        if (_outputDeviceName() is not { Length: > 0 } wanted)
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
    public async ValueTask DisposeAsync()
    {
        await StopInternalAsync().ConfigureAwait(false);
        _playbackLock.Dispose();
    }
}
