# StormATC

A free, open-source, voice-first air traffic control companion for flight simulation.

[Project website](https://stormairfly.github.io/StormATC/) ·
[Latest release](https://github.com/stormairfly/StormATC/releases/latest) ·
[Contribute](CONTRIBUTING.md)

> **StormATC is an entertainment product for flight simulation only. It must not be used for
> real-world aviation, navigation, flight planning, or air traffic control.**

---

## What it is

StormATC gives a simulated flight a controller to talk to. You press a key, say what a pilot
would say, and a controller answers — with clearances that follow procedure, readbacks that are
actually checked, and handoffs that happen when they should.

It is deliberately **modular** and **bring-your-own-API-key**. The application is free; the
optional speech and language services are third-party products you connect with your own
accounts.

## What it is not

It is not a real-world tool, and it is not a replacement for anything used in real aviation.
It is not affiliated with, derived from, or compatible with any commercial ATC add-on. All code,
prompts, data, and interface in this repository are original.

---

## The design principle

Everything in StormATC follows from one rule:

> **The simulator determines what is physically true.
> The deterministic ATC engine determines what is permitted.
> The language model helps understand the pilot and express approved instructions naturally.**

A language model is never authoritative for aircraft position, altitude, heading, speed,
configuration, frequency, runway availability, traffic separation, squawk codes, taxi routes,
cleared altitudes or routes, assigned headings, controller jurisdiction, flight phase, whether a
readback was correct, or whether a takeoff or landing clearance is available. Those come from the
simulator and from `StormAtcStateEngine`, which is ordinary, testable, deterministic C#.

Where a model *is* used, its output is treated as untrusted input:

- A **parsed intent** is re-derived through the domain value objects. An invalid runway, an
  out-of-band frequency, a squawk containing an 8 — any of these and the whole answer is
  discarded and the local parser's result stands.
- **Generated phraseology** is checked against the facts the engine approved. Every number in the
  text must correspond to an approved value. A fluent response containing an altitude the engine
  never issued is rejected, and the deterministic wording is spoken instead.

See [docs/architecture.md](docs/architecture.md).

---

## Features

- **Voice-first**: push-to-talk in, spoken controller replies out, with a radio-effect audio chain
- **Hardware push-to-talk**: bind a button on a USB joystick, yoke, gamepad, or HOTAS
- **Accent-aware voice trainer**: personal keyterms and explicit corrections without storing audio
- **Typed fallback**: use the complete ATC pipeline without a microphone or cloud account
- **Deterministic ATC engine**: clearance delivery, ground, tower, departure, centre, approach
- **Real readback checking**: item by item, with targeted corrections naming the value you got wrong
- **Taxi routing**: shortest-path over an aerodrome graph, avoiding runway crossings where it can
- **MSFS via SimConnect**, connected automatically when the simulator is running, and switched to
  on its own if you start it later — with a scripted mock aircraft standing in until then
- **SimBrief import from inside the app**: enter your user ID once and StormATC fetches your
  latest plan. Nothing to download, nothing to import by hand
- **Local-only history**: transcripts and sessions live in a SQLite file on your machine
- **Protected credentials**: API keys are DPAPI-encrypted and can only be sent to the official
  provider endpoints
- **Works with nothing configured**: no API keys, no simulator, and no network still gives you a
  complete, playable ATC session with typed transmissions and deterministic phraseology

---

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build, or the .NET 10 Desktop Runtime to run
- Optional: Microsoft Flight Simulator with the SimConnect SDK
- Optional: an [OpenRouter](https://openrouter.ai) account for natural phrasing
- Optional: a [Deepgram](https://deepgram.com) account for speech recognition and the controller voice

---

## Build and run

```bash
git clone https://github.com/stormatc/stormatc.git
```

```bash
cd stormatc && dotnet restore --locked-mode
```

```bash
dotnet build StormATC.sln -c Release
```

```bash
dotnet test StormATC.sln
```

```bash
dotnet run --project src/StormATC.Desktop
```

The first run shows a screen covering scope, cost, and what leaves your computer. Declining the
optional cloud services is a supported outcome — the application works fully without them.

### Always-latest developer shortcut

Run this once from PowerShell:

```powershell
.\scripts\Create-StormATC-Shortcut.ps1
```

It creates **StormATC (Latest)** on the Windows desktop. The shortcut closes this checkout's
existing StormATC window cleanly, runs an incremental Release build, and then launches the
result. Build failures are shown with paths to local launcher logs instead of opening an old
executable.

Keyboard navigation is available throughout the shell: **Ctrl+1** through **Ctrl+6** opens each
page, and **Ctrl+,** opens Settings.

---

## What happens the first time you run it

Nothing has to be configured. Each part decides for itself, and says what it decided:

| | Without any setup | Once you set it up |
| --- | --- | --- |
| **Simulator** | A scripted aircraft flies the bundled fictional route STMR → STMD | MSFS is connected automatically when it is running. Start it later and StormATC switches over on its own |
| **Flight plan** | The bundled fictional sample flight | Enter your SimBrief user ID on the Flight Plan page and press import |
| **Speaking** | Use Typed transmission on the Dashboard | Add a Deepgram key and hold push-to-talk |
| **Phrasing** | Deterministic phraseology, always available | Add an OpenRouter key and model for natural wording |

Every one of those is independent. Adding a SimBrief ID does not enable any cloud service, and
connecting a simulator does not require a key. Nothing contacts anything until you configure it,
and there is an **Offline mode** in Settings that guarantees that regardless of what is configured.

---

## Cost

**StormATC is free and open source.** It has no accounts, no telemetry, and no subscription.

**OpenRouter and Deepgram are independent third-party services.** They bill you according to their
own pricing, which is theirs to set and change. StormATC does not resell them, does not have a
free tier to offer on their behalf, and makes no claim about what they cost. Check their current
rates before enabling them.

---

## API keys

Keys are stored by the operating system under your user account, via DPAPI. StormATC:

- never writes a key to `appsettings.json` or any other settings file
- never stores a key in the SQLite database
- never writes a key to a log
- never includes a key in an exception message or a diagnostics export
- never displays a stored key back to you
- stores a key only when you explicitly click Save
- requires explicit cloud-services consent before using a key
- refuses to send a key anywhere except the official OpenRouter or Deepgram HTTPS endpoint
- disables automatic HTTP redirects on credential-bearing requests

Enter, test, and remove keys on the Settings page. See
[docs/privacy-and-api-keys.md](docs/privacy-and-api-keys.md).

---

## Privacy

- Transcripts and session history are stored **on your computer only**, in
  `%LOCALAPPDATA%\StormATC\stormatc.db`. Clear them at any time from Settings.
- The microphone is open **only while push-to-talk is held**. StormATC never listens continuously.
- Raw microphone audio is **never written to disk**.
- With no keys configured, **nothing leaves your machine at all**.
- With Deepgram enabled, held-PTT audio goes to Deepgram for transcription and controller text
  goes there to be spoken.
- With OpenRouter enabled, transcripts and the facts a controller has approved go to the model
  you chose.

---

## Configuration

Settings are layered, lowest priority first:

1. `appsettings.json` beside the executable
2. `appsettings.{Environment}.json`
3. `%LOCALAPPDATA%\StormATC\usersettings.json` — what the Settings page writes
4. environment variables prefixed `STORMATC_`
5. command-line arguments

API keys are read from none of these. See [docs/development.md](docs/development.md).

---

## Preparing a GitHub release

Run the repository safety check before every commit or release:

```powershell
.\scripts\Test-RepositorySafety.ps1
```

To create a normal Windows Setup wizard plus a portable ZIP, install Inno Setup, review the
SimConnect licence files supplied with your exact MSFS SDK, and run:

```powershell
winget install --id JRSoftware.InnoSetup --exact
.\scripts\New-StormATCInstaller.ps1 -Version 0.1.0 -AcknowledgeSimConnectLicenseReview
```

The publisher runs the secret scan and full Release tests, requires both SimConnect runtime
files, excludes development configuration and local state, creates a self-contained `win-x64`
installer and portable ZIP, and writes SHA-256 checksums under `artifacts/release/`.

Microsoft's SDK licence permits distribution only for code it designates as distributable, while
the managed SimConnect documentation describes the SDK copy as a build-time reference. Review the
licence files installed with your exact SDK version before uploading those Microsoft binaries.
The acknowledgement switch prevents doing this accidentally; it is not a legal determination.

The repository also includes locked NuGet dependencies, GitHub Actions CI, Dependabot updates,
issue forms that warn against sharing credentials, and a security-focused pull-request checklist.
Release binaries are unsigned until the project has a trusted Windows code-signing certificate;
do not claim otherwise when publishing them.

The complete first-time repository setup, version-tag, draft-release, artifact-upload, installer,
and optional code-signing process is in [docs/releasing.md](docs/releasing.md).

---

## Repository layout

```
src/
  StormATC.Domain/          ATC rules, value objects, phraseology. No dependencies.
  StormATC.Application/     Use cases, abstractions, the transmission pipeline.
  StormATC.Infrastructure/  SimConnect, SimBrief, OpenRouter, Deepgram, SQLite, DPAPI.
  StormATC.Desktop/         WPF shell and view models. No domain logic.
tests/                      Domain, Application, and Infrastructure test suites.
prompts/                    Controller prompts, edited without rebuilding.
data/                       Fictional sample aerodromes, flights, and sectors.
docs/                       Architecture and setup documentation.
```

---

## Sample data

Everything under `data/` is **fictional**. Storm International (STMR), Storm Delta Regional
(STMD), and every coordinate, frequency, runway, taxiway, and stand in them are invented for the
demonstration flight. The `ST..` ICAO prefix is not assigned to any real region. StormATC bundles
no real navigation data and none of it may be used for navigation.

---

## Documentation

| Document | What it covers |
| --- | --- |
| [architecture.md](docs/architecture.md) | Layers, the authority boundary, how a transmission flows |
| [atc-state-machine.md](docs/atc-state-machine.md) | Phases, controller jurisdiction, clearance rules |
| [prompt-system.md](docs/prompt-system.md) | The prompt files, how they are used, how to edit them |
| [simconnect-setup.md](docs/simconnect-setup.md) | Building with and without the MSFS SDK |
| [simbrief-integration.md](docs/simbrief-integration.md) | Importing a flight plan |
| [development.md](docs/development.md) | Building, testing, configuration, coding standards |
| [contributor-quickstart.md](docs/contributor-quickstart.md) | Forking, setup, first changes, and pull requests |
| [privacy-and-api-keys.md](docs/privacy-and-api-keys.md) | What is stored, what is sent, how keys are held |
| [releasing.md](docs/releasing.md) | Building the installer and publishing a release |
| [website.md](docs/website.md) | Editing, previewing, and deploying the GitHub Pages site |

---

## Contributing

StormATC is actively looking for co-builders: C# developers, UI designers, testers, documentation
writers, pilots, controller enthusiasts, and people with different accents and hardware. You do
not need MSFS, an API key, or previous open-source experience to start.

Run one command to verify a new checkout:

```powershell
.\scripts\Setup-Development.ps1
```

Then read the [contributor quickstart](docs/contributor-quickstart.md), choose a scoped item from
the [roadmap](ROADMAP.md), and see [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request.
Project decisions and the path to becoming a reviewer or maintainer are documented in
[GOVERNANCE.md](GOVERNANCE.md).

Use GitHub Discussions for questions and early ideas, Issues for reproducible bugs and agreed
work, and [SECURITY.md](SECURITY.md) for private vulnerability reporting. Community participation
follows the [code of conduct](CODE_OF_CONDUCT.md).

## Licence

[MIT](LICENSE).
