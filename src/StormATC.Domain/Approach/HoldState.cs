namespace StormATC.Domain.Approach;

/// <summary>A hold the controller ordered and the aircraft has not left yet.</summary>
public sealed record HoldState
{
    /// <summary>The fix to hold over, or null for the present position.</summary>
    public string? Fix { get; init; }

    /// <summary>When the hold was ordered.</summary>
    public required DateTimeOffset Since { get; init; }
}
