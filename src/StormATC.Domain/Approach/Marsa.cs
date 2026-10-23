namespace StormATC.Domain.Approach;

/// <summary>The phase of the flight a MARSA covers.</summary>
public enum MarsaKind
{
    /// <summary>A MARSA departure.</summary>
    Departure,

    /// <summary>A MARSA approach.</summary>
    Approach,
}

/// <summary>
/// A MARSA in force (Military Assumes Responsibility for Separation of Aircraft): the aircraft of a formation keep
/// their own separation from each other, so the controller treats the flight as one.
/// </summary>
public sealed record MarsaState
{
    /// <summary>Departure or approach.</summary>
    public required MarsaKind Kind { get; init; }

    /// <summary>How many aircraft are in the flight.</summary>
    public required int FlightSize { get; init; }

    /// <summary>The trail distance behind the leader in nautical miles, for an approach in trail.</summary>
    public int? TrailMiles { get; init; }

    /// <summary>When the controller approved it.</summary>
    public required DateTimeOffset Since { get; init; }
}
