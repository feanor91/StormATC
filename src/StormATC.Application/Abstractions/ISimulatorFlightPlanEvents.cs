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

/// <summary>Carries a flight plan route read by the StormATC module inside the simulator.</summary>
/// <param name="source">Where the route came from, one of <see cref="SimRouteSources"/>.</param>
/// <param name="initial">True for the route that was already there when StormATC connected.</param>
/// <param name="routeJson">The route, as the module published it.</param>
public sealed class SimulatorRouteEventArgs(string source, bool initial, string routeJson) : EventArgs
{
    /// <summary>Where the route came from.</summary>
    public string Source { get; } = source;

    /// <summary>True for a route that was already there when StormATC connected.</summary>
    public bool Initial { get; } = initial;

    /// <summary>The route, as the module published it.</summary>
    public string RouteJson { get; } = routeJson;
}

/// <summary>A simulator connection that reports flight plan activity.</summary>
public interface ISimulatorFlightPlanEvents
{
    /// <summary>Raised when the simulator announces a flight plan or a loaded flight.</summary>
    event EventHandler<SimulatorFlightPlanEventArgs>? FlightPlanAnnounced;

    /// <summary>
    /// Raised when the StormATC module in the simulator publishes a flight plan route: the pilot
    /// sent the plan to ATC or to the avionics, or the EFB's plan changed.
    /// </summary>
    event EventHandler<SimulatorRouteEventArgs>? RouteReceived;
}
