#if MSFS_SDK_AVAILABLE
using Microsoft.FlightSimulator.SimConnect;
#endif
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Application.FlightPlanning;

namespace StormATC.Infrastructure.Simulator;

/// <summary>The part of the connector that listens for flight plans.</summary>
public sealed partial class MsfsSimConnectConnector : ISimulatorFlightPlanEvents
{
#if MSFS_SDK_AVAILABLE
    /// <inheritdoc />
    public event EventHandler<SimulatorFlightPlanEventArgs>? FlightPlanAnnounced;

    /// <inheritdoc />
    public event EventHandler<SimulatorRouteEventArgs>? RouteReceived;

    /// <summary>The client data area the StormATC module publishes its routes in.</summary>
    private const string RouteClientDataName = "StormATC.Route";

    /// <summary>The size of that area, the largest SimConnect allows.</summary>
    private const uint RouteClientDataSize = 8192;

    private long _lastRouteSequence = -1;
    private int _secondsWithoutRoute;

    /// <summary>The module's message, as SimConnect hands the area over.</summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential,
        Pack = 1,
        CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private struct RouteClientData
    {
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.ByValTStr,
            SizeConst = (int)RouteClientDataSize)]
        public string Json;
    }

    /// <summary>
    /// Reads the routes the StormATC WASM module publishes: the route already there when StormATC
    /// connects, and every route published afterwards.
    /// </summary>
    private void SubscribeToRouteClientData(SimConnect simConnect)
    {
        _lastRouteSequence = -1;
        _secondsWithoutRoute = 0;
        simConnect.MapClientDataNameToID(RouteClientDataName, ClientDataAreas.Route);
        simConnect.AddToClientDataDefinition(Definitions.RouteClientData, 0, RouteClientDataSize, 0, 0);
        simConnect.RegisterStruct<SIMCONNECT_RECV_CLIENT_DATA, RouteClientData>(Definitions.RouteClientData);
        simConnect.OnRecvClientData += OnReceiveClientData;

        // Without the module the area does not exist and nothing ever arrives: that is not an error.
        simConnect.RequestClientData(
            ClientDataAreas.Route,
            Requests.RouteClientDataInitial,
            Definitions.RouteClientData,
            SIMCONNECT_CLIENT_DATA_PERIOD.ONCE,
            SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT,
            0,
            0,
            0);
        simConnect.RequestClientData(
            ClientDataAreas.Route,
            Requests.RouteClientDataUpdates,
            Definitions.RouteClientData,
            SIMCONNECT_CLIENT_DATA_PERIOD.ON_SET,
            SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT,
            0,
            0,
            0);
    }

    private void UnsubscribeFromRouteClientData(SimConnect simConnect) =>
        simConnect.OnRecvClientData -= OnReceiveClientData;

    private void OnReceiveClientData(SimConnect sender, SIMCONNECT_RECV_CLIENT_DATA data)
    {
        var request = (Requests)data.dwRequestID;
        if (request is not (Requests.RouteClientDataInitial or Requests.RouteClientDataUpdates)
            || data.dwData is not { Length: > 0 } values
            || values[0] is not RouteClientData area)
        {
            return;
        }

        if (!SimRouteMessage.TryParse(area.Json, out SimRouteMessage? message))
        {
            _logger.LogInformation(
                "The simulator module's data area was read ({Request}) but holds no route message yet ({Length} characters).",
                request,
                area.Json?.Length ?? 0);
            return;
        }

        // The same message can arrive twice (the first read and the first update).
        if (message.Sequence != 0 && message.Sequence == _lastRouteSequence)
        {
            return;
        }

        _lastRouteSequence = message.Sequence;
        bool initial = request == Requests.RouteClientDataInitial;
        _logger.LogInformation(
            "The StormATC module in the simulator published the {Source} route (message {Sequence}{Initial}).",
            message.Source,
            message.Sequence,
            initial ? ", read when StormATC connected" : string.Empty);

        RouteReceived?.Invoke(this, new SimulatorRouteEventArgs(message.Source, initial, message.RouteJson));
    }

    private void RaiseFlightPlanAnnounced(SimulatorFlightPlanEventKind kind, string? fileName)
    {
        _logger.LogInformation(
            "The simulator announced a flight plan event: {Kind}, file {FileName}.",
            kind,
            string.IsNullOrWhiteSpace(fileName) ? "(none)" : fileName);

        FlightPlanAnnounced?.Invoke(this, new SimulatorFlightPlanEventArgs(kind, fileName));
    }

    /// <summary>Asks the simulator to announce flight plans and loaded flights.</summary>
    private void SubscribeToFlightPlanEvents(SimConnect simConnect)
    {
        simConnect.SubscribeToSystemEvent(Events.FlightPlanActivated, "FlightPlanActivated");
        simConnect.SubscribeToSystemEvent(Events.FlightPlanDeactivated, "FlightPlanDeactivated");
        simConnect.SubscribeToSystemEvent(Events.FlightLoaded, "FlightLoaded");
        simConnect.SubscribeToSystemEvent(Events.OneSecond, "1sec");

        // "Sim" is 1 while the pilot is flying and 0 in the menus; ask for it once now too, since
        // StormATC usually connects after the flight began.
        _simRunning = -1;
        simConnect.SubscribeToSystemEvent(Events.SimRunning, "Sim");
        simConnect.OnRecvSystemState += OnReceiveSystemState;
        simConnect.RequestSystemState(Requests.SimState, "Sim");

        simConnect.OnRecvEventFilename += OnReceiveEventFilename;
        simConnect.OnRecvEvent += OnReceiveSystemEvent;
    }

    /// <summary>1 while the pilot is in control of an aircraft, 0 in the menus, -1 before the simulator said.</summary>
    private int _simRunning = -1;

    /// <summary>
    /// Whether the pilot is in the aircraft: the simulator says it is running and the data is that of
    /// a real position (in the menus the altitude is nonsense such as -19,000,000 ft).
    /// </summary>
    /// <param name="indicatedAltitudeFeet">The altitude the simulator reports.</param>
    private bool IsUserInAircraft(double indicatedAltitudeFeet) =>
        Volatile.Read(ref _simRunning) != 0 && indicatedAltitudeFeet > -2000;

    private void OnReceiveSystemState(SimConnect sender, SIMCONNECT_RECV_SYSTEM_STATE data)
    {
        if ((Requests)data.dwRequestID == Requests.SimState)
        {
            SetSimRunning((int)data.dwInteger);
        }
    }

    private void SetSimRunning(int value)
    {
        if (Interlocked.Exchange(ref _simRunning, value == 0 ? 0 : 1) != (value == 0 ? 0 : 1))
        {
            if (value == 0)
            {
                _logger.LogInformation("The simulator is not running a flight (menus): ATIS and the radio wait for the aircraft.");

                // The simulator sends no more frames in its menus, so the last one would keep saying the pilot is in the
                // aircraft. Say it now.
                if (_lastAircraftState is { } last)
                {
                    StormATC.Domain.Aircraft.AircraftState inMenus = last with { Timestamp = DateTimeOffset.UtcNow, InSimulatorMenus = true, IsUserInAircraft = false };
                    _lastAircraftState = inMenus;
                    AircraftStateChanged?.Invoke(this, new AircraftStateChangedEventArgs(inMenus));
                }
            }
            else
            {
                _logger.LogInformation("The pilot is in the aircraft.");
            }
        }
    }

    private void UnsubscribeFromFlightPlanEvents(SimConnect simConnect)
    {
        simConnect.OnRecvSystemState -= OnReceiveSystemState;
        simConnect.OnRecvEventFilename -= OnReceiveEventFilename;
        simConnect.OnRecvEvent -= OnReceiveSystemEvent;
    }

    private void OnReceiveEventFilename(SimConnect sender, SIMCONNECT_RECV_EVENT_FILENAME data)
    {
        switch ((Events)data.uEventID)
        {
            case Events.FlightPlanActivated:
                RaiseFlightPlanAnnounced(SimulatorFlightPlanEventKind.Activated, data.szFileName);
                RequestRouteOnce(sender);
                break;
            case Events.FlightLoaded:
                RaiseFlightPlanAnnounced(SimulatorFlightPlanEventKind.FlightLoaded, data.szFileName);
                RequestRouteOnce(sender);
                break;
        }
    }

    // FlightPlanDeactivated carries no file name; "1sec" is the clock for the module check.
    private void OnReceiveSystemEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
    {
        switch ((Events)data.uEventID)
        {
            case Events.SimRunning:
                SetSimRunning((int)data.dwData);
                break;

            case Events.FlightPlanDeactivated:
                RaiseFlightPlanAnnounced(SimulatorFlightPlanEventKind.Deactivated, null);
                break;

            case Events.OneSecond when _lastRouteSequence < 0 && ++_secondsWithoutRoute % 10 == 0:
                // Nothing has come from the module yet. It may have been loaded after StormATC
                // connected: ask for the area again every ten seconds until it answers.
                RequestRouteOnce(sender);
                break;
        }
    }

    /// <summary>Asks for the module's current message once, whatever it was published before.</summary>
    private void RequestRouteOnce(SimConnect simConnect)
    {
        try
        {
            simConnect.RequestClientData(
                ClientDataAreas.Route,
                Requests.RouteClientDataInitial,
                Definitions.RouteClientData,
                SIMCONNECT_CLIENT_DATA_PERIOD.ONCE,
                SIMCONNECT_CLIENT_DATA_REQUEST_FLAG.DEFAULT,
                0,
                0,
                0);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _logger.LogDebug(ex, "The route client data could not be requested.");
        }
    }
#else
    /// <inheritdoc />
    /// <remarks>Without the MSFS SDK nothing is ever announced.</remarks>
    public event EventHandler<SimulatorFlightPlanEventArgs>? FlightPlanAnnounced
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event EventHandler<SimulatorRouteEventArgs>? RouteReceived
    {
        add { }
        remove { }
    }
#endif
}
