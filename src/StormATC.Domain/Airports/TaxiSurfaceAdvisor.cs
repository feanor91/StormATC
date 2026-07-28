using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Airports;

/// <summary>
/// Resolves an aircraft's surface position and progress along an issued taxi path.
/// </summary>
/// <remarks>
/// This service only interprets the airport graph supplied by MSFS or the configured airport
/// provider. It does not claim to read physical sign objects: sign cues describe what the
/// graph says the pilot should expect at a junction.
/// </remarks>
public static class TaxiSurfaceAdvisor
{
    private const double StandCaptureRadiusMeters = 45;
    private const double OnRouteToleranceMeters = 80;

    /// <summary>Finds the stand or taxiway segment closest to the aircraft.</summary>
    public static TaxiSurfacePosition Locate(TaxiGraph graph, GeoPosition position)
    {
        ArgumentNullException.ThrowIfNull(graph);

        TaxiNode? closestStand = null;
        double standDistance = double.MaxValue;
        foreach (TaxiNode node in graph.Nodes.Values.Where(node => node.Kind == TaxiNodeKind.Stand))
        {
            double distance = position.DistanceMetersTo(node.Position);
            if (distance < standDistance)
            {
                closestStand = node;
                standDistance = distance;
            }
        }

        if (closestStand is not null && standDistance <= StandCaptureRadiusMeters)
        {
            return new TaxiSurfacePosition
            {
                Found = true,
                StandName = closestStand.StandName,
                NearestNodeName = closestStand.Name,
                OffsetMeters = standDistance,
            };
        }

        TaxiEdge? closestEdge = null;
        double edgeDistance = double.MaxValue;
        double edgeFraction = 0;
        foreach (TaxiEdge edge in graph.Edges)
        {
            if (!graph.Nodes.TryGetValue(edge.FromNode, out TaxiNode? from)
                || !graph.Nodes.TryGetValue(edge.ToNode, out TaxiNode? to))
            {
                continue;
            }

            SegmentProjection projection = ProjectToSegment(position, from.Position, to.Position);
            if (projection.DistanceMeters < edgeDistance)
            {
                closestEdge = edge;
                edgeDistance = projection.DistanceMeters;
                edgeFraction = projection.Fraction;
            }
        }

        if (closestEdge is null)
        {
            TaxiNode? nearest = TaxiPathfinder.FindNearestNode(graph, position);
            return nearest is null
                ? TaxiSurfacePosition.NotFound
                : new TaxiSurfacePosition
                {
                    Found = true,
                    StandName = nearest.StandName,
                    NearestNodeName = nearest.Name,
                    OffsetMeters = position.DistanceMetersTo(nearest.Position),
                };
        }

        string nearestNode = edgeFraction < 0.5 ? closestEdge.FromNode : closestEdge.ToNode;
        return new TaxiSurfacePosition
        {
            Found = true,
            TaxiwayName = closestEdge.TaxiwayName,
            NearestNodeName = nearestNode,
            OffsetMeters = edgeDistance,
        };
    }

    /// <summary>
    /// Calculates route progress, the next taxiway change, and expected junction sign cues.
    /// </summary>
    public static TaxiRouteProgress Analyze(
        TaxiGraph graph,
        TaxiRoute route,
        GeoPosition position)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(route);

        TaxiSurfacePosition surfacePosition = Locate(graph, position);
        if (!route.Found || route.Nodes.Count == 0)
        {
            return TaxiRouteProgress.NotFound with { SurfacePosition = surfacePosition };
        }

        if (route.Edges.Count == 0)
        {
            return new TaxiRouteProgress
            {
                Found = true,
                IsOnRoute = true,
                IsAtDestination = true,
                SurfacePosition = surfacePosition,
                RemainingDistanceMeters = 0,
                DistanceToNextInstructionMeters = 0,
                JunctionSignCues = [],
            };
        }

        int closestIndex = -1;
        SegmentProjection closestProjection = SegmentProjection.NotFound;
        for (int i = 0; i < route.Edges.Count; i++)
        {
            if (!graph.Nodes.TryGetValue(route.Nodes[i], out TaxiNode? from)
                || !graph.Nodes.TryGetValue(route.Nodes[i + 1], out TaxiNode? to))
            {
                continue;
            }

            SegmentProjection projection = ProjectToSegment(position, from.Position, to.Position);
            if (projection.DistanceMeters < closestProjection.DistanceMeters)
            {
                closestProjection = projection;
                closestIndex = i;
            }
        }

        if (closestIndex < 0)
        {
            return TaxiRouteProgress.NotFound with { SurfacePosition = surfacePosition };
        }

        TaxiEdge currentEdge = route.Edges[closestIndex];
        double remaining = currentEdge.LengthMeters * (1 - closestProjection.Fraction);
        for (int i = closestIndex + 1; i < route.Edges.Count; i++)
        {
            remaining += route.Edges[i].LengthMeters;
        }

        int nextChangeIndex = -1;
        for (int i = closestIndex + 1; i < route.Edges.Count; i++)
        {
            if (!string.Equals(
                    route.Edges[i].TaxiwayName,
                    currentEdge.TaxiwayName,
                    StringComparison.OrdinalIgnoreCase))
            {
                nextChangeIndex = i;
                break;
            }
        }

        double distanceToInstruction = remaining;
        string? nextTaxiway = null;
        TaxiTurnDirection? nextTurn = null;
        IReadOnlyList<TaxiwaySignCue> signCues = [];

        if (nextChangeIndex >= 0)
        {
            distanceToInstruction = currentEdge.LengthMeters * (1 - closestProjection.Fraction);
            for (int i = closestIndex + 1; i < nextChangeIndex; i++)
            {
                distanceToInstruction += route.Edges[i].LengthMeters;
            }

            nextTaxiway = route.Edges[nextChangeIndex].TaxiwayName;
            string junctionNode = route.Nodes[nextChangeIndex];
            if (graph.Nodes.TryGetValue(route.Nodes[nextChangeIndex - 1], out TaxiNode? before)
                && graph.Nodes.TryGetValue(junctionNode, out TaxiNode? junction)
                && graph.Nodes.TryGetValue(route.Nodes[nextChangeIndex + 1], out TaxiNode? after))
            {
                double inboundBearing = before.Position.TrueBearingTo(junction.Position).Degrees;
                double outboundBearing = junction.Position.TrueBearingTo(after.Position).Degrees;
                nextTurn = ClassifyTurn(outboundBearing - inboundBearing);
                signCues = BuildJunctionSignCues(
                    graph,
                    junctionNode,
                    route.Nodes[nextChangeIndex - 1],
                    route.Nodes[nextChangeIndex + 1],
                    inboundBearing);
            }
        }

        TaxiNode? destination = graph.FindNode(route.Nodes[^1]);
        bool atDestination = remaining <= 20;
        return new TaxiRouteProgress
        {
            Found = true,
            IsOnRoute = closestProjection.DistanceMeters <= OnRouteToleranceMeters,
            IsAtDestination = atDestination,
            IsApproachingHoldShort =
                destination?.Kind == TaxiNodeKind.HoldingPoint && remaining <= 150,
            SurfacePosition = surfacePosition with
            {
                TaxiwayName = currentEdge.TaxiwayName,
                OffsetMeters = closestProjection.DistanceMeters,
            },
            CurrentTaxiway = currentEdge.TaxiwayName,
            NextTaxiway = nextTaxiway,
            NextTurn = nextTurn,
            DistanceToNextInstructionMeters = distanceToInstruction,
            RemainingDistanceMeters = remaining,
            JunctionSignCues = signCues,
        };
    }

    private static TaxiwaySignCue[] BuildJunctionSignCues(
        TaxiGraph graph,
        string junctionNode,
        string inboundNode,
        string clearedOutboundNode,
        double inboundBearing)
    {
        var cues = new List<TaxiwaySignCue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (TaxiEdge edge in graph.Edges)
        {
            string? otherNode = null;
            if (string.Equals(edge.FromNode, junctionNode, StringComparison.OrdinalIgnoreCase))
            {
                otherNode = edge.ToNode;
            }
            else if (!edge.IsOneWay
                && string.Equals(edge.ToNode, junctionNode, StringComparison.OrdinalIgnoreCase))
            {
                otherNode = edge.FromNode;
            }

            if (otherNode is null
                || string.Equals(otherNode, inboundNode, StringComparison.OrdinalIgnoreCase)
                || !graph.Nodes.TryGetValue(junctionNode, out TaxiNode? junction)
                || !graph.Nodes.TryGetValue(otherNode, out TaxiNode? other)
                || !seen.Add(edge.TaxiwayName))
            {
                continue;
            }

            double bearing = junction.Position.TrueBearingTo(other.Position).Degrees;
            cues.Add(new TaxiwaySignCue
            {
                TaxiwayName = edge.TaxiwayName,
                Direction = ClassifyTurn(bearing - inboundBearing),
                IsClearedRoute =
                    string.Equals(otherNode, clearedOutboundNode, StringComparison.OrdinalIgnoreCase),
            });
        }

        return cues
            .OrderByDescending(cue => cue.IsClearedRoute)
            .ThenBy(cue => cue.TaxiwayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TaxiTurnDirection ClassifyTurn(double deltaDegrees)
    {
        double normalized = ((deltaDegrees + 540) % 360) - 180;
        return normalized switch
        {
            < -25 => TaxiTurnDirection.Left,
            > 25 => TaxiTurnDirection.Right,
            _ => TaxiTurnDirection.Straight,
        };
    }

    private static SegmentProjection ProjectToSegment(
        GeoPosition point,
        GeoPosition start,
        GeoPosition end)
    {
        const double latitudeScale = 111_320.0;
        double longitudeScale = latitudeScale
            * Math.Cos(point.Latitude * Math.PI / 180.0);

        double startX = (start.Longitude - point.Longitude) * longitudeScale;
        double startY = (start.Latitude - point.Latitude) * latitudeScale;
        double endX = (end.Longitude - point.Longitude) * longitudeScale;
        double endY = (end.Latitude - point.Latitude) * latitudeScale;
        double dx = endX - startX;
        double dy = endY - startY;
        double denominator = (dx * dx) + (dy * dy);
        double fraction = denominator <= double.Epsilon
            ? 0
            : Math.Clamp(-((startX * dx) + (startY * dy)) / denominator, 0, 1);
        double nearestX = startX + (fraction * dx);
        double nearestY = startY + (fraction * dy);
        return new SegmentProjection(
            fraction,
            Math.Sqrt((nearestX * nearestX) + (nearestY * nearestY)));
    }

    private readonly record struct SegmentProjection(double Fraction, double DistanceMeters)
    {
        public static SegmentProjection NotFound { get; } = new(0, double.MaxValue);
    }
}

/// <summary>The closest surface feature to the aircraft.</summary>
public sealed record TaxiSurfacePosition
{
    public static TaxiSurfacePosition NotFound { get; } = new() { Found = false };

    public required bool Found { get; init; }
    public string? StandName { get; init; }
    public string? TaxiwayName { get; init; }
    public string? NearestNodeName { get; init; }
    public double OffsetMeters { get; init; }
}

/// <summary>Turn direction at the next taxiway junction.</summary>
public enum TaxiTurnDirection
{
    Straight = 0,
    Left,
    Right,
}

/// <summary>An expected direction-sign cue inferred from the taxi graph.</summary>
public sealed record TaxiwaySignCue
{
    public required string TaxiwayName { get; init; }
    public required TaxiTurnDirection Direction { get; init; }
    public bool IsClearedRoute { get; init; }
}

/// <summary>Live progress along one deterministic taxi route.</summary>
public sealed record TaxiRouteProgress
{
    public static TaxiRouteProgress NotFound { get; } = new()
    {
        Found = false,
        SurfacePosition = TaxiSurfacePosition.NotFound,
        JunctionSignCues = [],
    };

    public required bool Found { get; init; }
    public bool IsOnRoute { get; init; }
    public bool IsAtDestination { get; init; }
    public bool IsApproachingHoldShort { get; init; }
    public required TaxiSurfacePosition SurfacePosition { get; init; }
    public string? CurrentTaxiway { get; init; }
    public string? NextTaxiway { get; init; }
    public TaxiTurnDirection? NextTurn { get; init; }
    public double DistanceToNextInstructionMeters { get; init; }
    public double RemainingDistanceMeters { get; init; }
    public required IReadOnlyList<TaxiwaySignCue> JunctionSignCues { get; init; }
}
