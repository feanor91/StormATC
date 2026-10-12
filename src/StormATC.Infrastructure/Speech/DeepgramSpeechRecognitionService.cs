using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using StormATC.Application.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Application.Sessions;
using StormATC.Infrastructure.Audio;
using StormATC.Infrastructure.Security;

namespace StormATC.Infrastructure.Speech;

/// <summary>
/// Streams push-to-talk microphone audio to Deepgram and supports guided personal calibration.
/// </summary>
/// <remarks>
/// The microphone opens only for a push-to-talk transmission or a user-initiated training sample.
/// Training audio is not sent to the ATC engine and is never saved to disk by this service.
/// </remarks>
public sealed class DeepgramSpeechRecognitionService : ISpeechRecognitionService
{
    private readonly IOptionsMonitor<DeepgramOptions> _options;
    private readonly IOptionsMonitor<ApplicationOptions> _applicationOptions;
    private readonly ISecretStore _secretStore;
    private readonly AtcSessionContext _sessionContext;
    private readonly ILogger<DeepgramSpeechRecognitionService> _logger;
    private readonly SemaphoreSlim _transmissionLock = new(1, 1);
    private readonly Lock _profileLock = new();

    private SpeechRecognitionProfile _profile;
    private ClientWebSocket? _socket;
    private WaveInEvent? _waveIn;
    private CancellationTokenSource? _transmissionCancellation;
    private Task? _receiveTask;
    private SpeechRecognitionProfile? _activeProfile;
    private readonly StringBuilder _finalTranscript = new();
    private double _confidenceTotal;
    private int _confidenceSegments;
    private string? _lastError;
    private bool _publishFinalTranscript;
    private bool _disposed;

    /// <summary>Creates the Deepgram speech provider.</summary>
    public DeepgramSpeechRecognitionService(
        IOptionsMonitor<DeepgramOptions> options,
        IOptionsMonitor<ApplicationOptions> applicationOptions,
        ISecretStore secretStore,
        AtcSessionContext sessionContext,
        ILogger<DeepgramSpeechRecognitionService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _applicationOptions = applicationOptions
            ?? throw new ArgumentNullException(nameof(applicationOptions));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _profile = DeepgramRecognitionProfile.FromOptions(_options.CurrentValue);
    }

    /// <inheritdoc />
    public string ProviderName => "Deepgram";

    /// <inheritdoc />
    public bool IsConfigured { get; private set; }

    /// <inheritdoc />
    public event EventHandler<InterimTranscriptEventArgs>? InterimTranscriptReceived;

    /// <inheritdoc />
    public event EventHandler<FinalTranscriptEventArgs>? FinalTranscriptReceived;

    /// <inheritdoc />
    public event EventHandler<SpeechErrorEventArgs>? ErrorOccurred;

    /// <inheritdoc />
    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

    /// <inheritdoc />
    public IReadOnlyList<string> InputDeviceNames =>
        MicrophoneDevices.ListInputDeviceNames(_logger);

    /// <inheritdoc />
    public Task StartTransmissionAsync(CancellationToken cancellationToken) =>
        StartCaptureSessionAsync(
            DeepgramRecognitionProfile.ForSessionLanguage(
                GetProfile(),
                _sessionContext.CurrentSession.Language),
            publishFinalTranscript: true,
            cancellationToken);

    /// <inheritdoc />
    public async Task StopTransmissionAsync(CancellationToken cancellationToken) =>
        _ = await StopCaptureSessionAsync(cancellationToken).ConfigureAwait(false);

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

        SpeechRecognitionProfile profile = DeepgramRecognitionProfile.ForSessionLanguage(
            GetProfile(),
            _sessionContext.CurrentSession.Language) with
        {
            InputDeviceName = string.IsNullOrWhiteSpace(inputDeviceName)
                ? null
                : inputDeviceName.Trim(),
        };

        bool started = await StartCaptureSessionAsync(
            profile,
            publishFinalTranscript: false,
            cancellationToken).ConfigureAwait(false);

        if (!started)
        {
            return new SpeechTrainingSample
            {
                Message = _lastError ?? UiText.T("The training sample could not be started."),
            };
        }

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopCaptureSessionAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return await StopCaptureSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void ApplyRecognitionProfile(SpeechRecognitionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var sanitized = new SpeechRecognitionProfile
        {
            InputDeviceName = string.IsNullOrWhiteSpace(profile.InputDeviceName)
                ? null
                : profile.InputDeviceName.Trim(),
            Language = string.IsNullOrWhiteSpace(profile.Language) ? "en" : profile.Language.Trim(),
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

        lock (_profileLock)
        {
            _profile = sanitized;
        }
    }

    private SpeechRecognitionProfile GetProfile()
    {
        lock (_profileLock)
        {
            return _profile with
            {
                Keyterms = _profile.Keyterms.ToArray(),
                TranscriptCorrections = new Dictionary<string, string>(
                    _profile.TranscriptCorrections,
                    StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    private async Task<bool> StartCaptureSessionAsync(
        SpeechRecognitionProfile profile,
        bool publishFinalTranscript,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _transmissionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket is not null)
            {
                // Windows repeats key-down while push-to-talk is held. Treat that as idempotent,
                // but do not let the voice trainer interrupt a live radio transmission.
                return publishFinalTranscript && _publishFinalTranscript;
            }

            if (!_applicationOptions.CurrentValue.CloudServicesConsentGiven)
            {
                IsConfigured = false;
                RaiseError(
                    UiText.T("Cloud speech services are disabled."),
                    UiText.T("Enable cloud-services consent in Settings, or use typed transmissions."));
                return false;
            }

            if (!ProviderEndpointPolicy.IsAllowedDeepgramStreaming(
                    _options.CurrentValue.StreamingUrl))
            {
                IsConfigured = false;
                RaiseError(
                    UiText.T("The configured Deepgram endpoint was blocked for security."),
                    UiText.T("Restore the official Deepgram endpoint in appsettings.json."));
                return false;
            }

            string? apiKey = await _secretStore
                .GetSecretAsync(SecretKeys.DeepgramApiKey, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                IsConfigured = false;
                RaiseError(
                    UiText.T("No Deepgram API key is configured."),
                    UiText.T("Add a Deepgram API key in Settings, or use typed transmissions."));
                return false;
            }

            IsConfigured = true;
            _lastError = null;
            _finalTranscript.Clear();
            _confidenceTotal = 0;
            _confidenceSegments = 0;
            _activeProfile = profile;
            _publishFinalTranscript = publishFinalTranscript;

            _transmissionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _transmissionCancellation.CancelAfter(
                _options.CurrentValue.MaximumTransmissionDuration);

            await OpenSocketAsync(
                apiKey,
                profile,
                _transmissionCancellation.Token).ConfigureAwait(false);

            StartCapture(profile.InputDeviceName);
            _receiveTask = ReceiveLoopAsync(_transmissionCancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RaiseError(
                UiText.T("Deepgram did not respond before the connection timeout."),
                UiText.T("Check the internet connection and try again."));
            await CleanUpAsync().ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            await CleanUpAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start a Deepgram transmission.");
            RaiseError(
                UiText.T("Speech recognition could not be started."),
                UiText.T("Check the selected microphone and internet connection, then try again."));
            await CleanUpAsync().ConfigureAwait(false);
            return false;
        }
        finally
        {
            _transmissionLock.Release();
        }
    }

    private async Task<SpeechTrainingSample> StopCaptureSessionAsync(
        CancellationToken cancellationToken)
    {
        await _transmissionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket is null)
            {
                return new SpeechTrainingSample
                {
                    Message = _lastError ?? UiText.T("No microphone capture is active."),
                };
            }

            _waveIn?.StopRecording();

            if (_socket.State == WebSocketState.Open)
            {
                try
                {
                    byte[] closeMessage = Encoding.UTF8.GetBytes("""{"type":"CloseStream"}""");
                    await _socket.SendAsync(
                        closeMessage,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // The receive loop may already have observed a server-side close.
                }
            }

            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask
                        .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    _logger.LogDebug(
                        "Deepgram did not deliver a final transcript before the timeout.");
                }
            }

            SpeechTrainingSample sample = BuildFinalSample(
                applyCorrections: _publishFinalTranscript);

            if (_publishFinalTranscript)
            {
                if (sample.Success)
                {
                    FinalTranscriptReceived?.Invoke(
                        this,
                        new FinalTranscriptEventArgs(sample.Transcript, sample.Confidence));
                }
                else
                {
                    RaiseError(
                        UiText.T("Nothing was transcribed from that transmission."),
                        UiText.T("Check the microphone level and speak after pressing push-to-talk."));
                }
            }

            return sample;
        }
        finally
        {
            await CleanUpAsync().ConfigureAwait(false);
            _transmissionLock.Release();
        }
    }

    private async Task OpenSocketAsync(
        string apiKey,
        SpeechRecognitionProfile profile,
        CancellationToken cancellationToken)
    {
        if (!ProviderEndpointPolicy.IsAllowedDeepgramStreaming(
                _options.CurrentValue.StreamingUrl))
        {
            throw new InvalidOperationException(
                "The configured Deepgram endpoint was blocked for security.");
        }

        var builder = new UriBuilder(_options.CurrentValue.StreamingUrl);
        var query = new List<string>
        {
            $"model={Uri.EscapeDataString(_options.CurrentValue.SpeechToTextModel)}",
            $"language={Uri.EscapeDataString(profile.Language)}",
            "encoding=linear16",
            $"sample_rate={_options.CurrentValue.SampleRate}",
            "channels=1",
            "interim_results=true",
            "smart_format=true",
            "punctuate=true",

            // A longer endpoint gives accented or deliberately paced aviation speech room for
            // small pauses between digits without prematurely ending the phrase.
            "endpointing=700",
        };

        foreach (string keyterm in DeepgramRecognitionProfile.BuildKeyterms(
            profile,
            _sessionContext.CurrentSession))
        {
            query.Add($"keyterm={Uri.EscapeDataString(keyterm)}");
        }

        builder.Query = string.Join('&', query);

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Token {apiKey}");

        using var connectTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(_options.CurrentValue.ConnectionTimeout);

        await socket.ConnectAsync(builder.Uri, connectTimeout.Token).ConfigureAwait(false);
        _socket = socket;

        _logger.LogDebug(
            "Deepgram streaming connection opened with {KeytermCount} recognition keyterms.",
            query.Count(item => item.StartsWith("keyterm=", StringComparison.Ordinal)));
    }

    private void StartCapture(string? inputDeviceName)
    {
        int deviceNumber = ResolveInputDevice(inputDeviceName);
        string selectedName = WaveInEvent.GetCapabilities(deviceNumber).ProductName;

        var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(
                _options.CurrentValue.SampleRate,
                bits: 16,
                channels: 1),
            BufferMilliseconds = 50,
            DeviceNumber = deviceNumber,
        };

        waveIn.DataAvailable += async (_, args) =>
        {
            RaiseAudioLevel(args.Buffer, args.BytesRecorded);

            ClientWebSocket? socket = _socket;
            if (socket is null || socket.State != WebSocketState.Open)
            {
                return;
            }

            try
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(args.Buffer, 0, args.BytesRecorded),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    _transmissionCancellation?.Token ?? CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException
                or OperationCanceledException
                or ObjectDisposedException)
            {
                // The socket closed while the final audio buffer was being delivered.
            }
        };

        waveIn.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null)
            {
                _logger.LogWarning(args.Exception, "Microphone capture stopped unexpectedly.");
                RaiseError(
                    UiText.T("The microphone stopped unexpectedly."),
                    UiText.T("Check that the selected input device is still connected."));
            }
        };

        try
        {
            waveIn.StartRecording();
            _waveIn = waveIn;
            _logger.LogInformation("Capturing pilot audio from {InputDevice}.", selectedName);
        }
        catch
        {
            waveIn.Dispose();
            throw;
        }
    }

    private int ResolveInputDevice(string? configuredName) =>
        MicrophoneDevices.ResolveInputDevice(configuredName, _logger);

    /// <summary>
    /// Replaces WinMM's 31-character product labels with the corresponding complete Core Audio
    /// endpoint names while retaining any legacy-only recording devices.
    /// </summary>
    internal static IReadOnlyList<string> PreferFullInputDeviceNames(
        IReadOnlyList<string> legacyNames,
        IReadOnlyList<string> fullEndpointNames) =>
        MicrophoneDevices.PreferFullInputDeviceNames(legacyNames, fullEndpointNames);

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        ClientWebSocket? socket = _socket;
        if (socket is null)
        {
            return;
        }

        byte[] buffer = new byte[8192];

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                HandleTranscriptMessage(
                    Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length)));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal when a transmission ends or reaches its maximum duration.
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "The Deepgram websocket closed unexpectedly.");
            RaiseError(
                UiText.T("The connection to Deepgram was lost."),
                UiText.T("Check the internet connection, then press push-to-talk again."));
        }
    }

    private void HandleTranscriptMessage(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("channel", out JsonElement channel)
                || !channel.TryGetProperty("alternatives", out JsonElement alternatives)
                || alternatives.GetArrayLength() == 0)
            {
                return;
            }

            JsonElement best = alternatives[0];
            if (!best.TryGetProperty("transcript", out JsonElement transcriptElement)
                || transcriptElement.GetString() is not { Length: > 0 } transcript)
            {
                return;
            }

            bool isFinal = root.TryGetProperty("is_final", out JsonElement finalElement)
                && finalElement.ValueKind == JsonValueKind.True;

            if (isFinal)
            {
                if (_finalTranscript.Length > 0)
                {
                    _finalTranscript.Append(' ');
                }

                _finalTranscript.Append(transcript);

                if (best.TryGetProperty("confidence", out JsonElement confidenceElement)
                    && confidenceElement.TryGetDouble(out double confidence))
                {
                    _confidenceTotal += Math.Clamp(confidence, 0, 1);
                    _confidenceSegments++;
                }
            }
            else
            {
                string interim = TranscriptNormalizer.FormatForDisplay(
                    $"{_finalTranscript} {transcript}".Trim());
                InterimTranscriptReceived?.Invoke(
                    this,
                    new InterimTranscriptEventArgs(interim));
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Ignoring a malformed Deepgram message.");
        }
    }

    private SpeechTrainingSample BuildFinalSample(bool applyCorrections)
    {
        string text = _finalTranscript.ToString().Trim();
        if (text.Length == 0)
        {
            return new SpeechTrainingSample
            {
                Message = UiText.T("Nothing was transcribed. Check the selected microphone and its level."),
            };
        }

        if (applyCorrections && _activeProfile is SpeechRecognitionProfile profile)
        {
            text = DeepgramRecognitionProfile.ApplyCorrections(
                text,
                profile.TranscriptCorrections);
        }

        text = TranscriptNormalizer.FormatForDisplay(text);

        double confidence = _confidenceSegments == 0
            ? 0
            : _confidenceTotal / _confidenceSegments;

        return new SpeechTrainingSample
        {
            Success = true,
            Transcript = text,
            Confidence = confidence,
            Message = UiText.T($"Heard with {confidence:P0} confidence."),
        };
    }

    private void RaiseAudioLevel(byte[] buffer, int bytesRecorded)
    {
        if (AudioLevelChanged is null || bytesRecorded < 2)
        {
            return;
        }

        AudioLevelChanged.Invoke(
            this,
            new AudioLevelEventArgs(MicrophoneDevices.PeakLevel(buffer, bytesRecorded)));
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        if (!_applicationOptions.CurrentValue.CloudServicesConsentGiven)
        {
            return ProviderTestResult.Fail(
                UiText.T("Cloud services are disabled. Enable consent in Settings before testing."));
        }

        if (!ProviderEndpointPolicy.IsAllowedDeepgramStreaming(
                _options.CurrentValue.StreamingUrl))
        {
            return ProviderTestResult.Fail(
                UiText.T("The configured Deepgram endpoint was blocked. Restore the official endpoint."));
        }

        string? apiKey = await _secretStore
            .GetSecretAsync(SecretKeys.DeepgramApiKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ProviderTestResult.Fail(
                UiText.T("No Deepgram API key is stored. Enter one and save first."));
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!ProviderEndpointPolicy.IsAllowedDeepgramStreaming(
                    _options.CurrentValue.StreamingUrl))
            {
                return ProviderTestResult.Fail(
                    UiText.T("The configured Deepgram endpoint was blocked. Restore the official endpoint."));
            }

            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Token {apiKey}");

            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.CurrentValue.ConnectionTimeout);

            SpeechRecognitionProfile profile = GetProfile();
            var uri = new Uri(
                $"{_options.CurrentValue.StreamingUrl}?model={Uri.EscapeDataString(_options.CurrentValue.SpeechToTextModel)}"
                + $"&language={Uri.EscapeDataString(profile.Language)}"
                + $"&encoding=linear16&sample_rate={_options.CurrentValue.SampleRate}&channels=1");

            await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();

            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Test complete",
                CancellationToken.None).ConfigureAwait(false);

            return ProviderTestResult.Ok(
                UiText.T($"Connected. Model '{_options.CurrentValue.SpeechToTextModel}', language '{profile.Language}'."),
                stopwatch.Elapsed);
        }
        catch (WebSocketException ex)
        {
            return ProviderTestResult.Fail(
                ex.Message.Contains("401", StringComparison.Ordinal)
                    ? UiText.T("Deepgram rejected the API key.")
                    : UiText.T("Could not connect to Deepgram. Check the internet connection."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderTestResult.Fail(
                UiText.T("Deepgram did not respond before the timeout elapsed."));
        }
    }

    private void RaiseError(string message, string suggestedAction)
    {
        _lastError = message;
        ErrorOccurred?.Invoke(this, new SpeechErrorEventArgs(message, suggestedAction));
    }

    private async Task CleanUpAsync()
    {
        if (_waveIn is not null)
        {
            try
            {
                _waveIn.StopRecording();
                _waveIn.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring an error while disposing the microphone.");
            }

            _waveIn = null;
        }

        if (_socket is not null)
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Transmission complete",
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                // Already closed.
            }

            _socket.Dispose();
            _socket = null;
        }

        if (_transmissionCancellation is not null)
        {
            await _transmissionCancellation.CancelAsync().ConfigureAwait(false);
            _transmissionCancellation.Dispose();
            _transmissionCancellation = null;
        }

        _receiveTask = null;
        _activeProfile = null;
        _publishFinalTranscript = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CleanUpAsync().ConfigureAwait(false);
        _transmissionLock.Dispose();
    }
}
