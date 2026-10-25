#if MSFS_SDK_AVAILABLE
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
#endif
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Domain;
using StormATC.Domain.ValueObjects;

namespace StormATC.Infrastructure.Simulator;

/// <summary>The part of the connector that moves the aircraft and shuts its systems down.</summary>
public sealed partial class MsfsSimConnectConnector
{
#if MSFS_SDK_AVAILABLE
    /// <summary>First definition identifier handed to the one-variable definitions used to switch things off.</summary>
    private const int SwitchDefinitionBase = 100;

    /// <summary>
    /// What the shutdown switches off, one variable each so that a variable the aircraft does not have (the
    /// simulator refuses it) does not spoil the others. Each is set to 0.
    /// </summary>
    private static readonly string[] ShutdownVariables =
    [
        "GENERAL ENG COMBUSTION:1",
        "GENERAL ENG COMBUSTION:2",
        "GENERAL ENG COMBUSTION:3",
        "GENERAL ENG COMBUSTION:4",
        "AVIONICS MASTER SWITCH:1",
        "ELECTRICAL MASTER BATTERY:1",
        "ELECTRICAL MASTER BATTERY:2",
        "LIGHT NAV",
        "LIGHT BEACON",
        "LIGHT STROBE",
        "LIGHT LANDING",
        "LIGHT TAXI",
        "LIGHT LOGO",
        "LIGHT WING",
        "LIGHT CABIN",
        "LIGHT PANEL",
        "AUTOPILOT MASTER",
    ];

    private readonly ConcurrentQueue<AircraftControlRequest> _aircraftControlQueue = new();
    private readonly AutoResetEvent _aircraftControlSignal = new(false);
    private readonly Dictionary<string, int> _switchDefinitions = [];

    private sealed record AircraftControlRequest(
        Func<SimConnect, bool> Apply,
        TaskCompletionSource<bool> Completion);

    /// <summary>The position and speed the aircraft is given when it is moved.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PlacementStruct
    {
        public double Latitude;
        public double Longitude;
        public double TrueHeading;
        public double VelocityBodyX;
        public double VelocityBodyY;
        public double VelocityBodyZ;
    }

    private void RegisterAircraftControl(SimConnect simConnect)
    {
        _switchDefinitions.Clear();
        foreach ((string variable, string unit) in new[]
        {
            ("PLANE LATITUDE", "degrees"),
            ("PLANE LONGITUDE", "degrees"),
            ("PLANE HEADING DEGREES TRUE", "degrees"),
            ("VELOCITY BODY X", "feet per second"),
            ("VELOCITY BODY Y", "feet per second"),
            ("VELOCITY BODY Z", "feet per second"),
        })
        {
            simConnect.AddToDataDefinition(
                Definitions.Placement,
                variable,
                unit,
                SIMCONNECT_DATATYPE.FLOAT64,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);
        }

        simConnect.RegisterDataDefineStruct<PlacementStruct>(Definitions.Placement);
        simConnect.MapClientEventToSimEvent(Events.EngineAutoShutdown, "ENGINE_AUTO_SHUTDOWN");
        simConnect.MapClientEventToSimEvent(Events.ParkingBrakes, "PARKING_BRAKES");
    }

    /// <inheritdoc />
    public Task<bool> PlaceAircraftAsync(
        GeoPosition position,
        double trueHeadingDegrees,
        CancellationToken cancellationToken)
    {
        double heading = ((trueHeadingDegrees % 360) + 360) % 360;
        return EnqueueAircraftControlAsync(
            simConnect =>
            {
                simConnect.SetDataOnSimObject(
                    Definitions.Placement,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                    new PlacementStruct
                    {
                        Latitude = position.Latitude,
                        Longitude = position.Longitude,
                        TrueHeading = heading,
                    });
                _logger.LogInformation(
                    "Asked the simulator to place the aircraft at {Position}, heading {Heading:F0} true.",
                    position.ToDisplay(),
                    heading);
                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ShutDownAircraftAsync(CancellationToken cancellationToken) =>
        EnqueueAircraftControlAsync(
            simConnect =>
            {
                simConnect.TransmitClientEvent(
                    SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    Events.EngineAutoShutdown,
                    0,
                    NotificationGroups.Highest,
                    SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);

                foreach (string variable in ShutdownVariables)
                {
                    SetSwitchOff(simConnect, variable);
                }

                _logger.LogInformation("Asked the simulator to shut the aircraft down (engines, electrics, avionics, lights).");
                return true;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> ToggleParkingBrakeAsync(CancellationToken cancellationToken) =>
        EnqueueAircraftControlAsync(
            simConnect =>
            {
                simConnect.TransmitClientEvent(
                    SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    Events.ParkingBrakes,
                    0,
                    NotificationGroups.Highest,
                    SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                return true;
            },
            cancellationToken);

    private void SetSwitchOff(SimConnect simConnect, string variable)
    {
        if (!_switchDefinitions.TryGetValue(variable, out int definition))
        {
            definition = SwitchDefinitionBase + _switchDefinitions.Count;
            simConnect.AddToDataDefinition(
                (Definitions)definition,
                variable,
                "bool",
                SIMCONNECT_DATATYPE.INT32,
                0.0f,
                SimConnect.SIMCONNECT_UNUSED);
            _switchDefinitions[variable] = definition;
        }

        simConnect.SetDataOnSimObject(
            (Definitions)definition,
            SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_DATA_SET_FLAG.DEFAULT,
            0);
    }

    /// <summary>Hands a request to the worker, the only thread that talks to SimConnect, and waits for its answer.</summary>
    private async Task<bool> EnqueueAircraftControlAsync(Func<SimConnect, bool> apply, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ConnectionState != SimulatorConnectionState.Connected)
        {
            _logger.LogInformation("The aircraft cannot be controlled: SimConnect is not connected.");
            return false;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _aircraftControlQueue.Enqueue(new AircraftControlRequest(apply, completion));
        _aircraftControlSignal.Set();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            completion.TrySetResult(false);
            _logger.LogWarning("The simulator did not take the aircraft request in time.");
            return false;
        }
    }

    private void ApplyPendingAircraftControl()
    {
        while (_aircraftControlQueue.TryDequeue(out AircraftControlRequest? request))
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
                request.Completion.TrySetResult(request.Apply(_simConnect));
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                request.Completion.TrySetResult(false);
                _logger.LogWarning(ex, "The simulator refused a request about the aircraft.");
            }
        }
    }

    private void CompletePendingAircraftControl(bool result)
    {
        while (_aircraftControlQueue.TryDequeue(out AircraftControlRequest? request))
        {
            request.Completion.TrySetResult(result);
        }
    }
#else
    /// <inheritdoc />
    public Task<bool> PlaceAircraftAsync(
        GeoPosition position,
        double trueHeadingDegrees,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogWarning("Cannot place the aircraft: the SimConnect SDK is unavailable.");
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<bool> ShutDownAircraftAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogWarning("Cannot shut the aircraft down: the SimConnect SDK is unavailable.");
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<bool> ToggleParkingBrakeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
#endif
}
