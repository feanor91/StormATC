using StormATC.Application.FlightPlanning;
using StormATC.Domain.FlightPlanning;

namespace StormATC.Application.Abstractions;

/// <summary>Gives the points of an approach's missed approach from the simulator's navigation data.</summary>
public interface IApproachProcedureSource
{
    /// <summary>
    /// Reads the missed approach of an approach at an aerodrome.
    /// </summary>
    /// <param name="airportIcao">The aerodrome.</param>
    /// <param name="approach">The approach: its type, suffix and runway.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The points in order, or null when the simulator could not say.</returns>
    Task<IReadOnlyList<MissedApproachPoint>?> GetMissedApproachAsync(
        string airportIcao,
        SimApproach approach,
        CancellationToken cancellationToken);
}
