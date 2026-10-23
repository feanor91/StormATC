using StormATC.Application.Abstractions;
using StormATC.Application.Phraseology;
using StormATC.Application.Transmissions;
using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>
/// In-memory stand-ins for the infrastructure the coordinator depends on.
/// </summary>
/// <remarks>
/// These are deliberately simple and honest: a provider that is configured to fail actually
/// throws, and a language model stand-in returns exactly what the test told it to. Nothing here
/// pretends to succeed on the application's behalf.
/// </remarks>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    /// <summary>Moves the clock forward, for tests that care about ordering.</summary>
    public void Advance(TimeSpan amount) => UtcNow += amount;
}

/// <summary>Serves a single in-memory aerodrome pair.</summary>
internal sealed class StubAirportDataProvider(
    bool shouldThrow = false,
    AirportData? departureOverride = null) : IAirportDataProvider
{
    public int CallCount { get; private set; }

    public Task<AirportData?> GetAirportAsync(string icao, CancellationToken cancellationToken)
    {
        CallCount++;

        if (shouldThrow)
        {
            throw new InvalidOperationException("The airport data source is unavailable.");
        }

        return Task.FromResult<AirportData?>(icao.ToUpperInvariant() switch
        {
            "STMR" => departureOverride ?? TestAerodromes.Departure,
            "STMD" => TestAerodromes.Destination,
            _ => null,
        });
    }

    public Task<IReadOnlyList<string>> ListAvailableAirportsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(["STMR", "STMD"]);
}

/// <summary>Traffic that the test injects directly.</summary>
internal sealed class StubTrafficProvider : ITrafficProvider
{
    public List<TrafficContact> Traffic { get; } = [];

    public bool ShouldThrow { get; set; }

    public Task<IReadOnlyList<TrafficContact>> GetRelevantTrafficAsync(
        AircraftState aircraftState,
        CancellationToken cancellationToken,
        double radiusNauticalMiles = 15)
    {
        if (ShouldThrow)
        {
            throw new InvalidOperationException("The traffic provider is unavailable.");
        }

        return Task.FromResult<IReadOnlyList<TrafficContact>>([.. Traffic]);
    }
}

/// <summary>Weather the test controls, including the meaningful absence of it.</summary>
internal sealed class StubWeatherProvider : IWeatherProvider
{
    public WeatherSnapshot? Snapshot { get; set; }

    public bool ShouldThrow { get; set; }

    public Task<WeatherSnapshot?> GetAirportWeatherAsync(string icao, CancellationToken cancellationToken)
    {
        if (ShouldThrow)
        {
            throw new InvalidOperationException("The weather provider is unavailable.");
        }

        return Task.FromResult(Snapshot);
    }
}

/// <summary>A phraseology renderer standing in for a language model.</summary>
internal sealed class StubLanguageModelRenderer : ILanguageModelPhraseologyRenderer
{
    public bool Available { get; init; } = true;

    /// <summary>The text to return. Null means "produced nothing usable".</summary>
    public string? Response { get; set; }

    public Exception? Failure { get; set; }

    public int CallCount { get; private set; }

    public bool IsAvailable => Available;

    public Task<RenderedPhraseology?> RenderAsync(
        AtcDecision decision,
        AtcSession session,
        CancellationToken cancellationToken)
    {
        CallCount++;

        if (Failure is Exception failure)
        {
            throw failure;
        }

        if (Response is not { Length: > 0 } text)
        {
            return Task.FromResult<RenderedPhraseology?>(null);
        }

        return Task.FromResult<RenderedPhraseology?>(new RenderedPhraseology
        {
            SpokenText = text,
            DisplayText = text,
            UsedDeterministicFallback = false,
        });
    }
}

/// <summary>Records everything written, so persistence can be asserted without a database.</summary>
internal sealed class RecordingSessionRepository : ISessionRepository
{
    public List<AtcSession> SavedSessions { get; } = [];

    public List<ConversationTurn> SavedTurns { get; } = [];

    public List<PersistedDecision> SavedDecisions { get; } = [];

    public List<Clearance> SavedClearances { get; } = [];

    public List<ControllerHandoff> SavedHandoffs { get; } = [];

    public bool ShouldThrow { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SaveSessionAsync(AtcSession session, CancellationToken cancellationToken)
    {
        Throw();
        SavedSessions.Add(session);
        return Task.CompletedTask;
    }

    public Task SaveConversationTurnAsync(Guid sessionId, ConversationTurn turn, CancellationToken cancellationToken)
    {
        Throw();
        SavedTurns.Add(turn);
        return Task.CompletedTask;
    }

    public Task SaveDecisionAsync(Guid sessionId, PersistedDecision decision, CancellationToken cancellationToken)
    {
        Throw();
        SavedDecisions.Add(decision);
        return Task.CompletedTask;
    }

    public Task SaveClearanceAsync(Guid sessionId, Clearance clearance, CancellationToken cancellationToken)
    {
        Throw();
        SavedClearances.Add(clearance);
        return Task.CompletedTask;
    }

    public Task SaveHandoffAsync(Guid sessionId, ControllerHandoff handoff, CancellationToken cancellationToken)
    {
        Throw();
        SavedHandoffs.Add(handoff);
        return Task.CompletedTask;
    }

    public Task SaveApplicationEventAsync(PersistedApplicationEvent applicationEvent, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SessionSummary>>([]);

    public Task<IReadOnlyList<ConversationTurn>> LoadConversationAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConversationTurn>>([.. SavedTurns]);

    public Task ClearAllHistoryAsync(CancellationToken cancellationToken)
    {
        SavedSessions.Clear();
        SavedTurns.Clear();
        SavedDecisions.Clear();
        return Task.CompletedTask;
    }

    public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken) => Task.FromResult(1);

    private void Throw()
    {
        if (ShouldThrow)
        {
            throw new InvalidOperationException("The database is unavailable.");
        }
    }
}

/// <summary>The fictional aerodromes used by the application-layer tests.</summary>
internal static class TestAerodromes
{
    internal static GeoPosition StandA1 { get; } = GeoPosition.Create(52.30550, 4.69400);

    internal static GeoPosition HoldingPoint18 { get; } = GeoPosition.Create(52.31050, 4.69940);

    internal static AirportData Departure { get; } = new()
    {
        Icao = "STMR",
        Name = "Storm International",
        ReferencePosition = GeoPosition.Create(52.30000, 4.70000),
        ElevationFeet = 20,
        IsFictional = true,
        Frequencies = new AirportFrequencies
        {
            ClearanceDelivery = Frequency.FromMegahertz(121.800m),
            Ground = Frequency.FromMegahertz(121.700m),
            Tower = Frequency.FromMegahertz(118.300m),
            Departure = Frequency.FromMegahertz(119.100m),
            Approach = Frequency.FromMegahertz(120.400m),
            Center = Frequency.FromMegahertz(128.500m),
            Atis = Frequency.FromMegahertz(132.150m),
        },
        Runways =
        [
            new RunwayData
            {
                Identifier = RunwayIdentifier.Create("18"),
                ThresholdPosition = GeoPosition.Create(52.31200, 4.70000),
                EndPosition = GeoPosition.Create(52.28900, 4.70000),
                LengthFeet = 9800,
                IsPreferred = true,
                Approaches = ["ILS"],
                HoldingPointNodes = ["H18"],
                ExitNodes = ["E18A"],
            },
        ],
        Stands =
        [
            new StandData
            {
                Name = "A1",
                Position = StandA1,
                TaxiNodeName = "S_A1",
                RequiresPushback = true,
            },
        ],
        TaxiGraph = TaxiGraph.Build(
            [
                new TaxiNode
                {
                    Name = "S_A1", Kind = TaxiNodeKind.Stand, StandName = "A1", Position = StandA1,
                },
                new TaxiNode
                {
                    Name = "A_N", Kind = TaxiNodeKind.Intersection,
                    Position = GeoPosition.Create(52.30700, 4.69550),
                },
                new TaxiNode
                {
                    Name = "C_W", Kind = TaxiNodeKind.Intersection,
                    Position = GeoPosition.Create(52.30950, 4.69550),
                },
                new TaxiNode
                {
                    Name = "H18", Kind = TaxiNodeKind.HoldingPoint,
                    AssociatedRunway = RunwayIdentifier.Create("18"),
                    Position = HoldingPoint18,
                },
                new TaxiNode
                {
                    Name = "E18A", Kind = TaxiNodeKind.RunwayExit,
                    AssociatedRunway = RunwayIdentifier.Create("18"),
                    Position = GeoPosition.Create(52.29600, 4.70000),
                },
            ],
            [
                new TaxiEdge { FromNode = "S_A1", ToNode = "A_N", TaxiwayName = "A", LengthMeters = 180 },
                new TaxiEdge { FromNode = "A_N", ToNode = "C_W", TaxiwayName = "A", LengthMeters = 280 },
                new TaxiEdge { FromNode = "C_W", ToNode = "H18", TaxiwayName = "C", LengthMeters = 345 },
                new TaxiEdge { FromNode = "E18A", ToNode = "A_N", TaxiwayName = "A", LengthMeters = 400 },
            ]),
    };

    internal static AirportData Destination { get; } = new()
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
            Approach = Frequency.FromMegahertz(120.400m),
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
                Approaches = ["ILS"],
            },
        ],
    };

    /// <summary>An aircraft parked at stand A1.</summary>
    internal static AircraftState AtStand(DateTimeOffset now) => new()
    {
        Timestamp = now,
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
        Radio = RadioState.Default with
        {
            Com1Active = Frequency.FromMegahertz(121.800m),
            Com1Standby = Frequency.FromMegahertz(121.700m),
        },
        Transponder = TransponderState.Default,
    };
}
