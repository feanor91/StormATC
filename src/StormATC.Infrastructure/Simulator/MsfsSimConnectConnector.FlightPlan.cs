#if MSFS_SDK_AVAILABLE
using Microsoft.FlightSimulator.SimConnect;
#endif
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;

namespace StormATC.Infrastructure.Simulator;

/// <summary>The part of the connector that listens for flight plans.</summary>
public sealed partial class MsfsSimConnectConnector : ISimulatorFlightPlanEvents
{
#if MSFS_SDK_AVAILABLE
    /// <inheritdoc />
    public event EventHandler<SimulatorFlightPlanEventArgs>? FlightPlanAnnounced;

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
#endif
}
