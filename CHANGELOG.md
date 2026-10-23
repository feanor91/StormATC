# Changelog

All notable user-visible changes are documented here. StormATC uses semantic versioning for
public releases.

## Unreleased

## 0.8.0 - 2026-10-23

### Added

- Military arrival in CAM V, following Instruction 1950 DSAE/DIRCAM: the first call ("demande le break") is
  answered with the runway, the wind and the QFE and "rappelez initiale"; the report at the initial point is
  answered "correct, numero un, rappelez dernier virage, train sorti verrouille" (a wrong QFE is corrected); the
  last turn is cleared to land only once the gear is reported "train sorti verrouille", otherwise the controller says
  "verifiez train sorti verrouille".
- The QFE (the QNH less the elevation of the field) is given in military arrivals and taxi clearances, only when a
  weather source supplies the QNH: it is never invented.

## 0.7.0 - 2026-10-23

### Added

- `scripts/Update-VacCharts.ps1` updates the charts in the Cartes VAC folder from the SIA's current AIRAC cycle,
  replacing a file only when it changed; `-IncludeMissing` also fetches the charts of the list of aerodromes.
- 404 VFR profiles extracted from the text of the VAC charts (services and frequencies, elevation, position,
  runways): StormATC now knows which aerodromes have no air traffic service, and their air-air frequency, all over
  France. Circuit rules and reporting points are drawings and are not extracted.
- Profiles of 11 military bases from the SIA eAIP (Cazaux, Mont-de-Marsan, Solenzara, Orange, Avord, Villacoublay,
  Nancy-Ochey, Luxeuil, Evreux, Landivisiau, Lanveoc): VHF frequencies and runways, CAM V and CAM I regimes.

## 0.6.3 - 2026-10-23

### Fixed

- The Charts tab no longer jumps back to the top of the list every few seconds: the list is rebuilt only when
  its content changes.

## 0.6.2 - 2026-10-23

### Changed

- The Charts tab of the panel, with more than 15 charts, shows the flight's charts, the last five opened and an
  index of letter tiles (by aerodrome name) instead of one long list: no keyboard needed, so it works in VR.

## 0.6.1 - 2026-10-23

### Changed

- The Charts tab of the panel shows the aerodrome's name beside each chart ("AD-2.LFPL – Lognes-Émerainville").

## 0.6.0 - 2026-10-23

### Added

- Military regimes **CAM V** (Victor, à vue) and **CAM I** (India, aux instruments), selectable on the flight plan
  page. CAM V is handled like VFR (no clearance, circuit with the tower) and CAM I like IFR, and the regime is
  kept through the whole flight.
- The pilot can name the regime in a call ("Rafale douze, CAM Victor, demande le roulage") or ask to change it
  ("demande CAM India"): the controller answers "vous êtes maintenant en CAM India".
- The panel's phraseology tab shows the military calls (taxi in CAM Victor, final with gear check "train sorti
  verrouillé", change of regime) when the flight is in a CAM regime.
- VFR profile for Saint-Dizier-Robinson (LFSI), the first military base: VHF frequencies, runways, CTR.
  Initial/break, PAR/SRA guidance, QFE and CAM T are not simulated yet.

## 0.5.0 - 2026-10-22

### Changed

- At an aerodrome whose VFR profile says it has no air traffic service (auto-information, such as Chartres,
  Etampes, Pithiviers or Buno-Bonnevaux), StormATC no longer plays a controller: it says so in the conversation
  when the plan is loaded, answers a call with the air-air frequency to use, and does not invent a tower.

## 0.4.1 - 2026-10-22

### Added

- VFR profile for Chartres-Métropole (LFOR): preferred runway 091, circuit north of the runways, air-air frequency.

## 0.4.0 - 2026-10-22

### Removed

- Reading a chart with a language model (Settings › "VFR profiles from charts", added in 0.3.0): the profiles are
  made by reading each chart by hand and checking it, which is more reliable. A profile placed in
  `%LOCALAPPDATA%\StormATC\vfr-profiles` is still read before the bundled ones. Requests to the language model no
  longer carry images.

## 0.3.2 - 2026-10-22

### Fixed

- A model that fails while it generates ("finish reason: error") is tried again instead of giving up, and a
  chart request to OpenRouter asks for little reasoning, since a model that deliberates first can run out of time
  before it answers. The blank lines some providers send while they work no longer fill the log.

## 0.3.1 - 2026-10-22

### Fixed

- Reading a chart leaves the model room to reason before it answers (12,000 output tokens instead of 4,000),
  and an empty answer now says why (the finish reason) and is logged with the start of the reply.

## 0.3.0 - 2026-10-22

## 0.2.2 - 2026-10-22

### Fixed

- COM1 is tuned to the ATIS again until it holds: the simulator sets the radios while it brings the aircraft
  up, so the first tuning after the pilot got into the aircraft was overwritten.

## 0.2.1 - 2026-10-22

### Added

- **Read a chart with a language model.** Settings › "VFR profiles from charts": StormATC draws the pages of a
  chart in the charts folder, sends them to the pilot's own provider (a model that reads images: OpenAI,
  OpenRouter, Groq or Anthropic), checks the strict JSON answer, and shows each fact with the words of the
  chart it was read from. The pilot keeps the facts that are right (what the model was unsure of, and the
  circuit side, start unticked) and the profile is saved in `%LOCALAPPDATA%\StormATC\vfr-profiles`, which
  is read before the bundled profiles. The model never decides anything for the ATC engine. Requests can
  now carry images.
- VFR profiles for Melun-Villaroche, Paris Saclay Versailles (Toussus), Étampes-Mondésir,
  Orléans-Saint-Denis-de-l'Hôtel, Le Puy-Loudes, Pithiviers, Buno-Bonnevaux and Paris-Orly, read from the
  AIP France AD 2 charts (reporting points, circuit height and preferred runway where the chart gives
  them).
- **Charts in the in-simulator panel** (normal and VR): a third tab shows the PDF charts the pilot puts
  in the `Cartes VAC` folder beside the application, one page at a time with zoom and page buttons; the
  charts of the flight plan's departure and destination come first, and the destination's chart opens by
  itself. Pages are drawn by StormATC with
  Windows' own PDF renderer. The Settings page has a button that opens the folder, and the installer
  creates it (and never removes it).

- A French README (`README.fr.md`) next to the English one, and a README that covers the French radio,
  the local voices, the MSFS panel, the EFB module, the installer and the version policy.

## 0.2.0 - 2026-10-20

### Added

- **In-simulator toolbar panel** (works in VR): the ATC dialogue, the phraseology of the current
  phase of flight (with a phase chooser, VFR or IFR), the aircraft's registration as the title, and
  an "I don't like this voice" button, so that the pilot does not have to leave the simulator.
- **A WASM module that reads the EFB's flight plan live.** "Send to ATC" (and to the avionics) in the
  EFB reaches StormATC immediately: aerodromes, runways, VFR or IFR, route, and, for IFR, the SID,
  the STAR and the approach. The missed approach of the planned approach is read from the
  simulator's navigation data and shown on the Flight Plan page. A `.PLN` file can also be loaded.
- **The installer installs everything**: the panel and the module in MSFS's Community folder (Store
  or Steam), and, on request, Whisper, Piper and the French voices, with the settings wired up.
- Download links and PDF guides for the third-party software and voices, from the Settings page.
- IFR clearances say the departure procedure the way a controller does ("PILU two Foxtrot").

- Community governance, conduct, roadmap, support, and contributor onboarding.
- One-command contributor environment validation.
- Responsive GitHub Pages landing site with automatic deployment and social link preview.
- Live surface guidance showing the aircraft's matched stand or taxiway, the complete cleared
  route, remaining distance, next turn, clearance limit, and expected taxiway-sign cues inferred
  from the MSFS scenery graph.
- A continuously repeating spoken departure ATIS generated from the published airport frequency,
  assigned runway, and current simulator weather, with its complete text, information code,
  source, and cached replay shown on the Radio page.

### Changed

- The departure ATIS is read only to a pilot who is in the aircraft: not when StormATC starts without
  the simulator (the scripted aircraft), nor while MSFS is in its menus. When the pilot gets into the
  aircraft on the ground, COM1 is tuned to the ATIS again (the simulator drops that command in its
  menus) and the ATIS is read. The Radio page says it is waiting.

### Fixed

- A plan the simulator announces several times is loaded once (each load reset the session), and the
  EFB's plan wins over the plan file of an earlier flight when StormATC connects.
- The installer could not write to MSFS's Community folder (the Store edition keeps it behind an NTFS
  junction, which Windows' redirection guard blocks).
- Taxi routes now begin at the aircraft's live simulator position even when a stale or unrelated
  stand was assigned earlier. The exact graph path is retained with the clearance for moving
  progress guidance instead of routing from the first facility-array parking position.
- Tuning the automatically selected ATIS frequency now starts an actual StormATC broadcast instead
  of relying on Microsoft Flight Simulator to provide station audio.
- Unchanged ATIS repeats now reuse named in-memory audio without another synthesis request. Runway
  and normalized weather values are compared locally, and the information letter advances only
  after a material operational change; no language model participates.

## 0.1.2 - 2026-07-28

### Added

- A new **Live integrations** settings page for opt-in nearby simulator traffic, FSLTL model
  detection, generated traffic radio, optional language-model wording, optional spoken voices,
  and a hard hourly exchange ceiling.
- Nearby traffic now includes observed heading and vertical speed, and generated controller/pilot
  exchanges are shown on the dashboard with their simulator source.

### Changed

- All traffic scanning, generated traffic radio, recurring language-model requests, and traffic
  speech start disabled. Enabling one does not silently enable the credit-using options beneath it.
- Generated chatter yields to the user's push-to-talk and pauses by default when vPilot, xPilot,
  swift, or PilotEdge is running.

### Fixed

- Missing word/number boundaries in speech transcripts (for example,
  `Runway20Climb7000Squawk2555`) are repaired before display and parsing, so clearance fields
  remain readable and partial readback corrections retain already-confirmed values.
- Taxi readbacks now preserve named surfaces and alphanumeric taxiways, so routes such as
  `via apron, Alfa six` correctly compare as `apron, A6`.
- Strict, Standard, and Relaxed profiles now control transcript/readback tolerance. Standard is
  correction-smart by default: it prefers safe field-specific parsing, retains partial
  confirmations, and reserves “negative” for clearly announced conflicting critical values.
- Settings tabs now wrap cleanly instead of clipping at narrower window widths.
- Live-integration and dashboard status badges keep their intended compact width instead of
  stretching across the card.
- Microphone selection now displays complete Windows Core Audio endpoint names instead of WinMM's
  truncated 31-character product labels.
- Real Microsoft Flight Simulator airports now load their live runways, frequencies, parking
  positions, taxi points, taxiway names, and taxi paths through the SimConnect Facility Data API
  instead of falling back to “detailed taxi routing unavailable”.
- Outbound taxi routing now starts at the parking position closest to the live aircraft when no
  stand was assigned by the flight plan.
- Missing airport lookups are retried after SimConnect becomes available instead of being cached
  for the entire session.
- GSX pushback directions now use aircraft-relative “tail left” and “tail right” instructions
  instead of compass directions.

## 0.1.1 - 2026-07-28

### Added

- Direct, user-selectable language-model support for OpenRouter, OpenAI, Groq, DeepSeek, and
  Anthropic, with a separate encrypted API-key input and model setting for each provider.
- Provider-specific structured-output requests and no-cost credential connection tests against
  official provider endpoints.
- Pushback facing-direction parsing for north, northeast, east, southeast, south, southwest,
  west, and northwest, including explicit controller output such as “pushback approved, face
  west” for GSX users.

### Changed

- Reworked controller prompts around current VATSIM training flows and standard readback-critical
  phraseology.
- Made deterministic phraseology the canonical model reference and aligned the renderer prompt
  with its actual JSON schema, reducing filler, invented details, and unnecessary variation.
- Clarified that a ChatGPT subscription and OpenAI API billing are separate.

### Security

- Pinned OpenAI, Groq, DeepSeek, and Anthropic credentials to their official HTTPS API hosts and
  kept automatic redirects disabled.

## 0.1.0 - 2026-07-27

### Added

- Gate-to-gate deterministic ATC session engine with readback validation.
- Microsoft Flight Simulator connection through SimConnect with an offline mock fallback.
- SimBrief flight-plan import.
- Local typed transmissions and optional Deepgram speech recognition and controller voice.
- Optional OpenRouter intent interpretation and phraseology rendering.
- Keyboard and joystick push-to-talk.
- Voice-training phrase support for accent-aware aviation recognition hints.
- Encrypted Windows DPAPI storage for API keys.
- Windows per-user Setup executable, portable ZIP, checksums, and GitHub release automation.

### Security

- Explicit cloud-service consent, provider endpoint pinning, redirect blocking, and offline mode.
- Credential redaction in logs and diagnostics.
- Repository secret scanning, dependency locking, NuGet auditing, and CI checks.
