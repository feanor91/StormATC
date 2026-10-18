using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using StormATC.Application.Abstractions;
using StormATC.Application.FlightPlanning;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Application.Sessions;
using StormATC.Desktop.Services;
using StormATC.Domain;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;
using StormATC.Infrastructure.FlightPlanning;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Desktop.ViewModels;

/// <summary>
/// The Flight Plan page: import, review, and confirm the plan the session runs against.
/// </summary>
/// <remarks>
/// Nothing here changes ATC state. The plan is a set of intentions; what the aircraft is actually
/// cleared to do comes from the engine, and only from the engine.
/// </remarks>
public sealed partial class FlightPlanViewModel : ObservableObject, IRefreshablePage, IDisposable
{
    private readonly AtcSessionContext _sessionContext;
    private readonly FlightPlanProviderRouter _flightPlanProvider;
    private readonly SimulatorConnectorRouter _simulatorConnector;
    private readonly ProviderStatusService _providerStatus;
    private readonly UserSettingsService _userSettings;
    private readonly SimFlightPlanService _simFlightPlan;
    private readonly IClock _clock;
    private readonly SimBriefOptions _simBriefOptions;
    private readonly ILogger<FlightPlanViewModel> _logger;

    private bool _disposed;

    private bool _syncingFlightRules;

    /// <summary>The aerodrome of a local VFR flight, as an ICAO code such as LFPL.</summary>
    [ObservableProperty]
    private string _vfrAerodrome = string.Empty;

    /// <summary>Start this flight again each time StormATC opens with the simulator running.</summary>
    [ObservableProperty]
    private bool _vfrAutoStart = true;

    /// <summary>The callsign of a local VFR flight; offered from the simulator's aircraft identifier.</summary>
    [ObservableProperty]
    private string _vfrCallsign = string.Empty;

    /// <summary>
    /// Imports the flight plan made in the simulator (the EFB, the world map, or the flight that
    /// was just started): the plan the simulator announced last, or its newest plan file.
    /// </summary>
    [RelayCommand]
    private async Task ImportSimulatorPlanAsync()
    {
        IsImporting = true;
        StatusIsError = false;

        try
        {
            SimFlightPlanImportResult result = await _simFlightPlan
                .ImportLatestAsync(CancellationToken.None)
                .ConfigureAwait(true);

            StatusIsError = !result.Imported;
            StatusMessage = result.Message;
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>Imports a <c>.PLN</c> flight plan file the pilot picks, such as one saved from the EFB.</summary>
    [RelayCommand]
    private async Task OpenPlnFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = UiText.T("Open a flight plan file"),
            Filter = UiText.T("Flight plans (*.pln)|*.pln|All files|*.*"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsImporting = true;
        StatusIsError = false;

        try
        {
            SimFlightPlanImportResult result = await _simFlightPlan
                .ImportPlnFileAsync(dialog.FileName, CancellationToken.None)
                .ConfigureAwait(true);

            StatusIsError = !result.Imported;
            StatusMessage = result.Message;
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// Starts a local VFR flight at the aerodrome the pilot is on, with no flight plan: the plan
    /// is only the aerodrome and the callsign, and the flight is VFR from the first call.
    /// </summary>
    [RelayCommand]
    private async Task StartVfrLocalFlightAsync()
    {
        if (!VfrLocalFlight.TryCreatePlan(VfrAerodrome, VfrCallsign, _clock.UtcNow, out FlightPlan? plan, out string? problem))
        {
            StatusIsError = true;
            StatusMessage = problem == "aerodrome"
                ? UiText.T("Enter the four-letter ICAO code of the aerodrome, such as LFPL.")
                : UiText.T("Enter the callsign or registration of your aircraft, such as F-GABC.");
            return;
        }

        string icao = plan.DepartureIcao;

        IsImporting = true;
        StatusIsError = false;

        try
        {
            _sessionContext.PreassignedSquawk = null;
            AtcSession session = await _sessionContext
                .LoadFlightPlanAsync(plan, CancellationToken.None)
                .ConfigureAwait(true);
            session = _sessionContext.Mutate(current => current.WithFlightRules(FlightRules.Vfr));

            _providerStatus.SetFlightPlanStatus(true, $"{plan.ToSummary()} · VFR local");

            // Remembered, so the next launch with the simulator running starts it again.
            await _userSettings
                .SaveManyAsync(
                    [
                        (VfrLocalOptions.SectionName, "Aerodrome", JsonValue.Create(icao)),
                        (VfrLocalOptions.SectionName, "Callsign", JsonValue.Create(plan.Callsign.Original)),
                        (VfrLocalOptions.SectionName, "AutoStart", JsonValue.Create(VfrAutoStart)),
                    ],
                    CancellationToken.None)
                .ConfigureAwait(true);

            StatusMessage = session.CurrentFrequency is Frequency assigned
                ? UiText.T($"Local VFR flight at {icao}. ")
                    + UiText.T($"Tune the frequencies yourself in the aircraft, starting with {assigned.ToDisplay()}: StormATC follows the one you tune.")
                : UiText.T($"Local VFR flight at {icao}. No frequencies were found for this aerodrome: connect the simulator so StormATC can read them.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Starting the local VFR flight failed.");
            StatusIsError = true;
            StatusMessage = UiText.T("The local VFR flight could not be started. See the Diagnostics page for details.");
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>The flight rules the pilot can choose between.</summary>
    public IReadOnlyList<string> FlightRulesChoices { get; } = ["IFR", "VFR"];

    /// <summary>
    /// The rules the flight is operated under. A VFR flight needs no IFR clearance and stays with
    /// Tower in the circuit. The pilot can also declare VFR on the radio.
    /// </summary>
    [ObservableProperty]
    private string _selectedFlightRules = "IFR";

    partial void OnSelectedFlightRulesChanged(string value)
    {
        if (_syncingFlightRules)
        {
            return;
        }

        FlightRules rules = string.Equals(value, "VFR", StringComparison.Ordinal) ? FlightRules.Vfr : FlightRules.Ifr;
        _sessionContext.Mutate(session => session.WithFlightRules(rules));
    }

    [ObservableProperty]
    private string _callsign = "—";

    [ObservableProperty]
    private string _aircraftType = "—";

    [ObservableProperty]
    private string _departure = "—";

    [ObservableProperty]
    private string _destination = "—";

    [ObservableProperty]
    private string _alternates = "—";

    [ObservableProperty]
    private string _cruiseAltitude = "—";

    [ObservableProperty]
    private string _departureRunway = "—";

    [ObservableProperty]
    private string _arrivalRunway = "—";

    [ObservableProperty]
    private string _departureProcedure = "—";

    [ObservableProperty]
    private string _arrivalProcedure = "—";

    [ObservableProperty]
    private string _route = UiText.T("No route filed");

    [ObservableProperty]
    private string _blockTime = "—";

    [ObservableProperty]
    private string _source = "—";

    [ObservableProperty]
    private string _generatedAt = "—";

    [ObservableProperty]
    private bool _isStale;

    [ObservableProperty]
    private string _staleMessage = string.Empty;

    [ObservableProperty]
    private bool _hasPlan;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _statusIsError;

    [ObservableProperty]
    private string _simBriefUserId = string.Empty;

    /// <summary>Creates the flight plan view model.</summary>
    public FlightPlanViewModel(
        AtcSessionContext sessionContext,
        FlightPlanProviderRouter flightPlanProvider,
        SimulatorConnectorRouter simulatorConnector,
        ProviderStatusService providerStatus,
        UserSettingsService userSettings,
        SimFlightPlanService simFlightPlan,
        IClock clock,
        IOptions<SimBriefOptions> simBriefOptions,
        ILogger<FlightPlanViewModel> logger)
    {
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _flightPlanProvider = flightPlanProvider ?? throw new ArgumentNullException(nameof(flightPlanProvider));
        _simulatorConnector =
            simulatorConnector ?? throw new ArgumentNullException(nameof(simulatorConnector));
        _providerStatus = providerStatus ?? throw new ArgumentNullException(nameof(providerStatus));
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        _simFlightPlan = simFlightPlan ?? throw new ArgumentNullException(nameof(simFlightPlan));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _simBriefOptions = simBriefOptions?.Value ?? throw new ArgumentNullException(nameof(simBriefOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _simBriefUserId = _simBriefOptions.UserId ?? string.Empty;

        RouteLegs = [];
        Warnings = [];

        _sessionContext.SessionChanged += OnSessionChanged;
        UpdateFromSession(_sessionContext.CurrentSession);
    }

    /// <summary>The filed route, leg by leg.</summary>
    public ObservableCollection<RouteLegDisplay> RouteLegs { get; }

    /// <summary>Non-fatal problems reported by the importer.</summary>
    public ObservableCollection<string> Warnings { get; }

    /// <summary>
    /// What the import button will actually contact, given what is configured right now.
    /// </summary>
    /// <remarks>
    /// Recomputed rather than fixed at construction: typing a SimBrief user ID changes the
    /// answer, and the button should say so before it is pressed rather than afterwards.
    /// </remarks>
    public string ProviderName =>
        string.IsNullOrWhiteSpace(SimBriefUserId) ? _flightPlanProvider.ProviderName : "SimBrief";

    /// <summary>True when SimBrief is what an import would use.</summary>
    public bool UsesSimBrief => !string.IsNullOrWhiteSpace(SimBriefUserId);

    /// <summary>The label on the import button.</summary>
    public string ImportButtonText =>
        UsesSimBrief ? UiText.T("Import latest SimBrief plan") : UiText.T("Load the bundled sample flight");

    /// <inheritdoc />
    public Task RefreshAsync()
    {
        UpdateFromSession(_sessionContext.CurrentSession);
        return Task.CompletedTask;
    }

    partial void OnSimBriefUserIdChanged(string value)
    {
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(UsesSimBrief));
        OnPropertyChanged(nameof(ImportButtonText));
    }

    /// <summary>Imports the latest plan and attaches it to the session.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        IsImporting = true;
        StatusIsError = false;
        StatusMessage = UsesSimBrief
            ? UiText.T("Fetching your latest flight plan from SimBrief…")
            : UiText.T("Loading the bundled sample flight…");

        try
        {
            string? userId = string.IsNullOrWhiteSpace(SimBriefUserId)
                ? _simBriefOptions.UserId
                : SimBriefUserId.Trim();

            if (!string.IsNullOrWhiteSpace(userId))
            {
                // Saved before the import, not after, so a failed fetch still leaves the ID in
                // place to retry with. A SimBrief user ID is an account number, not a credential,
                // so it belongs in ordinary settings rather than the secret store.
                await _userSettings
                    .SaveAsync(SimBriefOptions.SectionName, "UserId", userId, CancellationToken.None)
                    .ConfigureAwait(true);
            }

            var request = new FlightPlanImportRequest
            {
                UserId = userId,
                ForceRefresh = true,
            };

            FlightPlanImportResult result = await _flightPlanProvider
                .ImportLatestAsync(request, CancellationToken.None)
                .ConfigureAwait(true);

            Warnings.Clear();
            foreach (string warning in result.Warnings)
            {
                Warnings.Add(warning);
            }

            if (!result.Success || result.FlightPlan is not FlightPlan plan)
            {
                StatusIsError = true;
                StatusMessage = result.SuggestedAction is { Length: > 0 } action
                    ? $"{result.ErrorMessage} {action}"
                    : result.ErrorMessage ?? UiText.T("The import failed.");

                _providerStatus.SetFlightPlanStatus(false, UiText.T("Import failed"));
                return;
            }

            // Only the sample scenario pins a transponder code. Reading it from the router rather
            // than testing the provider type means a SimBrief import correctly clears it.
            _sessionContext.PreassignedSquawk = _flightPlanProvider.PreassignedSquawk;

            // An imported plan replaces the remembered local flight at the next launch too.
            await _userSettings
                .SaveAsync(VfrLocalOptions.SectionName, "AutoStart", JsonValue.Create(false), CancellationToken.None)
                .ConfigureAwait(true);

            AtcSession session = await _sessionContext
                .LoadFlightPlanAsync(plan, CancellationToken.None)
                .ConfigureAwait(true);

            bool tuned = session.CurrentFrequency is not Frequency initialFrequency
                || await _simulatorConnector
                    .TuneCom1Async(initialFrequency, CancellationToken.None)
                    .ConfigureAwait(true);

            _providerStatus.SetFlightPlanStatus(
                true,
                $"{plan.ToSummary()} · {_flightPlanProvider.LastProviderUsed}");

            StatusMessage = tuned
                ? UiText.T($"Imported {plan.ToSummary()} from {_flightPlanProvider.LastProviderUsed}. ")
                    + UiText.T($"COM1 is tuned to {session.CurrentFrequency?.ToDisplay()}.")
                : UiText.T($"Imported {plan.ToSummary()} from {_flightPlanProvider.LastProviderUsed}. ")
                    + UiText.T($"Tune COM1 to {session.CurrentFrequency?.ToDisplay()} before transmitting.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The flight plan import failed.");
            StatusIsError = true;
            StatusMessage = UiText.T("The import failed. See the Diagnostics page for details.");
        }
        finally
        {
            IsImporting = false;
        }
    }

    private void OnSessionChanged(object? sender, AtcSession session) =>
        RunOnUiThread(() => UpdateFromSession(session));

    private void UpdateFromSession(AtcSession session)
    {
        if (string.IsNullOrWhiteSpace(VfrCallsign) && !string.IsNullOrWhiteSpace(session.AircraftState.AtcIdentifier))
        {
            VfrCallsign = session.AircraftState.AtcIdentifier;
        }

        if (session.FlightPlan is not FlightPlan plan)
        {
            HasPlan = false;
            RouteLegs.Clear();
            return;
        }

        HasPlan = true;

        _syncingFlightRules = true;
        try
        {
            SelectedFlightRules = session.FlightRules == FlightRules.Vfr ? "VFR" : "IFR";
        }
        finally
        {
            _syncingFlightRules = false;
        }

        Callsign = plan.Callsign.Original;
        AircraftType = plan.AircraftIcaoType ?? "—";
        Departure = plan.DepartureIcao;
        Destination = plan.DestinationIcao;
        Alternates = plan.AlternateIcaos.Count > 0 ? string.Join(", ", plan.AlternateIcaos) : UiText.T("None filed");
        CruiseAltitude = plan.CruiseAltitude?.ToDisplay() ?? "—";
        DepartureRunway = plan.DepartureRunway?.ToDisplay() ?? "—";
        ArrivalRunway = plan.ArrivalRunway?.ToDisplay() ?? "—";
        DepartureProcedure = plan.StandardInstrumentDeparture ?? "—";
        ArrivalProcedure = plan.StandardTerminalArrival ?? plan.Approach ?? "—";
        Route = plan.Route is { Length: > 0 } route ? route : UiText.T("No route filed");
        Source = plan.SourceIdentifier ?? "—";

        BlockTime = plan.EstimatedBlockTime is TimeSpan block
            ? $"{(int)block.TotalHours:00}:{block.Minutes:00}"
            : "—";

        GeneratedAt = plan.GeneratedAt is DateTimeOffset generated
            ? generated.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture)
            : "—";

        // A stale plan is a warning, not a refusal: the pilot may well intend to fly it anyway.
        if (plan.GeneratedAt is DateTimeOffset when)
        {
            TimeSpan age = _clock.UtcNow - when;
            IsStale = age > _simBriefOptions.StaleThreshold;
            StaleMessage = IsStale
                ? UiText.T($"This plan was generated {(int)age.TotalHours} hours ago and may not reflect current conditions.")
                : string.Empty;
        }
        else
        {
            IsStale = false;
            StaleMessage = string.Empty;
        }

        RouteLegs.Clear();
        foreach (RouteLeg leg in plan.RouteLegs)
        {
            RouteLegs.Add(new RouteLegDisplay(
                leg.Identifier,
                leg.Via ?? "DCT",
                leg.PlannedAltitude?.ToDisplay() ?? "—",
                leg.CumulativeDistanceNauticalMiles is double distance ? $"{distance:F0} NM" : "—",
                leg.Stage ?? "—"));
        }
    }

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
    }
}

/// <summary>One row in the route table.</summary>
public sealed record RouteLegDisplay(
    string Identifier,
    string Via,
    string PlannedAltitude,
    string Distance,
    string Stage);
