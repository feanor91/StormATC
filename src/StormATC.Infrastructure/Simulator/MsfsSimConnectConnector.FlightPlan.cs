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
            || values[0] is not RouteClientData area
            || !SimRouteMessage.TryParse(area.Json, out SimRouteMessage? message))
        {
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
        simConnect.OnRecvEventFilename += OnReceiveEventFilename;
        simConnect.OnRecvEvent += OnReceiveSystemEvent;
    }

    private void UnsubscribeFromFlightPlanEvents(SimConnect simConnect)
    {
        simConnect.OnRecvEventFilename -= OnReceiveEventFilename;
        simConnect.OnRecvEvent -= OnReceiveSystemEvent;
    }

    private void OnReceiveEventFilename(SimConnect sender, SIMCONNECT_RECV_EVENT_FILENAME data)
    {
        switch ((Events)data.uEventID)
        {
            case Events.FlightPlanActivated:
                RaiseFlightPlanAnnounced(SimulatorFlightPlanEventKind.Activated, data.szFileName);
                break;
            case Events.FlightLoaded:
                RaiseFlightPlanAnnounced(SimulatorFlightPlanEventKind.FlightLoaded, data.szFileName);
                break;
        }
    }

    // FlightPlanDeactivated carries no file name.
    private void OnReceiveSystemEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
    {
        if ((Events)data.uEventID == Events.FlightPlanDeactivated)
        {
            RaiseFlightPlanAnnounced(SimulatorFlightPlanEventKind.Deactivated, null);
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
