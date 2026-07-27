using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Airports;

/// <summary>
/// The taxiway network of an aerodrome, as a weighted graph.
/// </summary>
/// <remarks>
/// <para>
/// Taxi routes are computed, never generated. The language model is not allowed anywhere near
/// route selection: a model that invents "via Delta" at an aerodrome with no taxiway Delta
/// produces an instruction the pilot cannot follow. Every taxiway named in a StormATC taxi
/// clearance comes out of this graph.
/// </para>
/// <para>
/// Edges carry a <see cref="TaxiEdge.CrossesRunway"/> flag. Routing treats those edges as
/// expensive so that a route avoids runway crossings when any alternative exists, and the
/// engine requires an explicit crossing clearance when one is unavoidable.
/// </para>
/// </remarks>
public sealed record TaxiGraph
{
    /// <summary>Nodes keyed by name, case-insensitively.</summary>
    public required IReadOnlyDictionary<string, TaxiNode> Nodes { get; init; }

    /// <summary>All edges in the graph. Edges are bidirectional unless marked one-way.</summary>
    public required IReadOnlyList<TaxiEdge> Edges { get; init; }

    /// <summary>Builds a graph from a node and edge list, indexing the nodes by name.</summary>
    public static TaxiGraph Build(IEnumerable<TaxiNode> nodes, IEnumerable<TaxiEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var nodeIndex = new Dictionary<string, TaxiNode>(StringComparer.OrdinalIgnoreCase);
        foreach (TaxiNode node in nodes)
        {
            nodeIndex[node.Name] = node;
        }

        return new TaxiGraph
        {
            Nodes = nodeIndex,
            Edges = [.. edges],
        };
    }

    /// <summary>Finds a node by name, or null when it does not exist.</summary>
    public TaxiNode? FindNode(string name) =>
        Nodes.TryGetValue(name, out TaxiNode? node) ? node : null;

    /// <summary>True when the graph contains a node with this name.</summary>
    public bool HasNode(string name) => Nodes.ContainsKey(name);
}

/// <summary>A point in the taxiway network.</summary>
public sealed record TaxiNode
{
    /// <summary>Unique node name within the aerodrome.</summary>
    public required string Name { get; init; }

    /// <summary>Node position.</summary>
    public required GeoPosition Position { get; init; }

    /// <summary>What this node represents.</summary>
    public required TaxiNodeKind Kind { get; init; }

    /// <summary>
    /// For a holding point or runway entry/exit, the runway it protects or serves.
    /// </summary>
    public RunwayIdentifier? AssociatedRunway { get; init; }

    /// <summary>Stand name, when this node is a stand.</summary>
    public string? StandName { get; init; }
}

/// <summary>The role a taxi node plays.</summary>
public enum TaxiNodeKind
{
    /// <summary>An ordinary taxiway intersection.</summary>
    Intersection = 0,

    /// <summary>A parking stand or gate.</summary>
    Stand,

    /// <summary>A runway holding point. Crossing this without clearance is a violation.</summary>
    HoldingPoint,

    /// <summary>A point on the runway itself, used as an entry or exit.</summary>
    RunwayEntry,

    /// <summary>A runway exit used after landing.</summary>
    RunwayExit,
}

/// <summary>A traversable link between two taxi nodes.</summary>
public sealed record TaxiEdge
{
    /// <summary>Name of the node the edge starts at.</summary>
    public required string FromNode { get; init; }

    /// <summary>Name of the node the edge ends at.</summary>
    public required string ToNode { get; init; }

    /// <summary>
    /// The taxiway this edge belongs to, as it will be spoken in the clearance. For example
    /// <c>Alpha</c>. This is the only source of taxiway names in a taxi instruction.
    /// </summary>
    public required string TaxiwayName { get; init; }

    /// <summary>Edge length in metres. Used as the routing cost.</summary>
    public required double LengthMeters { get; init; }

    /// <summary>True when this edge may only be traversed from <see cref="FromNode"/>.</summary>
    public bool IsOneWay { get; init; }

    /// <summary>
    /// True when traversing this edge means entering or crossing an active runway surface.
    /// Routing penalises these heavily and the engine requires an explicit crossing clearance.
    /// </summary>
    public bool CrossesRunway { get; init; }

    /// <summary>The runway crossed, when <see cref="CrossesRunway"/> is set.</summary>
    public RunwayIdentifier? CrossedRunway { get; init; }
}
