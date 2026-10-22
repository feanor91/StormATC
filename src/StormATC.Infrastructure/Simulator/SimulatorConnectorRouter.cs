using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Domain;
using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.Simulator;

/// <summary>The connector names accepted by <see cref="SimulatorOptions.PreferredConnector"/>.</summary>
public static class SimulatorConnectorNames
{
    /// <summary>Use Microsoft Flight Simulator, falling back to the mock when it is not there.</summary>
    public const string Msfs = "MSFS";

    /// <summary>Never contact SimConnect; always fly the bundled scripted aircraft.</summary>
    public const string Mock = "Mock";

    /// <summary>Every selectable connector, in display order.</summary>
    public static IReadOnlyList<string> All { get; } = [Msfs, Mock];

    /// <summary>Normalises a configured name, defaulting to <see cref="Msfs"/>.</summary>
    /// <remarks>
    /// MSFS is the default because preferring the real simulator costs nothing when it is absent:
    /// the router falls back to the mock and says so. Defaulting to the mock would instead mean a
    /// running simulator was quietly ignored until the user found the setting.
    /// </remarks>
    public static string Normalize(string? name) =>
        string.Equals(name?.Trim(), Mock, StringComparison.OrdinalIgnoreCase) ? Mock : Msfs;
}

/// <summary>
/// Presents one <see cref="ISimulatorConnector"/> to the application while choosing, at runtime,
/// which real connector is behind it.
/// </summary>
/// <remarks>
/// <para>
/// StormATC must be usable with no simulator at all, and must also pick up a simulator that
/// appears later — the common case being a user who starts StormATC while MSFS is still loading.
/// This router is what makes both true:
/// </para>
/// <list type="bullet">
///   <item><description>MSFS is tried first when it is the preferred connector.</description></item>
///   <item><description>When it is unavailable the mock takes over and the reason is reported,
///     rather than the session running with no aircraft state.</description></item>
///   <item><description>While the mock stands in, MSFS is retried in the background, and the
///     switch happens on its own once the simulator answers.</description></item>
/// </list>
/// <para>
/// Exactly one connector is active at a time. The inactive one is disconnected, so a stale
/// scripted aircraft can never overwrite real simulator state — that separation is the whole
/// reason this is a router rather than a pair of subscriptions.
/// </para>
/// <para>
/// Nothing downstream knows any of this happened: the coordinator, the engine, and the view
/// models see one <see cref="ISimulatorConnector"/> whose <see cref="ConnectorName"/> and
/// <see cref="ConnectionState"/> tell the truth about whichever connector is live.
/// </para>
/// </remarks>
public sealed class SimulatorConnectorRouter : ISimulatorConnector
{
    /// <summary>Floor for the background retry, so a misconfigured interval cannot spin.</summary>
    private static readonly TimeSpan MinimumRetryInterval = TimeSpan.FromSeconds(3);

    private readonly MockSimulatorConnector _mock;
    private readonly MsfsSimConnectConnector _msfs;
    private readonly SimulatorOptions _options;
    private readonly ILogger<SimulatorConnectorRouter> _logger;

    /// <summary>Serialises connect, switch, and disconnect; each of them awaits I/O.</summary>
    private readonly SemaphoreSlim _transitionLock = new(1, 1);

    private ISimulatorConnector? _active;
    private CancellationTokenSource? _retryCancellation;
    private Task? _retryLoop;
    private bool _disposed;
    private Frequency? _requestedCom1Frequency;

    /// <summary>Whether the pilot was in the aircraft at the last snapshot; null before the first one.</summary>
    private bool? _wasUserInAircraft;

    /// <summary>Creates the router.</summary>
    public SimulatorConnectorRouter(
        MockSimulatorConnector mock,
        MsfsSimConnectConnector msfs,
        IOptions<SimulatorOptions> options,
        ILogger<SimulatorConnectorRouter> logger)
    {
        _mock = mock ?? throw new ArgumentNullException(nameof(mock));
        _msfs = msfs ?? throw new ArgumentNullException(nameof(msfs));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        PreferredConnector = SimulatorConnectorNames.Normalize(_options.PreferredConnector);
    }

    /// <inheritdoc />
    public string ConnectorName => _active?.ConnectorName ?? UiText.T("No simulator connected");

    /// <inheritdoc />
    public SimulatorConnectionState ConnectionState =>
        _active?.ConnectionState ?? SimulatorConnectionState.Disconnected;

    /// <inheritdoc />
    public event EventHandler<AircraftStateChangedEventArgs>? AircraftStateChanged;

    /// <inheritdoc />
    public event EventHandler<SimulatorConnectionChangedEventArgs>? ConnectionChanged;

    /// <summary>The connector the user asked for, which may not be the one that is running.</summary>
    public string PreferredConnector { get; private set; }

    /// <summary>True while the scripted aircraft is the source of aircraft state.</summary>
    /// <remarks>
    /// The developer controls on the Dashboard drive the mock directly, so they are only
    /// meaningful — and only shown — when this is true.
    /// </remarks>
    public bool IsMockActive => ReferenceEquals(_active, _mock);

    /// <summary>Why the running connector is the running one, in one sentence for the UI.</summary>
    public string StatusMessage { get; private set; } = UiText.T("The simulator has not been connected yet.");

    /// <summary>True while MSFS is being retried in the background.</summary>
    public bool IsWaitingForMsfs => _retryLoop is { IsCompleted: false };

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _transitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ConnectPreferredAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    /// <summary>
    /// Changes the preferred connector and applies it immediately.
    /// </summary>
    /// <remarks>
    /// Provider selection used to happen once at startup, so changing the simulator meant
    /// restarting. It does not any more: the Settings page calls this and the change takes effect
    /// on the next aircraft update.
    /// </remarks>
    /// <param name="connectorName">A name from <see cref="SimulatorConnectorNames"/>.</param>
    /// <param name="cancellationToken">Cancels the switch.</param>
    /// <returns>The message describing what is now running.</returns>
    public async Task<string> SwitchToAsync(string connectorName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string requested = SimulatorConnectorNames.Normalize(connectorName);

        await _transitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PreferredConnector = requested;
            await StopRetryLoopAsync().ConfigureAwait(false);
            await ConnectPreferredAsync(cancellationToken).ConfigureAwait(false);
            return StatusMessage;
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _transitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopRetryLoopAsync().ConfigureAwait(false);
            await DeactivateAsync(cancellationToken).ConfigureAwait(false);

            StatusMessage = UiText.T("The simulator connection was closed.");
            ConnectionChanged?.Invoke(
                this,
                new SimulatorConnectionChangedEventArgs(SimulatorConnectionState.Disconnected, StatusMessage));
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> TuneCom1Async(
        Frequency frequency,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _transitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _requestedCom1Frequency = frequency;

            return _active is ISimulatorConnector connector
                && connector.ConnectionState == SimulatorConnectionState.Connected
                && await connector.TuneCom1Async(frequency, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    /// <summary>Connects the preferred connector, falling back to the mock. Caller holds the lock.</summary>
    private async Task ConnectPreferredAsync(CancellationToken cancellationToken)
    {
        if (PreferredConnector == SimulatorConnectorNames.Mock)
        {
            await ActivateAsync(_mock, cancellationToken).ConfigureAwait(false);
            StatusMessage = UiText.T("Running the scripted mock aircraft. No flight simulator is required.");
            return;
        }

        if (!MsfsSimConnectConnector.IsSdkAvailable)
        {
            // Nothing to try: the SDK is a build input, so this build can never reach MSFS.
            await ActivateAsync(_mock, cancellationToken).ConfigureAwait(false);
            StatusMessage =
                UiText.T($"{MsfsSimConnectConnector.SdkUnavailableMessage} Running the scripted mock aircraft instead.");

            _logger.LogInformation(
                "MSFS was preferred but this build has no SimConnect assembly; using the mock connector.");
            return;
        }

        string failureMessage;
        SimulatorConnectionFailureKind failureKind;

        try
        {
            await ActivateAsync(_msfs, cancellationToken).ConfigureAwait(false);
            failureMessage = _msfs.StatusMessage;
            failureKind = _msfs.LastFailureKind;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep an unexpected loader/JIT failure from preventing the safe mock fallback.
            _logger.LogError(ex, "Starting the MSFS SimConnect connector failed.");
            failureMessage = UiText.T($"SimConnect failed unexpectedly: {ex.GetBaseException().Message}");
            failureKind = SimulatorConnectionFailureKind.Unexpected;
        }

        if (_msfs.ConnectionState == SimulatorConnectionState.Connected)
        {
            StatusMessage = UiText.T("Connected to Microsoft Flight Simulator.");
            return;
        }

        // MSFS is installed but not answering — almost always because it is not running yet.
        // The mock keeps the session usable while the retry loop waits for the simulator.
        await ActivateAsync(_mock, cancellationToken).ConfigureAwait(false);

        if (failureKind == SimulatorConnectionFailureKind.SimulatorUnavailable)
        {
            StatusMessage =
                UiText.T($"{failureMessage} Using the scripted mock aircraft and watching for the "
                + $"simulator to start.");
            StartRetryLoop();
        }
        else
        {
            StatusMessage = UiText.T($"{failureMessage} Using the scripted mock aircraft instead.");
        }
    }

    /// <summary>Makes one connector the live one, disconnecting the other.</summary>
    private async Task ActivateAsync(ISimulatorConnector connector, CancellationToken cancellationToken)
    {
        if (ReferenceEquals(_active, connector))
        {
            await connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await DeactivateAsync(cancellationToken).ConfigureAwait(false);

        connector.AircraftStateChanged += OnAircraftStateChanged;
        connector.ConnectionChanged += OnConnectionChanged;
        _active = connector;
        _wasUserInAircraft = null;

        await connector.ConnectAsync(cancellationToken).ConfigureAwait(false);

        if (connector.ConnectionState == SimulatorConnectionState.Connected
            && _requestedCom1Frequency is Frequency requested)
        {
            await connector.TuneCom1Async(requested, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Disconnects and unsubscribes from whichever connector is live.</summary>
    private async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        if (_active is not ISimulatorConnector previous)
        {
            return;
        }

        previous.AircraftStateChanged -= OnAircraftStateChanged;
        previous.ConnectionChanged -= OnConnectionChanged;
        _active = null;

        try
        {
            await previous.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A connector that cannot close cleanly must not block the one taking over.
            _logger.LogWarning(ex, "Disconnecting {Connector} failed.", previous.ConnectorName);
        }
    }

    /// <summary>Starts watching for MSFS to appear. Caller holds the lock.</summary>
    private void StartRetryLoop()
    {
        if (!_options.AutoReconnect || _retryLoop is { IsCompleted: false })
        {
            return;
        }

        _retryCancellation = new CancellationTokenSource();
        _retryLoop = RunRetryLoopAsync(_retryCancellation.Token);
    }

    private async Task StopRetryLoopAsync()
    {
        if (_retryCancellation is not null)
        {
            await _retryCancellation.CancelAsync().ConfigureAwait(false);
        }

        if (_retryLoop is not null)
        {
            try
            {
                await _retryLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop is cooperative.
            }
        }

        _retryCancellation?.Dispose();
        _retryCancellation = null;
        _retryLoop = null;
    }

    /// <summary>
    /// Polls MSFS while the mock is standing in, and hands over the moment it answers.
    /// </summary>
    /// <remarks>
    /// Deliberately not a fixed number of attempts. Starting StormATC before the simulator is the
    /// normal way round for most people, and giving up after a minute would make the MSFS setting
    /// look broken to exactly those users.
    /// </remarks>
    private async Task RunRetryLoopAsync(CancellationToken cancellationToken)
    {
        TimeSpan interval = _options.ReconnectInterval < MinimumRetryInterval
            ? MinimumRetryInterval
            : _options.ReconnectInterval;

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (PreferredConnector != SimulatorConnectorNames.Msfs)
                {
                    return;
                }

                await _transitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!IsMockActive || PreferredConnector != SimulatorConnectorNames.Msfs)
                    {
                        return;
                    }

                    // CancellationToken.None deliberately: a successful connection outlives this
                    // loop, and linking its message pump to the loop's token would tear the pump
                    // down the moment the loop was stopped.
                    await _msfs.ConnectAsync(CancellationToken.None).ConfigureAwait(false);

                    if (_msfs.ConnectionState != SimulatorConnectionState.Connected)
                    {
                        // Still not there. Leave the mock running and try again next tick.
                        await _msfs.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogInformation(
                        "Microsoft Flight Simulator became available; switching away from the mock connector.");

                    // Take over the already-connected MSFS connector: unsubscribe the mock,
                    // subscribe MSFS, and leave it connected.
                    _mock.AircraftStateChanged -= OnAircraftStateChanged;
                    _mock.ConnectionChanged -= OnConnectionChanged;
                    await _mock.DisconnectAsync(cancellationToken).ConfigureAwait(false);

                    _msfs.AircraftStateChanged += OnAircraftStateChanged;
                    _msfs.ConnectionChanged += OnConnectionChanged;
                    _active = _msfs;

                    if (_requestedCom1Frequency is Frequency requested)
                    {
                        await _msfs.TuneCom1Async(requested, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    StatusMessage = UiText.T("Connected to Microsoft Flight Simulator.");
                    ConnectionChanged?.Invoke(
                        this,
                        new SimulatorConnectionChangedEventArgs(
                            SimulatorConnectionState.Connected, StatusMessage));

                    return;
                }
                finally
                {
                    _transitionLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown, or a switch that superseded this loop.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watching for Microsoft Flight Simulator stopped unexpectedly.");
        }
    }

    private void OnAircraftStateChanged(object? sender, AircraftStateChangedEventArgs e)
    {
        // Guards against a connector that raises one last event while it is being torn down.
        if (ReferenceEquals(sender, _active))
        {
            bool inAircraft = e.State.IsUserInAircraft;
            bool entered = _wasUserInAircraft == false && inAircraft;
            _wasUserInAircraft = inAircraft;

            AircraftStateChanged?.Invoke(this, e);

            // StormATC tunes COM1 to the ATIS when the plan is loaded, but a simulator in its menus
            // drops the command. When the pilot gets into the aircraft on the ground, tune it again
            // so that the departure ATIS is the first thing heard.
            if (entered
                && e.State.IsOnGround
                && sender is ISimulatorConnector connector
                && _requestedCom1Frequency is Frequency requested)
            {
                _ = TuneAfterEnteringAsync(connector, requested);
            }
        }
    }

    private async Task TuneAfterEnteringAsync(ISimulatorConnector connector, Frequency frequency)
    {
        try
        {
            bool tuned = await connector.TuneCom1Async(frequency, CancellationToken.None).ConfigureAwait(false);
            if (tuned)
            {
                _logger.LogInformation("The pilot is in the aircraft: COM1 tuned to {Frequency}.", frequency.ToDisplay());
            }
            else
            {
                _logger.LogInformation("The pilot is in the aircraft, but COM1 could not be tuned to {Frequency}.", frequency.ToDisplay());
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "COM1 could not be tuned after the pilot got into the aircraft.");
        }
    }

    private void OnConnectionChanged(object? sender, SimulatorConnectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _active))
        {
            return;
        }

        if (e.Message is { Length: > 0 } message)
        {
            StatusMessage = message;
        }

        ConnectionChanged?.Invoke(this, e);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopRetryLoopAsync().ConfigureAwait(false);
        await DeactivateAsync(CancellationToken.None).ConfigureAwait(false);

        // The two connectors are container-owned singletons, so the container disposes them.
        // Disconnecting the active one above is this router's whole responsibility.
        _transitionLock.Dispose();
    }
}
