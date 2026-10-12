using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Guidance;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Application.Sessions;
using StormATC.Application.Transmissions;
using StormATC.Desktop.Services;
using StormATC.Domain;
using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.Phraseology;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;
using StormATC.Infrastructure.Mocks;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Desktop.ViewModels;

/// <summary>
/// The dashboard: live session state, the push-to-talk control, and the developer tools.
/// </summary>
/// <remarks>
/// <para>
/// Holds no business logic. It observes <see cref="AtcSessionContext"/> and forwards user
/// actions to <see cref="PilotTransmissionCoordinator"/>; every decision about what is
/// permitted happens in the domain.
/// </para>
/// <para>
/// Session and simulator events arrive on background threads, so every property update is
/// marshalled to the dispatcher before it touches a binding.
/// </para>
/// </remarks>
public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly AtcSessionContext _sessionContext;
    private readonly PilotTransmissionCoordinator _coordinator;
    private readonly RadioActivityState _radioActivity;
    private readonly ISpeechRecognitionService _speechRecognition;
    private readonly ITextToSpeechService _textToSpeech;
    private readonly SimulatorConnectorRouter _simulatorRouter;
    private readonly MsfsSimConnectConnector _msfsConnector;
    private readonly TrafficRadioService _trafficRadio;
    private readonly MockSimulatorConnector _mockSimulator;
    private readonly MockTrafficProvider _mockTraffic;
    private readonly ProviderStatusService _providerStatus;
    private readonly IClock _clock;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly IOptionsMonitor<ApplicationOptions> _applicationOptions;
    private readonly IOptionsMonitor<AudioOptions> _audioOptions;
    private readonly IOptionsMonitor<TrafficOptions> _trafficOptions;
    private readonly IDisposable? _audioOptionsSubscription;
    private readonly IDisposable? _trafficOptionsSubscription;

    private bool _disposed;

    // --- Session state -------------------------------------------------------------------

    [ObservableProperty]
    private string _callsign = "—";

    [ObservableProperty]
    private string _routeSummary = UiText.T("No flight plan loaded");

    [ObservableProperty]
    private string _currentController = "—";

    [ObservableProperty]
    private string _currentFrequency = "—";

    [ObservableProperty]
    private string _frequencySource = UiText.T("No frequency assigned");

    [ObservableProperty]
    private string _tunedFrequency = "—";

    [ObservableProperty]
    private string _frequencyStatus = string.Empty;

    [ObservableProperty]
    private bool _hasFrequencyMismatch;

    [ObservableProperty]
    private string _flightPhase = UiText.T("Preflight");

    [ObservableProperty]
    private string _altitude = "—";

    [ObservableProperty]
    private string _heading = "—";

    [ObservableProperty]
    private string _groundSpeed = "—";

    [ObservableProperty]
    private string _indicatedAirspeed = "—";

    [ObservableProperty]
    private string _verticalSpeed = "—";

    [ObservableProperty]
    private string _altitudeAboveGround = "—";

    [ObservableProperty]
    private string _aircraftIdentity = UiText.T("Waiting for simulator data");

    [ObservableProperty]
    private string _aircraftConfiguration = UiText.T("No live aircraft state");

    [ObservableProperty]
    private string _activeRunway = "—";

    [ObservableProperty]
    private string _squawk = "—";

    [ObservableProperty]
    private string _activeClearance = UiText.T("No clearance issued");

    [ObservableProperty]
    private string _pendingReadback = UiText.T("None");

    [ObservableProperty]
    private bool _hasPendingReadback;

    [ObservableProperty]
    private string _lastPilotTransmission = "—";

    [ObservableProperty]
    private string _lastControllerTransmission = "—";

    [ObservableProperty]
    private string _activeWarning = string.Empty;

    [ObservableProperty]
    private bool _hasActiveWarning;

    [ObservableProperty]
    private string _nextCallTitle = UiText.T("Import a flight plan");

    [ObservableProperty]
    private string _nextCallDetail = string.Empty;

    [ObservableProperty]
    private string _nextCallExample = string.Empty;

    [ObservableProperty]
    private bool _hasNextCallExample;

    [ObservableProperty]
    private string _simulatorStatus = UiText.T("SIMULATOR OFFLINE");

    [ObservableProperty]
    private string _simulatorStatusDetail = UiText.T("Waiting for Microsoft Flight Simulator");

    [ObservableProperty]
    private string _weatherSummary = UiText.T("Weather unavailable");

    [ObservableProperty]
    private string _weatherDetail = UiText.T("Connect MSFS to read ambient conditions");

    [ObservableProperty]
    private string _trafficSummary = UiText.T("No traffic feed");

    [ObservableProperty]
    private string _trafficDetail = UiText.T("Connect MSFS to scan nearby aircraft");

    [ObservableProperty]
    private string _latestTrafficRadio = string.Empty;

    [ObservableProperty]
    private bool _hasTrafficRadioExchange;

    [ObservableProperty]
    private string _airportDataSummary = UiText.T("Airport data pending");

    [ObservableProperty]
    private string _airportDataDetail = UiText.T("Load a flight plan to read live scenery");

    [ObservableProperty]
    private bool _hasSurfaceGuidance;

    [ObservableProperty]
    private string _surfacePosition = UiText.T("Position unavailable");

    [ObservableProperty]
    private string _surfaceDestination = UiText.T("No taxi clearance");

    [ObservableProperty]
    private string _surfaceRoute = UiText.T("Request taxi to calculate a route");

    [ObservableProperty]
    private string _surfaceNextAction = UiText.T("Remain on the stand");

    [ObservableProperty]
    private string _surfaceSignCue = UiText.T("Keep the airport chart open while taxiing");

    [ObservableProperty]
    private string _surfaceProgress = string.Empty;

    // --- Speech and PTT ------------------------------------------------------------------

    [ObservableProperty]
    private SpeechState _speechState = SpeechState.Idle;

    [ObservableProperty]
    private string _speechStateText = UiText.T("Ready");

    [ObservableProperty]
    private string _pushToTalkLabel = UiText.T("PUSH TO TALK");

    [ObservableProperty]
    private string _interimTranscript = string.Empty;

    [ObservableProperty]
    private double _inputLevel;

    [ObservableProperty]
    private bool _isTransmitting;

    // --- Developer tools -----------------------------------------------------------------

    [ObservableProperty]
    private string _manualTransmissionText = string.Empty;

    [ObservableProperty]
    private string _mockStageText = UiText.T("At stand");

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Creates the dashboard view model.</summary>
    public DashboardViewModel(
        AtcSessionContext sessionContext,
        PilotTransmissionCoordinator coordinator,
        RadioActivityState radioActivity,
        ISpeechRecognitionService speechRecognition,
        ITextToSpeechService textToSpeech,
        SimulatorConnectorRouter simulatorRouter,
        MsfsSimConnectConnector msfsConnector,
        TrafficRadioService trafficRadio,
        MockSimulatorConnector mockSimulator,
        MockTrafficProvider mockTraffic,
        ProviderStatusService providerStatus,
        IClock clock,
        IOptionsMonitor<ApplicationOptions> applicationOptions,
        IOptionsMonitor<AudioOptions> audioOptions,
        IOptionsMonitor<TrafficOptions> trafficOptions,
        ILogger<DashboardViewModel> logger)
    {
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _radioActivity = radioActivity ?? throw new ArgumentNullException(nameof(radioActivity));
        _speechRecognition = speechRecognition ?? throw new ArgumentNullException(nameof(speechRecognition));
        _textToSpeech = textToSpeech ?? throw new ArgumentNullException(nameof(textToSpeech));
        _simulatorRouter = simulatorRouter ?? throw new ArgumentNullException(nameof(simulatorRouter));
        _msfsConnector = msfsConnector ?? throw new ArgumentNullException(nameof(msfsConnector));
        _trafficRadio = trafficRadio ?? throw new ArgumentNullException(nameof(trafficRadio));
        _mockSimulator = mockSimulator ?? throw new ArgumentNullException(nameof(mockSimulator));
        _mockTraffic = mockTraffic ?? throw new ArgumentNullException(nameof(mockTraffic));
        _providerStatus = providerStatus ?? throw new ArgumentNullException(nameof(providerStatus));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _applicationOptions = applicationOptions ?? throw new ArgumentNullException(nameof(applicationOptions));
        _audioOptions = audioOptions ?? throw new ArgumentNullException(nameof(audioOptions));
        _trafficOptions = trafficOptions ?? throw new ArgumentNullException(nameof(trafficOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _audioOptionsSubscription = _audioOptions.OnChange(
            _ => RunOnUiThread(() => OnPropertyChanged(nameof(PushToTalkHint))));
        _trafficOptionsSubscription = _trafficOptions.OnChange(
            _ => RunOnUiThread(() => UpdateLiveContext(_sessionContext.CurrentSession)));

        _sessionContext.SessionChanged += OnSessionChanged;
        _coordinator.TransmissionProcessed += OnTransmissionProcessed;
        _simulatorRouter.AircraftStateChanged += OnAircraftStateChanged;
        _simulatorRouter.ConnectionChanged += OnSimulatorConnectionChanged;
        _trafficRadio.ExchangeGenerated += OnTrafficRadioExchangeGenerated;

        _speechRecognition.InterimTranscriptReceived += OnInterimTranscript;
        _speechRecognition.FinalTranscriptReceived += OnFinalTranscript;
        _speechRecognition.ErrorOccurred += OnSpeechError;
        _speechRecognition.AudioLevelChanged += OnAudioLevel;

        UpdateFromSession(_sessionContext.CurrentSession);
    }

    /// <summary>Provider status badges shown across the top of the dashboard.</summary>
    public ProviderStatusService ProviderStatus => _providerStatus;

    /// <summary>
    /// True when the developer tools are visible.
    /// </summary>
    /// <remarks>
    /// The tools drive the scripted aircraft directly, so they are only shown when the scripted
    /// aircraft is what is flying. Advancing a mock stage while a real simulator is connected
    /// would change nothing on screen and look like a bug.
    /// </remarks>
    public bool IsDeveloperMode =>
        _applicationOptions.CurrentValue.DeveloperMode && _simulatorRouter.IsMockActive;

    /// <summary>True when aircraft state is coming from the scripted mock aircraft.</summary>
    public bool IsMockMode => _simulatorRouter.IsMockActive;

    /// <summary>The mode badge text.</summary>
    public string ModeBadge => IsMockMode ? UiText.T("MOCK") : UiText.T("LIVE");

    /// <summary>Explains every active push-to-talk input, including the configured keyboard key.</summary>
    public string PushToTalkHint =>
        UiText.T(
            $"Hold the dashboard button, {_audioOptions.CurrentValue.PushToTalkKey}, "
            + $"or your bound joystick button. Release to send.");

    // =====================================================================================
    // Push to talk
    // =====================================================================================

    /// <summary>Starts capturing a transmission. Bound to PTT press and to the Space key.</summary>
    [RelayCommand]
    private async Task StartTransmissionAsync()
    {
        if (IsTransmitting)
        {
            return;
        }

        if (!CanTransmitToCurrentStation(out string reason))
        {
            StatusMessage = reason;
            SetSpeechState(
                SpeechState.Idle,
                _sessionContext.CurrentSession.CurrentController == ControllerPosition.Atis
                    ? UiText.T("ATIS — receive only")
                    : UiText.T("Wrong frequency"),
                UiText.T("PUSH TO TALK"));
            return;
        }

        IsTransmitting = true;
        _radioActivity.SetUserTransmitting(true);
        SetSpeechState(SpeechState.Connecting, UiText.T("Connecting…"), UiText.T("RELEASE TO SEND"));
        InterimTranscript = string.Empty;

        try
        {
            await _speechRecognition.StartTransmissionAsync(CancellationToken.None)
                .ConfigureAwait(true);

            // The service raises an error event when it is unconfigured, which resets the state.
            if (SpeechState == SpeechState.Connecting)
            {
                SetSpeechState(SpeechState.Listening, UiText.T("Listening…"), UiText.T("RELEASE TO SEND"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start a transmission.");
            SetSpeechState(SpeechState.Error, UiText.T("Could not start capture"), UiText.T("PUSH TO TALK"));
            IsTransmitting = false;
            _radioActivity.SetUserTransmitting(false);
        }
    }

    /// <summary>Stops capturing and submits the final transcript.</summary>
    [RelayCommand]
    private async Task StopTransmissionAsync()
    {
        if (!IsTransmitting)
        {
            return;
        }

        IsTransmitting = false;
        _radioActivity.SetUserTransmitting(false);
        SetSpeechState(SpeechState.Processing, UiText.T("Processing…"), UiText.T("PUSH TO TALK"));

        try
        {
            await _speechRecognition.StopTransmissionAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop the transmission.");
            SetSpeechState(SpeechState.Error, UiText.T("Capture failed"), UiText.T("PUSH TO TALK"));
        }
    }

    // =====================================================================================
    // Guidance actions
    // =====================================================================================

    /// <summary>Copies the recommended next transmission for quick practice or typed use.</summary>
    [RelayCommand]
    private void CopyNextCall()
    {
        string text = string.IsNullOrWhiteSpace(NextCallExample)
            ? NextCallTitle
            : NextCallExample;

        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = UiText.T("There is no suggested call to copy yet.");
            return;
        }

        try
        {
            Clipboard.SetText(text);
            StatusMessage = UiText.T("Suggested call copied to the clipboard.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The suggested call could not be copied.");
            StatusMessage = UiText.T("The suggested call could not be copied.");
        }
    }

    // =====================================================================================
    // Developer tools
    // =====================================================================================

    /// <summary>
    /// Submits typed text as a pilot transmission.
    /// </summary>
    /// <remarks>
    /// This bypasses speech-to-text and nothing else. The text goes through the same
    /// normalisation, parsing, engine evaluation, and persistence as a spoken transmission, so
    /// the ATC pipeline can be exercised and tested without a microphone or an API key.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanSendManualTransmission))]
    private async Task SendManualTransmissionAsync()
    {
        string text = ManualTransmissionText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        ManualTransmissionText = string.Empty;
        await SubmitTransmissionAsync(text, TransmissionSource.ManualText).ConfigureAwait(true);
    }

    private bool CanSendManualTransmission() =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(ManualTransmissionText)
        && CanTransmitToCurrentStation(out _);

    partial void OnManualTransmissionTextChanged(string value) =>
        SendManualTransmissionCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value) =>
        SendManualTransmissionCommand.NotifyCanExecuteChanged();

    /// <summary>Advances the mock simulator to its next scripted stage.</summary>
    [RelayCommand]
    private async Task AdvanceMockStageAsync()
    {
        _mockSimulator.AdvanceStage();
        MockStageText = FormatStage(_mockSimulator.Stage);

        // Let the engine observe the new physical state — it may detect a takeoff, a touchdown,
        // or a hold-short violation.
        await _coordinator.ProcessStateChangeAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Resets the mock simulator to the stand.</summary>
    [RelayCommand]
    private void ResetMockSimulator()
    {
        _mockSimulator.Reset();
        MockStageText = FormatStage(_mockSimulator.Stage);
        StatusMessage = UiText.T("Mock simulator reset to the stand.");
    }

    /// <summary>Puts the aircraft on the runway without clearance, to test the incursion warning.</summary>
    [RelayCommand]
    private async Task TriggerRunwayIncursionAsync()
    {
        // The runway 18 threshold at the fictional STMR.
        _mockSimulator.TriggerRunwayIncursion(GeoPosition.Create(52.30500, 4.70000));
        await _coordinator.ProcessStateChangeAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Places traffic on the departure runway, so takeoff clearance is refused.</summary>
    [RelayCommand]
    private void TriggerRunwayOccupied()
    {
        AtcSession session = _sessionContext.CurrentSession;

        if (session.AssignedDepartureRunway is not RunwayIdentifier runway)
        {
            StatusMessage = UiText.T("No departure runway is assigned yet.");
            return;
        }

        _mockTraffic.OccupyRunway(runway, session.AircraftState.Position);
        StatusMessage =
            UiText.T($"Traffic placed on runway {runway.ToDisplay()}. Takeoff clearance will be refused.");
    }

    /// <summary>Removes the injected traffic, so takeoff clearance becomes available again.</summary>
    [RelayCommand]
    private void ClearMockTraffic()
    {
        _mockTraffic.Clear();
        StatusMessage = UiText.T("The runway is clear of injected traffic.");
    }

    /// <summary>Replays the most recent controller transmission.</summary>
    [RelayCommand]
    private async Task ReplayLastTransmissionAsync()
    {
        await _textToSpeech.ReplayLastAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Submits a transmission through the full pipeline.</summary>
    public async Task SubmitTransmissionAsync(string text, TransmissionSource source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        IsBusy = true;
        SetSpeechState(SpeechState.Processing, UiText.T("Processing…"), UiText.T("PUSH TO TALK"));
        LastPilotTransmission = text;

        try
        {
            var transmission = PilotTransmission.Create(
                _sessionContext.CurrentSession.SessionId, text, source, _clock.UtcNow);

            TransmissionProcessingResult result = await _coordinator
                .ProcessAsync(transmission, CancellationToken.None)
                .ConfigureAwait(true);

            if (result.ErrorMessage is { Length: > 0 } error)
            {
                StatusMessage = error;
                SetSpeechState(SpeechState.Error, UiText.T("Processing failed"), UiText.T("PUSH TO TALK"));
                return;
            }

            if (result.WasIgnoredOnAtis)
            {
                StatusMessage =
                    UiText.T("ATIS is receive-only. Copy the information, then tune Delivery before calling.");
                SetSpeechState(SpeechState.Idle, UiText.T("ATIS — receive only"), UiText.T("PUSH TO TALK"));
                return;
            }

            if (result.WasIgnoredOffFrequency)
            {
                StatusMessage = UiText.T(
                    $"No controller heard the transmission. Tune the selected transmit radio to "
                    + $"{result.ExpectedFrequency?.ToDisplay()}.");
                SetSpeechState(SpeechState.Idle, UiText.T("Wrong frequency"), UiText.T("PUSH TO TALK"));
                return;
            }

            if (result.ControllerSpokenText is { Length: > 0 } spoken)
            {
                SetSpeechState(SpeechState.PlayingAudio, UiText.T("Controller responding…"), UiText.T("PUSH TO TALK"));

                await _textToSpeech
                    .SpeakAsync(
                        spoken,
                        VoiceConfiguration.For(_sessionContext.CurrentSession.Language),
                        CancellationToken.None)
                    .ConfigureAwait(true);
            }

            SetSpeechState(SpeechState.Idle, UiText.T("Ready"), UiText.T("PUSH TO TALK"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The pilot transmission pipeline failed.");
            StatusMessage =
                UiText.T("The transmission could not be completed. Check Diagnostics and try again.");
            SetSpeechState(SpeechState.Error, UiText.T("Transmission failed"), UiText.T("PUSH TO TALK"));
        }
        finally
        {
            IsBusy = false;
            InterimTranscript = string.Empty;
        }
    }

    // =====================================================================================
    // Event handlers. All of these can arrive off the UI thread.
    // =====================================================================================

    private void OnSessionChanged(object? sender, AtcSession session) =>
        RunOnUiThread(() => UpdateFromSession(session));

    private void OnTransmissionProcessed(object? sender, TransmissionProcessedEventArgs e) =>
        RunOnUiThread(() =>
        {
            if (e.Result.ControllerDisplayText is { Length: > 0 } text)
            {
                LastControllerTransmission = text;
            }
        });

    /// <summary>
    /// Re-reads the properties that depend on which connector is live.
    /// </summary>
    /// <remarks>
    /// The router can hand over from the mock to MSFS mid-session, without anyone asking, when
    /// the simulator finally starts. The badge and the developer tools have to follow it.
    /// </remarks>
    private void OnSimulatorConnectionChanged(object? sender, SimulatorConnectionChangedEventArgs e) =>
        RunOnUiThread(() =>
        {
            OnPropertyChanged(nameof(IsMockMode));
            OnPropertyChanged(nameof(IsDeveloperMode));
            OnPropertyChanged(nameof(ModeBadge));
            UpdateLiveContext(_sessionContext.CurrentSession);

            if (e.Message is { Length: > 0 } message)
            {
                StatusMessage = message;
            }
        });

    private void OnAircraftStateChanged(object? sender, AircraftStateChangedEventArgs e)
    {
        // Simulator updates arrive five times a second. Pushing the session update through the
        // dispatcher every time would flood the UI queue, so the session is updated on this
        // thread and only the display strings are marshalled.
        _sessionContext.UpdateAircraftState(e.State);
    }

    private void OnInterimTranscript(object? sender, InterimTranscriptEventArgs e) =>
        RunOnUiThread(() => InterimTranscript = e.Text);

    private void OnFinalTranscript(object? sender, FinalTranscriptEventArgs e) =>
        _ = RunOnUiThreadAsync(
            () => SubmitTransmissionAsync(e.Text, TransmissionSource.Voice));

    private void OnSpeechError(object? sender, SpeechErrorEventArgs e) =>
        RunOnUiThread(() =>
        {
            IsTransmitting = false;
            _radioActivity.SetUserTransmitting(false);
            SetSpeechState(SpeechState.Error, e.Message, UiText.T("PUSH TO TALK"));

            StatusMessage = e.SuggestedAction is { Length: > 0 } action
                ? $"{e.Message} {action}"
                : e.Message;
        });

    private void OnAudioLevel(object? sender, AudioLevelEventArgs e) =>
        RunOnUiThread(() => InputLevel = e.Level);

    private void OnTrafficRadioExchangeGenerated(
        object? sender,
        TrafficRadioExchangeEventArgs e) =>
        RunOnUiThread(() =>
        {
            string source = e.IsFslTlTraffic ? "FSLTL" : "MSFS";
            string wording = e.Exchange.UsedLanguageModel ? UiText.T("AI wording") : UiText.T("local wording");
            LatestTrafficRadio =
                $"ATC: {e.Exchange.ControllerLine}\n"
                + UiText.T($"PILOT: {e.Exchange.PilotLine}\n")
                + $"{source} · {wording} · {e.Timestamp:HH:mm}";
            HasTrafficRadioExchange = true;
        });

    /// <summary>Projects the session into display strings.</summary>
    private void UpdateFromSession(AtcSession session)
    {
        Callsign = session.Callsign?.Normalized ?? "—";
        RouteSummary = session.FlightPlan?.ToSummary() ?? UiText.T("No flight plan loaded");
        CurrentController = session.CurrentController == ControllerPosition.None
            ? "—"
            : Domain.Phraseology.DeterministicPhraseology.SpokenControllerName(session.CurrentController);

        Frequency tuned = session.AircraftState.Radio.ActiveTransmitFrequency;
        TunedFrequency = tuned.ToDisplay();
        CurrentFrequency = session.CurrentFrequency?.ToDisplay() ?? tuned.ToDisplay();
        FrequencySource = session.CurrentFrequency is null
            ? UiText.T("Tuned COM — no local frequency data")
            : session.CurrentController == ControllerPosition.Atis
                ? UiText.T("Departure ATIS")
                : UiText.T("ATC assigned");
        HasFrequencyMismatch =
            session.CurrentFrequency is Frequency assigned && assigned != tuned;
        FrequencyStatus = session.CurrentController == ControllerPosition.Atis
            ? UiText.T("Listen to ATIS, then tune Delivery. ATIS does not receive pilot calls.")
            : HasFrequencyMismatch
                ? UiText.T($"Tune COM to {session.CurrentFrequency?.ToDisplay()} before transmitting.")
                : session.CurrentFrequency is null
                    ? UiText.T("StormATC is using your active transmit radio.")
                    : UiText.T("Radio tuned correctly.");
        SendManualTransmissionCommand.NotifyCanExecuteChanged();

        FlightPhase = FormatPhase(session.Phase);

        Altitude = session.AircraftState.IndicatedAltitudeFeet > 0
            ? $"{session.AircraftState.IndicatedAltitudeRounded:N0} ft"
            : "—";

        Heading = session.AircraftState.MagneticHeading.ToDisplay();
        GroundSpeed = $"{session.AircraftState.GroundSpeedKnots:F0} kt";
        IndicatedAirspeed = $"{session.AircraftState.IndicatedAirspeedKnots:F0} kt IAS";
        VerticalSpeed = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{session.AircraftState.VerticalSpeedFeetPerMinute:+0;-0;0} fpm");
        AltitudeAboveGround = $"{session.AircraftState.AltitudeAboveGroundFeet:N0} ft AGL";
        AircraftIdentity = !string.IsNullOrWhiteSpace(session.AircraftState.AtcIdentifier)
            ? $"{session.AircraftState.AtcIdentifier} · {session.AircraftState.AircraftTitle}"
            : session.AircraftState.AircraftTitle;
        AircraftConfiguration = string.Join(
            "  ·  ",
            session.AircraftState.EnginesRunning ? UiText.T("ENGINES ON") : UiText.T("ENGINES OFF"),
            session.AircraftState.ParkingBrakeSet ? UiText.T("PARK BRAKE SET") : UiText.T("PARK BRAKE RELEASED"),
            session.AircraftState.AutopilotEngaged ? UiText.T("AUTOPILOT ON") : UiText.T("AUTOPILOT OFF"),
            session.AircraftState.GearHandleDown ? UiText.T("GEAR DOWN") : UiText.T("GEAR UP"),
            UiText.T($"FLAPS {session.AircraftState.FlapsHandleIndex}"));
        Squawk = session.AircraftState.Transponder.Code.ToDisplay();

        ActiveRunway = (session.AssignedDepartureRunway ?? session.AssignedArrivalRunway)?.ToDisplay() ?? "—";

        ActiveClearance = FormatClearance(session);

        HasPendingReadback = session.PendingReadback is not null;
        PendingReadback = session.PendingReadback is PendingReadback pending
            ? string.Join(
                ", ",
                pending.ExpectedItems
                    .Where(item => !pending.SatisfiedItems.Contains(item.Type))
                    .Select(item => FormatReadbackItem(item.Type)))
            : UiText.T("None");

        AtcWarning? warning = session.ActiveWarnings.Count > 0
            ? session.ActiveWarnings[^1]
            : null;

        HasActiveWarning = warning is not null;
        ActiveWarning = warning?.Message ?? string.Empty;

        AtcGuidance guidance = AtcGuidanceAdvisor.ForSession(session);
        NextCallTitle = guidance.Title;
        NextCallDetail = guidance.Detail;
        NextCallExample = guidance.Example;
        HasNextCallExample = guidance.HasExample;

        UpdateLiveContext(session);
        MockStageText = FormatStage(_mockSimulator.Stage);
    }

    private bool CanTransmitToCurrentStation(out string reason)
    {
        AtcSession session = _sessionContext.CurrentSession;
        if (session.CurrentController == ControllerPosition.Atis)
        {
            reason =
                UiText.T("ATIS is receive-only. Copy the information, then tune Delivery before calling.");
            return false;
        }

        Frequency tuned = session.AircraftState.Radio.ActiveTransmitFrequency;
        if (session.CurrentFrequency is Frequency expected
            && !tuned.IsSameChannel(expected))
        {
            reason =
                UiText.T($"The selected transmit radio is on {tuned.ToDisplay()}. ")
                + UiText.T($"Tune {expected.ToDisplay()} before transmitting.");
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void UpdateLiveContext(AtcSession session)
    {
        if (_msfsConnector.ConnectionState != SimulatorConnectionState.Connected)
        {
            SimulatorStatus = IsMockMode ? UiText.T("TRAINING MODE") : UiText.T("SIMULATOR OFFLINE");
            SimulatorStatusDetail = IsMockMode
                ? UiText.T("Using the scripted training aircraft")
                : _msfsConnector.StatusMessage;
            WeatherSummary = IsMockMode ? UiText.T("Sample conditions") : UiText.T("Weather unavailable");
            WeatherDetail = IsMockMode
                ? UiText.T("Fictional training weather is used when available")
                : UiText.T("Connect MSFS to read ambient conditions");
            TrafficSummary = IsMockMode
                ? UiText.T("Scripted traffic")
                : _trafficOptions.CurrentValue.EnableLiveTraffic
                    ? UiText.T("No traffic feed")
                    : UiText.T("Traffic disabled");
            TrafficDetail = IsMockMode
                ? UiText.T("Developer scenarios can inject runway traffic")
                : _trafficOptions.CurrentValue.EnableLiveTraffic
                    ? UiText.T("Connect MSFS to scan nearby aircraft")
                    : UiText.T("Enable it in Settings > Live integrations");
            AirportDataSummary = IsMockMode ? UiText.T("Sample airport data") : UiText.T("Airport data pending");
            AirportDataDetail = IsMockMode
                ? UiText.T("Bundled fictional airports support repeatable training")
                : UiText.T("Connect MSFS and load a flight plan");
            HasSurfaceGuidance = false;
            return;
        }

        SimulatorStatus = UiText.T("MSFS LIVE");
        SimulatorStatusDetail = _msfsConnector.StatusMessage;

        if (_msfsConnector.LatestWeather is WeatherSnapshot weather)
        {
            string wind = weather.HasWind
                ? $"{weather.WindDirection!.Value.ToDisplay()} / {weather.WindSpeedKnots} kt"
                : UiText.T("Wind unavailable");
            WeatherSummary = wind;

            var details = new List<string>();
            if (weather.QnhHectopascals is int qnh)
            {
                details.Add($"QNH {qnh}");
            }

            if (weather.TemperatureCelsius is int temperature)
            {
                details.Add($"{temperature:+0;-0;0} °C");
            }

            if (weather.VisibilityMeters is int visibility)
            {
                details.Add(visibility >= 10_000
                    ? "VIS 10+ km"
                    : $"VIS {visibility / 1000.0:F1} km");
            }

            WeatherDetail = details.Count > 0
                ? string.Join("  ·  ", details)
                : UiText.T("Ambient conditions at the aircraft");
        }
        else
        {
            WeatherSummary = UiText.T("Reading live weather…");
            WeatherDetail = UiText.T("Waiting for the next simulator data frame");
        }

        if (!_trafficOptions.CurrentValue.EnableLiveTraffic)
        {
            TrafficSummary = UiText.T("Traffic disabled");
            TrafficDetail = UiText.T("Enable it in Settings > Live integrations");
        }
        else
        {
            IReadOnlyList<MsfsTrafficSnapshot> traffic = _msfsConnector.NearbyTraffic;
            TrafficSummary = traffic.Count switch
            {
                0 => UiText.T("No nearby traffic"),
                1 => UiText.T("1 nearby aircraft"),
                _ => UiText.T($"{traffic.Count} nearby aircraft"),
            };

            MsfsTrafficSnapshot? nearest = traffic
                .OrderBy(contact =>
                    contact.Position.DistanceNauticalMilesTo(session.AircraftState.Position))
                .FirstOrDefault();
            TrafficDetail = nearest is null
                ? UiText.T("20 km simulator traffic scan is clear")
                : $"{nearest.Callsign} · "
                    + $"{nearest.Position.DistanceNauticalMilesTo(session.AircraftState.Position):F1} NM";
        }

        string? relevantIcao = RelevantAirportIcao(session);
        AirportData? airport = relevantIcao is not null
            ? _msfsConnector.GetCachedAirport(relevantIcao)
            : _msfsConnector.GetCachedAirports()
                .OrderBy(candidate =>
                    candidate.ReferencePosition.DistanceNauticalMilesTo(
                        session.AircraftState.Position))
                .FirstOrDefault();

        if (airport is null)
        {
            AirportDataSummary = relevantIcao is null
                ? UiText.T("Load a flight plan")
                : UiText.T($"Reading {relevantIcao} scenery…");
            AirportDataDetail = UiText.T("Taxiways, stands and frequencies load automatically");
            HasSurfaceGuidance = false;
        }
        else
        {
            AirportDataSummary = airport.SupportsTaxiRouting
                ? UiText.T($"{airport.Icao} routing ready")
                : UiText.T($"{airport.Icao} limited data");
            AirportDataDetail = airport.SupportsTaxiRouting
                ? UiText.T($"{airport.Stands.Count} stands  ·  ")
                    + UiText.T($"{airport.TaxiGraph?.Edges.Count ?? 0} taxi paths  ·  ")
                    + UiText.T($"{airport.Runways.Count} runway ends")
                : UiText.T("The installed scenery did not publish a usable taxi graph");
            UpdateSurfaceGuidance(session, airport);
        }
    }

    private void UpdateSurfaceGuidance(AtcSession session, AirportData airport)
    {
        if (!session.AircraftState.IsOnGround || airport.TaxiGraph is not TaxiGraph graph)
        {
            HasSurfaceGuidance = false;
            return;
        }

        HasSurfaceGuidance = true;
        TaxiSurfacePosition position = TaxiSurfaceAdvisor.Locate(
            graph,
            session.AircraftState.Position);
        SurfacePosition = FormatSurfacePosition(position);

        Clearance? clearance = session.TaxiClearance;
        if (clearance is null || clearance.Type is not (ClearanceType.Taxi or ClearanceType.TaxiToStand))
        {
            SurfaceDestination = UiText.T("No taxi clearance issued");
            SurfaceRoute = UiText.T("Request taxi from Ground to calculate the route");
            SurfaceNextAction = UiText.T("Hold position until Ground issues a taxi clearance");
            SurfaceSignCue =
                UiText.T("Chart check: black/yellow identifies your current taxiway; ")
                + UiText.T("yellow/black with an arrow points to another taxiway.");
            SurfaceProgress = UiText.T("MSFS scenery position is live");
            return;
        }

        SurfaceDestination = clearance.Runway is RunwayIdentifier runway
            ? UiText.T($"Holding point runway {runway.ToDisplay()}")
            : clearance.TaxiDestination is { Length: > 0 } stand
                ? UiText.T($"Stand {stand}")
                : UiText.T("Taxi clearance limit");

        string routeText = clearance.TaxiRoute.Count > 0
            ? string.Join("  >  ", clearance.TaxiRoute.Select(FormatTaxiwayDesignator))
            : UiText.T("Route detail unavailable");
        SurfaceRoute = $"{routeText}  >  {SurfaceDestination}";

        TaxiRoute route = clearance.TaxiPathNodes.Count > 0
            ? TaxiPathfinder.FromNodeSequence(
                graph,
                clearance.TaxiPathNodes,
                clearance.TaxiPathTaxiways)
            : TaxiRoute.NotFound;
        TaxiRouteProgress progress = TaxiSurfaceAdvisor.Analyze(
            graph,
            route,
            session.AircraftState.Position);

        if (!progress.Found)
        {
            SurfaceNextAction =
                UiText.T("The clearance has no stored geometry. Stop if unsure and request progressive taxi.");
            SurfaceSignCue =
                UiText.T("Use the current aerodrome chart; StormATC will not invent a junction or sign.");
            SurfaceProgress = clearance.TaxiRouteLengthMeters is double planned
                ? UiText.T($"{FormatDistance(planned)} planned")
                : UiText.T("Position matched, route geometry unavailable");
            return;
        }

        SurfacePosition = FormatSurfacePosition(progress.SurfacePosition);
        SurfaceProgress =
            UiText.T($"{FormatDistance(progress.RemainingDistanceMeters)} remaining")
            + (progress.IsOnRoute ? UiText.T(" · ON CLEARED ROUTE") : UiText.T(" · ROUTE DEVIATION"));

        if (!progress.IsOnRoute)
        {
            SurfaceNextAction = UiText.T(
                $"Stop if unsure — aircraft is {progress.SurfacePosition.OffsetMeters:F0} m "
                + $"from the cleared path. Ask Ground for progressive taxi.");
            SurfaceSignCue =
                UiText.T("Do not chase a sign across the apron. Confirm position with Ground before continuing.");
            return;
        }

        if (progress.IsAtDestination || progress.IsApproachingHoldShort)
        {
            SurfaceNextAction = progress.IsAtDestination
                ? UiText.T($"Stop at {SurfaceDestination}")
                : UiText.T(
                    $"Continue {FormatDistance(progress.RemainingDistanceMeters)} to "
                    + $"{SurfaceDestination}, then stop before the hold line");
            SurfaceSignCue = clearance.Runway is RunwayIdentifier holdRunway
                ? UiText.T(
                    $"MANDATORY: look for the red/white runway {holdRunway.ToDisplay()} sign "
                    + $"and the runway holding-position marking.")
                : UiText.T("Confirm the stand number and follow the stand lead-in line.");
            return;
        }

        if (progress.NextTaxiway is { Length: > 0 } nextTaxiway
            && progress.NextTurn is TaxiTurnDirection turn)
        {
            string action = turn switch
            {
                TaxiTurnDirection.Left => UiText.T("turn left"),
                TaxiTurnDirection.Right => UiText.T("turn right"),
                _ => UiText.T("continue straight"),
            };
            SurfaceNextAction =
                $"{FormatDistance(progress.DistanceToNextInstructionMeters)}: "
                + UiText.T($"{action} onto {FormatTaxiwayDesignator(nextTaxiway)}");
        }
        else
        {
            SurfaceNextAction = UiText.T(
                $"Continue on {FormatTaxiwayDesignator(progress.CurrentTaxiway ?? UiText.T("taxiway"))} "
                + $"for {FormatDistance(progress.RemainingDistanceMeters)} to {SurfaceDestination}");
        }

        TaxiwaySignCue[] cues =
        [
            .. progress.JunctionSignCues
                .Where(cue => !string.IsNullOrWhiteSpace(cue.TaxiwayName))
                .Take(4),
        ];
        SurfaceSignCue = cues.Length > 0
            ? UiText.T("EXPECTED AT NEXT JUNCTION: ")
                + string.Join("  ·  ", cues.Select(FormatSignCue))
                + UiText.T(". Yellow/black panels are direction signs; the route cue is marked.")
            : UiText.T("Follow the centreline and confirm your position against the airport chart.");
    }

    private static string FormatSurfacePosition(TaxiSurfacePosition position)
    {
        if (!position.Found)
        {
            return UiText.T("Outside the published MSFS taxi graph");
        }

        if (position.StandName is { Length: > 0 } stand)
        {
            return UiText.T($"Stand {stand} · live aircraft position");
        }

        if (position.TaxiwayName is { Length: > 0 } taxiway)
        {
            string offset = position.OffsetMeters <= 12
                ? UiText.T("on centreline")
                : UiText.T($"{position.OffsetMeters:F0} m from centreline");
            return $"{FormatTaxiwayDesignator(taxiway)} · {offset}";
        }

        return position.NearestNodeName is { Length: > 0 } node
            ? UiText.T($"Near surface point {node}")
            : UiText.T("Position available, surface feature unknown");
    }

    private static string FormatTaxiwayDesignator(string value)
    {
        if (string.Equals(value, "apron", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.T("APRON");
        }

        bool isCompactDesignator =
            value.Length == 1
            || value.Any(char.IsDigit) && value.All(char.IsLetterOrDigit);
        if (value.Contains(' ') || !isCompactDesignator)
        {
            return value.ToUpperInvariant();
        }

        return $"{value.ToUpperInvariant()} ({PhoneticAlphabet.SpellOut(value)})";
    }

    private static string FormatSignCue(TaxiwaySignCue cue)
    {
        string arrow = cue.Direction switch
        {
            TaxiTurnDirection.Left => "←",
            TaxiTurnDirection.Right => "→",
            _ => "↑",
        };
        string route = cue.IsClearedRoute ? UiText.T(" [ROUTE]") : string.Empty;
        return $"{cue.TaxiwayName.ToUpperInvariant()} {arrow}{route}";
    }

    private static string FormatDistance(double metres) =>
        metres >= 1000
            ? $"{metres / 1000:F1} km"
            : $"{Math.Max(0, metres):F0} m";

    private static string? RelevantAirportIcao(AtcSession session)
    {
        if (session.FlightPlan is null)
        {
            return null;
        }

        return session.Phase is StormATC.Domain.FlightPhase.Arrival
            or StormATC.Domain.FlightPhase.Approach
            or StormATC.Domain.FlightPhase.Final
            or StormATC.Domain.FlightPhase.Landed
            or StormATC.Domain.FlightPhase.TaxiIn
            or StormATC.Domain.FlightPhase.Parked
            or StormATC.Domain.FlightPhase.GoAround
            ? session.FlightPlan.DestinationIcao
            : session.FlightPlan.DepartureIcao;
    }

    private static string FormatClearance(AtcSession session)
    {
        if (session.IfrClearance is not Clearance clearance)
        {
            return UiText.T("No clearance issued");
        }

        var parts = new List<string>();

        if (clearance.ClearedToDestination is { Length: > 0 } destination)
        {
            parts.Add($"→ {destination}");
        }

        if (clearance.Runway is RunwayIdentifier runway)
        {
            parts.Add($"RWY {runway.ToDisplay()}");
        }

        if (session.ClearedAltitude is Altitude altitude)
        {
            parts.Add(altitude.ToDisplay());
        }

        if (clearance.Squawk is SquawkCode squawk)
        {
            parts.Add($"SQ {squawk.ToDisplay()}");
        }

        string summary = string.Join("  ·  ", parts);
        return clearance.IsAcknowledged ? summary : UiText.T($"{summary}  (readback outstanding)");
    }

    private static string FormatReadbackItem(ReadbackItemType type) => type switch
    {
        ReadbackItemType.Callsign => UiText.T("callsign"),
        ReadbackItemType.Runway => UiText.T("runway"),
        ReadbackItemType.InitialAltitude => UiText.T("initial altitude"),
        ReadbackItemType.CruiseAltitude => UiText.T("altitude"),
        ReadbackItemType.Squawk => UiText.T("squawk"),
        ReadbackItemType.Frequency => UiText.T("frequency"),
        ReadbackItemType.Heading => UiText.T("heading"),
        ReadbackItemType.HoldShortRunway => UiText.T("hold short"),
        ReadbackItemType.TaxiRoute => UiText.T("taxi route"),
        ReadbackItemType.DepartureProcedure => UiText.T("departure"),
        ReadbackItemType.ApproachName => UiText.T("approach"),
        _ => type.ToString(),
    };

    /// <summary>Inserts spaces into a PascalCase phase name for display.</summary>
    private static string FormatPhase(FlightPhase phase)
    {
        string name = phase.ToString();
        var builder = new System.Text.StringBuilder(name.Length + 4);

        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(name[i]);
        }

        return builder.ToString();
    }

    private static string FormatStage(MockFlightStage stage) => stage switch
    {
        MockFlightStage.AtStand => UiText.T("At stand"),
        MockFlightStage.PushedBack => UiText.T("Pushed back"),
        MockFlightStage.Taxiing => UiText.T("Taxiing out"),
        MockFlightStage.HoldingShort => UiText.T("Holding short"),
        MockFlightStage.LiningUp => UiText.T("Lining up"),
        MockFlightStage.TakeoffRoll => UiText.T("Takeoff roll"),
        MockFlightStage.Climbing => UiText.T("Climbing"),
        MockFlightStage.Cruise => UiText.T("Cruise"),
        MockFlightStage.Descending => UiText.T("Descending"),
        MockFlightStage.OnApproach => UiText.T("On approach"),
        MockFlightStage.ShortFinal => UiText.T("Short final"),
        MockFlightStage.Landed => UiText.T("Landed"),
        MockFlightStage.TaxiingIn => UiText.T("Taxiing in"),
        MockFlightStage.Parked => UiText.T("Parked"),
        _ => stage.ToString(),
    };

    private void SetSpeechState(SpeechState state, string text, string pushToTalkLabel)
    {
        SpeechState = state;
        SpeechStateText = text;
        PushToTalkLabel = pushToTalkLabel;
    }

    /// <summary>Marshals an action onto the dispatcher when it is not already there.</summary>
    private static void RunOnUiThread(Action action)
    {
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private async Task RunOnUiThreadAsync(Func<Task> action)
    {
        try
        {
            System.Windows.Threading.Dispatcher? dispatcher =
                System.Windows.Application.Current?.Dispatcher;

            if (dispatcher is null || dispatcher.CheckAccess())
            {
                await action().ConfigureAwait(true);
                return;
            }

            await dispatcher.InvokeAsync(action).Task.Unwrap().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A recognized speech transcript could not be submitted.");
            RunOnUiThread(() =>
            {
                IsBusy = false;
                IsTransmitting = false;
                _radioActivity.SetUserTransmitting(false);
                StatusMessage =
                    UiText.T("The recognized transmission could not be submitted. Check Diagnostics and try again.");
                SetSpeechState(SpeechState.Error, UiText.T("Transmission failed"), UiText.T("PUSH TO TALK"));
            });
        }
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
        _coordinator.TransmissionProcessed -= OnTransmissionProcessed;
        _simulatorRouter.AircraftStateChanged -= OnAircraftStateChanged;
        _simulatorRouter.ConnectionChanged -= OnSimulatorConnectionChanged;
        _trafficRadio.ExchangeGenerated -= OnTrafficRadioExchangeGenerated;
        _speechRecognition.InterimTranscriptReceived -= OnInterimTranscript;
        _speechRecognition.FinalTranscriptReceived -= OnFinalTranscript;
        _speechRecognition.ErrorOccurred -= OnSpeechError;
        _speechRecognition.AudioLevelChanged -= OnAudioLevel;
        _audioOptionsSubscription?.Dispose();
        _trafficOptionsSubscription?.Dispose();
        _radioActivity.SetUserTransmitting(false);
    }
}
