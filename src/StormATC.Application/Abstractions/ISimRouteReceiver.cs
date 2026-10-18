namespace StormATC.Application.Abstractions;

/// <summary>Where a flight plan route read inside the simulator came from.</summary>
public static class SimRouteSources
{
    /// <summary>The route the pilot filed with the simulator's ATC ("send to ATC").</summary>
    public const string Atc = "atc";

    /// <summary>The route synced to the avionics ("send to avionics").</summary>
    public const string Avionics = "avionics";

    /// <summary>The route as the EFB shows it, whenever it changes.</summary>
    public const string Efb = "efb";
}

/// <summary>Receives the flight plan routes the in-simulator panel reads from the simulator.</summary>
public interface ISimRouteReceiver
{
    /// <summary>A route arrived.</summary>
    /// <param name="source">Where it came from, one of <see cref="SimRouteSources"/>.</param>
    /// <param name="initial">
    /// True for the route that was already there when the panel started, which is remembered but
    /// not applied on its own.
    /// </param>
    /// <param name="routeJson">The route, as the simulator's JavaScript hands it over.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the route was applied to the session.</returns>
    Task<bool> ReceiveRouteAsync(string source, bool initial, string routeJson, CancellationToken cancellationToken);
}
