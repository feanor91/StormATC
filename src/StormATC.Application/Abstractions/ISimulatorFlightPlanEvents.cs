namespace StormATC.Application.Abstractions;

/// <summary>What the simulator announced about a flight plan.</summary>
public enum SimulatorFlightPlanEventKind
{
    /// <summary>The pilot activated a flight plan (the EFB, the world map); a file name may come with it.</summary>
    Activated,

    /// <summary>The active flight plan was cleared.</summary>
    Deactivated,

    /// <summary>A flight was loaded; its <c>.FLT</c> file name comes with it.</summary>
    FlightLoaded,
}

/// <summary>Carries a flight plan notice from the simulator.</summary>
/// <param name="kind">What happened.</param>
/// <param name="fileName">The file the simulator named, or null when it named none.</param>
public sealed class SimulatorFlightPlanEventArgs(SimulatorFlightPlanEventKind kind, string? fileName) : EventArgs
{
    /// <summary>What happened.</summary>
    public SimulatorFlightPlanEventKind Kind { get; } = kind;

    /// <summary>The file the simulator named, or null.</summary>
    public string? FileName { get; } = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim();
}

/// <summary>A simulator connection that reports flight plan activity.</summary>
public interface ISimulatorFlightPlanEvents
{
    /// <summary>Raised when the simulator announces a flight plan or a loaded flight.</summary>
    event EventHandler<SimulatorFlightPlanEventArgs>? FlightPlanAnnounced;
}
