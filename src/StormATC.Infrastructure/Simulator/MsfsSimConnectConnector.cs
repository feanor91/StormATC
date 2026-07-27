using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;
using StormATC.Domain;
using StormATC.Domain.ValueObjects;

#if MSFS_SDK_AVAILABLE
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
/// not vendored in this repository. See <c>docs/simconnect-setup.md</c>. StormATC only reads
/// simulator variables; it never writes to the simulator.
/// </remarks>
public sealed partial class MsfsSimConnectConnector : ISimulatorConnector
{
    private readonly ILogger<MsfsSimConnectConnector> _logger;
    private readonly SimulatorOptions _options;

    /// <summary>Creates the connector.</summary>
    public MsfsSimConnectConnector(
        IOptions<SimulatorOptions> options,
        ILogger<MsfsSimConnectConnector> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
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
    public string StatusMessage { get; private set; } = "SimConnect has not been started.";

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

#if !MSFS_SDK_AVAILABLE

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
            "Disconnected from Microsoft Flight Simulator.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

#else

    /// <summary>Identifies our single data definition and request.</summary>
    private enum Definitions { AircraftState = 0 }
    private enum Requests { AircraftState = 0 }

    private enum PumpExitReason
    {
        Cancelled,
        ConnectionLost,
        FatalFailure,
    }

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

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

    /// <summary>
    /// The simulator variables StormATC reads, laid out to match <see cref="AircraftStateStruct"/>.
    /// </summary>
    private static readonly (string Variable, string Unit, SIMCONNECT_DATATYPE Type)[] DataDefinition =
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
                "Connecting to Microsoft Flight Simulator.");

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
                "SimConnect cannot load because StormATC is not running as a 64-bit process.",
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
                "The managed SimConnect wrapper is missing from the StormATC application folder. "
                + "Rebuild with the MSFS SDK configured.",
                SimulatorConnectionFailureKind.ManagedAssemblyMissing);
            return false;
        }

        string nativePath = Path.Combine(AppContext.BaseDirectory, "SimConnect.dll");
        if (!File.Exists(nativePath))
        {
            SetConnectionState(
                SimulatorConnectionState.Error,
                "The native SimConnect.dll is missing from the StormATC application folder. "
                + "Configure MsfsSimConnectNativePath or MSFS_SIMCONNECT_NATIVE_PATH and rebuild.",
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
                        "Connected to Microsoft Flight Simulator.");

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
                            _callbackFailureMessage ?? "SimConnect reported a fatal connection error.",
                            _callbackFailureKind == SimulatorConnectionFailureKind.None
                                ? SimulatorConnectionFailureKind.Unexpected
                                : _callbackFailureKind);
                        return;
                    }

                    if (!_options.AutoReconnect)
                    {
                        SetConnectionState(
                            SimulatorConnectionState.Disconnected,
                            "The simulator connection was lost.");
                        return;
                    }

                    SetConnectionState(
                        SimulatorConnectionState.Reconnecting,
                        "The simulator connection was lost. Reconnecting.");
                }
                catch (Exception ex) when (connectedOnce && IsRetryableConnectionFailure(ex))
                {
                    DisposeSimConnect();
                    _logger.LogWarning(
                        "SimConnect is still unavailable while reconnecting (0x{HResult:X8}).",
                        ex.HResult);

                    SetConnectionState(
                        SimulatorConnectionState.Reconnecting,
                        "Microsoft Flight Simulator is not answering SimConnect. Reconnecting.");
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

        _simConnect.OnRecvSimobjectData += OnReceiveSimObjectData;
        _simConnect.OnRecvQuit += OnReceiveQuit;
        _simConnect.OnRecvException += OnReceiveException;

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
    }

    private static void RegisterDataDefinition(SimConnect simConnect)
    {
        foreach ((string variable, string unit, SIMCONNECT_DATATYPE type) in DataDefinition)
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

    private PumpExitReason PumpConnectedSimulator(CancellationToken cancellationToken)
    {
        WaitHandle[] handles = [_messageSignal!, cancellationToken.WaitHandle];

        while (true)
        {
            int signalled = WaitHandle.WaitAny(handles);
            if (signalled == 1)
            {
                return PumpExitReason.Cancelled;
            }

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
            AircraftTitle = "MSFS aircraft",
            Radio = new RadioState
            {
                Com1Active = SafeFrequency(raw.Com1Active),
                Com1Standby = SafeFrequency(raw.Com1Standby),
                Com2Active = SafeFrequency(raw.Com2Active),
                Com2Standby = SafeFrequency(raw.Com2Standby),
                TransmitOnCom1 = true,
            },
            Transponder = new TransponderState
            {
                Code = SafeSquawk(raw.TransponderCode),
                Mode = raw.OnGround != 0
                    ? TransponderMode.Standby
                    : TransponderMode.AltitudeReporting,
            },
        };

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

    private static Frequency SafeFrequency(double megahertz) =>
        double.IsFinite(megahertz)
        && Frequency.TryFromMegahertz(
            (decimal)Math.Round(megahertz, 3),
            out Frequency frequency)
            ? frequency
            : Frequency.FromMegahertz(121.500m);

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
                "The installed SimConnect client is incompatible with this version of "
                + "Microsoft Flight Simulator.";

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
                "Disconnected from Microsoft Flight Simulator.");
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
    }

    private void ReportRuntimeFailure(Exception exception)
    {
        if (FindException<DllNotFoundException>(exception) is { } nativeMissing)
        {
            _logger.LogError(nativeMissing, "The native SimConnect runtime could not be loaded.");
            SetConnectionState(
                SimulatorConnectionState.Error,
                "The native SimConnect.dll could not be loaded. Verify that the x64 native DLL "
                + "is beside StormATC.exe.",
                SimulatorConnectionFailureKind.NativeLibraryMissing);
            return;
        }

        if (FindException<BadImageFormatException>(exception) is { } badImage)
        {
            _logger.LogError(badImage, "The SimConnect runtime architecture is incompatible.");
            SetConnectionState(
                SimulatorConnectionState.Error,
                "SimConnect has the wrong architecture. StormATC and both SimConnect DLLs must be x64.",
                SimulatorConnectionFailureKind.ArchitectureMismatch);
            return;
        }

        if (FindException<FileNotFoundException>(exception) is { } managedMissing)
        {
            _logger.LogError(managedMissing, "The managed SimConnect wrapper could not be loaded.");
            SetConnectionState(
                SimulatorConnectionState.Error,
                "The managed Microsoft.FlightSimulator.SimConnect.dll could not be loaded from "
                + "the StormATC application folder.",
                SimulatorConnectionFailureKind.ManagedAssemblyMissing);
            return;
        }

        if (FindException<FileLoadException>(exception) is { } managedLoadFailure)
        {
            _logger.LogError(managedLoadFailure, "The managed SimConnect wrapper could not be loaded.");
            SetConnectionState(
                SimulatorConnectionState.Error,
                "The managed SimConnect wrapper was found but could not be loaded. Repair the MSFS SDK "
                + "and rebuild StormATC.",
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
                "Microsoft Flight Simulator is not running or did not accept the local SimConnect connection.",
                SimulatorConnectionFailureKind.SimulatorUnavailable);
            return;
        }

        _logger.LogError(exception, "SimConnect connection failed unexpectedly.");
        SetConnectionState(
            SimulatorConnectionState.Error,
            $"SimConnect failed unexpectedly: {exception.GetBaseException().Message}",
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
        _lifecycleLock.Dispose();
    }

#endif
}
