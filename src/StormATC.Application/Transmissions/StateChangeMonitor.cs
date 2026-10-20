using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Application.Sessions;

namespace StormATC.Application.Transmissions;

/// <summary>
/// Lets the engine notice what the aircraft does on its own: lifting off, climbing out of the
/// aerodrome, leaving the terminal area, touching down, going around.
/// </summary>
/// <remarks>
/// <para>
/// A simulator reports the aircraft five times a second, but a controller does not need to
/// react that often, and every evaluation asks the weather and traffic providers. The monitor
/// therefore runs the engine's state-change check at most once per <see cref="MinimumInterval"/>,
/// never overlaps two checks, and does nothing until a callsign is known.
/// </para>
/// <para>
/// What the controller says is carried by
/// <see cref="PilotTransmissionCoordinator.TransmissionProcessed"/> with the correlation id
/// <see cref="PilotTransmissionCoordinator.StateChangeCorrelationId"/>.
/// </para>
/// </remarks>
public sealed class StateChangeMonitor
{
    /// <summary>The shortest time between two checks.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);

    private readonly PilotTransmissionCoordinator _coordinator;
    private readonly AtcSessionContext _sessionContext;
    private readonly IClock _clock;
    private readonly ILogger<StateChangeMonitor> _logger;
    private readonly object _gate = new();
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;
    private bool _running;

    /// <summary>Creates the monitor.</summary>
    public StateChangeMonitor(
        PilotTransmissionCoordinator coordinator,
        AtcSessionContext sessionContext,
        IClock clock,
        ILogger<StateChangeMonitor> logger)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs the state-change check if one is due. Returns true when a check ran.
    /// </summary>
    /// <remarks>Never throws: a failing provider must not stop the flight.</remarks>
    public async Task<bool> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionContext.CurrentSession.Callsign is null)
        {
            return false;
        }

        DateTimeOffset now = _clock.UtcNow;
        lock (_gate)
        {
            if (_running || now - _lastCheck < MinimumInterval)
            {
                return false;
            }

            _running = true;
            _lastCheck = now;
        }

        try
        {
            await _coordinator.ProcessStateChangeAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The state-change check failed; it will run again.");
            return false;
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }
}
