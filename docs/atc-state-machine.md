# The ATC state machine

> StormATC is an entertainment product for flight simulation only. It must not be used for
> real-world aviation, navigation, flight planning, or air traffic control.

This describes what `StormAtcStateEngine` permits and refuses. It is simulation logic. It models
procedure closely enough to be satisfying to fly, and nowhere near closely enough to be used for
anything real.

## Flight phases

```
Disconnected → Preflight → ClearanceRequested → IFRClearanceIssued
    → Pushback → TaxiOut → HoldingShort → LineUp → Takeoff
    → InitialClimb → Departure → Enroute
    → Descent → Arrival → Approach → Final
    → Landed → TaxiIn → Parked
```

`GoAround` and `Abnormal` are entered from wherever the aircraft happens to be.

The phase is **StormATC's own operational state**, not a simulator variable. The simulator reports
physical truth; the engine decides what that truth plus the clearance history adds up to. The two
can legitimately disagree for a moment — an aircraft can be physically airborne while still in
`Takeoff` until the engine observes a stable climb.

## Controller jurisdiction

| Position | Owns | Frequency source |
| --- | --- | --- |
| Clearance Delivery | The IFR clearance | Departure aerodrome |
| Ground | Pushback, taxi out, taxi to stand | Departure or destination |
| Tower | Line-up, takeoff, landing, runway vacated | Departure or destination |
| Departure | Initial climb, climb to cruise | Departure aerodrome |
| Centre | En-route level changes, direct-to, deviations | Sector data |
| Approach | Descent, approach clearance | Destination aerodrome |

Asking the wrong controller for something is refused, and the refusal names who to contact.
Frequencies come from the aerodrome data, never from a constant in code, and
`AirportFrequencies.ResolveWithFallback` handles aerodromes that do not staff every position.

## Handoffs

Handoffs happen for a reason, not on a timer:

| Trigger | From → to |
| --- | --- |
| Flight plan loaded with a published ATIS | None → ATIS |
| Pilot tunes the published Delivery frequency after monitoring ATIS | ATIS → Delivery |
| IFR clearance read back correctly | Delivery → Ground |
| Reporting holding short | Ground → Tower |
| Established in the climb, 1000 ft above aerodrome elevation | Tower → Departure |
| Descending inside the destination's terminal area | Departure/Centre → Approach |
| Reporting established on the approach | Approach → Tower |
| Reporting the runway vacated | Tower → Ground |

The two physical triggers — climb and terminal area — come from `EvaluateStateChange`, which
watches the simulator rather than the radio.

The session starts on the published departure ATIS and tunes COM1 to it once when the flight plan
is loaded. ATIS is receive-only. While the selected radio is on that frequency, StormATC generates
and speaks a deterministic departure broadcast from the airport, assigned runway, and observed
weather. It never asks a language model to invent or compare operational ATIS facts. The broadcast
loops while tuned using named in-memory audio, so an unchanged repeat makes no new synthesis
request. Between repeats, normalized runway and weather values are compared locally. Only a
material change advances the information letter and synthesizes one updated recording; time
passing and small simulator-value jitter do not.

Tuning away stops the broadcast. Returning to the ATIS channel resumes the cached loop without
moving controller jurisdiction back from Delivery.

Tuning the published Delivery frequency opens two-way ATC. After that, frequency changes remain
pilot actions. If the selected transmit radio disagrees with the assigned controller frequency,
the Radio page says so and the transmission is discarded before transcription, parsing,
persistence, or any language-model request.

## Clearances

Clearance types are distinct in the code because they are distinct in procedure:

```
IfrClearance   Pushback   Taxi   LineUp   Takeoff
Climb   Descent   DirectTo   Heading
ApproachClearance   LandingClearance   TaxiToStand   RunwayCrossing
```

`ApproachClearance` and `LandingClearance` are deliberately separate. **Being cleared for the
approach does not authorise landing.** The engine enforces that: an approach clearance carries
`"cleared to land"` in its prohibited facts, so a response containing that phrase is rejected
before it can be spoken.

### The IFR clearance

Issued only on the ground, before departure, with a flight plan loaded and aerodrome data
available. It contains destination, route, initial altitude, runway, departure procedure, squawk,
and the departure frequency.

The initial altitude is a conservative default well below any filed cruise level. A published SID
initial altitude would need procedure data StormATC does not bundle, and inventing one would be
exactly the kind of fabricated fact this design exists to prevent.

The squawk comes from the scenario when one is pinned — that is how the sample flight always gets
4271 and stays reproducible. Otherwise it is derived deterministically from the callsign: octal
digits only, first digit 1–6, never a reserved emergency code, and stable across re-evaluation so
the transponder is never silently reassigned.

### Taxi

The route comes from Dijkstra over the aerodrome's taxi graph. Runway-crossing edges carry a
50 km penalty, so the search takes a longer surface route whenever one exists and only crosses a
runway when there is genuinely no alternative. When it must cross, the crossing is declared and
needs its own clearance.

Consecutive edges on the same taxiway are collapsed, because "Alpha, Alpha, Charlie" is not how a
clearance is read.

Every taxi clearance to a runway carries a hold-short instruction, and that instruction is a
mandatory readback item.

### Takeoff

Refused unless all of:

- the aircraft is on the ground
- the aircraft is with Tower
- the phase is `HoldingShort`, `LineUp`, or `TaxiOut`
- a departure runway is assigned
- no traffic is reported on that runway

The wind is included only when a weather source supplied one.

## Readbacks

`DeterministicReadbackEvaluator` works in two passes. First it uses whatever structured values the
intent parser already extracted — the reliable path. Then, for any required item the parser did
not produce, it re-scans the transcript for **that specific item type**. A targeted second look
recovers readbacks the general parser missed without the risk of a general re-parse inventing
values.

Everything compares normalised values, so:

- "seven thousand" and "7000" are the same altitude
- FL120 and 12,000 ft are the same height
- "one two one decimal seven" and "121.700" are the same channel
- "01L" and "1L" are the same runway

Only **critical** items decide the verdict. A missing departure-frequency readback is a
phraseology slip — the pilot is told again at handoff. A missing hold-short instruction is not.

| Outcome | Meaning | What the controller says |
| --- | --- | --- |
| `Correct` | Every critical item matched | Readback correct, and any handoff that follows |
| `Incomplete` | A critical item was absent | "readback incomplete", naming what is missing |
| `Incorrect` | A critical item had the wrong value | "negative", naming the correct value |
| `WrongCallsign` | Another aircraft's callsign | "verify callsign, say again" |

Corrections are **never** handed to a language model. Exact wording matters more than natural
delivery when correcting a pilot, and a model asked to rephrase a correction can restate the
wrong value the pilot just used.

While a mandatory readback is outstanding, further requests are refused and the outstanding items
are repeated. Readback and correction intents are exempt — they are how the pilot resolves it.

## Hold-short detection

When a taxi clearance carries a hold-short instruction and the aircraft crosses within 40 m of
that runway's centreline while moving, the engine raises a warning.

**This is simulation logic only. It is not, and must not be presented as, real-world runway
incursion protection.** The warning message says "Simulated hold-short violation" for exactly that
reason.

The warning is raised once per runway per session, not once per simulator update — five warnings
a second would bury everything else. It is suppressed entirely once a line-up or takeoff clearance
exists, because entering the runway is then what was asked for. Setting the realism profile to
Relaxed turns the check off.

## Emergencies

`Mayday` and `PanPan` bypass **every** guard: confidence, callsign, jurisdiction, outstanding
readback. A pilot in trouble is acknowledged immediately, whatever else is going on.

## Realism profiles

| | Relaxed | Standard | Strict |
| --- | --- | --- | --- |
| Readback blocks progress | no | **yes** | yes |
| Wrong callsign refused | no | **yes** | yes |
| Hold-short enforced | no | **yes** | yes |
| Check-in required after handoff | no | no | yes |
| Minimum intent confidence | 0.6 | **0.6** | 0.75 |

Individual rules can also be set one at a time on the Settings page; a profile is just a bundle of
them. Standard is the default.

## What the engine will not do

- Issue a clearance without the data to compute it. Missing aerodrome data produces a refusal, not
  a guess.
- State a value it did not compute. Every spoken number is in `ApprovedFacts`.
- Invent weather. A null observation means the wind is omitted.
- Skip a phase. A takeoff clearance is unavailable to an aircraft still at the stand.
- Accept a readback it cannot verify. An unverifiable item counts as missing.
