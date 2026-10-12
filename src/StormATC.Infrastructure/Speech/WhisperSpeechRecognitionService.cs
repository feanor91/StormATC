using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using StormATC.Application.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Options;
using StormATC.Application.Sessions;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Infrastructure.Audio;

namespace StormATC.Infrastructure.Speech;

/// <summary>
/// Push-to-talk speech recognition on this computer with whisper.cpp. No account, no network.
/// </summary>
/// <remarks>
/// <para>
/// The microphone records only while push-to-talk is held. On release the transmission is
/// written to a temporary 16 kHz WAV file, transcribed by <c>whisper-cli</c> in the session's
/// language, and the file is deleted. Whisper is given a short prompt in that language with the
/// callsign and route identifiers, which steers it toward radiotelephony vocabulary and digits.
/// </para>
/// <para>
/// There are no interim transcripts: Whisper transcribes a whole transmission at once. A
/// multilingual model is required for French; <c>ggml-large-v3-turbo</c> is the most accurate,
/// <c>ggml-small</c> the lightest.
/// </para>
/// </remarks>
public sealed partial class WhisperSpeechRecognitionService : ISpeechRecognitionService
{
    /// <summary>Whisper's native input rate.</summary>
    private const int SampleRate = 16000;

    /// <summary>Hard cap on one transmission, so a stuck key cannot record forever.</summary>
    private const int MaximumSeconds = 30;

    /// <summary>Shorter than this is a key bounce, not a transmission.</summary>
    private const double MinimumSeconds = 0.35;

    /// <summary>Below this peak level the recording is treated as silence.</summary>
    private const double SilencePeak = 0.02;

    /// <summary>
    /// Text Whisper is known to invent on silence or noise, mostly from video subtitles in its
    /// training data. A transcript made only of these is discarded.
    /// </summary>
    private static readonly string[] KnownHallucinations =
    [
        "amara.org",
        "sous-titres",
        "sous titres",
        "sous-titrage",
        "merci d'avoir regardé",
        "thanks for watching",
        "thank you for watching",
        "subtitles by",
    ];

    private readonly IOptionsMonitor<LocalVoiceOptions> _options;
    private readonly AtcSessionContext _sessionContext;
    private readonly ILogger<WhisperSpeechRecognitionService> _logger;
    private readonly SemaphoreSlim _transmissionLock = new(1, 1);
    private readonly Lock _bufferLock = new();
    private readonly Lock _profileLock = new();

    private SpeechRecognitionProfile _profile;
    private WaveInEvent? _waveIn;
    private TaskCompletionSource? _recordingStopped;
    private MemoryStream? _pcm;
    private double _peak;
    private bool _publishFinalTranscript;
    private bool _disposed;

    /// <summary>Creates the Whisper recognizer.</summary>
    public WhisperSpeechRecognitionService(
        IOptionsMonitor<LocalVoiceOptions> options,
        IOptionsMonitor<DeepgramOptions> profileOptions,
        AtcSessionContext sessionContext,
        ILogger<WhisperSpeechRecognitionService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(profileOptions);
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The microphone, personal vocabulary and trained corrections are shared settings,
        // stored with the Deepgram section for compatibility with existing user files.
        _profile = DeepgramRecognitionProfile.FromOptions(profileOptions.CurrentValue);
    }

    /// <inheritdoc />
    public string ProviderName => "Whisper (local)";

    /// <inheritdoc />
    public bool IsConfigured =>
        File.Exists(_options.CurrentValue.WhisperExecutablePath)
        && File.Exists(_options.CurrentValue.WhisperModelPath);

    /// <inheritdoc />
    public event EventHandler<InterimTranscriptEventArgs>? InterimTranscriptReceived;

    /// <inheritdoc />
    public event EventHandler<FinalTranscriptEventArgs>? FinalTranscriptReceived;

    /// <inheritdoc />
    public event EventHandler<SpeechErrorEventArgs>? ErrorOccurred;

    /// <inheritdoc />
    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

    /// <inheritdoc />
    public IReadOnlyList<string> InputDeviceNames => MicrophoneDevices.ListInputDeviceNames(_logger);

    /// <inheritdoc />
    public Task StartTransmissionAsync(CancellationToken cancellationToken) =>
        StartCaptureAsync(GetProfile().InputDeviceName, publishFinalTranscript: true, cancellationToken);

    /// <inheritdoc />
    public async Task StopTransmissionAsync(CancellationToken cancellationToken) =>
        _ = await StopCaptureAndTranscribeAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<SpeechTrainingSample> CaptureTrainingSampleAsync(
        string? inputDeviceName,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (duration < TimeSpan.FromSeconds(2) || duration > TimeSpan.FromSeconds(15))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "A voice-training sample must be between 2 and 15 seconds.");
        }

        bool started = await StartCaptureAsync(
            string.IsNullOrWhiteSpace(inputDeviceName) ? null : inputDeviceName.Trim(),
            publishFinalTranscript: false,
            cancellationToken).ConfigureAwait(false);

        if (!started)
        {
            return new SpeechTrainingSample
            {
                Message = "The training sample could not be started. Check the Whisper paths in Settings.",
            };
        }

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopCaptureAndTranscribeAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return await StopCaptureAndTranscribeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void ApplyRecognitionProfile(SpeechRecognitionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (_profileLock)
        {
            _profile = profile with
            {
                InputDeviceName = string.IsNullOrWhiteSpace(profile.InputDeviceName)
                    ? null
                    : profile.InputDeviceName.Trim(),
                Keyterms = profile.Keyterms
                    .Where(term => !string.IsNullOrWhiteSpace(term))
                    .Select(term => term.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                TranscriptCorrections = new Dictionary<string, string>(
                    profile.TranscriptCorrections
                        .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                            && !string.IsNullOrWhiteSpace(pair.Value)),
                    StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        LocalVoiceOptions options = _options.CurrentValue;
        if (!File.Exists(options.WhisperExecutablePath))
        {
            return ProviderTestResult.Fail(
                "whisper-cli.exe was not found. Set its path in Settings (Local voice).");
        }

        if (!File.Exists(options.WhisperModelPath))
        {
            return ProviderTestResult.Fail(
                "The Whisper model (.bin) was not found. Set its path in Settings (Local voice).");
        }

        // One second of silence measures model loading, which dominates the delay per call.
        byte[] silence = new byte[SampleRate * 2];
        var stopwatch = Stopwatch.StartNew();
        WhisperRun run = await TranscribeAsync(
            silence,
            _sessionContext.CurrentSession,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        return run.Succeeded
            ? ProviderTestResult.Ok(
                $"Whisper ready. Model '{Path.GetFileName(options.WhisperModelPath)}', "
                + $"language '{_sessionContext.CurrentSession.Language.ToCode()}'.",
                stopwatch.Elapsed)
            : ProviderTestResult.Fail(run.Error ?? "Whisper did not run.");
    }

    /// <summary>
    /// The prompt that steers Whisper toward radio phraseology in the session's language.
    /// </summary>
    /// <remarks>
    /// Whisper continues the style of its prompt: an example transmission with digits makes it
    /// write numbers as digits, and the callsign and route identifiers make them recognisable.
    /// The prompt is kept short; Whisper reads at most about two hundred tokens of it.
    /// </remarks>
    internal static string BuildPrompt(AtcSession session, IReadOnlyList<string> personalTerms)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(personalTerms);

        bool french = session.Language == PhraseologyLanguage.French;
        var builder = new StringBuilder(french
            ? "Radiotéléphonie aéronautique en français entre pilote et contrôleur. "
              + "Roulons point d'attente piste 27 via Bravo, transpondeur 4521, "
              + "montons niveau 120, QNH 1013, contactons Tour 118,7."
            : "Aviation radio between pilot and air traffic control. "
              + "Taxi to holding point runway 27 via Bravo, squawk 4521, "
              + "climb flight level 120, QNH 1013, contact Tower 118.7.");

        var terms = new List<string>();
        if (session.Callsign is { } callsign)
        {
            terms.Add(callsign.Normalized);
        }

        if (session.FlightPlan is { } plan)
        {
            terms.Add(plan.DepartureIcao);
            terms.Add(plan.DestinationIcao);
            if (plan.StandardInstrumentDeparture is { Length: > 0 } sid)
            {
                terms.Add(sid);
            }

            if (plan.StandardTerminalArrival is { Length: > 0 } star)
            {
                terms.Add(star);
            }

            terms.AddRange(plan.RouteLegs
                .Select(leg => leg.Identifier)
                .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
                .Take(8));
        }

        terms.AddRange(personalTerms.Take(10));

        string[] distinct = terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinct.Length > 0)
        {
            builder.Append(' ').Append(string.Join(", ", distinct)).Append('.');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Joins Whisper's output lines and drops its non-speech annotations ("[BLANK_AUDIO]",
    /// "(musique)") and known silence hallucinations.
    /// </summary>
    internal static string CleanTranscript(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        string joined = string.Join(
            ' ',
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        joined = AnnotationRegex().Replace(joined, " ");
        joined = WhitespaceRegex().Replace(joined, " ").Trim();

        string lowered = joined.ToLowerInvariant();
        if (KnownHallucinations.Any(phrase => lowered.Contains(phrase, StringComparison.Ordinal)))
        {
            return string.Empty;
        }

        return joined.Trim(' ', '.', ',', '-', '…');
    }

    private SpeechRecognitionProfile GetProfile()
    {
        lock (_profileLock)
        {
            return _profile;
        }
    }

    private async Task<bool> StartCaptureAsync(
        string? inputDeviceName,
        bool publishFinalTranscript,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _transmissionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_waveIn is not null)
            {
                // Windows repeats key-down while push-to-talk is held: idempotent, but the voice
                // trainer may not interrupt a live transmission.
                return publishFinalTranscript && _publishFinalTranscript;
            }

            if (!IsConfigured)
            {
                RaiseError(
                    "Whisper is not set up.",
                    "Set the whisper-cli.exe and model paths in Settings, or use typed transmissions.");
                return false;
            }

            lock (_bufferLock)
            {
                _pcm = new MemoryStream();
                _peak = 0;
            }

            _publishFinalTranscript = publishFinalTranscript;
            _recordingStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            int deviceNumber = MicrophoneDevices.ResolveInputDevice(inputDeviceName, _logger);
            var waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SampleRate, bits: 16, channels: 1),
                BufferMilliseconds = 50,
                DeviceNumber = deviceNumber,
            };

            waveIn.DataAvailable += OnDataAvailable;
            TaskCompletionSource stopped = _recordingStopped;
            waveIn.RecordingStopped += (_, args) =>
            {
                if (args.Exception is not null)
                {
                    _logger.LogWarning(args.Exception, "Microphone capture stopped unexpectedly.");
                    RaiseError(
                        "The microphone stopped unexpectedly.",
                        "Check that the selected input device is still connected.");
                }

                stopped.TrySetResult();
            };

            try
            {
                waveIn.StartRecording();
            }
            catch
            {
                waveIn.Dispose();
                throw;
            }

            _waveIn = waveIn;
            _logger.LogInformation(
                "Capturing pilot audio from {InputDevice} for Whisper.",
                WaveInEvent.GetCapabilities(deviceNumber).ProductName);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start a Whisper transmission.");
            RaiseError(
                "The microphone could not be opened.",
                "Check the selected microphone in Settings, then try again.");
            CleanUpCapture();
            return false;
        }
        finally
        {
            _transmissionLock.Release();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (args.BytesRecorded <= 0)
        {
            return;
        }

        double level = MicrophoneDevices.PeakLevel(args.Buffer, args.BytesRecorded);
        AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(level));

        lock (_bufferLock)
        {
            if (_pcm is null || _pcm.Length >= SampleRate * 2 * MaximumSeconds)
            {
                return;
            }

            _pcm.Write(args.Buffer, 0, args.BytesRecorded);
            _peak = Math.Max(_peak, level);
        }
    }

    private async Task<SpeechTrainingSample> StopCaptureAndTranscribeAsync(
        CancellationToken cancellationToken)
    {
        await _transmissionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_waveIn is null)
            {
                return new SpeechTrainingSample { Message = "No microphone capture is active." };
            }

            bool publish = _publishFinalTranscript;
            _waveIn.StopRecording();

            if (_recordingStopped is TaskCompletionSource stopped)
            {
                try
                {
                    // The last buffer arrives before RecordingStopped; wait for it briefly.
                    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogDebug("The microphone did not report the end of recording in time.");
                }
            }

            byte[] pcm;
            double peak;
            lock (_bufferLock)
            {
                pcm = _pcm?.ToArray() ?? [];
                peak = _peak;
            }

            CleanUpCapture();

            SpeechTrainingSample sample = await TranscribeRecordingAsync(
                pcm,
                peak,
                applyCorrections: publish,
                cancellationToken).ConfigureAwait(false);

            if (publish)
            {
                if (sample.Success)
                {
                    FinalTranscriptReceived?.Invoke(
                        this,
                        new FinalTranscriptEventArgs(sample.Transcript, sample.Confidence));
                }
                else
                {
                    RaiseError(sample.Message, "Check the microphone level and speak after pressing push-to-talk.");
                }
            }

            return sample;
        }
        finally
        {
            _transmissionLock.Release();
        }
    }

    private async Task<SpeechTrainingSample> TranscribeRecordingAsync(
        byte[] pcm,
        double peak,
        bool applyCorrections,
        CancellationToken cancellationToken)
    {
        double seconds = pcm.Length / (SampleRate * 2.0);
        if (seconds < MinimumSeconds)
        {
            return new SpeechTrainingSample
            {
                Message = "The transmission was too short to transcribe.",
            };
        }

        if (peak < SilencePeak)
        {
            return new SpeechTrainingSample
            {
                Message = "Nothing was heard. Check the selected microphone and its level.",
            };
        }

        InterimTranscriptReceived?.Invoke(this, new InterimTranscriptEventArgs("…"));

        WhisperRun run = await TranscribeAsync(pcm, _sessionContext.CurrentSession, cancellationToken)
            .ConfigureAwait(false);

        if (!run.Succeeded)
        {
            return new SpeechTrainingSample { Message = run.Error ?? "Whisper did not run." };
        }

        string text = run.Text;
        if (text.Length == 0)
        {
            return new SpeechTrainingSample
            {
                Message = "Nothing was transcribed. Check the selected microphone and its level.",
            };
        }

        if (applyCorrections)
        {
            text = DeepgramRecognitionProfile.ApplyCorrections(text, GetProfile().TranscriptCorrections);
        }

        text = TranscriptNormalizer.FormatForDisplay(text);

        return new SpeechTrainingSample
        {
            Success = true,
            Transcript = text,

            // whisper-cli does not report a calibrated confidence for the whole utterance.
            Confidence = 1.0,
            Message = $"Heard in {run.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s.",
        };
    }

    private async Task<WhisperRun> TranscribeAsync(
        byte[] pcm,
        AtcSession session,
        CancellationToken cancellationToken)
    {
        LocalVoiceOptions options = _options.CurrentValue;
        string wavPath = Path.Combine(Path.GetTempPath(), $"stormatc-ptt-{Guid.NewGuid():N}.wav");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using (var file = new FileStream(
                wavPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous))
            {
                await using var writer = new WaveFileWriter(file, new WaveFormat(SampleRate, 16, 1));
                writer.Write(pcm, 0, pcm.Length);
            }

            var startInfo = new ProcessStartInfo(options.WhisperExecutablePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(options.WhisperExecutablePath) ?? string.Empty,
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(options.WhisperModelPath);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(wavPath);
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add(session.Language.ToCode());
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(options.WhisperThreads.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-nt");
            startInfo.ArgumentList.Add("-np");
            startInfo.ArgumentList.Add("--prompt");
            startInfo.ArgumentList.Add(BuildPrompt(session, GetProfile().Keyterms));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.WhisperTimeout);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);

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

                return WhisperRun.Failed(
                    $"Whisper took longer than {options.WhisperTimeout.TotalSeconds:0} s. "
                    + "Use a smaller model or the GPU build of whisper.cpp.");
            }

            stopwatch.Stop();

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "whisper-cli failed with exit code {ExitCode}: {Error}",
                    process.ExitCode,
                    Truncate(stderr.Result));
                return WhisperRun.Failed("Whisper could not transcribe the transmission. See Diagnostics.");
            }

            string text = CleanTranscript(stdout.Result);
            _logger.LogDebug(
                "Whisper transcribed {Seconds:0.0} s of audio in {Elapsed} ms.",
                pcm.Length / (SampleRate * 2.0),
                stopwatch.ElapsedMilliseconds);

            return new WhisperRun(true, text, null, stopwatch.Elapsed);
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
            _logger.LogWarning(ex, "Whisper transcription failed.");
            return WhisperRun.Failed("Whisper could not be started. Check its path in Settings.");
        }
        finally
        {
            TryDelete(wavPath);
        }
    }

    private void CleanUpCapture()
    {
        if (_waveIn is not null)
        {
            try
            {
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring an error while disposing the microphone.");
            }

            _waveIn = null;
        }

        lock (_bufferLock)
        {
            _pcm?.Dispose();
            _pcm = null;
        }

        _recordingStopped = null;
        _publishFinalTranscript = false;
    }

    private void RaiseError(string message, string suggestedAction) =>
        ErrorOccurred?.Invoke(this, new SpeechErrorEventArgs(message, suggestedAction));

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
            _logger.LogDebug(ex, "whisper-cli had already exited.");
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
            _logger.LogDebug(ex, "Could not delete the temporary transmission file.");
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
        await _transmissionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            CleanUpCapture();
        }
        finally
        {
            _transmissionLock.Release();
            _transmissionLock.Dispose();
        }
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|♪")]
    private static partial Regex AnnotationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed record WhisperRun(bool Succeeded, string Text, string? Error, TimeSpan Elapsed)
    {
        public static WhisperRun Failed(string error) => new(false, string.Empty, error, TimeSpan.Zero);
    }
}
