# Changelog

All notable user-visible changes are documented here. StormATC uses semantic versioning for
public releases.

## Unreleased

### Added

- Community governance, conduct, roadmap, support, and contributor onboarding.
- One-command contributor environment validation.
- Responsive GitHub Pages landing site with automatic deployment and social link preview.
- Live surface guidance showing the aircraft's matched stand or taxiway, the complete cleared
  route, remaining distance, next turn, clearance limit, and expected taxiway-sign cues inferred
  from the MSFS scenery graph.
- A continuously repeating spoken departure ATIS generated from the published airport frequency,
  assigned runway, and current simulator weather, with its complete text, information code,
  source, and cached replay shown on the Radio page.

### Fixed

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
