using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace StormATC.Desktop.Services;

/// <summary>
/// Writes a line to the log whenever the UI thread stops responding for a few seconds, and
/// another when it recovers.
/// </summary>
/// <remarks>
/// A frozen window leaves no exception behind, so without this a report of "it froze" has
/// nothing to go on. The log lines before and after the stall name what the application was
/// doing. A background timer posts a no-op to the dispatcher and checks that it ran.
/// </remarks>
internal sealed class UiResponsivenessWatchdog : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(3);

    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastPongMs;
    private int _pingPending;
    private bool _stalled;

    public UiResponsivenessWatchdog(Dispatcher dispatcher, ILogger logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _timer = new Timer(Check, null, CheckInterval, CheckInterval);
    }

    private void Check(object? state)
    {
        double sincePong = (_clock.ElapsedMilliseconds - Interlocked.Read(ref _lastPongMs)) / 1000.0;
        if (sincePong >= StallThreshold.TotalSeconds && !_stalled)
        {
            _stalled = true;
            _logger.LogWarning("The UI thread has not responded for {Seconds:F0} s.", sincePong);
        }

        if (Interlocked.Exchange(ref _pingPending, 1) == 1)
        {
            return;
        }

        _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () =>
            {
                double stall = (_clock.ElapsedMilliseconds - Interlocked.Read(ref _lastPongMs)) / 1000.0;
                Interlocked.Exchange(ref _lastPongMs, _clock.ElapsedMilliseconds);
                Interlocked.Exchange(ref _pingPending, 0);
                if (_stalled)
                {
                    _stalled = false;
                    _logger.LogWarning("The UI thread responded again after about {Seconds:F0} s.", stall);
                }
            });
    }

    public void Dispose() => _timer.Dispose();
}
