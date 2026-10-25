using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Approach;

/// <summary>
/// A VFR flight following in force: a flight information service watches the flight, gives it a transponder code and
/// passes it to the next service, or to the destination, as it goes.
/// </summary>
public sealed record FlightFollowingState
{
    /// <summary>The service that follows the flight, as it is named on the radio (for example <c>Paris Information</c>).</summary>
    public required string ServiceName { get; init; }

    /// <summary>The service's frequency now.</summary>
    public required Frequency Frequency { get; init; }

    /// <summary>The transponder code the service gave.</summary>
    public required SquawkCode Squawk { get; init; }

    /// <summary>When the flight was taken, or last handed to another sector.</summary>
    public required DateTimeOffset Since { get; init; }
}
