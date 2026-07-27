using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Guidance;
using StormATC.Application.Options;
using StormATC.Application.Sessions;
using StormATC.Application.Transmissions;
using StormATC.Desktop.Services;
using StormATC.Domain;
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
    private readonly ISpeechRecognitionService _speechRecognition;
    private readonly ITextToSpeechService _textToSpeech;
    private readonly SimulatorConnectorRouter _simulatorRouter;
    private readonly MockSimulatorConnector _mockSimulator;
    private readonly MockTrafficProvider _mockTraffic;
    private readonly ProviderStatusService _providerStatus;
    private readonly IClock _clock;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly IOptionsMonitor<ApplicationOptions> _applicationOptions;
    private readonly IOptionsMonitor<AudioOptions> _audioOptions;
    private readonly IDisposable? _audioOptionsSubscription;

    private bool _disposed;

    // --- Session state -------------------------------------------------------------------

    [ObservableProperty]
    private string _callsign = "—";

    [ObservableProperty]
    private string _routeSummary = "No flight plan loaded";

    [ObservableProperty]
    private string _currentController = "—";

    [ObservableProperty]
    private string _currentFrequency = "—";

    [ObservableProperty]
    private string _frequencySource = "No frequency assigned";

    [ObservableProperty]
    private string _tunedFrequency = "—";

    [ObservableProperty]
    private string _frequencyStatus = string.Empty;

    [ObservableProperty]
    private bool _hasFrequencyMismatch;

    [ObservableProperty]
    private string _flightPhase = "Preflight";

    [ObservableProperty]
    private string _altitude = "—";

    [ObservableProperty]
    private string _heading = "—";

    [ObservableProperty]
    private string _groundSpeed = "—";

    [ObservableProperty]
    private string _activeRunway = "—";

    [ObservableProperty]
    private string _squawk = "—";

    [ObservableProperty]
    private string _activeClearance = "No clearance issued";

    [ObservableProperty]
    private string _pendingReadback = "None";

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
    private string _nextCallTitle = "Import a flight plan";

    [ObservableProperty]
    private string _nextCallDetail = string.Empty;

    [ObservableProperty]
    private string _nextCallExample = string.Empty;

    [ObservableProperty]
    private bool _hasNextCallExample;

    // --- Speech and PTT ------------------------------------------------------------------

    [ObservableProperty]
    private SpeechState _speechState = SpeechState.Idle;

    [ObservableProperty]
    private string _speechStateText = "Ready";

    [ObservableProperty]
    private string _pushToTalkLabel = "PUSH TO TALK";

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
    private string _mockStageText = "At stand";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Creates the dashboard view model.</summary>
    public DashboardViewModel(
        AtcSessionContext sessionContext,
        PilotTransmissionCoordinator coordinator,
        ISpeechRecognitionService speechRecognition,
        ITextToSpeechService textToSpeech,
        SimulatorConnectorRouter simulatorRouter,
        MockSimulatorConnector mockSimulator,
        MockTrafficProvider mockTraffic,
        ProviderStatusService providerStatus,
        IClock clock,
        IOptionsMonitor<ApplicationOptions> applicationOptions,
        IOptionsMonitor<AudioOptions> audioOptions,
        ILogger<DashboardViewModel> logger)
    {
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _speechRecognition = speechRecognition ?? throw new ArgumentNullException(nameof(speechRecognition));
        _textToSpeech = textToSpeech ?? throw new ArgumentNullException(nameof(textToSpeech));
        _simulatorRouter = simulatorRouter ?? throw new ArgumentNullException(nameof(simulatorRouter));
        _mockSimulator = mockSimulator ?? throw new ArgumentNullException(nameof(mockSimulator));
        _mockTraffic = mockTraffic ?? throw new ArgumentNullException(nameof(mockTraffic));
        _providerStatus = providerStatus ?? throw new ArgumentNullException(nameof(providerStatus));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _applicationOptions = applicationOptions ?? throw new ArgumentNullException(nameof(applicationOptions));
        _audioOptions = audioOptions ?? throw new ArgumentNullException(nameof(audioOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _audioOptionsSubscription = _audioOptions.OnChange(
            _ => RunOnUiThread(() => OnPropertyChanged(nameof(PushToTalkHint))));

        _sessionContext.SessionChanged += OnSessionChanged;
        _coordinator.TransmissionProcessed += OnTransmissionProcessed;
        _simulatorRouter.AircraftStateChanged += OnAircraftStateChanged;
        _simulatorRouter.ConnectionChanged += OnSimulatorConnectionChanged;

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
    public string ModeBadge => IsMockMode ? "MOCK" : "LIVE";

    /// <summary>Explains every active push-to-talk input, including the configured keyboard key.</summary>
    public string PushToTalkHint =>
        $"Hold the dashboard button, {_audioOptions.CurrentValue.PushToTalkKey}, "
        + "or your bound joystick button. Release to send.";

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

        IsTransmitting = true;
        SetSpeechState(SpeechState.Connecting, "Connecting…", "RELEASE TO SEND");
        InterimTranscript = string.Empty;

        try
        {
            await _speechRecognition.StartTransmissionAsync(CancellationToken.None)
                .ConfigureAwait(true);

            // The service raises an error event when it is unconfigured, which resets the state.
            if (SpeechState == SpeechState.Connecting)
            {
                SetSpeechState(SpeechState.Listening, "Listening…", "RELEASE TO SEND");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start a transmission.");
            SetSpeechState(SpeechState.Error, "Could not start capture", "PUSH TO TALK");
            IsTransmitting = false;
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
        SetSpeechState(SpeechState.Processing, "Processing…", "PUSH TO TALK");

        try
        {
            await _speechRecognition.StopTransmissionAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop the transmission.");
            SetSpeechState(SpeechState.Error, "Capture failed", "PUSH TO TALK");
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
            StatusMessage = "There is no suggested call to copy yet.";
            return;
        }

        try
        {
            Clipboard.SetText(text);
            StatusMessage = "Suggested call copied to the clipboard.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The suggested call could not be copied.");
            StatusMessage = "The suggested call could not be copied.";
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

    private bool CanSendManualTransmission() => !IsBusy && !string.IsNullOrWhiteSpace(ManualTransmissionText);

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
        StatusMessage = "Mock simulator reset to the stand.";
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
            StatusMessage = "No departure runway is assigned yet.";
            return;
        }

        _mockTraffic.OccupyRunway(runway, session.AircraftState.Position);
        StatusMessage =
            $"Traffic placed on runway {runway.ToDisplay()}. Takeoff clearance will be refused.";
    }

    /// <summary>Removes the injected traffic, so takeoff clearance becomes available again.</summary>
    [RelayCommand]
    private void ClearMockTraffic()
    {
        _mockTraffic.Clear();
        StatusMessage = "The runway is clear of injected traffic.";
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
        SetSpeechState(SpeechState.Processing, "Processing…", "PUSH TO TALK");
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
                SetSpeechState(SpeechState.Error, "Processing failed", "PUSH TO TALK");
                return;
            }

            if (result.ControllerSpokenText is { Length: > 0 } spoken)
            {
                SetSpeechState(SpeechState.PlayingAudio, "Controller responding…", "PUSH TO TALK");

                await _textToSpeech
                    .SpeakAsync(spoken, VoiceConfiguration.Default, CancellationToken.None)
                    .ConfigureAwait(true);
            }

            SetSpeechState(SpeechState.Idle, "Ready", "PUSH TO TALK");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The pilot transmission pipeline failed.");
            StatusMessage =
                "The transmission could not be completed. Check Diagnostics and try again.";
            SetSpeechState(SpeechState.Error, "Transmission failed", "PUSH TO TALK");
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
            SetSpeechState(SpeechState.Error, e.Message, "PUSH TO TALK");

            StatusMessage = e.SuggestedAction is { Length: > 0 } action
                ? $"{e.Message} {action}"
                : e.Message;
        });

    private void OnAudioLevel(object? sender, AudioLevelEventArgs e) =>
        RunOnUiThread(() => InputLevel = e.Level);

    /// <summary>Projects the session into display strings.</summary>
    private void UpdateFromSession(AtcSession session)
    {
        Callsign = session.Callsign?.Normalized ?? "—";
        RouteSummary = session.FlightPlan?.ToSummary() ?? "No flight plan loaded";
        CurrentController = session.CurrentController == ControllerPosition.None
            ? "—"
            : Domain.Phraseology.DeterministicPhraseology.SpokenControllerName(session.CurrentController);

        Frequency tuned = session.AircraftState.Radio.ActiveTransmitFrequency;
        TunedFrequency = tuned.ToDisplay();
        CurrentFrequency = session.CurrentFrequency?.ToDisplay() ?? tuned.ToDisplay();
        FrequencySource = session.CurrentFrequency is null
            ? "Tuned COM — no local frequency data"
            : "ATC assigned";
        HasFrequencyMismatch =
            session.CurrentFrequency is Frequency assigned && assigned != tuned;
        FrequencyStatus = HasFrequencyMismatch
            ? $"Tune COM to {session.CurrentFrequency?.ToDisplay()} before transmitting."
            : session.CurrentFrequency is null
                ? "StormATC is using your active transmit radio."
                : "Radio tuned correctly.";

        FlightPhase = FormatPhase(session.Phase);

        Altitude = session.AircraftState.IndicatedAltitudeFeet > 0
            ? $"{session.AircraftState.IndicatedAltitudeRounded:N0} ft"
            : "—";

        Heading = session.AircraftState.MagneticHeading.ToDisplay();
        GroundSpeed = $"{session.AircraftState.GroundSpeedKnots:F0} kt";
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
            : "None";

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

        MockStageText = FormatStage(_mockSimulator.Stage);
    }

    private static string FormatClearance(AtcSession session)
    {
        if (session.IfrClearance is not Clearance clearance)
        {
            return "No clearance issued";
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
        return clearance.IsAcknowledged ? summary : $"{summary}  (readback outstanding)";
    }

    private static string FormatReadbackItem(ReadbackItemType type) => type switch
    {
        ReadbackItemType.Callsign => "callsign",
        ReadbackItemType.Runway => "runway",
        ReadbackItemType.InitialAltitude => "initial altitude",
        ReadbackItemType.CruiseAltitude => "altitude",
        ReadbackItemType.Squawk => "squawk",
        ReadbackItemType.Frequency => "frequency",
        ReadbackItemType.Heading => "heading",
        ReadbackItemType.HoldShortRunway => "hold short",
        ReadbackItemType.TaxiRoute => "taxi route",
        ReadbackItemType.DepartureProcedure => "departure",
        ReadbackItemType.ApproachName => "approach",
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
        MockFlightStage.AtStand => "At stand",
        MockFlightStage.PushedBack => "Pushed back",
        MockFlightStage.Taxiing => "Taxiing out",
        MockFlightStage.HoldingShort => "Holding short",
        MockFlightStage.LiningUp => "Lining up",
        MockFlightStage.TakeoffRoll => "Takeoff roll",
        MockFlightStage.Climbing => "Climbing",
        MockFlightStage.Cruise => "Cruise",
        MockFlightStage.Descending => "Descending",
        MockFlightStage.OnApproach => "On approach",
        MockFlightStage.ShortFinal => "Short final",
        MockFlightStage.Landed => "Landed",
        MockFlightStage.TaxiingIn => "Taxiing in",
        MockFlightStage.Parked => "Parked",
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
                StatusMessage =
                    "The recognized transmission could not be submitted. Check Diagnostics and try again.";
                SetSpeechState(SpeechState.Error, "Transmission failed", "PUSH TO TALK");
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
        _speechRecognition.InterimTranscriptReceived -= OnInterimTranscript;
        _speechRecognition.FinalTranscriptReceived -= OnFinalTranscript;
        _speechRecognition.ErrorOccurred -= OnSpeechError;
        _speechRecognition.AudioLevelChanged -= OnAudioLevel;
        _audioOptionsSubscription?.Dispose();
    }
}
