using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Abstractions;

/// <summary>
/// What StormATC can ask of the simulator about the aircraft itself, beyond reading it: where it stands and
/// whether its systems are on. Both are best effort; the simulator and the aircraft have the last word.
/// </summary>
public interface ISimulatorAircraftControl
{
    /// <summary>Moves the aircraft, at rest, to a position on the ground and turns it to a heading.</summary>
    /// <param name="position">Where to put the aircraft.</param>
    /// <param name="trueHeadingDegrees">The true heading it faces.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the simulator accepted the request.</returns>
    Task<bool> PlaceAircraftAsync(GeoPosition position, double trueHeadingDegrees, CancellationToken cancellationToken);

    /// <summary>
    /// Shuts the aircraft down: engines, battery, avionics and lights off, and the parking brake set.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the simulator accepted the requests.</returns>
    Task<bool> ShutDownAircraftAsync(CancellationToken cancellationToken);

    /// <summary>Presses the parking brake (the simulator only offers a toggle).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the simulator accepted the request.</returns>
    Task<bool> ToggleParkingBrakeAsync(CancellationToken cancellationToken);
}
