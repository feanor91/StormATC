using StormATC.Domain.Airports;
using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.Simulator;

/// <summary>
/// The simulator-neutral snapshot accumulated from one SimConnect Facility Data request.
/// Keeping this shape free of SDK types makes the safety-critical graph conversion testable
/// without a running copy of MSFS.
/// </summary>
internal sealed class MsfsAirportFacilitySnapshot(string requestedIcao)
{
    public string RequestedIcao { get; } = requestedIcao;
    public MsfsAirportFacility? Airport { get; set; }
    public Dictionary<uint, MsfsRunwayFacility> Runways { get; } = [];
    public Dictionary<uint, MsfsFrequencyFacility> Frequencies { get; } = [];
    public Dictionary<uint, MsfsTaxiPointFacility> TaxiPoints { get; } = [];
    public Dictionary<uint, MsfsTaxiParkingFacility> TaxiParkings { get; } = [];
    public Dictionary<uint, MsfsTaxiPathFacility> TaxiPaths { get; } = [];
    public Dictionary<uint, MsfsTaxiNameFacility> TaxiNames { get; } = [];
}

internal sealed record MsfsAirportFacility(
    string Icao,
    string Name,
    double Latitude,
    double Longitude,
    double AltitudeMeters,
    float MagneticVariationDegrees);

internal sealed record MsfsRunwayFacility(
    uint Index,
    double Latitude,
    double Longitude,
    float HeadingDegrees,
    float LengthMeters,
    float WidthMeters,
    int Surface,
    int PrimaryNumber,
    int PrimaryDesignator,
    int SecondaryNumber,
    int SecondaryDesignator);

internal sealed record MsfsFrequencyFacility(uint Index, int Type, uint FrequencyHertz);

internal sealed record MsfsTaxiPointFacility(
    uint Index,
    int Type,
    int Orientation,
    float BiasXMeters,
    float BiasZMeters);

internal sealed record MsfsTaxiParkingFacility(
    uint Index,
    int Type,
    int TaxiPointType,
    int Name,
    int Suffix,
    uint Number,
    int Orientation,
    float HeadingDegrees,
    float RadiusMeters,
    float BiasXMeters,
    float BiasZMeters);

internal sealed record MsfsTaxiPathFacility(
    uint Index,
    int Type,
    float WidthMeters,
    uint WeightLimitPounds,
    int RunwayNumber,
    int RunwayDesignator,
    int StartIndex,
    int EndIndex,
    uint NameIndex);

internal sealed record MsfsTaxiNameFacility(uint Index, string Name);

/// <summary>Converts MSFS facility records into StormATC's deterministic airport graph.</summary>
internal static class MsfsAirportFacilityMapper
{
    private const double MetersPerNauticalMile = 1852.0;
    private const double MetersToFeet = 3.280839895;

    /// <summary>Builds a complete airport or returns null when the facility response is unusable.</summary>
    public static AirportData? Map(MsfsAirportFacilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Airport is not MsfsAirportFacility rawAirport
            || !GeoPosition.TryCreate(
                rawAirport.Latitude,
                rawAirport.Longitude,
                out GeoPosition referencePosition))
        {
            return null;
        }

        List<RunwayData> runways = BuildRunways(snapshot.Runways.Values);
        var nodePositions = new Dictionary<int, GeoPosition>();
        var nodes = new Dictionary<int, TaxiNode>();
        var stands = new List<StandData>();
        var standNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (MsfsTaxiPointFacility point in snapshot.TaxiPoints.Values.OrderBy(item => item.Index))
        {
            if (point.Index > int.MaxValue)
            {
                continue;
            }

            int index = (int)point.Index;
            GeoPosition position = ProjectBias(
                referencePosition,
                point.BiasXMeters,
                point.BiasZMeters);
            nodePositions[index] = position;
            nodes[index] = new TaxiNode
            {
                Name = NodeName(index),
                Position = position,
                Kind = IsHoldShort(point.Type)
                    ? TaxiNodeKind.HoldingPoint
                    : TaxiNodeKind.Intersection,
            };
        }

        foreach (MsfsTaxiParkingFacility parking in snapshot.TaxiParkings.Values.OrderBy(item => item.Index))
        {
            if (parking.Index > int.MaxValue)
            {
                continue;
            }

            int index = (int)parking.Index;
            GeoPosition position = ProjectBias(
                referencePosition,
                parking.BiasXMeters,
                parking.BiasZMeters);
            string baseStandName = ParkingName(parking);
            string standName = standNames.Add(baseStandName)
                ? baseStandName
                : $"{baseStandName} ({index})";
            standNames.Add(standName);
            string nodeName = NodeName(index);

            nodePositions[index] = position;
            nodes[index] = new TaxiNode
            {
                Name = nodeName,
                Position = position,
                Kind = TaxiNodeKind.Stand,
                StandName = standName,
            };
            stands.Add(new StandData
            {
                Name = standName,
                Position = position,
                TaxiNodeName = nodeName,
                SuitableFor = SuitableAircraft(parking.Type),
                RequiresPushback = parking.Type is not 12 and not 13,
            });
        }

        var edges = new List<TaxiEdge>();
        foreach (MsfsTaxiPathFacility path in snapshot.TaxiPaths.Values.OrderBy(item => item.Index))
        {
            // Closed, vehicle, road and painted-line paths are not aircraft taxi routes.
            if (path.Type is 0 or >= 5
                || !nodePositions.TryGetValue(path.StartIndex, out GeoPosition start)
                || !nodePositions.TryGetValue(path.EndIndex, out GeoPosition end)
                || path.StartIndex == path.EndIndex)
            {
                continue;
            }

            RunwayIdentifier? pathRunway = CreateRunwayIdentifier(
                path.RunwayNumber,
                path.RunwayDesignator);
            bool isRunwayPath = path.Type == 2;
            string taxiwayName = ResolveTaxiwayName(path, snapshot.TaxiNames, pathRunway);

            edges.Add(new TaxiEdge
            {
                FromNode = NodeName(path.StartIndex),
                ToNode = NodeName(path.EndIndex),
                TaxiwayName = taxiwayName,
                LengthMeters = Math.Max(1, start.DistanceMetersTo(end)),
                IsOneWay = false,
                CrossesRunway = isRunwayPath,
                CrossedRunway = isRunwayPath ? pathRunway : null,
            });
        }

        // Associate every hold-short node with the geometrically nearest runway. Facility Data
        // exposes the hold-short point type but not a direct runway reference.
        foreach ((int index, TaxiNode node) in nodes.ToArray())
        {
            if (node.Kind != TaxiNodeKind.HoldingPoint || runways.Count == 0)
            {
                continue;
            }

            RunwayData nearest = runways
                .OrderBy(runway => node.Position.DistanceMetersTo(runway.ThresholdPosition))
                .First();
            nodes[index] = node with { AssociatedRunway = nearest.Identifier };
        }

        // Some third-party scenery omits hold-short point types for one end of a reciprocal
        // runway. Keep routing on the simulator's real graph by selecting the closest existing
        // off-runway intersection to that threshold; no path or taxiway name is invented.
        foreach (RunwayData runway in runways)
        {
            if (nodes.Values.Any(node => node.AssociatedRunway == runway.Identifier))
            {
                continue;
            }

            double runwayHalfWidthMeters = runway.WidthFeet / MetersToFeet / 2.0;
            KeyValuePair<int, TaxiNode>[] candidates =
            [
                .. nodes
                    .Where(pair => pair.Value.Kind == TaxiNodeKind.Intersection
                        && DistanceToRunwayMeters(pair.Value.Position, runway)
                            > runwayHalfWidthMeters + 5)
                    .OrderBy(pair =>
                        pair.Value.Position.DistanceMetersTo(runway.ThresholdPosition))
                    .Take(1),
            ];

            if (candidates.Length == 1
                && candidates[0].Value.Position.DistanceMetersTo(runway.ThresholdPosition)
                    <= 1_500)
            {
                (int index, TaxiNode node) = candidates[0];
                nodes[index] = node with
                {
                    Kind = TaxiNodeKind.HoldingPoint,
                    AssociatedRunway = runway.Identifier,
                };
            }
        }

        var completedRunways = new List<RunwayData>(runways.Count);
        foreach (RunwayData runway in runways)
        {
            string[] holdingPoints =
            [
                .. nodes.Values
                    .Where(node => node.Kind == TaxiNodeKind.HoldingPoint
                        && node.AssociatedRunway == runway.Identifier)
                    .Select(node => node.Name),
            ];

            string[] exits =
            [
                .. nodes.Values
                    .Where(node => node.Kind == TaxiNodeKind.Intersection
                        && DistanceToRunwayMeters(node.Position, runway)
                            <= Math.Max(80, runway.WidthFeet / MetersToFeet))
                    .Select(node => node.Name),
            ];

            completedRunways.Add(runway with
            {
                HoldingPointNodes = holdingPoints,
                ExitNodes = exits,
            });
        }

        TaxiGraph? graph = nodes.Count > 0 && edges.Count > 0
            ? TaxiGraph.Build(nodes.Values, edges)
            : null;

        return new AirportData
        {
            Icao = NormalizeIcao(rawAirport.Icao, snapshot.RequestedIcao),
            Name = string.IsNullOrWhiteSpace(rawAirport.Name)
                ? snapshot.RequestedIcao.ToUpperInvariant()
                : rawAirport.Name.Trim(),
            ReferencePosition = referencePosition,
            ElevationFeet = (int)Math.Round(rawAirport.AltitudeMeters * MetersToFeet),
            MagneticVariationDegrees = rawAirport.MagneticVariationDegrees,
            IsFictional = false,
            Runways = completedRunways,
            Stands = stands,
            Frequencies = BuildFrequencies(snapshot.Frequencies.Values),
            TaxiGraph = graph,
        };
    }

    private static List<RunwayData> BuildRunways(IEnumerable<MsfsRunwayFacility> source)
    {
        var results = new List<RunwayData>();
        var seen = new HashSet<RunwayIdentifier>();

        foreach (MsfsRunwayFacility raw in source.OrderBy(item => item.Index))
        {
            if (!GeoPosition.TryCreate(raw.Latitude, raw.Longitude, out GeoPosition center)
                || !float.IsFinite(raw.HeadingDegrees)
                || !float.IsFinite(raw.LengthMeters)
                || raw.LengthMeters <= 0)
            {
                continue;
            }

            Heading primaryBearing = Heading.FromDegrees(raw.HeadingDegrees);
            Heading reciprocalBearing = Heading.FromDegrees(raw.HeadingDegrees + 180);
            double halfLengthNm = raw.LengthMeters / 2.0 / MetersPerNauticalMile;
            GeoPosition primaryThreshold = center.Project(reciprocalBearing, halfLengthNm);
            GeoPosition secondaryThreshold = center.Project(primaryBearing, halfLengthNm);

            AddEnd(
                CreateRunwayIdentifier(raw.PrimaryNumber, raw.PrimaryDesignator),
                primaryThreshold,
                secondaryThreshold,
                raw,
                results,
                seen);
            AddEnd(
                CreateRunwayIdentifier(raw.SecondaryNumber, raw.SecondaryDesignator),
                secondaryThreshold,
                primaryThreshold,
                raw,
                results,
                seen);
        }

        return results;
    }

    private static void AddEnd(
        RunwayIdentifier? identifier,
        GeoPosition threshold,
        GeoPosition end,
        MsfsRunwayFacility raw,
        List<RunwayData> target,
        HashSet<RunwayIdentifier> seen)
    {
        if (identifier is not RunwayIdentifier runway || !seen.Add(runway))
        {
            return;
        }

        target.Add(new RunwayData
        {
            Identifier = runway,
            ThresholdPosition = threshold,
            EndPosition = end,
            LengthFeet = (int)Math.Round(raw.LengthMeters * MetersToFeet),
            WidthFeet = (int)Math.Round(Math.Max(0, raw.WidthMeters) * MetersToFeet),
            Surface = SurfaceName(raw.Surface),
            IsPreferred = target.Count == 0,
        });
    }

    private static RunwayIdentifier? CreateRunwayIdentifier(int number, int designator)
    {
        if (number is < 1 or > 36)
        {
            return null;
        }

        string suffix = designator switch
        {
            1 => "L",
            2 => "R",
            3 => "C",
            _ => string.Empty,
        };
        return RunwayIdentifier.TryCreate($"{number:D2}{suffix}", out RunwayIdentifier runway)
            ? runway
            : null;
    }

    private static string ResolveTaxiwayName(
        MsfsTaxiPathFacility path,
        Dictionary<uint, MsfsTaxiNameFacility> taxiNames,
        RunwayIdentifier? runway)
    {
        if (path.Type == 2)
        {
            return runway is RunwayIdentifier identifier
                ? $"runway {identifier.ToDisplay()}"
                : "runway";
        }

        if (taxiNames.TryGetValue(path.NameIndex, out MsfsTaxiNameFacility? named)
            && !string.IsNullOrWhiteSpace(named.Name))
        {
            return named.Name.Trim().ToUpperInvariant();
        }

        return path.Type is 3 or 4 ? "apron" : "unnamed taxiway";
    }

    private static AirportFrequencies BuildFrequencies(
        IEnumerable<MsfsFrequencyFacility> source)
    {
        Frequency? clearance = null;
        Frequency? ground = null;
        Frequency? tower = null;
        Frequency? departure = null;
        Frequency? approach = null;
        Frequency? center = null;
        Frequency? atis = null;

        foreach (MsfsFrequencyFacility raw in source.OrderBy(item => item.Index))
        {
            decimal megahertz = raw.FrequencyHertz / 1_000_000m;
            if (!Frequency.TryFromMegahertz(megahertz, out Frequency frequency))
            {
                continue;
            }

            switch (raw.Type)
            {
                case 1: atis ??= frequency; break;
                case 5: ground ??= frequency; break;
                case 6: tower ??= frequency; break;
                case 7: clearance ??= frequency; break;
                case 8: approach ??= frequency; break;
                case 9: departure ??= frequency; break;
                case 10: center ??= frequency; break;
            }
        }

        return new AirportFrequencies
        {
            ClearanceDelivery = clearance,
            Ground = ground,
            Tower = tower,
            Departure = departure,
            Approach = approach,
            Center = center,
            Atis = atis,
        };
    }

    private static GeoPosition ProjectBias(
        GeoPosition reference,
        float biasXMeters,
        float biasZMeters)
    {
        GeoPosition position = reference;
        if (float.IsFinite(biasZMeters) && Math.Abs(biasZMeters) > 0.001)
        {
            position = position.Project(
                Heading.FromDegrees(biasZMeters >= 0 ? 0 : 180),
                Math.Abs(biasZMeters) / MetersPerNauticalMile);
        }

        if (float.IsFinite(biasXMeters) && Math.Abs(biasXMeters) > 0.001)
        {
            position = position.Project(
                Heading.FromDegrees(biasXMeters >= 0 ? 90 : 270),
                Math.Abs(biasXMeters) / MetersPerNauticalMile);
        }

        return position;
    }

    private static double DistanceToRunwayMeters(GeoPosition point, RunwayData runway)
    {
        double latitudeScale = 111_320.0;
        double longitudeScale = latitudeScale
            * Math.Cos(runway.ThresholdPosition.Latitude * Math.PI / 180.0);

        double ax = runway.ThresholdPosition.Longitude * longitudeScale;
        double ay = runway.ThresholdPosition.Latitude * latitudeScale;
        double bx = runway.EndPosition.Longitude * longitudeScale;
        double by = runway.EndPosition.Latitude * latitudeScale;
        double px = point.Longitude * longitudeScale;
        double py = point.Latitude * latitudeScale;

        double dx = bx - ax;
        double dy = by - ay;
        double denominator = (dx * dx) + (dy * dy);
        double t = denominator <= double.Epsilon
            ? 0
            : Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / denominator, 0, 1);
        double nearestX = ax + (t * dx);
        double nearestY = ay + (t * dy);
        return Math.Sqrt(
            ((px - nearestX) * (px - nearestX))
            + ((py - nearestY) * (py - nearestY)));
    }

    private static bool IsHoldShort(int type) => type is 2 or 4 or 5 or 6;

    private static string NodeName(int index) => $"MSFS_{index}";

    private static string NormalizeIcao(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback.Trim().ToUpperInvariant()
            : value.Trim().ToUpperInvariant();

    private static string ParkingName(MsfsTaxiParkingFacility parking)
    {
        string prefix = parking.Name switch
        {
            1 => "Parking ",
            2 => "North Parking ",
            3 => "Northeast Parking ",
            4 => "East Parking ",
            5 => "Southeast Parking ",
            6 => "South Parking ",
            7 => "Southwest Parking ",
            8 => "West Parking ",
            9 => "Northwest Parking ",
            10 => "Gate ",
            11 => "Dock ",
            >= 12 and <= 37 => $"{(char)('A' + parking.Name - 12)}",
            _ => "Stand ",
        };
        return $"{prefix}{parking.Number}".Trim();
    }

    private static IReadOnlyList<string> SuitableAircraft(int parkingType) => parkingType switch
    {
        8 => ["Small"],
        9 => ["Narrowbody"],
        10 => ["Widebody"],
        _ => [],
    };

    private static string SurfaceName(int surface) => surface switch
    {
        0 => "Concrete",
        1 => "Grass",
        4 => "Asphalt",
        8 => "Snow",
        9 => "Ice",
        12 => "Dirt",
        14 => "Gravel",
        23 => "Tarmac",
        _ => "Unknown",
    };
}
