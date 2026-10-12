using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Domain.Phraseology;
using StormATC.Infrastructure.Audio;

namespace StormATC.Infrastructure.Speech;

/// <summary>
/// The controller voice, synthesised on this computer by Piper. No account, no network.
/// </summary>
/// <remarks>
/// <para>
/// Piper is started once per transmission with the voice model for the session's language and
/// writes a WAV file that is read back, deleted, and played through the same radio effect
/// chain as the cloud voice. A medium-quality voice synthesises a typical clearance in well
/// under a second on a desktop CPU.
/// </para>
/// <para>
/// Named transmissions such as the ATIS are cached in memory while their text and voice are
/// unchanged, so the broadcast loop does not re-run Piper every cycle.
/// </para>
/// </remarks>
public sealed partial class PiperTextToSpeechService : ITextToSpeechService
{
    private const int MaximumNamedCacheEntries = 8;

    private readonly IOptionsMonitor<LocalVoiceOptions> _options;
    private readonly IOptionsMonitor<AudioOptions> _audioOptions;
    private readonly ILogger<PiperTextToSpeechService> _logger;
    private readonly RadioVoicePlayer _player;
    private readonly SemaphoreSlim _synthesisLock = new(1, 1);
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, NamedAudio> _namedCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _namedCacheOrder = new();

    private byte[]? _lastAudio;
    private VoiceConfiguration _lastVoice = VoiceConfiguration.Default;
    private bool _disposed;

    /// <summary>Creates the Piper voice.</summary>
    public PiperTextToSpeechService(
        IOptionsMonitor<LocalVoiceOptions> options,
        IOptionsMonitor<AudioOptions> audioOptions,
        IOptionsMonitor<DeepgramOptions> deviceOptions,
        ILogger<PiperTextToSpeechService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _audioOptions = audioOptions ?? throw new ArgumentNullException(nameof(audioOptions));
        ArgumentNullException.ThrowIfNull(deviceOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The output device is chosen once in Settings for every voice provider.
        _player = new RadioVoicePlayer(
            () => _audioOptions.CurrentValue,
            () => deviceOptions.CurrentValue.OutputDeviceName,
            _logger);
    }

    /// <inheritdoc />
    public string ProviderName => "Piper (local)";

    /// <inheritdoc />
    public bool IsConfigured =>
        File.Exists(_options.CurrentValue.PiperExecutablePath)
        && (File.Exists(_options.CurrentValue.PiperEnglishVoicePath)
            || File.Exists(_options.CurrentValue.PiperFrenchVoicePath));

    /// <inheritdoc />
    public bool IsSpeaking => _player.IsSpeaking;

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

        byte[]? audio = await SynthesizeAsync(text, voice, cancellationToken).ConfigureAwait(false);
        if (audio is null)
        {
            return;
        }

        _lastAudio = audio;
        _lastVoice = voice;
        await _player.PlayWavAsync(audio, voice, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SpeakCachedAsync(
        string cacheKey,
        string text,
        VoiceConfiguration voice,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ArgumentNullException.ThrowIfNull(voice);

        if (string.IsNullOrWhiteSpace(text) || _audioOptions.CurrentValue.Muted)
        {
            return false;
        }

        string voicePath = ResolveVoicePath(voice.Language);
        byte[]? audio = FindNamedAudio(cacheKey, text, voicePath);
        if (audio is null)
        {
            audio = await SynthesizeAsync(text, voice, cancellationToken).ConfigureAwait(false);
            if (audio is null)
            {
                return false;
            }

            StoreNamedAudio(cacheKey, text, voicePath, audio);
        }

        _lastAudio = audio;
        _lastVoice = voice;
        await _player.PlayWavAsync(audio, voice, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _player.StopAsync(cancellationToken);

    /// <inheritdoc />
    public async Task ReplayLastAsync(CancellationToken cancellationToken)
    {
        if (_lastAudio is byte[] audio)
        {
            await _player.PlayWavAsync(audio, _lastVoice, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        LocalVoiceOptions options = _options.CurrentValue;
        if (!File.Exists(options.PiperExecutablePath))
        {
            return ProviderTestResult.Fail(
                UiText.T("piper.exe was not found. Set its path in Settings (Local voice)."));
        }

        PhraseologyLanguage language = File.Exists(options.PiperFrenchVoicePath)
            ? PhraseologyLanguage.French
            : PhraseologyLanguage.English;

        if (!File.Exists(ResolveVoicePath(language)))
        {
            return ProviderTestResult.Fail(
                UiText.T("No Piper voice (.onnx) was found. Set the English or French voice path in Settings."));
        }

        var stopwatch = Stopwatch.StartNew();
        byte[]? audio = await SynthesizeAsync(
            language == PhraseologyLanguage.French ? "Essai radio." : "Radio check.",
            VoiceConfiguration.For(language),
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        return audio is { Length: > 44 }
            ? ProviderTestResult.Ok(
                UiText.T($"Piper ready. Voice '{Path.GetFileNameWithoutExtension(ResolveVoicePath(language))}'."),
                stopwatch.Elapsed)
            : ProviderTestResult.Fail(
                UiText.T("Piper produced no audio. Check that the voice's .onnx.json file sits next to the .onnx model."));
    }

    /// <summary>
    /// Prepares controller text for Piper. French voices read short capitalised acronyms as
    /// words ("ILS" as "ils"), so they are spelled letter by letter, as controllers say them.
    /// </summary>
    internal static string PrepareText(string text, PhraseologyLanguage language)
    {
        string singleLine = WhitespaceRegex().Replace(text, " ").Trim();
        if (language != PhraseologyLanguage.French)
        {
            return singleLine;
        }

        return AcronymRegex().Replace(
            singleLine,
            match => string.Join(' ', match.Value.ToCharArray()));
    }

    private string ResolveVoicePath(PhraseologyLanguage language) =>
        language == PhraseologyLanguage.French
            ? _options.CurrentValue.PiperFrenchVoicePath
            : _options.CurrentValue.PiperEnglishVoicePath;

    private async Task<byte[]?> SynthesizeAsync(
        string text,
        VoiceConfiguration voice,
        CancellationToken cancellationToken)
    {
        LocalVoiceOptions options = _options.CurrentValue;
        string voicePath = ResolveVoicePath(voice.Language);

        if (!File.Exists(options.PiperExecutablePath) || !File.Exists(voicePath))
        {
            _logger.LogWarning(
                "Piper is not configured for {Language}; the controller response is text only.",
                voice.Language);
            return null;
        }

        string outputPath = Path.Combine(
            Path.GetTempPath(),
            $"stormatc-voice-{Guid.NewGuid():N}.wav");

        await _synthesisLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var startInfo = new ProcessStartInfo(options.PiperExecutablePath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,

                // Piper looks for espeak-ng-data next to its executable.
                WorkingDirectory = Path.GetDirectoryName(options.PiperExecutablePath) ?? string.Empty,
            };
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(voicePath);
            startInfo.ArgumentList.Add("--output_file");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("--length_scale");
            startInfo.ArgumentList.Add(
                options.PiperLengthScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.PiperTimeout);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.StandardInput
                .WriteLineAsync(PrepareText(text, voice.Language).AsMemory(), timeout.Token)
                .ConfigureAwait(false);
            process.StandardInput.Close();

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                _logger.LogWarning(
                    "Piper did not finish within {Timeout}; the response is text only.",
                    options.PiperTimeout);
                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                _logger.LogWarning(
                    "Piper failed with exit code {ExitCode}: {Error}",
                    process.ExitCode,
                    Truncate(stderr.Result));
                return null;
            }

            return await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Piper speech synthesis failed. The response is text only.");
            return null;
        }
        finally
        {
            _synthesisLock.Release();
            TryDelete(outputPath);
        }
    }

    private byte[]? FindNamedAudio(string cacheKey, string text, string voicePath)
    {
        lock (_cacheLock)
        {
            return _namedCache.TryGetValue(cacheKey, out NamedAudio? entry)
                && string.Equals(entry.Text, text, StringComparison.Ordinal)
                && string.Equals(entry.VoicePath, voicePath, StringComparison.Ordinal)
                ? entry.Audio
                : null;
        }
    }

    private void StoreNamedAudio(string cacheKey, string text, string voicePath, byte[] audio)
    {
        lock (_cacheLock)
        {
            if (!_namedCache.ContainsKey(cacheKey))
            {
                _namedCacheOrder.Enqueue(cacheKey);
            }

            _namedCache[cacheKey] = new NamedAudio(text, voicePath, audio);

            while (_namedCache.Count > MaximumNamedCacheEntries
                && _namedCacheOrder.TryDequeue(out string? oldest))
            {
                _namedCache.Remove(oldest);
            }
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            _logger.LogDebug(ex, "Piper had already exited.");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete the temporary voice file.");
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 400 ? value.Trim() : value[..400].Trim() + "…";

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _player.DisposeAsync().ConfigureAwait(false);
        lock (_cacheLock)
        {
            _namedCache.Clear();
            _namedCacheOrder.Clear();
        }

        _synthesisLock.Dispose();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\b[A-Z]{2,4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AcronymRegex();

    private sealed record NamedAudio(string Text, string VoicePath, byte[] Audio);
}
