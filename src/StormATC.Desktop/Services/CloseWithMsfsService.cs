using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Options;
using StormATC.Infrastructure.Simulator;

namespace StormATC.Desktop.Services;

/// <summary>Closes StormATC a little after MSFS has stopped, when the pilot asked for it in Settings.</summary>
public sealed class CloseWithMsfsService : IDisposable
{
    private readonly IOptionsMonitor<SimulatorLifecycleOptions> _options;
    private readonly ILogger<CloseWithMsfsService> _logger;
    private readonly MsfsExitWatcher _watcher = new();
    private readonly DispatcherTimer _timer;

    /// <summary>Creates the service and starts looking for MSFS every few seconds.</summary>
    public CloseWithMsfsService(IOptionsMonitor<SimulatorLifecycleOptions> options, ILogger<CloseWithMsfsService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_watcher.ShouldClose(MsfsExitWatcher.IsMsfsRunning(), DateTimeOffset.UtcNow, _options.CurrentValue.CloseWithMsfs))
        {
            _timer.Stop();
            _logger.LogInformation("MSFS has stopped: StormATC closes.");
            System.Windows.Application.Current.Shutdown();
        }
    }
}
