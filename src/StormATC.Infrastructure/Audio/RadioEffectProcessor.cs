using NAudio.Wave;

namespace StormATC.Infrastructure.Audio;

/// <summary>
/// Applies a subtle VHF radio treatment to controller audio.
/// </summary>
/// <remarks>
/// <para>
/// The design goal is <b>intelligibility first</b>. A radio effect that makes clearances hard to
/// understand actively harms the product: the user is trying to copy a squawk code, not enjoy an
/// atmosphere. Every stage is therefore deliberately restrained, and the whole chain can be
/// bypassed with clean-audio mode for accessibility.
/// </para>
/// <para>
/// The chain is:
/// </para>
/// <list type="number">
/// <item><b>Band-pass</b> roughly 300 Hz to 3 kHz — the classic communications passband. This is
/// what makes speech sound like a radio, and it does most of the work.</item>
/// <item><b>Soft compression</b> to even out level, as a real radio's AGC does.</item>
/// <item><b>Very light static</b>, scaled by the configured level.</item>
/// <item><b>Mild saturation</b> for a touch of transmitter grit.</item>
/// <item><b>Hard limiter</b> so nothing can clip, whatever the settings.</item>
/// </list>
/// </remarks>
public sealed class RadioEffectProcessor : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly RadioEffectSettings _settings;

    // Two one-pole filters combine into the band-pass: a high-pass removes rumble below the
    // passband, a low-pass removes the sibilance above it.
    private float _highPassState;
    private float _lowPassState;
    private readonly float _highPassCoefficient;
    private readonly float _lowPassCoefficient;

    // Compressor envelope follower.
    private float _envelope;
    private readonly float _attackCoefficient;
    private readonly float _releaseCoefficient;

    private readonly Random _noise = new();

    /// <summary>Creates the processor around a source provider.</summary>
    public RadioEffectProcessor(ISampleProvider source, RadioEffectSettings settings)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        int sampleRate = source.WaveFormat.SampleRate;

        // One-pole coefficients: y[n] = y[n-1] + k * (x[n] - y[n-1]), k derived from the cutoff.
        _highPassCoefficient = CalculateCoefficient(HighPassCutoffHz, sampleRate);
        _lowPassCoefficient = CalculateCoefficient(LowPassCutoffHz, sampleRate);

        _attackCoefficient = CalculateCoefficient(1f / AttackSeconds, sampleRate);
        _releaseCoefficient = CalculateCoefficient(1f / ReleaseSeconds, sampleRate);
    }

    /// <summary>Lower edge of the communications passband.</summary>
    private const float HighPassCutoffHz = 300f;

    /// <summary>Upper edge of the communications passband.</summary>
    private const float LowPassCutoffHz = 3000f;

    private const float AttackSeconds = 0.005f;
    private const float ReleaseSeconds = 0.080f;

    /// <summary>Level above which the compressor starts reducing gain.</summary>
    private const float CompressorThreshold = 0.25f;

    /// <inheritdoc />
    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);

        // Clean-audio mode short-circuits the entire chain: a user who cannot understand the
        // processed audio must be able to turn it off completely, not merely reduce it.
        if (!_settings.Enabled)
        {
            return read;
        }

        float strength = (float)Math.Clamp(_settings.Strength, 0, 1);
        float staticLevel = (float)Math.Clamp(_settings.StaticLevel, 0, 1);

        for (int i = 0; i < read; i++)
        {
            float sample = buffer[offset + i];

            // --- 1. Band-pass. ---------------------------------------------------------------
            _lowPassState += _lowPassCoefficient * (sample - _lowPassState);
            float lowPassed = _lowPassState;

            _highPassState += _highPassCoefficient * (lowPassed - _highPassState);
            float bandPassed = lowPassed - _highPassState;

            // Blend towards the filtered signal by the configured strength, so a low strength
            // keeps most of the original bandwidth.
            sample = Lerp(sample, bandPassed, strength);

            // --- 2. Compression. --------------------------------------------------------------
            float magnitude = Math.Abs(sample);
            float coefficient = magnitude > _envelope ? _attackCoefficient : _releaseCoefficient;
            _envelope += coefficient * (magnitude - _envelope);

            if (_envelope > CompressorThreshold)
            {
                // Gentle 3:1 reduction above the threshold.
                float excess = _envelope - CompressorThreshold;
                float gain = (CompressorThreshold + (excess / 3f)) / _envelope;
                sample *= Lerp(1f, gain, strength);
            }

            // Make up the level the compressor removed.
            sample *= 1f + (0.4f * strength);

            // --- 3. Static. -------------------------------------------------------------------
            if (staticLevel > 0)
            {
                // Scale noise with signal presence so silence between transmissions stays quiet
                // rather than hissing continuously.
                float presence = Math.Min(1f, _envelope * 4f);
                float noise = (float)((_noise.NextDouble() * 2.0) - 1.0);
                sample += noise * staticLevel * 0.05f * (0.3f + (0.7f * presence));
            }

            // --- 4. Saturation. ---------------------------------------------------------------
            if (strength > 0)
            {
                // tanh-style soft clip, scaled so it only bites on peaks.
                float drive = 1f + (strength * 0.8f);
                sample = (float)Math.Tanh(sample * drive) / drive * drive;
            }

            // --- 5. Limiter. ------------------------------------------------------------------
            buffer[offset + i] = Math.Clamp(sample, -0.98f, 0.98f);
        }

        return read;
    }

    private static float Lerp(float from, float to, float amount) =>
        from + ((to - from) * amount);

    private static float CalculateCoefficient(float cutoffHz, int sampleRate) =>
        (float)(1.0 - Math.Exp(-2.0 * Math.PI * cutoffHz / sampleRate));
}

/// <summary>Radio-effect settings, resolved from <see cref="Application.Options.AudioOptions"/>.</summary>
public sealed class RadioEffectSettings
{
    /// <summary>Whether the effect chain runs at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Effect intensity, 0 to 1.</summary>
    public double Strength { get; init; } = 0.5;

    /// <summary>Background static level, 0 to 1.</summary>
    public double StaticLevel { get; init; } = 0.08;
}

/// <summary>
/// Generates the short click that brackets a radio transmission.
/// </summary>
/// <remarks>
/// A brief filtered noise burst with a fast decay. It is generated rather than sampled so that
/// StormATC ships no audio assets and there is no question about their provenance.
/// </remarks>
public sealed class TransmissionClickProvider : ISampleProvider
{
    private readonly int _totalSamples;
    private readonly float _amplitude;
    private readonly Random _noise = new();
    private int _position;

    /// <summary>Creates a click of the given duration.</summary>
    public TransmissionClickProvider(int sampleRate, TimeSpan duration, float amplitude = 0.12f)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels: 1);
        _totalSamples = (int)(sampleRate * duration.TotalSeconds);
        _amplitude = amplitude;
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat { get; }

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        int remaining = _totalSamples - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        int toWrite = Math.Min(count, remaining);

        for (int i = 0; i < toWrite; i++)
        {
            // Exponential decay over the click's length.
            float progress = (float)_position / _totalSamples;
            float envelope = (float)Math.Exp(-6.0 * progress);
            float noise = (float)((_noise.NextDouble() * 2.0) - 1.0);

            buffer[offset + i] = noise * envelope * _amplitude;
            _position++;
        }

        return toWrite;
    }
}
