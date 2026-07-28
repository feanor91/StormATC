# Development

> StormATC is an entertainment product for flight simulation only. It must not be used for
> real-world aviation, navigation, flight planning, or air traffic control.

## Prerequisites

- Windows 10 or 11 (WPF, DPAPI, and NAudio are Windows-only)
- .NET 10 SDK

Those prerequisites cover the core build and automated tests. Live integration testing requires
the contributor's own simulator, SDK, hardware, service account, and API key as applicable.
StormATC does not provide shared credentials.

## Build and test

```bash
dotnet restore
```

```bash
dotnet build StormATC.sln
```

```bash
dotnet test StormATC.sln
```

```bash
dotnet run --project src/StormATC.Desktop
```

A build that produces warnings is a build to fix. The null-safety warnings are errors by
configuration, and `AnalysisLevel` is `latest-recommended`.

Run a single suite:

```bash
dotnet test tests/StormATC.Domain.Tests/StormATC.Domain.Tests.csproj
```

Run one test:

```bash
dotnet test StormATC.sln --filter "FullyQualifiedName~EndToEndMockSessionTests"
```

## Project layout

| Project | Target | Depends on |
| --- | --- | --- |
| `StormATC.Domain` | `net10.0` | **nothing** |
| `StormATC.Application` | `net10.0` | Domain, Hosting, Options |
| `StormATC.Infrastructure` | `net10.0-windows` | Application, SQLite, NAudio, DPAPI |
| `StormATC.Desktop` | `net10.0-windows` | all of the above |

Domain having zero package references is a deliberate constraint, not an accident. If a domain
change seems to need a package, the change belongs a layer up.

Package versions are centralised in `Directory.Packages.props`
(`ManagePackageVersionsCentrally`), with transitive pinning enabled so a flagged transitive
dependency can be fixed rather than suppressed. Individual `.csproj` files list package names
only.

## Configuration

Layered, lowest priority first:

1. `appsettings.json` beside the executable
2. `appsettings.{Environment}.json`
3. `%LOCALAPPDATA%\StormATC\usersettings.json` — what the Settings page writes
4. environment variables prefixed `STORMATC_`
5. command-line arguments

```bash
dotnet run --project src/StormATC.Desktop -- --Simulator:PreferredConnector=Mock
```

```bash
setx STORMATC_Application__DeveloperMode true
```

**API keys are read from none of these.** They live only in the OS secret store. `UserSettingsService`
refuses to write any key whose name looks like a credential, as a guard against a future change
routing one through by accident.

### Sections

| Section | Controls |
| --- | --- |
| `Application` | Offline mode, developer mode, realism profile, first-run state |
| `LanguageModel` | Active provider and intent/phraseology temperatures |
| `OpenRouter`, `OpenAI`, `Groq`, `DeepSeek`, `Anthropic` | Official base URL, model, timeout, retries |
| `Deepgram` | URLs, models, voice, sample rate, devices, transmission limits |
| `SimBrief` | Base URL, user ID, timeouts, staleness, caching |
| `Simulator` | Preferred connector, update rate, reconnection |
| `Audio` | Volume, radio effect, static, clicks, clean mode, PTT key |
| `Persistence` | Transcript storage, retention |
| `AtcRealism` | Readback enforcement, callsign checking, hold-short, thresholds |
| `Diagnostics` | Latency metrics, sample count, log retention, prompt hashes |

Options are bound with `ValidateDataAnnotations().ValidateOnStart()`, so an invalid value fails at
startup with a clear message rather than at the moment it is first used.

Every language-model provider, Deepgram, and SimBrief is additionally pinned to its official
HTTPS/WSS location. Environment variables and command-line arguments may tune ordinary options,
but they cannot redirect a credential-bearing request to another host.

### Which settings need a restart

Almost none. Provider selection is resolved at run time by
`SimulatorConnectorRouter` and `FlightPlanProviderRouter`, so changing the simulator connector or
the SimBrief user ID takes effect on the next connect or import. Language-model and Deepgram
configuration uses options monitors, so provider, model, voice, device, and PTT settings also
update after the Settings file reloads.

`Application:OfflineMode` is the exception: it decides which implementations are registered in the
container, which happens once when the host is built. The Settings page says so, and only when the
value has actually changed.

## Storage

| Path | Contents |
| --- | --- |
| `%LOCALAPPDATA%\StormATC\stormatc.db` | Sessions, turns, decisions, clearances, handoffs |
| `%LOCALAPPDATA%\StormATC\usersettings.json` | Non-secret user settings |
| `%LOCALAPPDATA%\StormATC\secrets.dat` | DPAPI-protected API keys |
| `%LOCALAPPDATA%\StormATC\flightplans\` | Raw SimBrief responses when optional caching is enabled |
| `%LOCALAPPDATA%\StormATC\logs\` | Rolling logs |

Tests never touch any of these. `AppStoragePaths` has a constructor taking explicit roots, and the
test helper points it at a temporary directory that is deleted afterwards.

## Coding standards

- Nullable reference types enabled; `CS8600;CS8602;CS8603;CS8618;CS8625` are errors.
- File-scoped namespaces. `_camelCase` private fields. XML docs on every public member.
- Async all the way down. **Never** `.Result` or `.Wait()`. Never block the UI thread.
- No static service locators. No global mutable state. Everything constructor-injected.
- No business logic in view models.
- Do not swallow exceptions. Handle at a boundary, log with a correlation id, surface something
  actionable.

### Logging

`LoggerMessage.Define` compiled delegates are used on the **per-transmission hot path only** —
`PilotTransmissionCoordinator`. Everywhere else uses ordinary structured logging, which is
clearer and runs cold. That policy is why `CA1873` is set to `suggestion` in `.editorconfig`
rather than silenced; the comment there says the same thing.

`RollingFileLoggerProvider` writes one file per day to `%LOCALAPPDATA%\StormATC\logs`, kept for
`Diagnostics:LogRetentionDays`. Writes are queued and drained on a background task, so a slow disk
cannot stall a transmission, and every failure inside the sink is swallowed — a log that cannot be
written must never take the application down with it. Diagnostics → **Open data folder** shows the
user where the files are.

Never log: API keys, `Authorization` headers, raw audio, or secret-store values. That discipline
lives at the call sites, which is the only place it can be enforced correctly; the file sink does
not attempt to redact, because a filter there would give a false sense of safety while mangling
legitimate text.

## Testing

| Suite | Covers |
| --- | --- |
| `StormATC.Domain.Tests` | Value objects, phraseology, readback, taxi routing, the ATC engine |
| `StormATC.Application.Tests` | Normalisation, intent parsing, validation, the pipeline |
| `StormATC.Infrastructure.Tests` | SimBrief, airport JSON, SQLite, secrets, prompts, provider routing, end-to-end |

The end-to-end test in `EndToEndMockSessionTests` flies the complete mock IFR session — clearance
request through to parked — through the real engine, the real parser, the real readback evaluator,
the real mock simulator, and a real SQLite database, with **no** cloud services. It asserts valid
phase progression, correct controller changes, correct frequencies, required readbacks, that no
value was invented, and that the session completed.

Tests needing the network, a key, or a simulator are traited and do not run by default:

```csharp
[Trait("Category", "Integration")]
[Trait("Category", "RequiresApiKey")]
[Trait("Category", "RequiresMsfs")]
```

**Do not delete or weaken a test to get a green run.**

## Developer tools

The **Typed transmission** card is always available on the Dashboard. It bypasses speech-to-text
and nothing else — normalisation, parsing, the engine, and persistence are all the production
ones. It is how you exercise the whole pipeline without a microphone, cloud account, or API key.

With `Application:DeveloperMode` on **and the scripted mock aircraft flying**, the Dashboard also
shows a mock-simulator panel. The controls drive the mock directly, so they are hidden when a real
simulator is connected rather than left on screen doing nothing.

- **Advance the mock simulator** through its scripted stages, or reset it to the stand.
- **Trigger a runway incursion** to see the hold-short warning.
- **Occupy the runway** to see a takeoff clearance refused, and clear it again.
- **Replay** the last controller transmission.

## Adding an aerodrome

Drop a JSON file in `data/sample-airports/`. Mark it fictional, invent every coordinate, and use
an ICAO prefix that is not assigned to any real region. `data/sample-airports/STMR.json` is the
worked example, including a taxi graph with a runway-crossing edge.

**Do not add real navigation data.** It is a licensing problem and a safety-messaging problem.

Reload without restarting: Diagnostics → **Reload prompts from disk** also drops the airport cache.

## Common tasks

**Add an intent.** Add to `PilotIntentType`, add a rule to `DeterministicPilotIntentParser.Rules`
in priority order, add a handler to the engine's dispatch, add phraseology, add tests.

**Add a controller position.** Add to `ControllerPosition`, add a frequency to
`AirportFrequencies`, add a prompt and map it in `PromptNames.ForController`, add jurisdiction
rules.

**Add a provider.** Define the abstraction in `Application/Abstractions`, implement it in
`Infrastructure`, register it in the one `AddProviders` method, and write a mock that fails
honestly rather than one that fabricates success. Choose it from its own configuration, not from a
shared mode flag — see the provider-selection table in
[architecture.md](architecture.md).
