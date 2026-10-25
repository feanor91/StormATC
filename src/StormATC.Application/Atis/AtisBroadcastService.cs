using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Application.Localization;
using StormATC.Application.Sessions;
using StormATC.Domain;
using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Atis;

/// <summary>
/// Plays the departure ATIS while the selected aircraft radio is on the published channel.
/// </summary>
/// <remarks>
/// The service behaves like a continuous broadcast from the pilot's point of view: tuning the
/// ATIS channel starts the current edition once, tuning away stops it, and returning to the
/// channel starts it again. Identical simulator snapshots cannot trigger duplicate synthesis.
/// </remarks>
public sealed class AtisBroadcastService : IDisposable
{
    private readonly AtcSessionContext _sessionContext;
    private readonly IWeatherProvider _weatherProvider;
    private readonly ITextToSpeechService _textToSpeech;
    private readonly IClock _clock;
    private readonly ILogger<AtisBroadcastService> _logger;
    private readonly TimeSpan _repeatDelay;
    private readonly TimeSpan _unavailableRetryDelay;
    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    private AtisPlaybackKey? _requestedPlayback;
    private CancellationTokenSource? _playbackCancellation;
    private AtisBroadcast? _currentBroadcast;
    private Guid? _broadcastSessionId;
    private long _informationSequence = -1;
    private string _statusMessage = UiText.T("Load a flight plan to receive departure ATIS.");
    private bool _disposed;

    /// <summary>Creates the service and starts observing radio/session changes.</summary>
    public AtisBroadcastService(
        AtcSessionContext sessionContext,
        IWeatherProvider weatherProvider,
        ITextToSpeechService textToSpeech,
        IClock clock,
        ILogger<AtisBroadcastService> logger,
        TimeSpan? repeatDelay = null,
        TimeSpan? unavailableRetryDelay = null)
    {
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _weatherProvider = weatherProvider ?? throw new ArgumentNullException(nameof(weatherProvider));
        _textToSpeech = textToSpeech ?? throw new ArgumentNullException(nameof(textToSpeech));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _repeatDelay = repeatDelay ?? TimeSpan.FromSeconds(2);
        _unavailableRetryDelay = unavailableRetryDelay ?? TimeSpan.FromSeconds(30);

        if (_repeatDelay < TimeSpan.Zero || _unavailableRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repeatDelay),
                "ATIS loop delays cannot be negative.");
        }

        _sessionContext.SessionChanged += OnSessionChanged;
        Observe(_sessionContext.CurrentSession);
    }

    /// <summary>Raised when the current edition or its playback status changes.</summary>
    public event EventHandler? BroadcastChanged;

    /// <summary>The most recently generated edition.</summary>
    public AtisBroadcast? CurrentBroadcast
    {
        get
        {
            lock (_stateLock)
            {
                return _currentBroadcast;
            }
        }
    }

    /// <summary>Actionable playback status for the Radio page.</summary>
    public string StatusMessage
    {
        get
        {
            lock (_stateLock)
            {
                return _statusMessage;
            }
        }
    }

    /// <summary>True only while the selected radio is on the active departure ATIS channel.</summary>
    public bool IsMonitoringAtis =>
        TryGetMonitoredAtisFrequency(_sessionContext.CurrentSession, out _);

    /// <summary>
    /// Re-evaluates the current radio state. The startup service calls this explicitly so the
    /// application's always-on ATIS behavior is visible in its startup sequence.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Observe(_sessionContext.CurrentSession);
    }

    /// <summary>Replays the cached ATIS audio without another synthesis request.</summary>
    public async Task ReplayAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsMonitoringAtis)
        {
            SetStatus(UiText.T("Tune the selected radio to the departure ATIS frequency before replaying."));
            return;
        }

        if (CurrentBroadcast is null)
        {
            SetStatus(UiText.T("The departure ATIS is still being prepared."));
            return;
        }

        SetStatus(UiText.T($"Replaying information {CurrentBroadcast.InformationCode} from the audio cache."));
        bool played = await _textToSpeech
            .SpeakCachedAsync(
                CacheKey(_sessionContext.CurrentSession.SessionId),
                CurrentBroadcast.SpokenText,
                VoiceConfiguration.For(CurrentBroadcast.Language),
                cancellationToken)
            .ConfigureAwait(false);
        SetStatus(played
            ? UiText.T($"Information {CurrentBroadcast.InformationCode} replay complete; automatic loop continues.")
            : UiText.T("ATIS text is ready, but voice audio is unavailable. Check Deepgram, mute, and cloud settings."));
    }

    private void OnSessionChanged(object? sender, AtcSession session) => Observe(session);

    private void Observe(AtcSession session)
    {
        if (_disposed)
        {
            return;
        }

        bool sessionChanged;
        lock (_stateLock)
        {
            sessionChanged = _broadcastSessionId is Guid existingSession
                && existingSession != session.SessionId;
            if (sessionChanged)
            {
                _currentBroadcast = null;
                _broadcastSessionId = null;
                _informationSequence = -1;
            }
        }

        // The ATIS is read out only to a pilot who is in the aircraft: not while the simulator is not
        // running (the scripted aircraft) nor while it is in its menus.
        bool waitingForAircraft = !session.AircraftState.IsUserInAircraft || !session.AircraftState.Com1Powered;
        Frequency frequency = default;
        if (waitingForAircraft || !TryGetMonitoredAtisFrequency(session, out frequency))
        {
            bool shouldStop;
            bool statusChanged;
            string nextStatus = waitingForAircraft
                ? session.AircraftState.IsUserInAircraft
                    ? UiText.T("The departure ATIS will be read once the aircraft's radio has power.")
                    : UiText.T("The departure ATIS will be read once you are in the aircraft in the simulator.")
                : session.CurrentController == ControllerPosition.Atis
                && session.CurrentFrequency is Frequency atis
                ? UiText.T($"Tune the selected radio to {atis.ToDisplay()} to receive departure ATIS.")
                : session.FlightPlan is null
                    ? UiText.T("Load a flight plan to receive departure ATIS.")
                    : UiText.T("Departure ATIS is no longer the active station.");

            lock (_stateLock)
            {
                shouldStop = _requestedPlayback is not null;
                statusChanged = !string.Equals(
                    _statusMessage,
                    nextStatus,
                    StringComparison.Ordinal);
                _requestedPlayback = null;
                _statusMessage = nextStatus;
                _playbackCancellation?.Cancel();
                _playbackCancellation?.Dispose();
                _playbackCancellation = null;
            }

            if (shouldStop || statusChanged)
            {
                BroadcastChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        var key = new AtisPlaybackKey(session.SessionId, frequency);
        CancellationToken playbackToken;

        lock (_stateLock)
        {
            if (_requestedPlayback == key)
            {
                return;
            }

            _requestedPlayback = key;
            _statusMessage = UiText.T("Preparing departure ATIS from current airport and weather data.");
            _playbackCancellation?.Cancel();
            _playbackCancellation?.Dispose();
            _playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            playbackToken = _playbackCancellation.Token;
        }

        BroadcastChanged?.Invoke(this, EventArgs.Empty);
        _ = RunBroadcastLoopSafelyAsync(key, playbackToken);
    }

    private async Task RunBroadcastLoopSafelyAsync(
        AtisPlaybackKey key,
        CancellationToken cancellationToken)
    {
        try
        {
            while (IsStillCurrent(key))
            {
                AtcSession session = _sessionContext.CurrentSession;
                if (session.SessionId != key.SessionId
                    || session.FlightPlan is not { DepartureIcao: { Length: > 0 } icao })
                {
                    SetStatus(UiText.T("Load a flight plan to receive departure ATIS."));
                    return;
                }

                Task<AirportData?> airportTask =
                    _sessionContext.GetAirportAsync(icao, cancellationToken);
                Task<WeatherSnapshot?> weatherTask =
                    GetWeatherSafelyAsync(icao, cancellationToken);
                await Task.WhenAll(airportTask, weatherTask).ConfigureAwait(false);

                // Once the pilot has moved to Delivery, the session's assigned frequency is no
                // longer ATIS. A temporary immutable projection keeps the broadcast tied to its
                // actual published channel without moving controller jurisdiction backwards.
                AtcSession atisSession = session with { CurrentFrequency = key.Frequency };
                (AtisBroadcast? broadcast, bool changed) = CreateOrUpdateBroadcast(
                    atisSession,
                    await airportTask.ConfigureAwait(false),
                    await weatherTask.ConfigureAwait(false),
                    _clock.UtcNow);

                if (broadcast is null || !IsStillCurrent(key))
                {
                    return;
                }

                SetStatus(changed
                    ? UiText.T($"ATIS changed: now receiving information {broadcast.InformationCode} "
                      + $"on {broadcast.Frequency.ToDisplay()}.")
                    : UiText.T($"Receiving information {broadcast.InformationCode} on "
                      + $"{broadcast.Frequency.ToDisplay()}."));

                bool played = await _textToSpeech
                    .SpeakCachedAsync(
                        CacheKey(key.SessionId),
                        broadcast.SpokenText,
                        VoiceConfiguration.For(broadcast.Language),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!IsStillCurrent(key))
                {
                    return;
                }

                TimeSpan delay;
                if (played)
                {
                    SetStatus(
                        UiText.T($"Information {broadcast.InformationCode} is looping from the audio cache. "
                        + $"Runway and weather are checked locally between repeats."));
                    delay = _repeatDelay;
                }
                else
                {
                    SetStatus(
                        UiText.T("ATIS text is ready, but voice audio is unavailable. "
                        + "Check Deepgram, mute, and cloud settings; no LLM is being called."));
                    delay = _unavailableRetryDelay;
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Tuning away from ATIS stops the broadcast, just like losing the station.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Departure ATIS generation or playback failed.");
            SetStatus(UiText.T("ATIS audio failed. Check the Radio audio settings and Diagnostics."));
        }
    }

    private (AtisBroadcast? Broadcast, bool Changed) CreateOrUpdateBroadcast(
        AtcSession session,
        AirportData? airport,
        WeatherSnapshot? weather,
        DateTimeOffset now)
    {
        AtisBroadcast? candidate = AtisBroadcastBuilder.Build(
            session,
            airport,
            weather,
            now,
            edition: 0);
        if (candidate is null)
        {
            return (null, false);
        }

        lock (_stateLock)
        {
            if (_broadcastSessionId == session.SessionId
                && _currentBroadcast is AtisBroadcast current
                && string.Equals(
                    current.OperationalSignature,
                    candidate.OperationalSignature,
                    StringComparison.Ordinal))
            {
                if (current.Language == candidate.Language)
                {
                    return (current, false);
                }

                // Same facts, new language: reword the current edition without moving to the
                // next information letter, since nothing operational changed.
                AtisBroadcast reworded = AtisBroadcastBuilder.Build(
                    session,
                    airport,
                    weather,
                    now,
                    _informationSequence)!;
                _currentBroadcast = reworded;
                return (reworded, true);
            }

            _informationSequence = _broadcastSessionId == session.SessionId
                ? (_informationSequence + 1) % 26
                : 0;

            AtisBroadcast updated = AtisBroadcastBuilder.Build(
                session,
                airport,
                weather,
                now,
                _informationSequence)!;
            _broadcastSessionId = session.SessionId;
            _currentBroadcast = updated;
            return (updated, true);
        }
    }

    private async Task<WeatherSnapshot?> GetWeatherSafelyAsync(
        string icao,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _weatherProvider
                .GetAirportWeatherAsync(icao, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Weather was unavailable for {Icao}; ATIS will omit weather values.",
                icao);
            return null;
        }
    }

    private bool IsStillCurrent(AtisPlaybackKey key)
    {
        lock (_stateLock)
        {
            return _requestedPlayback == key && !_disposed;
        }
    }

    private bool TryGetMonitoredAtisFrequency(
        AtcSession session,
        out Frequency frequency)
    {
        Frequency? atis = session.CurrentController == ControllerPosition.Atis
            ? session.CurrentFrequency
            : null;

        if (atis is null)
        {
            lock (_stateLock)
            {
                if (_broadcastSessionId == session.SessionId
                    && _currentBroadcast is AtisBroadcast current)
                {
                    atis = current.Frequency;
                }
            }
        }

        if (atis is Frequency published
            && session.AircraftState.Radio.ActiveTransmitFrequency.IsSameChannel(published))
        {
            frequency = published;
            return true;
        }

        frequency = default;
        return false;
    }

    private static string CacheKey(Guid sessionId) => $"departure-atis:{sessionId:N}";

    private void SetStatus(string status)
    {
        lock (_stateLock)
        {
            _statusMessage = status;
        }

        BroadcastChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionContext.SessionChanged -= OnSessionChanged;

        lock (_stateLock)
        {
            _playbackCancellation?.Cancel();
            _playbackCancellation?.Dispose();
            _playbackCancellation = null;
        }

        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    private sealed record AtisPlaybackKey(Guid SessionId, Frequency Frequency);
}
