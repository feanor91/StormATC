using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Domain.ValueObjects;
using StormATC.Infrastructure.Mocks;
using StormATC.Infrastructure.SimBrief;

namespace StormATC.Infrastructure.FlightPlanning;

/// <summary>
/// Chooses between SimBrief and the bundled sample flight for each import.
/// </summary>
/// <remarks>
/// <para>
/// The choice is made per import, from the user ID the caller supplies or the one in settings.
/// That is what lets someone type their SimBrief ID on the Flight Plan page and press
/// <b>Import</b> — no mode to change first, no restart, no downloading the plan by hand.
/// </para>
/// <para>
/// A SimBrief failure is <b>not</b> quietly replaced with the sample flight. Substituting a
/// different aircraft, route, and callsign for the one the pilot asked for would be a worse
/// outcome than reporting the failure, and the whole application is built on not inventing facts.
/// The sample is used when SimBrief was never asked for — not when it was asked for and failed.
/// </para>
/// <para>
/// A SimBrief user ID is an account number rather than a credential, so it lives in ordinary
/// settings. Nothing in this class touches <see cref="ISecretStore"/>.
/// </para>
/// </remarks>
public sealed class FlightPlanProviderRouter(
    MockFlightPlanProvider sample,
    SimBriefFlightPlanProvider simBrief,
    IOptionsMonitor<SimBriefOptions> simBriefOptions,
    IOptionsMonitor<ApplicationOptions> applicationOptions,
    ILogger<FlightPlanProviderRouter> logger) : IFlightPlanProvider
{
    private readonly MockFlightPlanProvider _sample =
        sample ?? throw new ArgumentNullException(nameof(sample));

    private readonly SimBriefFlightPlanProvider _simBrief =
        simBrief ?? throw new ArgumentNullException(nameof(simBrief));

    /// <summary>
    /// Monitored rather than snapshotted: the user ID is saved on the Flight Plan page and must
    /// take effect on the very next import, not after a restart.
    /// </summary>
    private readonly IOptionsMonitor<SimBriefOptions> _simBriefOptions =
        simBriefOptions ?? throw new ArgumentNullException(nameof(simBriefOptions));

    private readonly IOptionsMonitor<ApplicationOptions> _applicationOptions =
        applicationOptions ?? throw new ArgumentNullException(nameof(applicationOptions));

    private readonly ILogger<FlightPlanProviderRouter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string ProviderName => IsSimBriefAvailable ? _simBrief.ProviderName : _sample.ProviderName;

    /// <summary>True when an import would go to SimBrief rather than to the bundled sample.</summary>
    public bool IsSimBriefAvailable =>
        !_applicationOptions.CurrentValue.OfflineMode
        && HasUserId(_simBriefOptions.CurrentValue.UserId);

    /// <summary>Which provider served the most recent import, for the UI and diagnostics.</summary>
    public string LastProviderUsed { get; private set; } = "None";

    /// <summary>True when the most recent import went to SimBrief rather than to the sample.</summary>
    public bool LastImportUsedSimBrief { get; private set; }

    /// <summary>
    /// The transponder code pinned by the sample scenario, or null when a real plan was imported.
    /// </summary>
    /// <remarks>
    /// Exposed here so callers do not have to test the concrete provider type to find it. Only
    /// the sample pins a squawk, and only so the demonstration and the end-to-end test are
    /// reproducible; a code an aircraft actually squawks is something ATC assigns.
    /// </remarks>
    public SquawkCode? PreassignedSquawk { get; private set; }

    /// <inheritdoc />
    public async Task<FlightPlanImportResult> ImportLatestAsync(
        FlightPlanImportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The request wins: it carries what the user just typed, which may be newer than the
        // settings file the options monitor has seen.
        string? userId = HasUserId(request.UserId)
            ? request.UserId
            : _simBriefOptions.CurrentValue.UserId;

        if (!HasUserId(userId))
        {
            return await UseSampleAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (_applicationOptions.CurrentValue.OfflineMode)
        {
            _logger.LogInformation(
                "A SimBrief user ID is configured, but offline mode is on, so no request was made.");

            return await UseSampleAsync(
                request,
                cancellationToken,
                UiText.T("Offline mode is on, so SimBrief was not contacted. The bundled sample flight was loaded instead."))
                .ConfigureAwait(false);
        }

        _logger.LogInformation("Importing the latest SimBrief flight plan.");

        FlightPlanImportResult result = await _simBrief
            .ImportLatestAsync(request with { UserId = userId }, cancellationToken)
            .ConfigureAwait(false);

        LastProviderUsed = _simBrief.ProviderName;
        LastImportUsedSimBrief = true;

        // A real plan carries no pinned squawk; clearing it stops a code from an earlier sample
        // import from leaking into a session flying a SimBrief plan.
        PreassignedSquawk = null;

        return result;
    }

    private async Task<FlightPlanImportResult> UseSampleAsync(
        FlightPlanImportRequest request,
        CancellationToken cancellationToken,
        string? extraWarning = null)
    {
        FlightPlanImportResult result = await _sample
            .ImportLatestAsync(request, cancellationToken)
            .ConfigureAwait(false);

        LastProviderUsed = _sample.ProviderName;
        LastImportUsedSimBrief = false;
        PreassignedSquawk = result.Success ? _sample.PreassignedSquawk : null;

        if (extraWarning is not { Length: > 0 } || !result.Success)
        {
            return result;
        }

        return result with { Warnings = [.. result.Warnings, extraWarning] };
    }

    /// <summary>True when a user identifier is present and not just whitespace.</summary>
    private static bool HasUserId(string? userId) => !string.IsNullOrWhiteSpace(userId);
}
