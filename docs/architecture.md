# Architecture

> StormATC is an entertainment product for flight simulation only. It must not be used for
> real-world aviation, navigation, flight planning, or air traffic control.

## The authority boundary

Everything here follows from one rule:

> **The simulator determines what is physically true.
> The deterministic ATC engine determines what is permitted.
> The language model helps understand the pilot and express approved instructions naturally.**

Read that as three separate sources of truth that never overlap.

| Question | Answered by | Never answered by |
| --- | --- | --- |
| Where is the aircraft? How fast, how high, what heading? | Simulator | Engine, model |
| Is the parking brake set? Are the engines running? | Simulator | Engine, model |
| What frequency is tuned? | Simulator | Engine, model |
| Is a clearance available? What are its values? | Engine | Model |
| Which controller owns this aircraft? | Engine | Model |
| Was the readback correct? | Engine | Model |
| What flight phase is this? | Engine | Simulator, model |
| What did the pilot probably mean? | Local parser, then model | — |
| How should the approved instruction be worded? | Deterministic renderer, then model | — |

The last two rows are the only places a language model appears, and in both its output is
re-validated before it can matter. Everything else is ordinary deterministic C#.

## Layers

```
StormATC.Desktop         WPF shell. Views, view models, navigation, first-run.
      │                  No business logic. No provider calls.
      ▼
StormATC.Infrastructure  SimConnect, SimBrief, OpenRouter, Deepgram, SQLite,
      │                  DPAPI, NAudio, JSON data. Implements the abstractions.
      ▼
StormATC.Application     The transmission pipeline, session state, options,
      │                  and the interfaces infrastructure implements.
      ▼
StormATC.Domain          ATC rules, value objects, phraseology, readback.
                         Zero package references, by design.
```

Dependencies point inwards only. `Domain` has no package references at all, which is what makes
it fast to test and impossible to accidentally couple to a provider.

## Provider selection

All of it happens in one place —
`StormATC.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` — and each provider
is chosen on **its own** configuration:

| Provider | Chosen by | When unconfigured |
| --- | --- | --- |
| Secret store | Always DPAPI | No file exists until a key is stored |
| Simulator | `Simulator:PreferredConnector`, resolved at run time by `SimulatorConnectorRouter` | The scripted mock aircraft, with the reason shown |
| Flight plan | Whether a SimBrief user ID is set, resolved per import by `FlightPlanProviderRouter` | The bundled fictional sample flight |
| Language model | A stored key **and** a model name | Deterministic phraseology only |
| Speech | A stored Deepgram key | Typed transmissions, text responses |

Those are independent questions. Answering them together — which is what a single mock-mode
switch did — meant that enabling any one of them enabled all of them, and that a user who wanted
a SimBrief plan had to give up the sample aerodromes and the mock simulator to get it.

`Application:OfflineMode` is the one exception, and it is a guarantee rather than a preference:
with it on, the implementations that can reach the network are not registered at all. It is the
only setting that still needs a restart, because it decides what is in the container.

Nothing downstream branches on which implementation won. The coordinator, the engine, and the
view models see the same interfaces either way, which is what makes running with no providers a
genuine test of the real architecture rather than a parallel code path.

### The two routers

Both present one interface and decide behind it, so the rest of the application never learns that
a choice was made:

- **`SimulatorConnectorRouter`** connects MSFS when it can, falls back to the mock when it cannot,
  keeps watching for the simulator to appear, and hands over the moment it does. Exactly one
  connector is subscribed at a time, so a stale scripted aircraft can never overwrite real
  simulator state. It can also be switched while a session is running.
- **`FlightPlanProviderRouter`** sends the import to SimBrief when a user ID is available and to
  the bundled sample when it is not. A SimBrief *failure* is reported rather than silently
  replaced with the sample — substituting a different aircraft, route, and callsign for the one
  the pilot asked for would be a worse outcome than saying what went wrong.

## How a transmission flows

`PilotTransmissionCoordinator.ProcessAsync` runs ten ordered stages, one transmission at a time:

```
  pilot speaks / types
          │
   1  deduplicate by transmission id
   2  record the raw transcript as a conversation turn
   3  normalise ("squak" → "squawk", fillers removed, whitespace collapsed)
   4  parse intent deterministically
          └─ if unsure: ask the model, then re-validate every value it returned
   5  ask the ATC engine for a decision   ◀── evaluates any outstanding readback
   6  apply exactly the approved state changes, and nothing else
   7  render deterministic phraseology
   8  optionally ask the model to phrase it, then validate that output
          └─ any unapproved number: discard, keep the deterministic wording
   9  persist the turn, the decision, the clearance, the handoff
  10  publish the result to the UI and the audio pipeline
```

Two design points are worth calling out.

**Stage 6 is the only place a transmission changes session state.** `ApplyDecision` applies
`NewPhase`, `IssuedClearance`, `RequiredReadback`, `Handoff`, `ClearsPendingReadback`, and
`Warnings` exactly as the engine gave them. It infers nothing and fills in no gaps. If the engine
did not grant a clearance, none appears.

**Transmissions are serialised through a semaphore.** Two overlapping transmissions for the same
aircraft would otherwise race on the session snapshot and could derive two clearances from the
same starting state. Duplicate transmission ids are dropped, so a push-to-talk release that fires
twice cannot issue two clearances.

## The engine

`StormAtcStateEngine` is a guard chain followed by an intent dispatch.

```
Evaluate(context)
  ├─ no intent                      → silent
  ├─ emergency                      → acknowledge, bypassing every other guard
  ├─ state-free intent              → say again / radio check / unrecognised
  ├─ low confidence + safety-critical → say again, never a guess
  ├─ wrong callsign                 → refuse
  ├─ wrong controller               → refuse, and say who to contact
  ├─ mandatory readback outstanding → refuse, and repeat what is owed
  └─ dispatch to one of ~20 intent handlers
```

Each handler then checks only its own preconditions — a takeoff clearance checks the phase, the
runway assignment, and runway occupancy; a taxi clearance checks that a route exists.

`EvaluateStateChange(context)` is the other entry point. It watches physical state rather than
transmissions, and produces the events no one said out loud: entering a runway against a
hold-short instruction, becoming airborne, being established in the climb, touching down,
reaching the stand.

## Where facts come from

Every decision carries `ApprovedFacts`: a normalised dictionary of exactly what the response may
state. It is the payload the renderer is allowed to express, and nothing else.

This is what makes generated phrasing safe. `PhraseologyValidator` extracts **every number** from
the rendered text and requires each to appear in `ApprovedFacts`. A keyword check would let
"climb initially five thousand" through when the engine approved seven; extracting the numbers
does not.

The reverse also matters: a fact the engine did not compute is simply absent. When no weather
source is available, `WeatherSnapshot` is null, `ApprovedFacts` has no wind, and the response
omits the wind check rather than inventing one.

## Session state

`AtcSessionContext` is the single mutable holder of session state. Everything else works with
immutable `AtcSession` snapshots. One place to mutate means there is exactly one answer to "what
is the session right now", and the engine never has to reason about a half-applied update.

`AtcSession` is a record with `With*` transitions. It is the aggregate: flight plan, phase,
controller, frequency, clearances, pending readback, conversation, warnings, assignments.

## Threading

- Simulator updates arrive on a background thread at 5 Hz and update the session directly. Pushing
  each one through the dispatcher would flood the UI queue.
- View models marshal only the resulting **display strings** to the dispatcher.
- Provider events (speech, connection changes) are marshalled before they touch a binding.
- Nothing blocks the UI thread. `.Result` and `.Wait()` appear nowhere in the codebase.

## Degradation

Every external dependency is optional, and the absence of one is a normal state rather than an
error:

| Missing | What happens |
| --- | --- |
| Simulator | Aircraft state does not update; ATC still works on typed transmissions |
| OpenRouter | Deterministic phraseology and the local parser only |
| Deepgram | Type transmissions; controller responses are text |
| SimBrief | Load the bundled sample flight, or none |
| Database | The session runs; nothing is saved, and the UI says so |
| Airport data | Clearances that need it are refused, not guessed |
| Weather | Wind and QNH are omitted from responses |

The Diagnostics page shows each provider's real state, and the badges never report "connected"
until something has actually connected.
