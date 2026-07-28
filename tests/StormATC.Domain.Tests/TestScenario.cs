using StormATC.Domain.Aircraft;
using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;
using StormATC.Domain.Intents;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Domain.Tests;

/// <summary>
/// Builds the fictional Storm International aerodrome and a session at it, in code.
/// </summary>
/// <remarks>
/// <para>
/// The Domain project has no serialisation dependency by design, so these tests construct the
/// aerodrome directly rather than loading the bundled JSON. The layout mirrors
/// <c>data/sample-airports/STMR.json</c> — same runway, stands, taxiways, and the same single
/// runway-crossing edge — so a test that passes here describes the behaviour a user will see.
/// </para>
/// <para>
/// Every coordinate is fictional.
/// </para>
/// </remarks>
internal static class TestScenario
{
    internal static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    internal static GeoPosition StandA1 { get; } = GeoPosition.Create(52.30550, 4.69400);

    internal static GeoPosition HoldingPoint18 { get; } = GeoPosition.Create(52.31050, 4.69940);

    internal static GeoPosition Runway18Threshold { get; } = GeoPosition.Create(52.31200, 4.70000);

    internal static GeoPosition Runway18Midpoint { get; } = GeoPosition.Create(52.30050, 4.70000);

    internal static Frequency Delivery { get; } = Frequency.FromMegahertz(121.800m);

    internal static Frequency Ground { get; } = Frequency.FromMegahertz(121.700m);

    internal static Frequency Tower { get; } = Frequency.FromMegahertz(118.300m);

    internal static Frequency Departure { get; } = Frequency.FromMegahertz(119.100m);

    internal static Frequency Approach { get; } = Frequency.FromMegahertz(120.400m);

    internal static Frequency Center { get; } = Frequency.FromMegahertz(128.500m);

    /// <summary>The departure aerodrome, laid out as in the bundled sample data.</summary>
    internal static AirportData Departure_STMR { get; } = BuildStmr();

    /// <summary>The destination aerodrome. Simpler: one runway, two stands, no crossings.</summary>
    internal static AirportData Destination_STMD { get; } = BuildStmd();

    /// <summary>The sample flight plan: STMR to STMD, runway 18 off, runway 36 on.</summary>
    internal static FlightPlan SampleFlightPlan { get; } = new()
    {
        FlightPlanId = "test",
        Callsign = Callsign.Create("STORM123"),
        AircraftIcaoType = "A320",
        DepartureIcao = "STMR",
        DestinationIcao = "STMD",
        DepartureRunway = RunwayIdentifier.Create("18"),
        ArrivalRunway = RunwayIdentifier.Create("36"),
        StandardInstrumentDeparture = "STORM1A",
        Route = "STORM1A KILOX DCT NOVAR DCT STMD",
        CruiseAltitude = Altitude.FromFlightLevel(120),
        GeneratedAt = Now,
        SourceIdentifier = "Test",
    };

    /// <summary>An aircraft parked at stand A1 with the engines off.</summary>
    internal static AircraftState AtStand() => new()
    {
        Timestamp = Now,
        Position = StandA1,
        IndicatedAltitudeFeet = 20,
        PressureAltitudeFeet = 20,
        GroundSpeedKnots = 0,
        IndicatedAirspeedKnots = 0,
        TrueHeadingDegrees = 90,
        MagneticHeadingDegrees = 90,
        VerticalSpeedFeetPerMinute = 0,
        IsOnGround = true,
        ParkingBrakeSet = true,
        EnginesRunning = false,
        AircraftTitle = "Test Narrowbody",
        Radio = RadioState.Default with { Com1Active = Delivery, Com1Standby = Ground },
        Transponder = TransponderState.Default,
    };

    /// <summary>An aircraft stationary at the runway 18 holding point.</summary>
    internal static AircraftState AtHoldingPoint() => AtStand() with
    {
        Position = HoldingPoint18,
        ParkingBrakeSet = false,
        EnginesRunning = true,
        Radio = RadioState.Default with { Com1Active = Tower },
    };

    /// <summary>An aircraft airborne and climbing.</summary>
    internal static AircraftState Airborne(double altitudeFeet = 3_000) => AtStand() with
    {
        Position = Runway18Midpoint,
        IndicatedAltitudeFeet = altitudeFeet,
        PressureAltitudeFeet = altitudeFeet,
        GroundSpeedKnots = 220,
        IndicatedAirspeedKnots = 210,
        VerticalSpeedFeetPerMinute = 1_800,
        IsOnGround = false,
        ParkingBrakeSet = false,
        EnginesRunning = true,
        MagneticHeadingDegrees = 180,
        TrueHeadingDegrees = 180,
        Radio = RadioState.Default with { Com1Active = Departure },
    };

    /// <summary>A session with the sample flight plan loaded and delivery selected.</summary>
    internal static AtcSession NewSession(AircraftState? state = null)
    {
        AtcSession session = AtcSession.Create(
            Guid.CreateVersion7(), Now, SessionMode.Mock, state ?? AtStand());

        return session with
        {
            FlightPlan = SampleFlightPlan,
            AssignedDepartureRunway = RunwayIdentifier.Create("18"),
            AssignedArrivalRunway = RunwayIdentifier.Create("36"),
            AssignedStand = "A1",
            CurrentController = ControllerPosition.ClearanceDelivery,
            CurrentFrequency = Delivery,
        };
    }

    /// <summary>Builds an evaluation context for a session and an intent.</summary>
    internal static AtcEvaluationContext Context(
        AtcSession session,
        PilotIntent? intent,
        string transcript = "",
        IReadOnlyList<TrafficContact>? traffic = null,
        WeatherSnapshot? weather = null,
        RealismProfile? realism = null,
        string defaultPushbackDirection = PushbackDirections.TailLeft,
        SquawkCode? preassignedSquawk = null) => new()
    {
        Session = session,
        AircraftState = session.AircraftState,
        FlightPlan = session.FlightPlan,
        Intent = intent,
        Transcript = transcript,
        DepartureAirport = Departure_STMR,
        DestinationAirport = Destination_STMD,
        Traffic = traffic ?? [],
        Weather = weather,
        CurrentTime = Now,
        Realism = realism ?? RealismProfile.Standard,
        DefaultPushbackDirection = defaultPushbackDirection,
        PreassignedSquawk = preassignedSquawk,
    };

    /// <summary>Builds a deterministic-parser-shaped intent.</summary>
    internal static PilotIntent Intent(
        PilotIntentType type,
        string transcript,
        double confidence = 0.95,
        Callsign? callsign = null,
        IntentEntities? entities = null,
        IReadOnlyList<ParsedReadbackItem>? readbackItems = null) => new()
    {
        Type = type,
        Confidence = confidence,
        Callsign = callsign ?? Callsign.Create("STORM123"),
        Entities = entities ?? IntentEntities.Empty,
        ReadbackItems = readbackItems ?? [],
        Source = IntentParserSource.Deterministic,
        SourceTranscript = transcript,
    };

    private static AirportData BuildStmr()
    {
        var nodes = new List<TaxiNode>
        {
            Node("S_A1", TaxiNodeKind.Stand, 52.30550, 4.69400, standName: "A1"),
            Node("S_A2", TaxiNodeKind.Stand, 52.30480, 4.69400, standName: "A2"),
            Node("S_B1", TaxiNodeKind.Stand, 52.30400, 4.69250, standName: "B1"),

            Node("A_N", TaxiNodeKind.Intersection, 52.30700, 4.69550),
            Node("A_M", TaxiNodeKind.Intersection, 52.30500, 4.69550),
            Node("A_S", TaxiNodeKind.Intersection, 52.30250, 4.69550),

            Node("C_W", TaxiNodeKind.Intersection, 52.30950, 4.69550),
            Node("C_E", TaxiNodeKind.Intersection, 52.30950, 4.69880),
            Node("H18", TaxiNodeKind.HoldingPoint, 52.31050, 4.69940, runway: "18"),
            Node("R18", TaxiNodeKind.RunwayEntry, 52.31180, 4.70000, runway: "18"),

            Node("B_W", TaxiNodeKind.Intersection, 52.29100, 4.69550),
            Node("B_E", TaxiNodeKind.Intersection, 52.29100, 4.69880),
            Node("H36", TaxiNodeKind.HoldingPoint, 52.29000, 4.69940, runway: "36"),
            Node("R36", TaxiNodeKind.RunwayEntry, 52.28920, 4.70000, runway: "36"),

            Node("E18A", TaxiNodeKind.RunwayExit, 52.29600, 4.70000, runway: "18"),
            Node("E18B", TaxiNodeKind.RunwayExit, 52.29200, 4.70000, runway: "18"),
            Node("E36A", TaxiNodeKind.RunwayExit, 52.30500, 4.70000, runway: "36"),
            Node("E36B", TaxiNodeKind.RunwayExit, 52.30900, 4.70000, runway: "36"),

            Node("D_W", TaxiNodeKind.Intersection, 52.30100, 4.69880),
            Node("D_E", TaxiNodeKind.Intersection, 52.30100, 4.70150),
            Node("S_R1", TaxiNodeKind.Stand, 52.30100, 4.70300, standName: "R1"),
        };

        var edges = new List<TaxiEdge>
        {
            Edge("S_A1", "A_N", "A", 180),
            Edge("S_A2", "A_M", "A", 120),
            Edge("S_B1", "A_M", "A", 240),

            Edge("A_N", "A_M", "A", 225),
            Edge("A_M", "A_S", "A", 280),
            Edge("A_N", "C_W", "A", 280),
            Edge("A_S", "B_W", "A", 1280),

            Edge("C_W", "C_E", "C", 225),
            Edge("C_E", "H18", "C", 120),
            Edge("H18", "R18", "C", 155),

            Edge("B_W", "B_E", "B", 225),
            Edge("B_E", "H36", "B", 120),
            Edge("H36", "R36", "B", 110),

            Edge("E18A", "A_S", "A", 400),
            Edge("E18B", "B_W", "B", 320),
            Edge("E36A", "A_M", "A", 310),
            Edge("E36B", "C_W", "C", 320),

            Edge("A_S", "D_W", "D", 240),

            // The single runway-crossing edge, exactly as in the sample data.
            Edge("D_W", "D_E", "D", 185, crossesRunway: true, crossedRunway: "18"),
            Edge("D_E", "S_R1", "D", 105),
        };

        return new AirportData
        {
            Icao = "STMR",
            Name = "Storm International",
            ReferencePosition = GeoPosition.Create(52.30000, 4.70000),
            ElevationFeet = 20,
            MagneticVariationDegrees = 1.5,
            IsFictional = true,
            Frequencies = new AirportFrequencies
            {
                ClearanceDelivery = Delivery,
                Ground = Ground,
                Tower = Tower,
                Departure = Departure,
                Approach = Approach,
                Center = Center,
                Atis = Frequency.FromMegahertz(132.150m),
            },
            Runways =
            [
                new RunwayData
                {
                    Identifier = RunwayIdentifier.Create("18"),
                    ThresholdPosition = Runway18Threshold,
                    EndPosition = GeoPosition.Create(52.28900, 4.70000),
                    LengthFeet = 9800,
                    WidthFeet = 148,
                    Surface = "Asphalt",
                    IsPreferred = true,
                    Approaches = ["ILS", "RNAV"],
                    HoldingPointNodes = ["H18"],
                    ExitNodes = ["E18A", "E18B"],
                },
                new RunwayData
                {
                    Identifier = RunwayIdentifier.Create("36"),
                    ThresholdPosition = GeoPosition.Create(52.28900, 4.70000),
                    EndPosition = Runway18Threshold,
                    LengthFeet = 9800,
                    WidthFeet = 148,
                    Surface = "Asphalt",
                    Approaches = ["ILS", "RNAV"],
                    HoldingPointNodes = ["H36"],
                    ExitNodes = ["E36A", "E36B"],
                },
            ],
            Stands =
            [
                new StandData
                {
                    Name = "A1", Position = StandA1, TaxiNodeName = "S_A1",
                    SuitableFor = ["Narrowbody"], RequiresPushback = true,
                },
                new StandData
                {
                    Name = "A2", Position = GeoPosition.Create(52.30480, 4.69400),
                    TaxiNodeName = "S_A2", SuitableFor = ["Narrowbody"], RequiresPushback = true,
                },
            ],
            TaxiGraph = TaxiGraph.Build(nodes, edges),
        };
    }

    private static AirportData BuildStmd() => new()
    {
        Icao = "STMD",
        Name = "Storm Delta Regional",
        ReferencePosition = GeoPosition.Create(52.90000, 5.10000),
        ElevationFeet = 45,
        IsFictional = true,
        Frequencies = new AirportFrequencies
        {
            Ground = Frequency.FromMegahertz(121.900m),
            Tower = Frequency.FromMegahertz(118.700m),
            Approach = Approach,
        },
        Runways =
        [
            new RunwayData
            {
                Identifier = RunwayIdentifier.Create("36"),
                ThresholdPosition = GeoPosition.Create(52.88900, 5.10000),
                EndPosition = GeoPosition.Create(52.91100, 5.10000),
                LengthFeet = 7200,
                IsPreferred = true,
                Approaches = ["ILS", "RNAV"],
                HoldingPointNodes = ["H36"],
                ExitNodes = ["X1"],
            },
        ],
        Stands =
        [
            new StandData
            {
                Name = "C1",
                Position = GeoPosition.Create(52.90050, 5.09400),
                TaxiNodeName = "S_C1",
                RequiresPushback = true,
            },
        ],
    };

    private static TaxiNode Node(
        string name,
        TaxiNodeKind kind,
        double latitude,
        double longitude,
        string? runway = null,
        string? standName = null) => new()
    {
        Name = name,
        Kind = kind,
        Position = GeoPosition.Create(latitude, longitude),
        AssociatedRunway = runway is null ? null : RunwayIdentifier.Create(runway),
        StandName = standName,
    };

    private static TaxiEdge Edge(
        string from,
        string to,
        string taxiway,
        double lengthMeters,
        bool crossesRunway = false,
        string? crossedRunway = null) => new()
    {
        FromNode = from,
        ToNode = to,
        TaxiwayName = taxiway,
        LengthMeters = lengthMeters,
        CrossesRunway = crossesRunway,
        CrossedRunway = crossedRunway is null ? null : RunwayIdentifier.Create(crossedRunway),
    };
}
