// StormATC route reader: a standalone WASM module for Microsoft Flight Simulator 2024.
//
// It is loaded with the simulator, in the menus as well as in flight, and reads the flight plan
// route the pilot makes in the EFB through the Planned Route API. Whenever the pilot sends the plan
// to ATC or to the avionics, or the EFB's route changes, it publishes the route as JSON in a
// SimConnect client data area that StormATC reads.
//
// What it publishes (see route_json.h for the JSON):
//   "atc"       the EFB's route, when the simulator announces that a flight plan was activated
//               (what "Send to ATC" and "Send to ATC and avionics" produce)
//   "avionics"  the route pushed to the avionics from the EFB
//   "efb"       the EFB's route when it changes (the plan being edited)
//
// The message goes to StormATC by two roads, so that one that fails does not lose the plan:
//   - the SimConnect client data area above, which StormATC reads through SimConnect;
//   - an HTTP POST to StormATC's local panel server (127.0.0.1), the same address the toolbar
//     panel uses, through the simulator's network API.
// StormATC applies a plan once: the second copy of a plan it already has changes nothing.
//
// The module only reads: it never changes the plan, the aircraft or the simulator.

#include <MSFS/MSFS.h>
#include <MSFS/MSFS_Network.h>
#include <MSFS/MSFS_PlannedRoute.h>
#include <MSFS/MSFS_WindowsTypes.h>
#include <SimConnect.h>

#include <stdio.h>
#include <string.h>

#include "route_json.h"

namespace
{
const char* const ClientName = "StormATC route reader";
const char* const ClientDataName = "StormATC.Route";

// StormATC's local server (its default port; see ToolbarPanelOptions.Port).
const char* const ServerAddress = "http://127.0.0.1:48765/api/flightplan";

// The largest client data area SimConnect allows.
const DWORD AreaSize = 8192;

enum Ids : DWORD
{
    AreaId = 1,
    DefinitionId = 1,
    EventEverySecond = 1,
    EventFlightPlanActivated = 2,
    EventFlightPlanDeactivated = 3,
};

// HANDLE is an integer in the simulator's Windows types: 0 means "not open".
HANDLE g_simConnect = 0;

// Requests that are in flight keep their body until the simulator is done with it: a few buffers
// used in turn.
const int HttpBuffers = 4;
char g_httpBodies[HttpBuffers][AreaSize];
int g_httpNext = 0;
int g_httpFailures = 0;
long g_sequence = 0;
int g_seconds = 0;
char g_lastEfbRoute[AreaSize] = "";
bool g_registeredForBroadcast = false;

void OnHttpDone(FsNetworkRequestId, int errorCode, void*)
{
    // The simulator reports a failure when StormATC is not running: that is normal, say it once.
    if (errorCode != 0 && g_httpFailures++ == 0)
    {
        printf("StormATC route reader: the HTTP road to StormATC failed (code %d); the SimConnect road remains.\n", errorCode);
    }
}

// The second road to StormATC: the same message, posted to its local server.
void PostToStormAtc(const char* message)
{
    static char* headers[2] = {const_cast<char*>("X-StormATC: 1"), const_cast<char*>("Content-Type: application/json")};

    char* body = g_httpBodies[g_httpNext];
    g_httpNext = (g_httpNext + 1) % HttpBuffers;
    strncpy(body, message, AreaSize - 1);
    body[AreaSize - 1] = '\0';

    FsNetworkHttpRequestParam param;
    memset(&param, 0, sizeof(param));
    param.headerOptions = headers;
    param.headerOptionsSize = 2;
    param.data = reinterpret_cast<unsigned char*>(body);
    param.dataSize = static_cast<unsigned int>(strlen(body));

    fsNetworkHttpRequestPost(ServerAddress, &param, OnHttpDone, nullptr);
}

// Publishes a message in the client data area: SimConnect copies it, and StormATC, which asked to be
// told when the area changes, reads it.
void Publish(const char* source, const FsPlannedRoute& route)
{
    if (g_simConnect == 0)
    {
        return;
    }

    // A fixed-size area: the message is zero padded so no earlier text is left after its end.
    static char message[AreaSize];
    memset(message, 0, sizeof(message));

    g_sequence++;
    if (!stormatc::WriteMessage(message, sizeof(message), g_sequence, source, route))
    {
        // Too long: the writer kept a valid JSON with the legs that fit.
        printf("StormATC route reader: the route is longer than the area; some legs were left out.\n");
    }

    SimConnect_SetClientData(
        g_simConnect, AreaId, DefinitionId, SIMCONNECT_CLIENT_DATA_SET_FLAG_DEFAULT, 0, AreaSize, message);
    PostToStormAtc(message);
}

void PublishHello()
{
    if (g_simConnect == 0)
    {
        return;
    }

    static char message[AreaSize];
    memset(message, 0, sizeof(message));
    g_sequence++;
    stormatc::WriteHello(message, sizeof(message), g_sequence);
    SimConnect_SetClientData(
        g_simConnect, AreaId, DefinitionId, SIMCONNECT_CLIENT_DATA_SET_FLAG_DEFAULT, 0, AreaSize, message);
    PostToStormAtc(message);
}

// The route as the EFB shows it now; null when the EFB has none.
const FsPlannedRoute* EfbRoute()
{
    return fsPlannedRouteGetEfbRoute();
}

bool HasDeparture(const FsPlannedRoute& route)
{
    return route.departureAirport.ident[0] != '\0' || route.departureAirport.airport[0] != '\0';
}

void PublishEfbRoute(const char* source, bool onlyIfChanged)
{
    const FsPlannedRoute* route = EfbRoute();
    if (route == nullptr || !HasDeparture(*route))
    {
        return;
    }

    static char text[AreaSize];
    stormatc::WriteMessage(text, sizeof(text), 0, "efb", *route);

    if (onlyIfChanged && strcmp(text, g_lastEfbRoute) == 0)
    {
        return;
    }

    strncpy(g_lastEfbRoute, text, sizeof(g_lastEfbRoute) - 1);
    Publish(source, *route);
}

// The pilot pushed the route from the EFB to the avionics.
void OnBroadcast(const FsPlannedRoute* route, void*)
{
    if (route != nullptr && HasDeparture(*route))
    {
        Publish("avionics", *route);
    }
}

void CALLBACK Dispatch(SIMCONNECT_RECV* data, DWORD, void*)
{
    switch (data->dwID)
    {
        case SIMCONNECT_RECV_ID_EVENT:
        {
            const auto* event = static_cast<const SIMCONNECT_RECV_EVENT*>(static_cast<const void*>(data));
            if (event->uEventID == EventEverySecond)
            {
                // The EFB's route changes with every edit: look every two seconds.
                g_seconds++;
                if (g_seconds % 2 == 0)
                {
                    PublishEfbRoute("efb", true);
                }
            }

            break;
        }

        case SIMCONNECT_RECV_ID_EVENT_FILENAME:
        {
            const auto* event = static_cast<const SIMCONNECT_RECV_EVENT_FILENAME*>(static_cast<const void*>(data));
            if (event->uEventID == EventFlightPlanActivated)
            {
                // "Send to ATC": the plan the pilot sent is the EFB's route.
                PublishEfbRoute("atc", false);
            }

            break;
        }

        default:
            break;
    }
}
} // namespace

extern "C"
{
MSFS_CALLBACK void module_init(void)
{
    if (SimConnect_Open(&g_simConnect, ClientName, nullptr, 0, 0, 0) != S_OK)
    {
        printf("StormATC route reader: SimConnect could not be opened.\n");
        g_simConnect = 0;
        return;
    }

    SimConnect_MapClientDataNameToID(g_simConnect, ClientDataName, AreaId);
    SimConnect_CreateClientData(g_simConnect, AreaId, AreaSize, SIMCONNECT_CREATE_CLIENT_DATA_FLAG_DEFAULT);
    SimConnect_AddToClientDataDefinition(g_simConnect, DefinitionId, 0, AreaSize, 0, 0);

    SimConnect_SubscribeToSystemEvent(g_simConnect, EventEverySecond, "1sec");
    SimConnect_SubscribeToSystemEvent(g_simConnect, EventFlightPlanActivated, "FlightPlanActivated");
    SimConnect_SubscribeToSystemEvent(g_simConnect, EventFlightPlanDeactivated, "FlightPlanDeactivated");
    SimConnect_CallDispatch(g_simConnect, Dispatch, nullptr);

    g_registeredForBroadcast = fsPlannedRouteRegisterForBroadcast(OnBroadcast, nullptr);
    printf("StormATC route reader started (broadcast %s).\n", g_registeredForBroadcast ? "registered" : "not available");

    // Tell StormATC that the module is running, then publish a plan that is already in the EFB.
    PublishHello();
    PublishEfbRoute("efb", false);
}

MSFS_CALLBACK void module_deinit(void)
{
    if (g_registeredForBroadcast)
    {
        fsPlannedRouteUnregisterForBroadcast(OnBroadcast);
        g_registeredForBroadcast = false;
    }

    if (g_simConnect != 0)
    {
        SimConnect_Close(g_simConnect);
        g_simConnect = 0;
    }
}
}
