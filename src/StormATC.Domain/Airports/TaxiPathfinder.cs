using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Airports;

/// <summary>
/// Computes taxi routes across a <see cref="TaxiGraph"/> using Dijkstra's algorithm.
/// </summary>
/// <remarks>
/// <para>
/// Dijkstra rather than A*: aerodrome taxi graphs have tens of nodes, not thousands, so the
/// heuristic buys nothing and a plain shortest-path is easier to reason about when a route
/// looks wrong.
/// </para>
/// <para>
/// The cost function is edge length in metres, plus a large fixed penalty for runway-crossing
/// edges. The penalty is not a hard prohibition — sometimes the only route to a runway crosses
/// another one — but it makes the router prefer any reasonable alternative, matching how real
/// ground controllers route traffic.
/// </para>
/// </remarks>
public static class TaxiPathfinder
{
    /// <summary>
    /// Cost added for each runway crossing, in metres-equivalent. Set well above the diameter
    /// of a large aerodrome so that a crossing is only chosen when genuinely unavoidable.
    /// </summary>
    private const double RunwayCrossingPenaltyMeters = 50_000;

    /// <summary>
    /// Finds the lowest-cost route between two nodes.
    /// </summary>
    /// <param name="graph">The aerodrome taxi graph.</param>
    /// <param name="fromNode">Starting node name.</param>
    /// <param name="toNode">Destination node name.</param>
    /// <returns>
    /// A route, or <see cref="TaxiRoute.NotFound"/> when either endpoint is unknown or no path
    /// exists. Callers must handle the not-found case by declining to issue a taxi clearance —
    /// never by substituting a guess.
    /// </returns>
    public static TaxiRoute FindRoute(TaxiGraph graph, string fromNode, string toNode)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromNode);
        ArgumentException.ThrowIfNullOrWhiteSpace(toNode);

        if (!graph.HasNode(fromNode) || !graph.HasNode(toNode))
        {
            return TaxiRoute.NotFound;
        }

        if (string.Equals(fromNode, toNode, StringComparison.OrdinalIgnoreCase))
        {
            return new TaxiRoute
            {
                Found = true,
                Nodes = [fromNode],
                Edges = [],
                TaxiwayNames = [],
                TotalLengthMeters = 0,
            };
        }

        Dictionary<string, List<TaxiEdge>> adjacency = BuildAdjacency(graph);

        var distances = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var previousNode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previousEdge = new Dictionary<string, TaxiEdge>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // PriorityQueue gives an O((V+E) log V) Dijkstra without hand-rolling a heap.
        var queue = new PriorityQueue<string, double>();

        distances[fromNode] = 0;
        queue.Enqueue(fromNode, 0);

        while (queue.TryDequeue(out string? current, out double currentCost))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (string.Equals(current, toNode, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!adjacency.TryGetValue(current, out List<TaxiEdge>? outgoing))
            {
                continue;
            }

            foreach (TaxiEdge edge in outgoing)
            {
                string neighbour = string.Equals(edge.FromNode, current, StringComparison.OrdinalIgnoreCase)
                    ? edge.ToNode
                    : edge.FromNode;

                if (visited.Contains(neighbour))
                {
                    continue;
                }

                double cost = currentCost + EdgeCost(edge);

                if (!distances.TryGetValue(neighbour, out double existing) || cost < existing)
                {
                    distances[neighbour] = cost;
                    previousNode[neighbour] = current;
                    previousEdge[neighbour] = edge;
                    queue.Enqueue(neighbour, cost);
                }
            }
        }

        if (!distances.ContainsKey(toNode))
        {
            return TaxiRoute.NotFound;
        }

        return Reconstruct(fromNode, toNode, previousNode, previousEdge);
    }

    /// <summary>
    /// Finds a route from a stand to the holding point of a departure runway.
    /// </summary>
    /// <returns>
    /// The route, or <see cref="TaxiRoute.NotFound"/> when the aerodrome data does not describe
    /// a usable holding point for that runway.
    /// </returns>
    public static TaxiRoute FindRouteToHoldingPoint(
        AirportData airport,
        string standName,
        RunwayIdentifier runway)
    {
        ArgumentNullException.ThrowIfNull(airport);

        if (airport.TaxiGraph is not TaxiGraph graph)
        {
            return TaxiRoute.NotFound;
        }

        StandData? stand = airport.FindStand(standName);
        RunwayData? runwayData = airport.FindRunway(runway);

        if (stand is null || runwayData is null || runwayData.HoldingPointNodes.Count == 0)
        {
            return TaxiRoute.NotFound;
        }

        return FindBestRouteToAny(graph, stand.TaxiNodeName, runwayData.HoldingPointNodes);
    }

    /// <summary>
    /// Finds a route from a runway exit to a stand, for taxi-in after landing.
    /// </summary>
    public static TaxiRoute FindRouteFromRunwayToStand(
        AirportData airport,
        RunwayIdentifier landingRunway,
        string standName)
    {
        ArgumentNullException.ThrowIfNull(airport);

        if (airport.TaxiGraph is not TaxiGraph graph)
        {
            return TaxiRoute.NotFound;
        }

        StandData? stand = airport.FindStand(standName);
        RunwayData? runwayData = airport.FindRunway(landingRunway);

        if (stand is null || runwayData is null || runwayData.ExitNodes.Count == 0)
        {
            return TaxiRoute.NotFound;
        }

        // Try each published exit and keep the cheapest complete route to the stand.
        TaxiRoute best = TaxiRoute.NotFound;
        foreach (string exit in runwayData.ExitNodes)
        {
            TaxiRoute candidate = FindRoute(graph, exit, stand.TaxiNodeName);
            if (candidate.Found && (!best.Found || candidate.TotalLengthMeters < best.TotalLengthMeters))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>Finds the cheapest route from one node to whichever of several targets is closest.</summary>
    private static TaxiRoute FindBestRouteToAny(
        TaxiGraph graph,
        string fromNode,
        IReadOnlyList<string> candidateTargets)
    {
        TaxiRoute best = TaxiRoute.NotFound;

        foreach (string target in candidateTargets)
        {
            TaxiRoute candidate = FindRoute(graph, fromNode, target);
            if (candidate.Found && (!best.Found || candidate.TotalLengthMeters < best.TotalLengthMeters))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static double EdgeCost(TaxiEdge edge) =>
        edge.LengthMeters + (edge.CrossesRunway ? RunwayCrossingPenaltyMeters : 0);

    private static Dictionary<string, List<TaxiEdge>> BuildAdjacency(TaxiGraph graph)
    {
        var adjacency = new Dictionary<string, List<TaxiEdge>>(StringComparer.OrdinalIgnoreCase);

        foreach (TaxiEdge edge in graph.Edges)
        {
            AddEdge(adjacency, edge.FromNode, edge);

            // A two-way edge is reachable from either end.
            if (!edge.IsOneWay)
            {
                AddEdge(adjacency, edge.ToNode, edge);
            }
        }

        return adjacency;

        static void AddEdge(Dictionary<string, List<TaxiEdge>> target, string key, TaxiEdge edge)
        {
            if (!target.TryGetValue(key, out List<TaxiEdge>? list))
            {
                list = [];
                target[key] = list;
            }

            list.Add(edge);
        }
    }

    private static TaxiRoute Reconstruct(
        string fromNode,
        string toNode,
        Dictionary<string, string> previousNode,
        Dictionary<string, TaxiEdge> previousEdge)
    {
        var nodes = new List<string>();
        var edges = new List<TaxiEdge>();

        string cursor = toNode;
        while (!string.Equals(cursor, fromNode, StringComparison.OrdinalIgnoreCase))
        {
            nodes.Add(cursor);

            if (!previousEdge.TryGetValue(cursor, out TaxiEdge? edge)
                || !previousNode.TryGetValue(cursor, out string? previous))
            {
                return TaxiRoute.NotFound;
            }

            edges.Add(edge);
            cursor = previous;
        }

        nodes.Add(fromNode);
        nodes.Reverse();
        edges.Reverse();

        // Collapse consecutive edges on the same taxiway: a clearance says "via Alpha, Charlie",
        // not "via Alpha, Alpha, Alpha, Charlie".
        var taxiwayNames = new List<string>();
        double totalLength = 0;
        var crossedRunways = new List<RunwayIdentifier>();

        foreach (TaxiEdge edge in edges)
        {
            totalLength += edge.LengthMeters;

            if (taxiwayNames.Count == 0
                || !string.Equals(taxiwayNames[^1], edge.TaxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                taxiwayNames.Add(edge.TaxiwayName);
            }

            if (edge.CrossesRunway && edge.CrossedRunway is RunwayIdentifier crossed
                && !crossedRunways.Contains(crossed))
            {
                crossedRunways.Add(crossed);
            }
        }

        return new TaxiRoute
        {
            Found = true,
            Nodes = nodes,
            Edges = edges,
            TaxiwayNames = taxiwayNames,
            TotalLengthMeters = totalLength,
            CrossedRunways = crossedRunways,
        };
    }
}

/// <summary>The result of a taxi route computation.</summary>
public sealed record TaxiRoute
{
    /// <summary>The sentinel returned when no route could be computed.</summary>
    public static TaxiRoute NotFound { get; } = new()
    {
        Found = false,
        Nodes = [],
        Edges = [],
        TaxiwayNames = [],
        TotalLengthMeters = 0,
    };

    /// <summary>False when no route exists. Callers must not issue a clearance in that case.</summary>
    public required bool Found { get; init; }

    /// <summary>Node names traversed, in order, including both endpoints.</summary>
    public required IReadOnlyList<string> Nodes { get; init; }

    /// <summary>Edges traversed, in order.</summary>
    public required IReadOnlyList<TaxiEdge> Edges { get; init; }

    /// <summary>
    /// The taxiway names to speak, with consecutive duplicates collapsed. This is exactly what
    /// appears in the taxi clearance.
    /// </summary>
    public required IReadOnlyList<string> TaxiwayNames { get; init; }

    /// <summary>Total route length in metres.</summary>
    public required double TotalLengthMeters { get; init; }

    /// <summary>Runways this route crosses, each of which needs an explicit crossing clearance.</summary>
    public IReadOnlyList<RunwayIdentifier> CrossedRunways { get; init; } = [];

    /// <summary>True when the route requires crossing at least one runway.</summary>
    public bool RequiresRunwayCrossing => CrossedRunways.Count > 0;

    /// <summary>Renders the taxiway list for display: <c>"A, C"</c>.</summary>
    public string ToDisplayRoute() => string.Join(", ", TaxiwayNames);
}
