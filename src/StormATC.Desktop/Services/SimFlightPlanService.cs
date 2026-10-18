using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.FlightPlanning;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Application.Sessions;
using StormATC.Domain;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Sessions;

namespace StormATC.Desktop.Services;

/// <summary>What importing the simulator's flight plan came to.</summary>
/// <param name="Imported">True when a plan was loaded into the session.</param>
/// <param name="Message">What to tell the pilot.</param>
public sealed record SimFlightPlanImportResult(bool Imported, string Message);

/// <summary>
/// Brings the flight plan the pilot made in the simulator (the EFB, the world map, or the flight
/// that was just started) into the session.
/// </summary>
/// <remarks>
/// <para>
/// The simulator announces an activated plan with the name of its <c>.PLN</c> file, and a loaded
/// flight with the name of its <c>.FLT</c>. Whatever it announces is written to the log, so what
/// MSFS really sends can be read afterwards. A plan the pilot activates replaces the session's
/// plan; the plan of a flight that was just loaded only replaces a placeholder (the bundled
/// sample), never a plan the pilot imported.
/// </para>
/// <para>
/// The same import is available as a button on the Flight Plan page.
/// </para>
/// </remarks>
public sealed class SimFlightPlanService : IDisposable
{
    private readonly ISimulatorFlightPlanEvents _events;
    private readonly AtcSessionContext _sessionContext;
    private readonly ProviderStatusService _providerStatus;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<SimFlightPlanOptions> _options;
    private readonly ILogger<SimFlightPlanService> _logger;
    private readonly IReadOnlyList<string> _folders;
    private string? _lastAnnouncedFile;

    /// <summary>Creates the service and starts listening.</summary>
    public SimFlightPlanService(
        ISimulatorFlightPlanEvents events,
        AtcSessionContext sessionContext,
        ProviderStatusService providerStatus,
        IClock clock,
        IOptionsMonitor<SimFlightPlanOptions> options,
        ILogger<SimFlightPlanService> logger)
        : this(events, sessionContext, providerStatus, clock, options, logger, SimFlightPlanLocator.DefaultFolders())
    {
    }

    /// <summary>Creates the service with chosen folders for the simulator's plan files.</summary>
    internal SimFlightPlanService(
        ISimulatorFlightPlanEvents events,
        AtcSessionContext sessionContext,
        ProviderStatusService providerStatus,
        IClock clock,
        IOptionsMonitor<SimFlightPlanOptions> options,
        ILogger<SimFlightPlanService> logger,
        IReadOnlyList<string> folders)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _providerStatus = providerStatus ?? throw new ArgumentNullException(nameof(providerStatus));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _folders = folders ?? throw new ArgumentNullException(nameof(folders));

        _events.FlightPlanAnnounced += OnAnnounced;
    }

    /// <summary>Raised after a plan from the simulator was loaded into the session.</summary>
    public event EventHandler<FlightPlan>? PlanImported;

    /// <inheritdoc />
    public void Dispose() => _events.FlightPlanAnnounced -= OnAnnounced;

    /// <summary>
    /// Imports the plan the simulator announced last, or else the newest plan file it wrote.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SimFlightPlanImportResult> ImportLatestAsync(CancellationToken cancellationToken)
    {
        string? file = _lastAnnouncedFile is not null && File.Exists(_lastAnnouncedFile)
            ? _lastAnnouncedFile
            : SimFlightPlanLocator.FindNewest(_folders);

        if (file is null)
        {
            return new SimFlightPlanImportResult(
                false,
                UiText.T("The simulator has not written a flight plan. Activate a plan in the simulator, then try again."));
        }

        return await ImportFileAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private async void OnAnnounced(object? sender, SimulatorFlightPlanEventArgs e)
    {
        try
        {
            if (!_options.CurrentValue.Enabled)
            {
                return;
            }

            switch (e.Kind)
            {
                case SimulatorFlightPlanEventKind.Activated:
                    string? file = e.FileName is not null && File.Exists(e.FileName)
                        ? e.FileName
                        : FreshFile();
                    if (e.FileName is not null)
                    {
                        _lastAnnouncedFile = e.FileName;
                    }

                    if (file is null)
                    {
                        _logger.LogInformation(
                            "A flight plan was activated in the simulator, but no readable plan file came with it.");
                        return;
                    }

                    await ImportFileAsync(file, CancellationToken.None).ConfigureAwait(false);
                    break;

                case SimulatorFlightPlanEventKind.FlightLoaded when _options.CurrentValue.ImportOnFlightLoaded:
                    await ImportLoadedFlightAsync(e.FileName).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // This runs on a SimConnect callback: nothing may escape it.
            _logger.LogWarning(ex, "The flight plan announced by the simulator could not be imported.");
        }
    }

    /// <summary>A plan file the simulator wrote in the last two minutes, for an announcement with no name.</summary>
    private string? FreshFile()
    {
        string? newest = SimFlightPlanLocator.FindNewest(_folders);
        return newest is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(newest) < TimeSpan.FromMinutes(2)
            ? newest
            : null;
    }

    private async Task ImportLoadedFlightAsync(string? flightFile)
    {
        // The plan of a free flight sits next to the .FLT file, with the same name.
        string? plan = flightFile is null ? null : Path.ChangeExtension(flightFile, ".PLN");
        if (plan is null || !File.Exists(plan))
        {
            plan = SimFlightPlanLocator.FindNewest(_folders);
        }

        if (plan is null)
        {
            return;
        }

        FlightPlan? current = _sessionContext.CurrentSession.FlightPlan;
        bool placeholder = current is null || current.SourceIdentifier is "Sample" or "Mock";
        if (!placeholder)
        {
            _logger.LogInformation("A flight was loaded in the simulator; the plan already in use is kept.");
            return;
        }

        await ImportFileAsync(plan, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<SimFlightPlanImportResult> ImportFileAsync(string file, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The simulator's plan file {File} could not be read.", file);
            return new SimFlightPlanImportResult(false, UiText.T("The simulator's flight plan file could not be read."));
        }

        if (!PlnFlightPlanParser.TryParse(text, out SimFlightPlan? simPlan))
        {
            _logger.LogInformation("The file {File} holds no flight plan StormATC can read.", file);
            return new SimFlightPlanImportResult(false, UiText.T("The simulator's flight plan file holds no usable plan."));
        }

        AtcSession current = _sessionContext.CurrentSession;
        string? callsign = !string.IsNullOrWhiteSpace(current.AircraftState.AtcIdentifier)
            ? current.AircraftState.AtcIdentifier
            : current.FlightPlan is { SourceIdentifier: not ("Sample" or "Mock") } existing
                ? existing.Callsign.Original
                : null;

        if (!SimFlightPlanConverter.TryConvert(simPlan, callsign, _clock.UtcNow, out FlightPlan? plan))
        {
            return new SimFlightPlanImportResult(
                false,
                UiText.T("The aircraft's registration is not known yet: StormATC could not name the flight."));
        }

        _sessionContext.PreassignedSquawk = null;
        await _sessionContext.LoadFlightPlanAsync(plan, cancellationToken).ConfigureAwait(false);
        _sessionContext.Mutate(session => session.WithFlightRules(simPlan.IsVfr ? FlightRules.Vfr : FlightRules.Ifr));

        string rules = simPlan.IsVfr ? "VFR" : "IFR";
        _providerStatus.SetFlightPlanStatus(true, $"{plan.ToSummary()} · {rules} · {UiText.T("simulator")}");
        _logger.LogInformation("Imported the simulator's flight plan {Summary} ({Rules}) from {File}.", plan.ToSummary(), rules, file);

        PlanImported?.Invoke(this, plan);
        return new SimFlightPlanImportResult(
            true,
            UiText.T("Flight plan imported from the simulator: ") + $"{plan.DepartureIcao} → {plan.DestinationIcao} ({rules}).");
    }
}
