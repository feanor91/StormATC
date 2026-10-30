using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Atis;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Application.Sessions;
using StormATC.Application.Weather;
using StormATC.Desktop.Services;
using StormATC.Domain;
using StormATC.Domain.Airports;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Desktop.ViewModels;

/// <summary>
/// The Radio page: which frequency the session is on, what the aircraft has tuned, and the
/// audio treatment applied to the controller voice.
/// </summary>
/// <remarks>
/// <para>
/// The frequency the aircraft has tuned comes from the simulator and is authoritative. The
/// frequency the session expects comes from the initial ATIS selection or the last handoff the
/// engine issued. Startup may tune COM1 to ATIS once; subsequent frequency changes remain pilot
/// actions. When the selected transmitter and assigned frequency disagree, the controller
/// cannot hear the pilot.
/// </para>
/// </remarks>
public sealed partial class RadioViewModel : ObservableObject, IRefreshablePage, IDisposable
{
    private readonly AtcSessionContext _sessionContext;
    private readonly MockSimulatorConnector _mockSimulator;
    private readonly SimulatorConnectorRouter _simulatorRouter;
    private readonly ITextToSpeechService _textToSpeech;
    private readonly AtisBroadcastService _atisBroadcastService;
    private readonly UserSettingsService _userSettings;
    private readonly AudioOptions _audioOptions;
    private readonly AirportBriefingService _briefingService;

    private bool _disposed;

    [ObservableProperty]
    private string _listenIcao = string.Empty;

    [ObservableProperty]
    private string _listenFrequency = string.Empty;

    [ObservableProperty]
    private string _briefingStatus = UiText.T(
        "Type an aerodrome to read its weather and listen to its ATIS, even from the simulator's menus.");

    [ObservableProperty]
    private string _briefingMetar = string.Empty;

    [ObservableProperty]
    private string _briefingAtisText = string.Empty;

    [ObservableProperty]
    private bool _isReadingBriefing;

    [ObservableProperty]
    private string _sessionFrequency = "—";

    [ObservableProperty]
    private string _sessionController = "—";

    [ObservableProperty]
    private string _tunedFrequency = "—";

    [ObservableProperty]
    private bool _frequencyMismatch;

    [ObservableProperty]
    private string _mismatchMessage = string.Empty;

    [ObservableProperty]
    private string _handoffStatus = UiText.T("No handoff in progress");

    [ObservableProperty]
    private string _atisInformation = "—";

    [ObservableProperty]
    private string _atisText = UiText.T("Tune the published departure ATIS after loading a flight plan.");

    [ObservableProperty]
    private string _atisStatus = UiText.T("Waiting for departure ATIS.");

    [ObservableProperty]
    private string _atisWeatherSource = string.Empty;

    [ObservableProperty]
    private bool _hasPublishedFrequencies;

    [ObservableProperty]
    private string _frequencyDataMessage =
        UiText.T("No local airport frequency data is available for this flight.");

    [ObservableProperty]
    private double _controllerVolume;

    [ObservableProperty]
    private bool _radioEffectEnabled;

    [ObservableProperty]
    private double _radioEffectStrength;

    [ObservableProperty]
    private double _staticLevel;

    [ObservableProperty]
    private bool _transmissionClicksEnabled;

    [ObservableProperty]
    private bool _cleanAudioMode;

    [ObservableProperty]
    private bool _muted;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>Creates the radio view model.</summary>
    public RadioViewModel(
        AtcSessionContext sessionContext,
        MockSimulatorConnector mockSimulator,
        SimulatorConnectorRouter simulatorRouter,
        ITextToSpeechService textToSpeech,
        AtisBroadcastService atisBroadcastService,
        UserSettingsService userSettings,
        IOptions<AudioOptions> audioOptions,
        AirportBriefingService briefingService)
    {
        _briefingService = briefingService ?? throw new ArgumentNullException(nameof(briefingService));
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _mockSimulator = mockSimulator ?? throw new ArgumentNullException(nameof(mockSimulator));
        _simulatorRouter = simulatorRouter ?? throw new ArgumentNullException(nameof(simulatorRouter));
        _textToSpeech = textToSpeech ?? throw new ArgumentNullException(nameof(textToSpeech));
        _atisBroadcastService = atisBroadcastService
            ?? throw new ArgumentNullException(nameof(atisBroadcastService));
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        _audioOptions = audioOptions?.Value ?? throw new ArgumentNullException(nameof(audioOptions));

        _controllerVolume = _audioOptions.ControllerVolume;
        _radioEffectEnabled = _audioOptions.RadioEffectEnabled;
        _radioEffectStrength = _audioOptions.RadioEffectStrength;
        _staticLevel = _audioOptions.StaticLevel;
        _transmissionClicksEnabled = _audioOptions.TransmissionClicksEnabled;
        _cleanAudioMode = _audioOptions.CleanAudioMode;
        _muted = _audioOptions.Muted;

        AvailableFrequencies = [];

        _sessionContext.SessionChanged += OnSessionChanged;
        _atisBroadcastService.BroadcastChanged += OnAtisBroadcastChanged;
        UpdateFromSession(_sessionContext.CurrentSession);
        UpdateAtisBroadcast();
    }

    /// <summary>Every published frequency at the departure aerodrome.</summary>
    public ObservableCollection<FrequencyEntry> AvailableFrequencies { get; }

    /// <summary>The report of the typed aerodrome, line by line.</summary>
    public ObservableCollection<MetarLine> BriefingLines { get; } = [];

    /// <summary>The push-to-talk key, shown so the binding is discoverable.</summary>
    public string PushToTalkKey => _audioOptions.PushToTalkKey;

    /// <summary>
    /// True when the mock simulator can be retuned from here.
    /// </summary>
    /// <remarks>
    /// The published-frequency buttons remain mock-only. The real simulator receives only the
    /// automatic initial ATIS tune; subsequent changes are made in the aircraft.
    /// </remarks>
    public bool CanTuneRadio => _simulatorRouter.IsMockActive;

    /// <inheritdoc />
    public async Task RefreshAsync()
    {
        UpdateFromSession(_sessionContext.CurrentSession);
        await LoadFrequenciesAsync().ConfigureAwait(true);

        // The aerodrome the pilot is about to leave is the one they most likely want to read.
        if (string.IsNullOrEmpty(ListenIcao)
            && _sessionContext.CurrentSession.FlightPlan?.DepartureIcao is { Length: > 0 } departure)
        {
            ListenIcao = departure;
        }
    }

    /// <summary>Fetches the METAR of the typed aerodrome and decodes it.</summary>
    [RelayCommand]
    private Task ReadWeatherAsync() => LoadBriefingAsync(speak: false);

    /// <summary>Reads out the ATIS of the typed aerodrome, made from its current METAR.</summary>
    [RelayCommand]
    private Task ListenToAtisAsync() => LoadBriefingAsync(speak: true);

    /// <summary>Stops the ATIS being read out.</summary>
    [RelayCommand]
    private async Task StopListeningAsync() =>
        await _textToSpeech.StopAsync(CancellationToken.None).ConfigureAwait(true);

    private async Task LoadBriefingAsync(bool speak)
    {
        Frequency? frequency = null;
        if (ListenFrequency.Trim() is { Length: > 0 } typed)
        {
            if (!decimal.TryParse(
                    typed.Replace(',', '.'),
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out decimal megahertz)
                || !Frequency.TryFromMegahertz(megahertz, out Frequency parsed))
            {
                BriefingStatus = UiText.T("That is not a frequency. Use 118.000 to 136.975, for example 127.825.");
                return;
            }

            frequency = parsed;
        }

        IsReadingBriefing = true;
        BriefingStatus = UiText.T("Fetching the weather…");
        try
        {
            AirportBriefing briefing = await _briefingService
                .GetAsync(
                    ListenIcao,
                    frequency,
                    _sessionContext.CurrentSession.Language,
                    CancellationToken.None)
                .ConfigureAwait(true);

            ListenIcao = briefing.Icao;
            if (briefing.Frequency is Frequency resolved)
            {
                ListenFrequency = resolved.ToDisplay();
            }

            BriefingStatus = briefing.Message;
            BriefingMetar = briefing.RawMetar ?? string.Empty;
            BriefingLines.Clear();
            foreach (MetarLine line in briefing.Lines)
            {
                BriefingLines.Add(line);
            }

            BriefingAtisText = briefing.Atis?.DisplayText ?? string.Empty;

            if (speak && briefing.Atis is { } atis)
            {
                bool played = await _textToSpeech
                    .SpeakCachedAsync(
                        $"manual-atis:{atis.AirportIcao}:{atis.SpokenText.GetHashCode(StringComparison.Ordinal)}",
                        atis.SpokenText,
                        VoiceConfiguration.For(atis.Language),
                        CancellationToken.None)
                    .ConfigureAwait(true);
                if (!played)
                {
                    BriefingStatus = UiText.T(
                        "ATIS text is ready, but voice audio is unavailable. Check Deepgram, mute, and cloud settings.");
                }
            }
        }
        finally
        {
            IsReadingBriefing = false;
        }
    }

    /// <summary>Tunes the mock aircraft's COM1 to a published frequency.</summary>
    [RelayCommand]
    private void TuneTo(FrequencyEntry? entry)
    {
        if (entry is null || !CanTuneRadio)
        {
            return;
        }

        _mockSimulator.SetCom1Active(entry.Frequency);
        StatusMessage = UiText.T($"COM1 tuned to {entry.Frequency.ToDisplay()}.");
        UpdateFromSession(_sessionContext.CurrentSession);
    }

    /// <summary>Replays the last controller transmission.</summary>
    [RelayCommand]
    private async Task ReplayLastAsync() =>
        await _textToSpeech.ReplayLastAsync(CancellationToken.None).ConfigureAwait(true);

    /// <summary>Replays the current ATIS from the speech service's in-memory audio cache.</summary>
    [RelayCommand]
    private async Task ReplayAtisAsync() =>
        await _atisBroadcastService.ReplayAsync(CancellationToken.None).ConfigureAwait(true);

    /// <summary>Persists the audio settings to the user settings file.</summary>
    [RelayCommand]
    private async Task SaveAudioSettingsAsync()
    {
        await _userSettings.SaveManyAsync(
            [
                (AudioOptions.SectionName, "ControllerVolume", JsonValue.Create(Math.Round(ControllerVolume, 2))),
                (AudioOptions.SectionName, "RadioEffectEnabled", JsonValue.Create(RadioEffectEnabled)),
                (AudioOptions.SectionName, "RadioEffectStrength", JsonValue.Create(Math.Round(RadioEffectStrength, 2))),
                (AudioOptions.SectionName, "StaticLevel", JsonValue.Create(Math.Round(StaticLevel, 2))),
                (AudioOptions.SectionName, "TransmissionClicksEnabled", JsonValue.Create(TransmissionClicksEnabled)),
                (AudioOptions.SectionName, "CleanAudioMode", JsonValue.Create(CleanAudioMode)),
                (AudioOptions.SectionName, "Muted", JsonValue.Create(Muted)),
            ],
            CancellationToken.None).ConfigureAwait(true);

        StatusMessage = UiText.T("Audio settings saved. They take effect on the next transmission.");
    }

    private async Task LoadFrequenciesAsync()
    {
        AtcSession session = _sessionContext.CurrentSession;
        AvailableFrequencies.Clear();
        HasPublishedFrequencies = false;

        if (session.FlightPlan?.DepartureIcao is not { Length: > 0 } icao)
        {
            FrequencyDataMessage = UiText.T("Import a flight plan to load its airport frequencies.");
            return;
        }

        AirportData? airport = await _sessionContext
            .GetAirportAsync(icao, CancellationToken.None)
            .ConfigureAwait(true);

        if (airport is null)
        {
            FrequencyDataMessage =
                UiText.T($"StormATC has no bundled frequency data for {icao}. ")
                + UiText.T("It will keep local positions combined and never invent a frequency.");
            return;
        }

        AddFrequency(UiText.T("Clearance delivery"), airport.Frequencies.ClearanceDelivery);
        AddFrequency(UiText.T("Ground"), airport.Frequencies.Ground);
        AddFrequency(UiText.T("Tower"), airport.Frequencies.Tower);
        AddFrequency(UiText.T("Departure"), airport.Frequencies.Departure);
        AddFrequency(UiText.T("Approach"), airport.Frequencies.Approach);
        AddFrequency(UiText.T("Center"), airport.Frequencies.Center);
        AddFrequency("ATIS", airport.Frequencies.Atis);
        HasPublishedFrequencies = AvailableFrequencies.Count > 0;
        FrequencyDataMessage = HasPublishedFrequencies
            ? string.Empty
            : UiText.T($"The bundled {icao} airport file does not publish radio frequencies.");

        void AddFrequency(string label, Frequency? frequency)
        {
            if (frequency is Frequency value)
            {
                AvailableFrequencies.Add(new FrequencyEntry(label, value));
            }
        }
    }

    private void OnSessionChanged(object? sender, AtcSession session) =>
        RunOnUiThread(() => UpdateFromSession(session));

    private void OnAtisBroadcastChanged(object? sender, EventArgs eventArgs) =>
        RunOnUiThread(UpdateAtisBroadcast);

    private void UpdateAtisBroadcast()
    {
        AtisBroadcast? broadcast = _atisBroadcastService.CurrentBroadcast;
        AtisInformation = broadcast is null ? "—" : broadcast.InformationCode;
        AtisText = broadcast?.DisplayText
            ?? UiText.T("Tune the published departure ATIS after loading a flight plan.");
        AtisStatus = _atisBroadcastService.StatusMessage;
        AtisWeatherSource = broadcast?.WeatherSource is { Length: > 0 } source
            ? UiText.T($"Weather source: {source}")
            : string.Empty;
    }

    private void UpdateFromSession(AtcSession session)
    {
        Frequency tuned = session.AircraftState.Radio.ActiveTransmitFrequency;
        SessionFrequency = session.CurrentFrequency?.ToDisplay() ?? tuned.ToDisplay();

        SessionController = session.CurrentController == ControllerPosition.None
            ? "—"
            : Domain.Phraseology.DeterministicPhraseology.SpokenControllerName(session.CurrentController);

        TunedFrequency = tuned.ToDisplay();

        // The engine never assumes the pilot changed frequency. Saying so plainly is more
        // useful than quietly correcting it.
        FrequencyMismatch = session.CurrentFrequency is Frequency expected && expected != tuned;

        MismatchMessage = FrequencyMismatch
            ? UiText.T(
                $"The aircraft is tuned to {tuned.ToDisplay()} but the session is on "
                + $"{session.CurrentFrequency?.ToDisplay()}. Retune COM1 to be heard.")
            : string.Empty;

        HandoffStatus = session.HandoffState switch
        {
            HandoffState.Instructed => session.HandoffHistory.Count > 0
                ? UiText.T($"Instructed to contact {DescribeHandoff(session.HandoffHistory[^1])} — check in when ready.")
                : UiText.T("A frequency change has been issued."),
            HandoffState.Acknowledged => UiText.T("Frequency change read back. Check in on the new frequency."),
            HandoffState.CheckedIn => UiText.T("Checked in with the new controller."),
            _ => UiText.T("No handoff in progress"),
        };
    }

    private static string DescribeHandoff(ControllerHandoff handoff) =>
        UiText.T(
            $"{UiText.T(Domain.Phraseology.DeterministicPhraseology.SpokenControllerName(handoff.ToController))} "
            + $"on {handoff.ToFrequency.ToDisplay()}");

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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionContext.SessionChanged -= OnSessionChanged;
        _atisBroadcastService.BroadcastChanged -= OnAtisBroadcastChanged;
    }
}

/// <summary>One published frequency at an aerodrome.</summary>
public sealed record FrequencyEntry(string Label, Frequency Frequency)
{
    /// <summary>The frequency formatted for display.</summary>
    public string Display => Frequency.ToDisplay();
}
