# SimBrief integration

> StormATC is an entertainment product for flight simulation only. It must not be used for
> real-world aviation, navigation, flight planning, or air traffic control.

## What it does

SimBrief is a free flight-planning service used widely in flight simulation. StormATC can import
the most recent plan from your SimBrief account and use it as the basis for the session: callsign,
aerodromes, runways, route, cruise level, and the departure procedure.

You do not need a SimBrief account. Without one, StormATC loads the bundled fictional sample
flight, and everything works.

## Your user ID is not a credential

A SimBrief user ID is an account number, not a secret. It lives in ordinary settings —
`%LOCALAPPDATA%\StormATC\usersettings.json` — rather than in the OS secret store, and the Flight
Plan page says so where you type it.

There is no SimBrief API key to manage. If you ever find yourself entering something secret-shaped
for SimBrief, something is wrong.

## Setting it up

1. Generate a flight plan at [simbrief.com](https://www.simbrief.com).
2. Open the **Flight Plan** page in StormATC.
3. Enter your SimBrief user ID or username.
4. Click **Import latest SimBrief plan**.

That is the whole procedure. There is no mode to change first and no restart: the user ID is saved
as you import, and every import afterwards — including the one at startup — goes to SimBrief.
Clear the field to go back to the bundled sample.

The only thing that overrides this is **Offline mode** in Settings, which stops StormATC
contacting anything at all. With it on, an import loads the sample and says why.

## What is imported

| Field | Source in the OFP |
| --- | --- |
| Callsign | `general/atc_callsign`, else ICAO airline + flight number, else registration |
| Aerodromes | `origin/icao_code`, `destination/icao_code`, `alternate/icao_code` |
| Runways | `origin/plan_rwy`, `destination/plan_rwy` |
| Cruise level | `general/initial_altitude` |
| Route | `general/route` |
| Departure procedure | `general/sid_ident` |
| Arrival procedure | `general/star_ident` |
| Aircraft | `aircraft/icaocode`, `aircraft/reg` |
| Route legs | the `navlog` fixes, with positions, planned altitudes, and distances |
| Fuel and times | `fuel`, `times` |

A cruise level below 18,000 ft stays an **altitude**; at or above it becomes a **flight level**.
That distinction is not cosmetic — it changes how the level is spoken and how a readback is
compared.

## What is not imported

**Nothing that would amount to a clearance.** A flight plan states intentions. What the aircraft
is actually cleared to do comes from the ATC engine and only from the engine. The filed runway is
a preference the engine may honour; the filed cruise level is a ceiling it will not exceed.

## Failure handling

Every failure produces a message and something you can do about it, never a partial plan:

| Situation | What you see |
| --- | --- |
| Not valid XML | "SimBrief returned a response that is not valid XML." Regenerate and import again. |
| SimBrief reports an error | The service's own message, verbatim |
| Missing origin or destination | Refused; the plan cannot be used |
| Unusable callsign | Refused; set an ATC callsign in your SimBrief airline profile |
| No navlog | Imported with a warning; route legs are unavailable |
| No cruise altitude | Imported with a warning |
| Network failure or timeout | Reported with the previous plan left in place |

Warnings appear under the import button. They mean "this worked, but here is what was missing" —
they are not failures. A warning from the import that runs at startup is repeated on the
Diagnostics page, prefixed `SimBrief:`.

A failed SimBrief import is **never** quietly replaced with the bundled sample. Flying a different
aircraft, route, and callsign from the one you asked for would be worse than being told what went
wrong, so the previous plan stays in place and the error is reported.

## Staleness

A plan generated more than `SimBrief:StaleThreshold` ago (12 hours by default) is flagged on the
Flight Plan page. It is a warning, not a refusal: you may well intend to fly it anyway.

## Caching

When `SimBrief:CacheRawResponses` is on, the raw XML is written to
`%LOCALAPPDATA%\StormATC\flightplans\` so an import that parsed oddly can be inspected. The cache
contains flight-planning data only — no credentials.

## Configuration

```json
{
  "SimBrief": {
    "BaseUrl": "https://www.simbrief.com/api/xml.fetcher.php",
    "UserId": "",
    "Timeout": "00:00:15",
    "StaleThreshold": "12:00:00",
    "CacheRawResponses": true
  }
}
```

## Testing

`SimBriefFlightPlanProvider.ParseOfp` is a static method taking XML, so the parser is tested
against a fixture with no network:

```
tests/StormATC.Infrastructure.Tests/Fixtures/simbrief-ofp-sample.xml
```

That fixture is **fictional** — a made-up flight between the sample aerodromes, shaped like a real
OFP so the parser meets realistic nesting. Tests cover the happy path, malformed XML, an empty
response, a SimBrief error status, missing aerodromes, and a missing navlog.

Tests that call the live service are traited `Integration` and do not run by default.
