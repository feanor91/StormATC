using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Localization;
using StormATC.Application.Options;
using StormATC.Domain;
using StormATC.Domain.Airports;
using StormATC.Domain.Atc;
using StormATC.Domain.ValueObjects;

#if MSFS_SDK_AVAILABLE
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using StormATC.Domain.Aircraft;
#endif

namespace StormATC.Infrastructure.Simulator;

/// <summary>Identifies why a SimConnect connection could not be established.</summary>
public enum SimulatorConnectionFailureKind
{
    /// <summary>No connection failure is active.</summary>
    None,

    /// <summary>The project was built without the optional SimConnect SDK reference.</summary>
    SdkUnavailable,

    /// <summary>MSFS is not running or did not accept the local connection.</summary>
    SimulatorUnavailable,

    /// <summary>The managed SimConnect wrapper is absent from the application output.</summary>
    ManagedAssemblyMissing,

    /// <summary>The native SimConnect runtime is absent from the application output.</summary>
    NativeLibraryMissing,

    /// <summary>The application and SimConnect runtime architectures do not match.</summary>
    ArchitectureMismatch,

    /// <summary>The simulator rejected the client because its SimConnect version is incompatible.</summary>
    VersionMismatch,

    /// <summary>An unclassified connection failure occurred.</summary>
    Unexpected,
}

/// <summary>
/// Reads aircraft state from Microsoft Flight Simulator through SimConnect.
/// </summary>
/// <remarks>
/// The managed and native SimConnect assemblies are optional build inputs and are deliberately
/// not vendored in this repository. See <c>docs/simconnect-setup.md</c>. Aircraft state and
/// facilities are read from the simulator; the only write is an explicit COM1 tuning event when
/// a flight is loaded.
/// </remarks>
public sealed partial class MsfsSimConnectConnector : ISimulatorConnector
{
    private readonly ILogger<MsfsSimConnectConnector> _logger;
    private readonly SimulatorOptions _options;
    private readonly IOptionsMonitor<TrafficOptions> _trafficOptions;

    /// <summary>Creates the connector.</summary>
    public MsfsSimConnectConnector(
        IOptions<SimulatorOptions> options,
        IOptionsMonitor<TrafficOptions> trafficOptions,
        ILogger<MsfsSimConnectConnector> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _trafficOptions = trafficOptions ?? throw new ArgumentNullException(nameof(trafficOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether the managed SimConnect assembly was found at build time.</summary>
    public static bool IsSdkAvailable =>
#if MSFS_SDK_AVAILABLE
        true;
#else
        false;
#endif

    /// <summary>The message shown when the SDK could not be located at build time.</summary>
    public const string SdkUnavailableMessage =
        "SimConnect integration is unavailable: this build could not find the MSFS SDK. "
        + "See docs/simconnect-setup.md.";

    /// <inheritdoc />
    public string ConnectorName => "Microsoft Flight Simulator (SimConnect)";

    /// <inheritdoc />
    public SimulatorConnectionState ConnectionState { get; private set; } =
        SimulatorConnectionState.Disconnected;

    /// <summary>The most recent connection failure category.</summary>
    public SimulatorConnectionFailureKind LastFailureKind { get; private set; }

    /// <summary>A specific, user-facing description of the current connection state.</summary>
    public string StatusMessage { get; private set; } = UiText.T("SimConnect has not been started.");

#if MSFS_SDK_AVAILABLE
    private WeatherSnapshot? _latestWeather;
#endif
    private IReadOnlyList<MsfsTrafficSnapshot> _nearbyTraffic = [];
    private readonly Lock _airportCacheLock = new();
    private readonly Dictionary<string, AirportData> _airportCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The latest ambient conditions at the user aircraft.</summary>
#if MSFS_SDK_AVAILABLE
    public WeatherSnapshot? LatestWeather => Volatile.Read(ref _latestWeather);
#else
    public WeatherSnapshot? LatestWeather { get; private set; }
#endif

    /// <summary>Nearby simulator-managed aircraft from the latest traffic scan.</summary>
    public IReadOnlyList<MsfsTrafficSnapshot> NearbyTraffic =>
        Volatile.Read(ref _nearbyTraffic);

    /// <summary>Returns airport facility data already loaded during this connection.</summary>
    public AirportData? GetCachedAirport(string icao)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }

        lock (_airportCacheLock)
        {
            return _airportCache.GetValueOrDefault(icao.Trim());
        }
    }

    /// <summary>Returns a stable snapshot of every airport loaded during this connection.</summary>
    public IReadOnlyList<AirportData> GetCachedAirports()
    {
        lock (_airportCacheLock)
        {
            return [.. _airportCache.Values];
        }
    }

    // With no SDK there is no data source, so the event genuinely never fires in that build.
#pragma warning disable CS0067
    /// <inheritdoc />
    public event EventHandler<AircraftStateChangedEventArgs>? AircraftStateChanged;
#pragma warning restore CS0067

    /// <inheritdoc />
    public event EventHandler<SimulatorConnectionChangedEventArgs>? ConnectionChanged;

    private void SetConnectionState(
        SimulatorConnectionState state,
        string? message,
        SimulatorConnectionFailureKind failureKind = SimulatorConnectionFailureKind.None)
    {
        ConnectionState = state;
        LastFailureKind = failureKind;

        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusMessage = message;
        }

        ConnectionChanged?.Invoke(this, new SimulatorConnectionChangedEventArgs(state, message));
    }

    /// <summary>
    /// Converts the decimal number returned by <c>TRANSPONDER CODE:1</c> when requested in
    /// <c>number</c> units. Leading zeroes are restored before octal validation.
    /// </summary>
    internal static SquawkCode SafeSquawk(int decimalCode)
    {
        string digits = decimalCode is >= 0 and <= 9999
            ? decimalCode.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

        return SquawkCode.TryCreate(digits, out SquawkCode code)
            ? code
            : SquawkCode.Conspicuity;
    }

    /// <summary>Converts the domain MHz value to the integer-Hz payload SimConnect expects.</summary>
    internal static uint ToFrequencyHertz(Frequency frequency) =>
        checked((uint)Math.Round(
            frequency.Megahertz * 1_000_000m,
            MidpointRounding.AwayFromZero));

    /// <summary>
    /// Requests the simulator's live airport facility data, including runways, frequencies,
    /// parking positions, taxi points, taxi names and taxi paths.
    /// </summary>
    public Task<AirportData?> GetAirportDataAsync(
        string icao,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return Task.FromResult<AirportData?>(null);
        }

#if !MSFS_SDK_AVAILABLE
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetCachedAirport(icao));
#else
        return RequestAirportDataAsync(icao.Trim().ToUpperInvariant(), cancellationToken);
#endif
    }

#if !MSFS_SDK_AVAILABLE

    /// <inheritdoc />
    public Task<bool> TuneCom1Async(
        Frequency frequency,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogWarning(
            "Cannot tune COM1 to {Frequency}: the SimConnect SDK is unavailable.",
            frequency.ToDisplay());
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogWarning(
            "{Message} See docs/simconnect-setup.md for the supported SDK discovery options.",
            SdkUnavailableMessage);

        SetConnectionState(
            SimulatorConnectionState.Unavailable,
            SdkUnavailableMessage,
            SimulatorConnectionFailureKind.SdkUnavailable);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetConnectionState(
            SimulatorConnectionState.Disconnected,
            UiText.T("Disconnected from Microsoft Flight Simulator."));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

#else

    /// <summary>Identifiers owned by StormATC within this SimConnect client.</summary>
    private enum Definitions
    {
        AircraftState = 0,
        AirportFacility = 1,
        NearbyTraffic = 2,
    }

    private enum Requests
    {
        AircraftState = 0,
        AirportFacility = 1,
        NearbyTraffic = 2,
    }

    private enum Events
    {
        SetCom1ActiveHz = 0,
        SelectCom1Transmitter = 1,
        FlightPlanActivated = 2,
        FlightPlanDeactivated = 3,
        FlightLoaded = 4,
    }

    private enum NotificationGroups
    {
        Highest = 1,
    }

    private enum PumpExitReason
    {
        Cancelled,
        ConnectionLost,
        FatalFailure,
    }

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentQueue<AirportFacilityRequest> _airportRequestQueue = new();
    private readonly AutoResetEvent _airportRequestSignal = new(false);
    private readonly ConcurrentQueue<RadioTuneRequest> _radioTuneQueue = new();
    private readonly AutoResetEvent _radioTuneSignal = new(false);

    // SimConnect is not thread-safe. The long-running worker is the sole owner of these objects:
    // it opens the connection, receives every callback, reconnects, and disposes the handle.
    private SimConnect? _simConnect;
    private AutoResetEvent? _messageSignal;
    private CancellationTokenSource? _workerCancellation;
    private Task? _workerTask;

    private bool _disposed;
    private bool _quitReceived;
    private SimulatorConnectionFailureKind _callbackFailureKind;
    private string? _callbackFailureMessage;
    private int _firstFrameLogged;
    private AirportFacilityRequest? _activeAirportRequest;
    private readonly List<MsfsTrafficSnapshot> _trafficBuffer = [];
    private DateTimeOffset _nextTrafficRequestAt = DateTimeOffset.MinValue;
    private DateTimeOffset _trafficRequestStartedAt = DateTimeOffset.MinValue;
    private bool _trafficRequestPending;

    private sealed record AirportFacilityRequest(
        string Icao,
        TaskCompletionSource<AirportData?> Completion,
        MsfsAirportFacilitySnapshot Snapshot);

    private sealed record RadioTuneRequest(
        Frequency Frequency,
        TaskCompletionSource<bool> Completion);

    /// <summary>
    /// The simulator variables StormATC reads, laid out to match <see cref="AircraftStateStruct"/>.
    /// </summary>
    private static readonly (string Variable, string? Unit, SIMCONNECT_DATATYPE Type)[] DataDefinition =
    [
        ("PLANE LATITUDE",                  "degrees",          SIMCONNECT_DATATYPE.FLOAT64),
        ("PLANE LONGITUDE",                 "degrees",          SIMCONNECT_DATATYPE.FLOAT64),
        ("INDICATED ALTITUDE",              "feet",             SIMCONNECT_DATATYPE.FLOAT64),
        ("PRESSURE ALTITUDE",               "feet",             SIMCONNECT_DATATYPE.FLOAT64),
        ("GROUND VELOCITY",                 "knots",            SIMCONNECT_DATATYPE.FLOAT64),
        ("AIRSPEED INDICATED",              "knots",            SIMCONNECT_DATATYPE.FLOAT64),
        ("PLANE HEADING DEGREES TRUE",      "degrees",          SIMCONNECT_DATATYPE.FLOAT64),
        ("PLANE HEADING DEGREES MAGNETIC",  "degrees",          SIMCONNECT_DATATYPE.FLOAT64),
        ("VERTICAL SPEED",                  "feet per minute",  SIMCONNECT_DATATYPE.FLOAT64),
        ("SIM ON GROUND",                   "bool",             SIMCONNECT_DATATYPE.INT32),
        ("BRAKE PARKING POSITION",          "bool",             SIMCONNECT_DATATYPE.INT32),
        ("GENERAL ENG COMBUSTION:1",        "bool",             SIMCONNECT_DATATYPE.INT32),
        ("COM ACTIVE FREQUENCY:1",          "MHz",              SIMCONNECT_DATATYPE.FLOAT64),
        ("COM STANDBY FREQUENCY:1",         "MHz",              SIMCONNECT_DATATYPE.FLOAT64),
        ("COM ACTIVE FREQUENCY:2",          "MHz",              SIMCONNECT_DATATYPE.FLOAT64),
        ("COM STANDBY FREQUENCY:2",         "MHz",              SIMCONNECT_DATATYPE.FLOAT64),
        ("TRANSPONDER CODE:1",              "number",           SIMCONNECT_DATATYPE.INT32),
        ("TITLE",                           null,               SIMCONNECT_DATATYPE.STRING256),
        ("ATC ID",                          null,               SIMCONNECT_DATATYPE.STRING32),
        ("PLANE ALT ABOVE GROUND",          "feet",             SIMCONNECT_DATATYPE.FLOAT64),
        ("AUTOPILOT MASTER",                "bool",             SIMCONNECT_DATATYPE.INT32),
        ("GEAR HANDLE POSITION",            "bool",             SIMCONNECT_DATATYPE.INT32),
        ("FLAPS HANDLE INDEX",              "number",           SIMCONNECT_DATATYPE.FLOAT64),
        ("COM TRANSMIT:1",                  "bool",             SIMCONNECT_DATATYPE.INT32),
        ("COM TRANSMIT:2",                  "bool",             SIMCONNECT_DATATYPE.INT32),
        ("TRANSPONDER STATE:1",             "enum",             SIMCONNECT_DATATYPE.INT32),
        ("AMBIENT WIND DIRECTION",          "degrees",          SIMCONNECT_DATATYPE.FLOAT64),
        ("AMBIENT WIND VELOCITY",           "knots",            SIMCONNECT_DATATYPE.FLOAT64),
        ("SEA LEVEL PRESSURE",              "millibars",        SIMCONNECT_DATATYPE.FLOAT64),
        ("AMBIENT VISIBILITY",              "meters",           SIMCONNECT_DATATYPE.FLOAT64),
        ("AMBIENT TEMPERATURE",             "celsius",          SIMCONNECT_DATATYPE.FLOAT64),
    ];

    private static readonly (string Variable, string? Unit, SIMCONNECT_DATATYPE Type)[]
        TrafficDataDefinition =
    [
        ("ATC ID",                  null,       SIMCONNECT_DATATYPE.STRING32),
        ("TITLE",                   null,       SIMCONNECT_DATATYPE.STRING256),
        ("PLANE LATITUDE",          "degrees",  SIMCONNECT_DATATYPE.FLOAT64),
        ("PLANE LONGITUDE",         "degrees",  SIMCONNECT_DATATYPE.FLOAT64),
        ("INDICATED ALTITUDE",      "feet",     SIMCONNECT_DATATYPE.FLOAT64),
        ("GROUND VELOCITY",         "knots",    SIMCONNECT_DATATYPE.FLOAT64),
        ("PLANE HEADING DEGREES MAGNETIC", "degrees", SIMCONNECT_DATATYPE.FLOAT64),
        ("VERTICAL SPEED",           "feet per minute", SIMCONNECT_DATATYPE.FLOAT64),
        ("SIM ON GROUND",           "bool",     SIMCONNECT_DATATYPE.INT32),
    ];

    /// <summary>The marshalled layout SimConnect writes into.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct AircraftStateStruct
    {
        public double Latitude;
        public double Longitude;
        public double IndicatedAltitude;
        public double PressureAltitude;
        public double GroundSpeed;
        public double IndicatedAirspeed;
        public double TrueHeading;
        public double MagneticHeading;
        public double VerticalSpeed;
        public int OnGround;
        public int ParkingBrake;
        public int EngineCombustion;
        public double Com1Active;
        public double Com1Standby;
        public double Com2Active;
        public double Com2Standby;
        public int TransponderCode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string AtcIdentifier;

        public double AltitudeAboveGround;
        public int AutopilotMaster;
        public int GearHandlePosition;
        public double FlapsHandleIndex;
        public int Com1Transmit;
        public int Com2Transmit;
        public int TransponderState;
        public double AmbientWindDirection;
        public double AmbientWindVelocity;
        public double SeaLevelPressure;
        public double AmbientVisibility;
        public double AmbientTemperature;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct NearbyTrafficStruct
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string AtcIdentifier;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;

        public double Latitude;
        public double Longitude;
        public double IndicatedAltitude;
        public double GroundSpeed;
        public double MagneticHeading;
        public double VerticalSpeed;
        public int OnGround;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct AirportFacilityStruct
    {
        public double Latitude;
        public double Longitude;
        public double AltitudeMeters;
        public float MagneticVariationDegrees;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Name;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
        public string Icao;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RunwayFacilityStruct
    {
        public double Latitude;
        public double Longitude;
        public float HeadingDegrees;
        public float LengthMeters;
        public float WidthMeters;
        public int Surface;
        public int PrimaryNumber;
        public int PrimaryDesignator;
        public int SecondaryNumber;
        public int SecondaryDesignator;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct FrequencyFacilityStruct
    {
        public int Type;
        public uint FrequencyHertz;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaxiPointFacilityStruct
    {
        public int Type;
        public int Orientation;
        public float BiasXMeters;
        public float BiasZMeters;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaxiParkingFacilityStruct
    {
        public int Type;
        public int TaxiPointType;
        public int Name;
        public int Suffix;
        public uint Number;
        public int Orientation;
        public float HeadingDegrees;
        public float RadiusMeters;
        public float BiasXMeters;
        public float BiasZMeters;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaxiPathFacilityStruct
    {
        public int Type;
        public float WidthMeters;
        public uint WeightLimitPounds;
        public int RunwayNumber;
        public int RunwayDesignator;
        public int StartIndex;
        public int EndIndex;
        public uint NameIndex;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct TaxiNameFacilityStruct
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
    }

    private async Task<AirportData?> RequestAirportDataAsync(
        string icao,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ConnectionState != SimulatorConnectionState.Connected)
        {
            _logger.LogInformation(
                "Cannot request MSFS facility data for {Icao}: SimConnect is not connected.",
                icao);
            return null;
        }

        var completion = new TaskCompletionSource<AirportData?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _airportRequestQueue.Enqueue(new AirportFacilityRequest(
            icao,
            completion,
            new MsfsAirportFacilitySnapshot(icao)));
        _airportRequestSignal.Set();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        try
        {
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            completion.TrySetResult(null);
            _logger.LogWarning(
                "Timed out waiting for MSFS airport facility data for {Icao}.",
                icao);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TuneCom1Async(
        Frequency frequency,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ConnectionState != SimulatorConnectionState.Connected)
        {
            _logger.LogInformation(
                "Cannot tune COM1 to {Frequency}: SimConnect is not connected.",
                frequency.ToDisplay());
            return false;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _radioTuneQueue.Enqueue(new RadioTuneRequest(frequency, completion));
        _radioTuneSignal.Set();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            completion.TrySetResult(false);
            _logger.LogWarning(
                "Timed out while tuning MSFS COM1 to {Frequency}.",
                frequency.ToDisplay());
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_workerTask is { IsCompleted: false })
            {
                return;
            }

            CleanupCompletedWorker();

            if (!ValidateRuntimeArtifacts())
            {
                return;
            }

            SetConnectionState(
                SimulatorConnectionState.Connecting,
                UiText.T("Connecting to Microsoft Flight Simulator."));

            var initialAttempt = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _workerCancellation = new CancellationTokenSource();
            CancellationToken workerToken = _workerCancellation.Token;

            _workerTask = Task.Factory.StartNew(
                () => RunConnectionWorker(initialAttempt, workerToken),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            try
            {
                await initialAttempt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await _workerCancellation.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private bool ValidateRuntimeArtifacts()
    {
        if (!Environment.Is64BitProcess)
        {
            SetConnectionState(
                SimulatorConnectionState.Error,
                UiText.T("SimConnect cannot load because StormATC is not running as a 64-bit process."),
                SimulatorConnectionFailureKind.ArchitectureMismatch);
            return false;
        }

        string managedPath = Path.Combine(
            AppContext.BaseDirectory,
            "Microsoft.FlightSimulator.SimConnect.dll");

        if (!File.Exists(managedPath))
        {
            SetConnectionState(
                SimulatorConnectionState.Error,
                UiText.T("The managed SimConnect wrapper is missing from the StormATC application folder. "
                + "Rebuild with the MSFS SDK configured."),
                SimulatorConnectionFailureKind.ManagedAssemblyMissing);
            return false;
        }

        string nativePath = Path.Combine(AppContext.BaseDirectory, "SimConnect.dll");
        if (!File.Exists(nativePath))
        {
            SetConnectionState(
                SimulatorConnectionState.Error,
                UiText.T("The native SimConnect.dll is missing from the StormATC application folder. "
                + "Configure MsfsSimConnectNativePath or MSFS_SIMCONNECT_NATIVE_PATH and rebuild."),
                SimulatorConnectionFailureKind.NativeLibraryMissing);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Keeps loader failures catchable even when the CLR cannot JIT the SDK-dependent method.
    /// </summary>
    private void RunConnectionWorker(
        TaskCompletionSource<bool> initialAttempt,
        CancellationToken cancellationToken)
    {
        try
        {
            RunConnectionLoop(initialAttempt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            initialAttempt.TrySetResult(false);
        }
        catch (Exception ex)
        {
            ReportRuntimeFailure(ex);
            initialAttempt.TrySetResult(false);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RunConnectionLoop(
        TaskCompletionSource<bool> initialAttempt,
        CancellationToken cancellationToken)
    {
        bool connectedOnce = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    OpenConnection();
                    connectedOnce = true;
                    initialAttempt.TrySetResult(true);

                    SetConnectionState(
                        SimulatorConnectionState.Connected,
                        UiText.T("Connected to Microsoft Flight Simulator."));

                    _logger.LogInformation(
                        "SimConnect connected at approximately {Rate} updates per second.",
                        _options.UpdatesPerSecond);

                    PumpExitReason exitReason = PumpConnectedSimulator(cancellationToken);
                    DisposeSimConnect();

                    if (exitReason == PumpExitReason.Cancelled)
                    {
                        return;
                    }

                    if (exitReason == PumpExitReason.FatalFailure)
                    {
                        SetConnectionState(
                            SimulatorConnectionState.Error,
                            _callbackFailureMessage ?? UiText.T("SimConnect reported a fatal connection error."),
                            _callbackFailureKind == SimulatorConnectionFailureKind.None
                                ? SimulatorConnectionFailureKind.Unexpected
                                : _callbackFailureKind);
                        return;
                    }

                    if (!_options.AutoReconnect)
                    {
                        SetConnectionState(
                            SimulatorConnectionState.Disconnected,
                            UiText.T("The simulator connection was lost."));
                        return;
                    }

                    SetConnectionState(
                        SimulatorConnectionState.Reconnecting,
                        UiText.T("The simulator connection was lost. Reconnecting."));
                }
                catch (Exception ex) when (connectedOnce && IsRetryableConnectionFailure(ex))
                {
                    DisposeSimConnect();
                    _logger.LogWarning(
                        "SimConnect is still unavailable while reconnecting (0x{HResult:X8}).",
                        ex.HResult);

                    SetConnectionState(
                        SimulatorConnectionState.Reconnecting,
                        UiText.T("Microsoft Flight Simulator is not answering SimConnect. Reconnecting."));
                }
                catch (Exception ex)
                {
                    DisposeSimConnect();
                    ReportRuntimeFailure(ex);
                    initialAttempt.TrySetResult(false);
                    return;
                }

                if (cancellationToken.WaitHandle.WaitOne(GetReconnectInterval()))
                {
                    return;
                }
            }
        }
        finally
        {
            DisposeSimConnect();
            initialAttempt.TrySetResult(false);
        }
    }

    private void OpenConnection()
    {
        _quitReceived = false;
        _callbackFailureKind = SimulatorConnectionFailureKind.None;
        _callbackFailureMessage = null;

        // With no HWND, the SDK-supported event handle signals exactly when ReceiveMessage
        // should be called. This avoids polling and keeps every SimConnect call on one thread.
        _messageSignal = new AutoResetEvent(false);
        _simConnect = new SimConnect(
            "StormATC",
            IntPtr.Zero,
            0,
            _messageSignal,
            0);

        RegisterDataDefinition(_simConnect);
        RegisterTrafficDataDefinition(_simConnect);
        RegisterAirportFacilityDefinition(_simConnect);
        _simConnect.MapClientEventToSimEvent(Events.SetCom1ActiveHz, "COM_RADIO_SET_HZ");
        _simConnect.MapClientEventToSimEvent(
            Events.SelectCom1Transmitter,
            "COM1_TRANSMIT_SELECT");

        _simConnect.OnRecvSimobjectData += OnReceiveSimObjectData;
        _simConnect.OnRecvSimobjectDataBytype += OnReceiveNearbyTraffic;
        _simConnect.OnRecvFacilityData += OnReceiveFacilityData;
        _simConnect.OnRecvFacilityDataEnd += OnReceiveFacilityDataEnd;
        _simConnect.OnRecvQuit += OnReceiveQuit;
        _simConnect.OnRecvException += OnReceiveException;
        SubscribeToFlightPlanEvents(_simConnect);

        int requestedRate = Math.Max(1, _options.UpdatesPerSecond);
        uint frameInterval = (uint)Math.Max(0, (30 / requestedRate) - 1);

        _simConnect.RequestDataOnSimObject(
            Requests.AircraftState,
            Definitions.AircraftState,
            SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SIM_FRAME,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
            0,
            frameInterval,
            0);

        _nextTrafficRequestAt = DateTimeOffset.MinValue;
        _trafficRequestPending = false;
    }

    private static void RegisterDataDefinition(SimConnect simConnect)
    {
        foreach ((string variable, string? unit, SIMCONNECT_DATATYPE type) in DataDefinition)
        {
            simConnect.AddToDataDefinition(
                Definitions.AircraftState,
                variable,
                unit,
                type,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);
        }

        simConnect.RegisterDataDefineStruct<AircraftStateStruct>(Definitions.AircraftState);
    }

    private static void RegisterTrafficDataDefinition(SimConnect simConnect)
    {
        foreach ((string variable, string? unit, SIMCONNECT_DATATYPE type) in TrafficDataDefinition)
        {
            simConnect.AddToDataDefinition(
                Definitions.NearbyTraffic,
                variable,
                unit,
                type,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);
        }

        simConnect.RegisterDataDefineStruct<NearbyTrafficStruct>(Definitions.NearbyTraffic);
    }

    private static void RegisterAirportFacilityDefinition(SimConnect simConnect)
    {
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "OPEN AIRPORT");
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "LATITUDE");
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "LONGITUDE");
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "ALTITUDE");
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "MAGVAR");
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "NAME64");
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "ICAO");

        AddFacilityChild(
            simConnect,
            "RUNWAY",
            [
                "LATITUDE",
                "LONGITUDE",
                "HEADING",
                "LENGTH",
                "WIDTH",
                "SURFACE",
                "PRIMARY_NUMBER",
                "PRIMARY_DESIGNATOR",
                "SECONDARY_NUMBER",
                "SECONDARY_DESIGNATOR",
            ]);
        AddFacilityChild(simConnect, "FREQUENCY", ["TYPE", "FREQUENCY"]);
        AddFacilityChild(
            simConnect,
            "TAXI_POINT",
            ["TYPE", "ORIENTATION", "BIAS_X", "BIAS_Z"]);
        AddFacilityChild(
            simConnect,
            "TAXI_PARKING",
            [
                "TYPE",
                "TAXI_POINT_TYPE",
                "NAME",
                "SUFFIX",
                "NUMBER",
                "ORIENTATION",
                "HEADING",
                "RADIUS",
                "BIAS_X",
                "BIAS_Z",
            ]);
        AddFacilityChild(
            simConnect,
            "TAXI_PATH",
            [
                "TYPE",
                "WIDTH",
                "WEIGHT",
                "RUNWAY_NUMBER",
                "RUNWAY_DESIGNATOR",
                "START",
                "END",
                "NAME_INDEX",
            ]);
        AddFacilityChild(simConnect, "TAXI_NAME", ["NAME"]);

        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, "CLOSE AIRPORT");

        simConnect.RegisterFacilityDataDefineStruct<AirportFacilityStruct>(
            SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT);
        simConnect.RegisterFacilityDataDefineStruct<RunwayFacilityStruct>(
            SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY);
        simConnect.RegisterFacilityDataDefineStruct<FrequencyFacilityStruct>(
            SIMCONNECT_FACILITY_DATA_TYPE.FREQUENCY);
        simConnect.RegisterFacilityDataDefineStruct<TaxiPointFacilityStruct>(
            SIMCONNECT_FACILITY_DATA_TYPE.TAXI_POINT);
        simConnect.RegisterFacilityDataDefineStruct<TaxiParkingFacilityStruct>(
            SIMCONNECT_FACILITY_DATA_TYPE.TAXI_PARKING);
        simConnect.RegisterFacilityDataDefineStruct<TaxiPathFacilityStruct>(
            SIMCONNECT_FACILITY_DATA_TYPE.TAXI_PATH);
        simConnect.RegisterFacilityDataDefineStruct<TaxiNameFacilityStruct>(
            SIMCONNECT_FACILITY_DATA_TYPE.TAXI_NAME);
    }

    private static void AddFacilityChild(
        SimConnect simConnect,
        string childName,
        IReadOnlyList<string> fields)
    {
        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, $"OPEN {childName}");
        foreach (string field in fields)
        {
            simConnect.AddToFacilityDefinition(Definitions.AirportFacility, field);
        }

        simConnect.AddToFacilityDefinition(Definitions.AirportFacility, $"CLOSE {childName}");
    }

    private PumpExitReason PumpConnectedSimulator(CancellationToken cancellationToken)
    {
        WaitHandle[] handles =
            [_messageSignal!, _airportRequestSignal, _radioTuneSignal, cancellationToken.WaitHandle];

        while (true)
        {
            int signalled = WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(1));
            if (signalled == 3)
            {
                return PumpExitReason.Cancelled;
            }

            if (signalled == 1)
            {
                StartNextAirportFacilityRequest();
            }
            else if (signalled == 2)
            {
                ApplyPendingRadioTunes();
            }
            else if (signalled == 0)
            {
                try
                {
                    _simConnect!.ReceiveMessage();
                }
                catch (COMException ex)
                {
                    _logger.LogWarning(
                        "Lost the SimConnect connection (0x{HResult:X8}).",
                        ex.HResult);
                    return PumpExitReason.ConnectionLost;
                }
            }

            RequestNearbyTrafficIfDue();

            if (_callbackFailureKind != SimulatorConnectionFailureKind.None)
            {
                return PumpExitReason.FatalFailure;
            }

            if (_quitReceived)
            {
                return PumpExitReason.ConnectionLost;
            }
        }
    }

    private void ApplyPendingRadioTunes()
    {
        while (_radioTuneQueue.TryDequeue(out RadioTuneRequest? request))
        {
            if (request.Completion.Task.IsCompleted)
            {
                continue;
            }

            if (_simConnect is null)
            {
                request.Completion.TrySetResult(false);
                continue;
            }

            try
            {
                uint hertz = ToFrequencyHertz(request.Frequency);

                _simConnect.TransmitClientEvent(
                    SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    Events.SetCom1ActiveHz,
                    hertz,
                    NotificationGroups.Highest,
                    SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                _simConnect.TransmitClientEvent(
                    SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    Events.SelectCom1Transmitter,
                    0,
                    NotificationGroups.Highest,
                    SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);

                request.Completion.TrySetResult(true);
                _logger.LogInformation(
                    "Tuned MSFS COM1 active to {Frequency} and selected COM1 for transmission.",
                    request.Frequency.ToDisplay());
            }
            catch (Exception ex)
            {
                request.Completion.TrySetResult(false);
                _logger.LogWarning(
                    ex,
                    "Tuning MSFS COM1 to {Frequency} failed.",
                    request.Frequency.ToDisplay());
            }
        }
    }

    private void RequestNearbyTrafficIfDue()
    {
        if (_simConnect is null)
        {
            return;
        }

        if (!_trafficOptions.CurrentValue.EnableLiveTraffic)
        {
            _trafficRequestPending = false;
            _trafficBuffer.Clear();
            Volatile.Write(ref _nearbyTraffic, []);
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_trafficRequestPending)
        {
            if (now - _trafficRequestStartedAt < TimeSpan.FromSeconds(10))
            {
                return;
            }

            _logger.LogDebug("The previous nearby-traffic request expired.");
            _trafficRequestPending = false;
        }

        if (now < _nextTrafficRequestAt)
        {
            return;
        }

        _trafficBuffer.Clear();
        _trafficRequestPending = true;
        _trafficRequestStartedAt = now;
        _nextTrafficRequestAt = now.AddSeconds(5);

        try
        {
            _simConnect.RequestDataOnSimObjectType(
                Requests.NearbyTraffic,
                Definitions.NearbyTraffic,
                20_000,
                SIMCONNECT_SIMOBJECT_TYPE.AIRCRAFT);
        }
        catch (Exception ex)
        {
            _trafficRequestPending = false;
            _logger.LogDebug(ex, "Requesting nearby MSFS traffic failed.");
        }
    }

    private void StartNextAirportFacilityRequest()
    {
        if (_activeAirportRequest is not null || _simConnect is null)
        {
            return;
        }

        while (_airportRequestQueue.TryDequeue(out AirportFacilityRequest? request))
        {
            if (request.Completion.Task.IsCompleted)
            {
                continue;
            }

            _activeAirportRequest = request;
            try
            {
                _simConnect.RequestFacilityData(
                    Definitions.AirportFacility,
                    Requests.AirportFacility,
                    request.Icao,
                    string.Empty);
                _logger.LogInformation(
                    "Requested live MSFS airport facility data for {Icao}.",
                    request.Icao);
            }
            catch (Exception ex)
            {
                _activeAirportRequest = null;
                request.Completion.TrySetResult(null);
                _logger.LogWarning(
                    ex,
                    "Requesting MSFS airport facility data for {Icao} failed.",
                    request.Icao);
                _airportRequestSignal.Set();
            }

            return;
        }
    }

    private void OnReceiveSimObjectData(
        SimConnect sender,
        SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if ((Requests)data.dwRequestID != Requests.AircraftState
            || data.dwData.Length == 0
            || data.dwData[0] is not AircraftStateStruct raw)
        {
            return;
        }

        if (!GeoPosition.TryCreate(raw.Latitude, raw.Longitude, out GeoPosition position)
            || !double.IsFinite(raw.IndicatedAltitude)
            || !double.IsFinite(raw.GroundSpeed))
        {
            _logger.LogDebug("Discarded an invalid SimConnect data frame.");
            return;
        }

        var aircraftState = new AircraftState
        {
            Timestamp = DateTimeOffset.UtcNow,
            Position = position,
            IndicatedAltitudeFeet = raw.IndicatedAltitude,
            PressureAltitudeFeet = double.IsFinite(raw.PressureAltitude)
                ? raw.PressureAltitude
                : raw.IndicatedAltitude,
            GroundSpeedKnots = raw.GroundSpeed,
            IndicatedAirspeedKnots = double.IsFinite(raw.IndicatedAirspeed)
                ? raw.IndicatedAirspeed
                : 0,
            TrueHeadingDegrees = raw.TrueHeading,
            MagneticHeadingDegrees = raw.MagneticHeading,
            VerticalSpeedFeetPerMinute = double.IsFinite(raw.VerticalSpeed)
                ? raw.VerticalSpeed
                : 0,
            IsOnGround = raw.OnGround != 0,
            ParkingBrakeSet = raw.ParkingBrake != 0,
            EnginesRunning = raw.EngineCombustion != 0,
            AircraftTitle = string.IsNullOrWhiteSpace(raw.Title)
                ? "MSFS aircraft"
                : raw.Title.Trim(),
            AtcIdentifier = raw.AtcIdentifier?.Trim() ?? string.Empty,
            AltitudeAboveGroundFeet = double.IsFinite(raw.AltitudeAboveGround)
                ? Math.Max(0, raw.AltitudeAboveGround)
                : 0,
            AutopilotEngaged = raw.AutopilotMaster != 0,
            GearHandleDown = raw.GearHandlePosition != 0,
            FlapsHandleIndex = double.IsFinite(raw.FlapsHandleIndex)
                ? Math.Max(0, (int)Math.Round(raw.FlapsHandleIndex))
                : 0,
            Radio = new RadioState
            {
                Com1Active = SafeFrequency(raw.Com1Active),
                Com1Standby = SafeFrequency(raw.Com1Standby),
                Com2Active = SafeFrequency(raw.Com2Active),
                Com2Standby = SafeFrequency(raw.Com2Standby),
                TransmitOnCom1 = raw.Com1Transmit != 0 || raw.Com2Transmit == 0,
            },
            Transponder = new TransponderState
            {
                Code = SafeSquawk(raw.TransponderCode),
                Mode = SafeTransponderMode(raw.TransponderState, raw.OnGround != 0),
            },
        };

        Volatile.Write(
            ref _latestWeather,
            MsfsEnvironmentMapper.MapWeather(
                raw.AmbientWindDirection,
                raw.AmbientWindVelocity,
                raw.SeaLevelPressure,
                raw.AmbientVisibility,
                raw.AmbientTemperature,
                raw.TrueHeading,
                raw.MagneticHeading));

        if (Interlocked.Exchange(ref _firstFrameLogged, 1) == 0)
        {
            _logger.LogInformation(
                "Received live MSFS aircraft data: {Latitude:F5}, {Longitude:F5}, "
                + "{Altitude:F0} ft, COM1 {Com1:F3} MHz, squawk {Squawk}.",
                raw.Latitude,
                raw.Longitude,
                raw.IndicatedAltitude,
                raw.Com1Active,
                aircraftState.Transponder.Code);
        }

        AircraftStateChanged?.Invoke(this, new AircraftStateChangedEventArgs(aircraftState));
    }

    private void OnReceiveNearbyTraffic(
        SimConnect sender,
        SIMCONNECT_RECV_SIMOBJECT_DATA_BYTYPE data)
    {
        if ((Requests)data.dwRequestID != Requests.NearbyTraffic)
        {
            return;
        }

        if (data.dwentrynumber == 0)
        {
            _trafficBuffer.Clear();
        }

        if (data.dwObjectID != SimConnect.SIMCONNECT_OBJECT_ID_USER
            && data.dwData.Length > 0
            && data.dwData[0] is NearbyTrafficStruct raw
            && GeoPosition.TryCreate(raw.Latitude, raw.Longitude, out GeoPosition position)
            && double.IsFinite(raw.IndicatedAltitude)
            && double.IsFinite(raw.GroundSpeed))
        {
            string callsign = !string.IsNullOrWhiteSpace(raw.AtcIdentifier)
                ? raw.AtcIdentifier.Trim()
                : !string.IsNullOrWhiteSpace(raw.Title)
                    ? raw.Title.Trim()
                    : $"TRAFFIC {data.dwObjectID}";

            _trafficBuffer.Add(new MsfsTrafficSnapshot
            {
                ObjectId = data.dwObjectID,
                Callsign = callsign,
                ModelTitle = raw.Title?.Trim() ?? string.Empty,
                Position = position,
                AltitudeFeet = raw.IndicatedAltitude,
                GroundSpeedKnots = Math.Max(0, raw.GroundSpeed),
                MagneticHeadingDegrees = double.IsFinite(raw.MagneticHeading)
                    ? raw.MagneticHeading
                    : 0,
                VerticalSpeedFeetPerMinute = double.IsFinite(raw.VerticalSpeed)
                    ? raw.VerticalSpeed
                    : 0,
                IsOnGround = raw.OnGround != 0,
            });
        }

        if (data.dwoutof == 0 || data.dwentrynumber + 1 >= data.dwoutof)
        {
            Volatile.Write(
                ref _nearbyTraffic,
                (IReadOnlyList<MsfsTrafficSnapshot>)[.. _trafficBuffer]);
            _trafficRequestPending = false;
        }
    }

    private void OnReceiveFacilityData(
        SimConnect sender,
        SIMCONNECT_RECV_FACILITY_DATA data)
    {
        if (data.UserRequestId != (uint)Requests.AirportFacility
            || _activeAirportRequest is not AirportFacilityRequest request
            || data.Data.Length == 0)
        {
            return;
        }

        uint index = data.ItemIndex;
        object raw = data.Data[0];
        switch ((SIMCONNECT_FACILITY_DATA_TYPE)data.Type)
        {
            case SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT
                when raw is AirportFacilityStruct airport:
                request.Snapshot.Airport = new MsfsAirportFacility(
                    airport.Icao ?? request.Icao,
                    airport.Name ?? request.Icao,
                    airport.Latitude,
                    airport.Longitude,
                    airport.AltitudeMeters,
                    airport.MagneticVariationDegrees);
                break;

            case SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY
                when raw is RunwayFacilityStruct runway:
                request.Snapshot.Runways[index] = new MsfsRunwayFacility(
                    index,
                    runway.Latitude,
                    runway.Longitude,
                    runway.HeadingDegrees,
                    runway.LengthMeters,
                    runway.WidthMeters,
                    runway.Surface,
                    runway.PrimaryNumber,
                    runway.PrimaryDesignator,
                    runway.SecondaryNumber,
                    runway.SecondaryDesignator);
                break;

            case SIMCONNECT_FACILITY_DATA_TYPE.FREQUENCY
                when raw is FrequencyFacilityStruct frequency:
                request.Snapshot.Frequencies[index] =
                    new MsfsFrequencyFacility(index, frequency.Type, frequency.FrequencyHertz);
                break;

            case SIMCONNECT_FACILITY_DATA_TYPE.TAXI_POINT
                when raw is TaxiPointFacilityStruct point:
                request.Snapshot.TaxiPoints[index] = new MsfsTaxiPointFacility(
                    index,
                    point.Type,
                    point.Orientation,
                    point.BiasXMeters,
                    point.BiasZMeters);
                break;

            case SIMCONNECT_FACILITY_DATA_TYPE.TAXI_PARKING
                when raw is TaxiParkingFacilityStruct parking:
                request.Snapshot.TaxiParkings[index] = new MsfsTaxiParkingFacility(
                    index,
                    parking.Type,
                    parking.TaxiPointType,
                    parking.Name,
                    parking.Suffix,
                    parking.Number,
                    parking.Orientation,
                    parking.HeadingDegrees,
                    parking.RadiusMeters,
                    parking.BiasXMeters,
                    parking.BiasZMeters);
                break;

            case SIMCONNECT_FACILITY_DATA_TYPE.TAXI_PATH
                when raw is TaxiPathFacilityStruct path:
                request.Snapshot.TaxiPaths[index] = new MsfsTaxiPathFacility(
                    index,
                    path.Type,
                    path.WidthMeters,
                    path.WeightLimitPounds,
                    path.RunwayNumber,
                    path.RunwayDesignator,
                    path.StartIndex,
                    path.EndIndex,
                    path.NameIndex);
                break;

            case SIMCONNECT_FACILITY_DATA_TYPE.TAXI_NAME
                when raw is TaxiNameFacilityStruct taxiName:
                request.Snapshot.TaxiNames[index] =
                    new MsfsTaxiNameFacility(index, taxiName.Name ?? string.Empty);
                break;
        }
    }

    private void OnReceiveFacilityDataEnd(
        SimConnect sender,
        SIMCONNECT_RECV_FACILITY_DATA_END data)
    {
        if (data.RequestId != (uint)Requests.AirportFacility
            || _activeAirportRequest is not AirportFacilityRequest request)
        {
            return;
        }

        AirportData? airport = null;
        try
        {
            airport = MsfsAirportFacilityMapper.Map(request.Snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Converting MSFS facility data for {Icao} into a taxi graph failed.",
                request.Icao);
        }

        _activeAirportRequest = null;
        request.Completion.TrySetResult(airport);

        if (airport is not null)
        {
            lock (_airportCacheLock)
            {
                _airportCache[airport.Icao] = airport;
            }

            _logger.LogInformation(
                "Loaded {Icao} from MSFS: {Runways} runway directions, {Stands} stands, "
                + "{Nodes} taxi nodes and {Edges} taxi paths.",
                airport.Icao,
                airport.Runways.Count,
                airport.Stands.Count,
                airport.TaxiGraph?.Nodes.Count ?? 0,
                airport.TaxiGraph?.Edges.Count ?? 0);
        }
        else
        {
            _logger.LogWarning(
                "MSFS returned no usable airport facility data for {Icao}.",
                request.Icao);
        }

        _airportRequestSignal.Set();
    }

    private static Frequency SafeFrequency(double megahertz) =>
        double.IsFinite(megahertz)
        && Frequency.TryFromMegahertz(
            (decimal)Math.Round(megahertz, 3),
            out Frequency frequency)
            ? frequency
            : Frequency.FromMegahertz(121.500m);

    private static TransponderMode SafeTransponderMode(int simulatorState, bool isOnGround) =>
        simulatorState switch
        {
            0 => TransponderMode.Off,
            1 => TransponderMode.Standby,
            2 => TransponderMode.Test,
            3 => TransponderMode.On,
            4 => TransponderMode.AltitudeReporting,
            _ => isOnGround
                ? TransponderMode.Standby
                : TransponderMode.AltitudeReporting,
        };

    private void OnReceiveQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        _logger.LogInformation("Microsoft Flight Simulator is shutting down.");
        _quitReceived = true;
    }

    private void OnReceiveException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        if ((SIMCONNECT_EXCEPTION)data.dwException == SIMCONNECT_EXCEPTION.VERSION_MISMATCH)
        {
            _callbackFailureKind = SimulatorConnectionFailureKind.VersionMismatch;
            _callbackFailureMessage =
                UiText.T("The installed SimConnect client is incompatible with this version of "
                + "Microsoft Flight Simulator.");

            _logger.LogError("SimConnect rejected StormATC because the client version mismatched.");
            return;
        }

        _logger.LogWarning(
            "SimConnect reported exception {ExceptionId} for send {SendId} at index {Index}.",
            data.dwException,
            data.dwSendID,
            data.dwIndex);
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancellationTokenSource? cancellation = _workerCancellation;
            Task? worker = _workerTask;

            if (cancellation is not null)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }

            if (worker is not null)
            {
                try
                {
                    await worker.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning(
                        "Timed out waiting for the SimConnect worker to stop; it will finish in the background.");
                    return;
                }
            }

            CleanupCompletedWorker();
            SetConnectionState(
                SimulatorConnectionState.Disconnected,
                UiText.T("Disconnected from Microsoft Flight Simulator."));
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void CleanupCompletedWorker()
    {
        if (_workerTask is { IsCompleted: false })
        {
            return;
        }

        _workerCancellation?.Dispose();
        _workerCancellation = null;
        _workerTask = null;
    }

    private void DisposeSimConnect()
    {
        if (_simConnect is not null)
        {
            try
            {
                _simConnect.OnRecvSimobjectData -= OnReceiveSimObjectData;
                _simConnect.OnRecvSimobjectDataBytype -= OnReceiveNearbyTraffic;
                _simConnect.OnRecvFacilityData -= OnReceiveFacilityData;
                _simConnect.OnRecvFacilityDataEnd -= OnReceiveFacilityDataEnd;
                UnsubscribeFromFlightPlanEvents(_simConnect);
                _simConnect.OnRecvQuit -= OnReceiveQuit;
                _simConnect.OnRecvException -= OnReceiveException;
                _simConnect.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring an error while disposing the SimConnect handle.");
            }
            finally
            {
                _simConnect = null;
            }
        }

        _messageSignal?.Dispose();
        _messageSignal = null;
        _trafficRequestPending = false;
        _trafficBuffer.Clear();
        Volatile.Write(ref _nearbyTraffic, []);
        Volatile.Write(ref _latestWeather, null);
        CompletePendingAirportRequestsWithNoData();
        CompletePendingRadioTunes(false);
    }

    private void CompletePendingAirportRequestsWithNoData()
    {
        _activeAirportRequest?.Completion.TrySetResult(null);
        _activeAirportRequest = null;

        while (_airportRequestQueue.TryDequeue(out AirportFacilityRequest? request))
        {
            request.Completion.TrySetResult(null);
        }
    }

    private void CompletePendingRadioTunes(bool result)
    {
        while (_radioTuneQueue.TryDequeue(out RadioTuneRequest? request))
        {
            request.Completion.TrySetResult(result);
        }
    }

    private void ReportRuntimeFailure(Exception exception)
    {
        if (FindException<DllNotFoundException>(exception) is { } nativeMissing)
        {
            _logger.LogError(nativeMissing, "The native SimConnect runtime could not be loaded.");
            SetConnectionState(
                SimulatorConnectionState.Error,
                UiText.T("The native SimConnect.dll could not be loaded. Verify that the x64 native DLL "
                + "is beside StormATC.exe."),
                SimulatorConnectionFailureKind.NativeLibraryMissing);
            return;
        }

        if (FindException<BadImageFormatException>(exception) is { } badImage)
        {
            _logger.LogError(badImage, "The SimConnect runtime architecture is incompatible.");
            SetConnectionState(
                SimulatorConnectionState.Error,
                UiText.T("SimConnect has the wrong architecture. StormATC and both SimConnect DLLs must be x64."),
                SimulatorConnectionFailureKind.ArchitectureMismatch);
            return;
        }

        if (FindException<FileNotFoundException>(exception) is { } managedMissing)
        {
            _logger.LogError(managedMissing, "The managed SimConnect wrapper could not be loaded.");
            SetConnectionState(
                SimulatorConnectionState.Error,
                UiText.T("The managed Microsoft.FlightSimulator.SimConnect.dll could not be loaded from "
                + "the StormATC application folder."),
                SimulatorConnectionFailureKind.ManagedAssemblyMissing);
            return;
        }

        if (FindException<FileLoadException>(exception) is { } managedLoadFailure)
        {
            _logger.LogError(managedLoadFailure, "The managed SimConnect wrapper could not be loaded.");
            SetConnectionState(
                SimulatorConnectionState.Error,
                UiText.T("The managed SimConnect wrapper was found but could not be loaded. Repair the MSFS SDK "
                + "and rebuild StormATC."),
                SimulatorConnectionFailureKind.ManagedAssemblyMissing);
            return;
        }

        if (FindException<COMException>(exception) is { } connectionFailure)
        {
            _logger.LogWarning(
                "Could not connect to SimConnect (0x{HResult:X8}). Is MSFS running?",
                connectionFailure.HResult);

            SetConnectionState(
                SimulatorConnectionState.Unavailable,
                UiText.T("Microsoft Flight Simulator is not running or did not accept the local SimConnect connection."),
                SimulatorConnectionFailureKind.SimulatorUnavailable);
            return;
        }

        _logger.LogError(exception, "SimConnect connection failed unexpectedly.");
        SetConnectionState(
            SimulatorConnectionState.Error,
            UiText.T($"SimConnect failed unexpectedly: {exception.GetBaseException().Message}"),
            SimulatorConnectionFailureKind.Unexpected);
    }

    private static bool IsRetryableConnectionFailure(Exception exception) =>
        FindException<COMException>(exception) is not null;

    private static TException? FindException<TException>(Exception? exception)
        where TException : Exception
    {
        while (exception is not null)
        {
            if (exception is TException match)
            {
                return match;
            }

            exception = exception.InnerException;
        }

        return null;
    }

    private TimeSpan GetReconnectInterval() =>
        _options.ReconnectInterval < TimeSpan.FromSeconds(1)
            ? TimeSpan.FromSeconds(1)
            : _options.ReconnectInterval;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _airportRequestSignal.Dispose();
        _radioTuneSignal.Dispose();
        _lifecycleLock.Dispose();
    }

#endif
}
