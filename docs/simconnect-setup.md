# SimConnect setup

> StormATC is an entertainment product for flight simulation only. It must not be used for
> real-world aviation, navigation, flight planning, or air traffic control.

StormATC builds, runs, and passes its test suite without Microsoft Flight Simulator or the
SimConnect SDK. That build uses the scripted mock aircraft and explains on the Diagnostics page
that SimConnect was unavailable at build time.

Configure the SDK only when StormATC should follow a real MSFS flight.

## Supported simulators

StormATC uses the public SimConnect API from the MSFS 2020 SDK. It reads aircraft and airport
facility data and sends one radio event to tune COM1 to the published departure ATIS when a
flight is loaded. Microsoft documents that a client compiled with the 2020 SDK also runs against
MSFS 2024 as a legacy client. It will not receive 2024-only API features, which StormATC does not
currently need.

- [MSFS 2020 SimConnect documentation](https://docs.flightsimulator.com/html/Programming_Tools/SimConnect/SimConnect_SDK.htm)
- [MSFS 2024 SimConnect compatibility](https://docs.flightsimulator.com/msfs2024/html/6_Programming_APIs/SimConnect/SimConnect_SDK.htm)

## Why the SDK files are not in the repository

The managed wrapper and native SimConnect runtime come from Microsoft's SDK. StormATC does not
vendor them. `.gitignore` excludes `lib/msfs/`, `Microsoft.FlightSimulator.SimConnect.dll`, and
`SimConnect.dll`, so every developer supplies the files from their own SDK installation.

Both files are required at runtime:

| File | Typical SDK location | Purpose |
| --- | --- | --- |
| `Microsoft.FlightSimulator.SimConnect.dll` | `C:\MSFS SDK\SimConnect SDK\lib\managed\` | .NET wrapper |
| `SimConnect.dll` | `C:\MSFS SDK\SimConnect SDK\lib\` | x64 native runtime |

StormATC's SimConnect projects and executable explicitly target x64.

## Install the SDK

In MSFS 2020, enable **Options > General > Developers > Developer Mode**, then choose
**Help > SDK Installer (Core)** from the developer menu. Install or repair the Core SDK.

## How the build finds the files

The managed wrapper is resolved in this order:

1. `-p:MsfsSimConnectPath=<full path>`
2. `MSFS_SIMCONNECT_PATH`
3. `lib/msfs/Microsoft.FlightSimulator.SimConnect.dll`

The native runtime is resolved in this order:

1. `-p:MsfsSimConnectNativePath=<full path>`
2. `MSFS_SIMCONNECT_NATIVE_PATH`
3. `SimConnect.dll` beside the managed wrapper
4. `SimConnect.dll` one directory above the managed wrapper (the official SDK layout)

When the managed wrapper is absent, `MSFS_SDK_AVAILABLE` is not defined and the stub connector
is compiled. The full solution must build in both configurations.

### Build directly from the official SDK

The native path is inferred automatically from the standard SDK layout:

```powershell
dotnet build StormATC.sln -c Release `
  -p:MsfsSimConnectPath="C:\MSFS SDK\SimConnect SDK\lib\managed\Microsoft.FlightSimulator.SimConnect.dll"
```

For a nonstandard layout, pass both paths:

```powershell
dotnet build StormATC.sln -c Release `
  -p:MsfsSimConnectPath="D:\sdk\managed\Microsoft.FlightSimulator.SimConnect.dll" `
  -p:MsfsSimConnectNativePath="D:\sdk\native\SimConnect.dll"
```

### Use the untracked local folder

Create `lib\msfs`, then copy both DLLs into it:

```powershell
New-Item -ItemType Directory -Force .\lib\msfs
Copy-Item "C:\MSFS SDK\SimConnect SDK\lib\managed\Microsoft.FlightSimulator.SimConnect.dll" .\lib\msfs\
Copy-Item "C:\MSFS SDK\SimConnect SDK\lib\SimConnect.dll" .\lib\msfs\
dotnet build StormATC.sln -c Release
```

The final output beside `StormATC.exe` must contain both DLLs, and
`StormATC.deps.json` must list `Microsoft.FlightSimulator.SimConnect`.

## Enable MSFS in StormATC

`Simulator:PreferredConnector` defaults to `MSFS`, so there is nothing to change for a fresh
configuration. If **Mock** was selected earlier, choose **Settings > Simulator connector > MSFS**
and save. The change takes effect immediately.

If MSFS is not accepting connections, StormATC runs the mock aircraft and retries at
`Simulator:ReconnectInterval`. Packaging, architecture, and version errors do not trigger a
pointless retry loop; their exact cause is shown in Settings and Diagnostics.

## What StormATC reads

StormATC requests these 17 user-aircraft variables:

| Group | Variables |
| --- | --- |
| Position | latitude, longitude, indicated altitude, pressure altitude |
| Motion | ground speed, indicated airspeed, vertical speed, true heading, magnetic heading |
| Configuration | on-ground flag, parking brake, engine combustion |
| Radio | COM1 active and standby, COM2 active and standby |
| Transponder | code |

It never sends simulator events, moves the aircraft, changes radios, or otherwise writes
simulator state.

### FSLTL and nearby traffic

The **Settings > Live integrations** switch enables a local 20 km nearby-aircraft scan. StormATC
reads callsign, model title, position, altitude, ground speed, magnetic heading, vertical speed,
weight-on-wheels, and runway state. A model title containing `FSLTL` is identified as FSLTL in
the UI.

StormATC does not inject, spawn, steer, remove, or animate those aircraft. FSLTL supplies the
aircraft model and flight data through SimConnect, and MSFS manages the aircraft movement. Keep
FSLTL running when live injected traffic is wanted. FlyByWire SimBridge is a separate FlyByWire
aircraft companion service and is not required for StormATC traffic awareness.

Traffic reading and all generated-radio features default off. Generated exchanges can use local
wording without an API, while language-model wording and synthesized speech require separate
opt-ins. The app pauses generated chatter by default when an online ATC client is detected so it
does not compete with a real VATSIM or PilotEdge session.

For a real airport, StormATC also uses SimConnect's read-only Facility Data API. It requests the
airport reference point, runway directions, controller frequencies, parking spots, taxi points,
taxiway names, and taxi paths. Those records are converted into the same deterministic graph used
by bundled sample airports. If MSFS connects after startup, an earlier airport-data miss is
retried automatically.

Taxi clearance starts by snapping the aircraft's live latitude/longitude to a nearby published
surface node. A snap farther than 600 metres is rejected as a scenery/position mismatch rather
than silently routing from an unrelated stand. The clearance retains its exact graph nodes and
edge names so the dashboard can report remaining distance and the next taxiway change.

The SimConnect Facility Data interface exposes taxi points, paths, parking positions, names, and
hold-short point types, but it does not expose placed taxiway-sign objects. StormATC therefore
labels sign information as **expected sign cues inferred from the taxi graph**. These cues are a
training aid and must be cross-checked against the current aerodrome chart and visible scenery.

COM frequencies are requested in MHz. The transponder is requested in `number` units, which
returns its decimal display digits; StormATC pads that value to four digits and validates that
each digit is octal. It is not BCD-decoded unless the requested unit is changed to `Bco16`.

Initial radio tuning uses the documented `COM_RADIO_SET_HZ` event and then selects COM1 as the
transmit radio. ATIS is receive-only. Once the pilot selects Delivery, later controller handoffs
are not auto-tuned: the active transmit frequency must match the assigned frequency or StormATC
discards the transmission before speech recognition or any model request.

The marshalled data structure uses the SDK's documented managed-code layout:
`Sequential`, ANSI, and `Pack = 1`.

## Connection behaviour

- A dedicated background thread owns every SimConnect operation because the API is not
  thread-safe.
- An `AutoResetEvent` is supplied to SimConnect. The worker calls `ReceiveMessage` only after the
  SDK signals that event; there is no timer polling and no dependency on a WPF window handle.
- Data begins immediately and is requested continuously at approximately the configured rate.
- If MSFS closes after a successful connection, the same worker disposes the old handle and
  reconnects without replacing or leaking its cancellation source.
- Exactly one connector produces aircraft state: the mock or MSFS, never both.

A local connection does not require `SimConnect.cfg`. Microsoft documents that file for remote
client configuration. `SimConnect.ini` is optional diagnostic logging, not an application
requirement.

## Troubleshooting messages

**Build could not find the MSFS SDK**  
The no-SDK connector was compiled. Configure `MsfsSimConnectPath` and rebuild.

**Managed SimConnect wrapper is missing/could not be loaded**  
Confirm `Microsoft.FlightSimulator.SimConnect.dll` is beside `StormATC.exe` and listed in
`StormATC.deps.json`.

**Native SimConnect.dll is missing/could not be loaded**  
Confirm the x64 native DLL is beside `StormATC.exe`. Configure
`MsfsSimConnectNativePath` when it is not in the standard SDK layout.

**Wrong architecture**  
StormATC and both SDK DLLs must be x64. Do not use a legacy x86 SimConnect redistributable.

**Simulator is not running or did not accept the connection**  
Start MSFS and wait for its main menu or a loaded flight. StormATC will keep the mock active and
retry automatically.

**Client version is incompatible**  
Repair/update the current Core SDK, rebuild, and verify that stale SDK DLLs are not left in the
output directory.

## Verification

Run both build modes and the tests:

```powershell
dotnet build StormATC.sln -c Release
dotnet build StormATC.sln -c Release `
  -p:MsfsSimConnectPath="C:\MSFS SDK\SimConnect SDK\lib\managed\Microsoft.FlightSimulator.SimConnect.dll"
dotnet test StormATC.sln -c Release --no-build
```

A live verification additionally requires a running copy of MSFS. The application log should
contain `SimConnect connected`, `Received live MSFS aircraft data`, and `Loaded {ICAO} from MSFS`
with plausible aircraft values and non-zero taxi nodes and paths. Do not claim a live integration
test from mock-only evidence.

The connector was live-tested on 2026-07-28 with MSFS 2020 `1.39.12.0` and Core SDK `0.24.5`.
At LEAL, Facility Data returned 2 runway directions, 148 parking positions, 510 taxi nodes, and
517 taxi paths; a route to runway 10 was computed over the live graph.
