namespace StormATC.Infrastructure.Simulator;

/// <summary>
/// Decides when StormATC should close because MSFS stopped. It has to have seen MSFS running first, and MSFS has to stay
/// gone for a grace period, so a simulator that restarts (or a process list read at a bad moment) does not close it.
/// </summary>
public sealed class MsfsExitWatcher
{
    /// <summary>The process names of MSFS 2024 and MSFS 2020.</summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["FlightSimulator2024", "FlightSimulator"];

    /// <summary>How long MSFS must be gone before StormATC closes.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(20);

    private bool _seen;
    private DateTimeOffset? _goneSince;

    /// <summary>Records one look at the process list; returns true when StormATC should close.</summary>
    /// <param name="msfsRunning">Whether an MSFS process exists.</param>
    /// <param name="now">The time of the look.</param>
    /// <param name="enabled">Whether the option is on.</param>
    public bool ShouldClose(bool msfsRunning, DateTimeOffset now, bool enabled)
    {
        if (msfsRunning)
        {
            _seen = true;
            _goneSince = null;
            return false;
        }

        if (!_seen || !enabled)
        {
            _goneSince = null;
            return false;
        }

        _goneSince ??= now;
        return now - _goneSince.Value >= GracePeriod;
    }

    /// <summary>True when an MSFS process exists now.</summary>
    public static bool IsMsfsRunning()
    {
        foreach (string name in ProcessNames)
        {
            try
            {
                System.Diagnostics.Process[] processes = System.Diagnostics.Process.GetProcessesByName(name);
                bool found = processes.Length > 0;
                foreach (System.Diagnostics.Process process in processes)
                {
                    process.Dispose();
                }

                if (found)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // A process may exit while being enumerated; it is counted as not there for this look.
            }
        }

        return false;
    }
}
