# Changelog

All notable user-visible changes are documented here. StormATC uses semantic versioning for
public releases.

## Unreleased

## 0.19.0 - 2026-10-25

### Added

- **A direct or a new route is refused when it cannot be given**: for flow control, when the arrival is saturated (a hold
  is ordered, or three aircraft fly the circuit of the destination), and when the path to the fix crosses an active
  military area or a weather area at the level of the flight.
- **The controller turns a flight away from a military area or bad weather by himself**: a heading to avoid the area
  (to be read back), then "resume own navigation" once past it. The areas come from the optional file
  `hazard-areas.json` in the application data folder (see `docs/hazard-areas.md`); without it nothing changes. The
  direct the controller gives by himself is not given through an area either.
- **Parallel offset** ("demande un décalage de deux nautiques à droite"): approved up to twenty miles to an instrument
  flight in the cruise; "de retour sur la route" ends it.
- **CPDLC failure** ("panne CPDLC", "confirmez par phonie"): the controller takes note and continues by voice.
- **Radar sequence**: Approach tells an arrival its number for the approach ("numéro deux pour l'approche, attendez-vous
  à des vecteurs radar") when other arrivals are closer to the destination.

## 0.18.0 - 2026-10-25

### Added

- **The radar controller gives instructions by himself** to an IFR flight (Departure, Centre, Approach), each once in a
  flight and each to be read back (a readback that is wrong is corrected; one that is not given never blocks the flight):
  - a **speed** on the way in: reduce to 220 knots inside 35 nm of the destination when above 250, then to 180 inside
    18 nm when above 215;
  - a **hold**, from Approach inside 30 nm when two or more aircraft are in the circuit of the destination, over the
    route fix nearest the destination (or at the present position); it ends by itself after three minutes ("quittez
    l'attente"), or when the pilot says he is ready to leave it;
  - a **direct** from the Centre, in the cruise more than 60 nm from the destination, to a fix of the route that is ahead
    and shortens it.

## 0.17.0 - 2026-10-25

### Added

- **Phraseology manual, second part**: a new route, a speed (a number of knots, a Mach number, or free speed), a hold over
  a fix or at the present position and leaving it, RVSM (approved or negative), SELCAL and CPDLC, a tow, a runway
  inspection by a vehicle, a position report, and approaches by name (RNP, VPT, circling/MVL, NDB).
- A VFR arrival call that carries no ATIS letter now gets the aerodrome's conditions first (runway in use, wind, QNH); a
  flight that joins behind other circuit traffic is told its number in the sequence; a takeoff clearance given while a
  heavy aircraft (A380, B747, B777...) is close to the aerodrome carries a wake turbulence caution.

### Fixed

- The French rules for "demande" matched "demande" but never "demandons": "demandons un suivi", "demandons le break",
  "demandons un marsa"... were not recognised through those rules. They now are.

## 0.16.0 - 2026-10-25

### Added

- **More of the phraseology manual**, from the analysis of phraseo.aeronautique.xyz (see docs/analyse-phraseo.md):
  backtracking a runway ("remontée de piste"), the exact time, ident, "parlez plus lentement" (the controller repeats the
  last call slowly), cancelling IFR in flight, special VFR (refused under 1500 m of visibility), a transit across an
  aerodrome's zone (altitude and QNH, then "reçu, poursuivez" overhead), a 360 (to the left or the right), extending the
  downwind, closing the flight plan, ready for an immediate departure ("prêt immédiat": cleared for an immediate
  takeoff), and the answers to a traffic information ("je surveille", "trafic en vue", "pas de contact visuel").
- **Traffic information**: a controller who works a flight in the air (flight information, departure, centre, approach)
  tells an IFR flight or a followed VFR flight about traffic within 6 nm and 1000 ft: clock position, distance and
  relative altitude, at most once in three minutes.

## 0.15.1 - 2026-10-25

### Fixed

- The callsign is recognised from its last letters: "Alpha Sierra Tango" or "Sierra Tango" for F-HAST, and a few
  common speech-recognition spellings of "Sierra" ("cira", "sira"). Before, only the full registration or its first letter
  and last two were understood, and anything else made the controller ask to say again.
- A readback that leaves the callsign out is accepted when the rest is right (only the strict realism asks for it), and a
  taxi readback that gets the route wrong or only in part is accepted when the hold-short instruction is right.
- The language model that helps understand a call was given 400 tokens, and a reasoning model spent them all thinking and
  answered nothing: it now gets 1500 and is asked to reason briefly, so a request outside the usual phrases is understood
  instead of answered with "say again".

## 0.15.0 - 2026-10-25

### Added

- **VFR flight following** ("suivi VFR"). A VFR flight in the air that tunes a flight information frequency is followed by
  that service: "Paris Information, F-HAST, VFR de Lognes vers Pontoise, demande de suivi" is answered with the service's
  name and a transponder code. The service passes the flight to the next sector when it crosses a limit
  (Paris Nord 125.700, Sud 126.100, Ouest 129.625; Marseille Nord 124.500, Sud 120.550, the frequencies of the French AIP,
  GEN 3.4), lets it go when it leaves the area covered (the AIP publishes no flight information frequency for the west and
  the south-west), and hands it to the destination's approach or tower 15 nm before it. The limits of the sectors are an
  approximation: the AIP draws them on a chart.

## 0.14.3 - 2026-10-25

## 0.14.2 - 2026-10-25

### Fixed

- The traffic chatter and the departure ATIS stay silent while the aircraft's radio has no power (battery or avionics off,
  as after "Start cold and dark").
- "Start at the parking" and "Cold and dark" never ran: they waited for the simulator to announce a loaded flight, which
  MSFS 2024 does not do. They now act when the simulator goes from its menus to the cockpit, wait for a flight plan whose
  departure is where the aircraft stands, and write each decision to the log.

## 0.14.1 - 2026-10-25

### Fixed

- StormATC no longer speaks while the simulator is in its menus (loading a flight, the world map, the main menu): the
  controller's own calls (handoffs, warnings) and the background traffic chatter wait until you are in the aircraft.

## 0.14.0 - 2026-10-25

### Added

- **Start at the parking / cold and dark** (Settings > Live integrations > "Start of the flight"), two options, off by
  default. When MSFS loads a flight that starts at the holding point or on the runway, StormATC can move the aircraft to a
  free parking stand of the departure aerodrome (facing the way the stand points; a gate for an airliner, a ramp for a
  light aircraft, a military apron on a military base) and/or shut it down (engines, battery, avionics and lights off,
  parking brake set). They only act on a flight just loaded, with the aircraft on the ground and at rest within 5 nm of the
  departure of the flight plan. Best effort: aircraft with their own systems may ignore part of the shutdown.

## 0.13.0 - 2026-10-25

### Added

- **Suggested reply** in the panel: under the conversation of the Dialogue tab, what you have to say next (the readback
  still required, or an example of the next call), in the radio language. It is an option, on by default: Settings >
  Voice & PTT > In-simulator panel > "Show what to say in the panel's dialogue".

### Fixed

- The panel's chart buttons showed empty boxes instead of arrows (the simulator's browser lacks those characters):
  "< Cartes", "< Précédente", "Suivante >".
- When the aircraft's registration changes in the simulator (a new registration picked), a callsign that was the old
  registration follows it. A callsign that is not the registration (an airline's, SimBrief's) is left alone. If the panel
  title still shows the old registration, the simulator has not applied the new one yet.

## 0.12.2 - 2026-10-23

### Changed

- A local VFR flight started at a military base now starts in CAM V instead of civil VFR.

## 0.12.1 - 2026-10-23

### Changed

- A flight plan imported from the simulator's planner to or from a **military base** (a profile of a base) now starts in
  **CAM V** (VFR plan) or **CAM I** (IFR plan) instead of VFR or IFR. The planner offers a "military" flight type but
  does not pass it on (the MSFS route has only VFR or IFR), so the base is what tells StormATC. For any other plan, pick
  the regime on the Flight Plan page or on the radio.
- Importing the same plan again no longer undoes a CAM regime the pilot chose.

## 0.12.0 - 2026-10-23

### Added

- **Tactical picture** for a CAM T flight: "demande la situation" (or "situation", "picture", "bogey dope") gives the
  three nearest airborne contacts of the simulator's traffic as bearing (magnetic), range, altitude and aspect (hot,
  flank, cold). In CAM T the traffic is read out to 150 NM instead of 20 km, and the log says every 30 seconds how many
  aircraft the simulator reports, to check that the targets a tablet generates are seen. Needs "Enable live traffic
  awareness". StormATC names no side and gives no clearance to engage.
- A CAM T flight now follows the frequency it has tuned, like a VFR flight.

## 0.11.0 - 2026-10-23

### Added

- **CAM T** (Tango, tactique) regime, selectable on the Flight Plan page or asked for on the radio ("demandons CAM
  Tango"): the controller says it is a tactical flight outside the air traffic service and to call to resume CAM V or
  CAM I. The Instruction 1950 does not describe tactical radio procedures, so nothing else is invented.
- **MARSA** for a flight of at least two aircraft: "patrouille de deux, demande MARSA départ piste 29" is approved
  with the initial heading; "demande MARSA approche, trail trois milles" is approved for the runway in use and ends
  at landing. The Military tab of the panel shows it.
- `docs/militaire.md`: the military side explained in French (regimes, QFE, initial and break, PAR, SRA, MARSA, a
  typical flight at Saint-Dizier, what StormATC does and does not do).

### Changed

- The VAC charts have their own tab in Settings instead of sitting among the voice settings.

## 0.10.0 - 2026-10-23

### Added

- PAR and SRA radar approaches for a military flight ("demande PAR piste 29", "demande SRA piste 29"): the controller
  accepts, gives the QFE and says not to acknowledge, then talks the aircraft down from its position against the
  extended centreline: radar contact, "sur l'axe / légèrement à gauche de l'axe, route XXX", "sur la pente /
  au-dessus / sous la pente" and the distance (PAR), or the distance and the recommended altitude (SRA), every 4 to
  8 seconds. The gear check comes at 2 NM (PAR) or 3 NM (SRA). The PAR ends with the landing clearance at 0.9 NM; the
  SRA ends at 2 NM with "annoncez piste en vue ou remise de gaz", and "piste en vue" is answered with the clearance.
  A go-around ends the approach. The height is the radio height above ground; this is a simulation of the
  procedure, not a certified radar.
- A **Military** tab in the MSFS panel, shown in a CAM regime: the regime and the base, the radar approach in
  progress, the base's frequencies that an MSFS radio can tune (118 to 137 MHz), and the military calls by theme
  (taxi, arrival and break, PAR and SRA, regime change).
- Profiles list their frequencies with the service of each (APP, TWR, GND, PAR...).

## 0.9.1 - 2026-10-23

### Changed

- The SIA's VAC charts are not shipped in the installer nor kept in the repository: Settings > VAC charts downloads
  them (all of them into an empty folder), so the installer stays small and the charts are always the current AIRAC
  cycle. Version 0.9.0 was never published.

## 0.9.0 - 2026-10-23

### Added

- Where the data comes from, in the README (English and French): the SIA's charts and AIP data (reusable unaltered,
  with source and date quoted; SIA legal notice) and the list of aerodromes (Wikipedia, CC BY-SA 4.0). The charts are
  not shipped nor in the repository: StormATC downloads them on request, and `Cartes VAC/LISEZ-MOI.txt` says where
  they come from.
- The list of French aerodromes, with a column saying whether the SIA publishes a VAC chart for each, is installed in
  `data/aerodromes-france.csv`.
- Settings > VAC charts: shows the source and AIRAC cycle of the charts and updates them from the SIA
  (**Update the charts from the SIA**, **Download the missing charts**), replacing only the charts that changed; with
  an empty folder it downloads them all. The
  panel's Charts tab shows the same source line. It contacts `www.sia.aviation-civile.gouv.fr` only, on request.

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
