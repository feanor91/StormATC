using StormATC.Domain;
using StormATC.Domain.Aircraft;
using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.FlightPlanning;

namespace StormATC.Application.Abstractions;

/// <summary>
/// Supplies aerodrome data. Implementations may read bundled JSON, a user file, or a future
/// licensed navigation database.
/// </summary>
public interface IAirportDataProvider
{
    /// <summary>Loads an aerodrome by ICAO code, or returns null when it is not available.</summary>
    Task<AirportData?> GetAirportAsync(string icao, CancellationToken cancellationToken);

    /// <summary>Lists the ICAO codes this provider can supply.</summary>
    Task<IReadOnlyList<string>> ListAvailableAirportsAsync(CancellationToken cancellationToken);
}

/// <summary>Supplies the VFR procedures an aerodrome publishes: reporting points and circuit.</summary>
public interface IVfrProfileProvider
{
    /// <summary>Loads the VFR profile of an aerodrome, or returns null when none is available.</summary>
    Task<VfrAerodromeProfile?> GetProfileAsync(string icao, CancellationToken cancellationToken);
}

/// <summary>Supplies en-route sector definitions.</summary>
public interface IAirspaceDataProvider
{
    /// <summary>Finds the sector that owns a position and level, or null when none does.</summary>
    Task<SectorDefinition?> FindSectorAsync(
        Domain.ValueObjects.GeoPosition position,
        double altitudeFeet,
        CancellationToken cancellationToken);

    /// <summary>Lists every sector this provider knows about.</summary>
    Task<IReadOnlyList<SectorDefinition>> ListSectorsAsync(CancellationToken cancellationToken);
}

/// <summary>Supplies nearby traffic for the runway-occupancy and separation model.</summary>
public interface ITrafficProvider
{
    /// <summary>Returns traffic relevant to the given aircraft state.</summary>
    /// <param name="aircraftState">The aircraft.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="radiusNauticalMiles">How far around the aircraft the traffic is wanted: 15 for the circuit and the
    /// runways, more for a tactical picture.</param>
    Task<IReadOnlyList<TrafficContact>> GetRelevantTrafficAsync(
        AircraftState aircraftState,
        CancellationToken cancellationToken,
        double radiusNauticalMiles = 15);
}

/// <summary>
/// Supplies weather. Returning null is meaningful: it tells the engine there is no weather
/// source, so wind and QNH must be omitted rather than invented.
/// </summary>
public interface IWeatherProvider
{
    /// <summary>Returns the observation for an aerodrome, or null when none is available.</summary>
    Task<WeatherSnapshot?> GetAirportWeatherAsync(string icao, CancellationToken cancellationToken);
}

/// <summary>Imports a flight plan from an external planning system.</summary>
public interface IFlightPlanProvider
{
    /// <summary>A stable identifier for this provider, shown in the UI.</summary>
    string ProviderName { get; }

    /// <summary>Imports the most recent plan for the requested account.</summary>
    Task<FlightPlanImportResult> ImportLatestAsync(
        FlightPlanImportRequest request,
        CancellationToken cancellationToken);
}

/// <summary>A request to import a flight plan.</summary>
public sealed record FlightPlanImportRequest
{
    /// <summary>The account identifier at the planning service, for example a SimBrief user ID.</summary>
    public string? UserId { get; init; }

    /// <summary>When true, bypass any cached copy and fetch a fresh plan.</summary>
    public bool ForceRefresh { get; init; }
}

/// <summary>The outcome of a flight-plan import.</summary>
public sealed record FlightPlanImportResult
{
    /// <summary>Whether a plan was successfully imported.</summary>
    public required bool Success { get; init; }

    /// <summary>The imported plan, when the import succeeded.</summary>
    public FlightPlan? FlightPlan { get; init; }

    /// <summary>A user-facing explanation of what went wrong.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>One concrete thing the user can do about the failure.</summary>
    public string? SuggestedAction { get; init; }

    /// <summary>Non-fatal problems found while parsing, such as missing optional fields.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Builds a success result.</summary>
    public static FlightPlanImportResult Ok(FlightPlan plan, IReadOnlyList<string>? warnings = null) => new()
    {
        Success = true,
        FlightPlan = plan,
        Warnings = warnings ?? [],
    };

    /// <summary>Builds a failure result.</summary>
    public static FlightPlanImportResult Fail(string message, string suggestedAction) => new()
    {
        Success = false,
        ErrorMessage = message,
        SuggestedAction = suggestedAction,
    };
}

/// <summary>
/// A connection to a flight simulator. Implementations must never block and must survive the
/// simulator restarting underneath them.
/// </summary>
public interface ISimulatorConnector : IAsyncDisposable
{
    /// <summary>A stable identifier for this connector, shown on the Diagnostics page.</summary>
    string ConnectorName { get; }

    /// <summary>Current link state.</summary>
    SimulatorConnectionState ConnectionState { get; }

    /// <summary>Raised roughly five times a second while connected.</summary>
    event EventHandler<AircraftStateChangedEventArgs>? AircraftStateChanged;

    /// <summary>Raised whenever <see cref="ConnectionState"/> changes.</summary>
    event EventHandler<SimulatorConnectionChangedEventArgs>? ConnectionChanged;

    /// <summary>Opens the connection. Returns once connected or definitively unavailable.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Closes the connection.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Tunes COM1 active to a published frequency and selects COM1 for transmission.
    /// Returns false when the active connector cannot apply radio commands.
    /// </summary>
    Task<bool> TuneCom1Async(
        Domain.ValueObjects.Frequency frequency,
        CancellationToken cancellationToken);
}

/// <summary>Carries a new aircraft snapshot.</summary>
public sealed class AircraftStateChangedEventArgs(AircraftState state) : EventArgs
{
    /// <summary>The new state.</summary>
    public AircraftState State { get; } = state;
}

/// <summary>Carries a simulator connection state change.</summary>
public sealed class SimulatorConnectionChangedEventArgs(
    SimulatorConnectionState state,
    string? message = null) : EventArgs
{
    /// <summary>The new state.</summary>
    public SimulatorConnectionState State { get; } = state;

    /// <summary>A user-facing explanation, where one is useful.</summary>
    public string? Message { get; } = message;
}

/// <summary>An en-route sector.</summary>
public sealed record SectorDefinition
{
    /// <summary>Sector identifier.</summary>
    public required string SectorId { get; init; }

    /// <summary>The controller name spoken on the radio.</summary>
    public required string ControllerName { get; init; }

    /// <summary>Sector frequency.</summary>
    public required Domain.ValueObjects.Frequency Frequency { get; init; }

    /// <summary>Southern boundary in degrees.</summary>
    public required double MinimumLatitude { get; init; }

    /// <summary>Northern boundary in degrees.</summary>
    public required double MaximumLatitude { get; init; }

    /// <summary>Western boundary in degrees.</summary>
    public required double MinimumLongitude { get; init; }

    /// <summary>Eastern boundary in degrees.</summary>
    public required double MaximumLongitude { get; init; }

    /// <summary>Lower vertical limit in feet.</summary>
    public required int MinimumAltitudeFeet { get; init; }

    /// <summary>Upper vertical limit in feet.</summary>
    public required int MaximumAltitudeFeet { get; init; }

    /// <summary>Identifiers of adjacent sectors.</summary>
    public IReadOnlyList<string> NeighborSectorIds { get; init; } = [];

    /// <summary>Which controller position works this sector.</summary>
    public ControllerPosition Position { get; init; } = ControllerPosition.Center;

    /// <summary>True when a position and level fall inside this sector.</summary>
    public bool Contains(Domain.ValueObjects.GeoPosition position, double altitudeFeet) =>
        position.Latitude >= MinimumLatitude
        && position.Latitude <= MaximumLatitude
        && position.Longitude >= MinimumLongitude
        && position.Longitude <= MaximumLongitude
        && altitudeFeet >= MinimumAltitudeFeet
        && altitudeFeet <= MaximumAltitudeFeet;
}
