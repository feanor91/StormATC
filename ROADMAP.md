# StormATC roadmap

StormATC is in an early community-release stage. This roadmap communicates direction; it is not a
promise of dates or a claim that an item is already assigned.

Items marked **Done** have shipped; **Partly done** items say what remains. The notes are kept
short; the commit history has the detail.

## Current priorities

### Speech and readback reliability

- Improve aviation-number, callsign, waypoint, runway, SID, squawk, and frequency normalisation.
  *Partly done:* French numbers, squawk pairs and "unité", frequencies said without "décimale"
  ("cent dix-huit sept cinq"), runway sides said as a letter, and registrations spelled in the
  phonetic alphabet (full or abbreviated). SIDs and waypoints are unchanged.
- Expand accent-tolerant phrase matching without training on or storing a user's raw voice.
- Make partial readbacks and corrections feel natural instead of repetitive.
- Add repeatable transcript fixtures for real failure cases with all personal data removed.

### Provider choice and local AI

- Keep OpenRouter, OpenAI, Groq, DeepSeek, Anthropic, and Deepgram optional rather than hard
  dependencies.
- Expand provider-specific structured-output compatibility as model capabilities evolve.
- Add more speech-to-text and text-to-speech provider choices behind the existing interfaces.
- Investigate local and fully offline speech recognition and text-to-speech, including controller
  voices that do not require a Deepgram account. **Done:** Whisper (recognition) and Piper (voice)
  run on the PC, chosen in Settings; see `docs/french-and-local-voice.md`.
- Preserve explicit consent, encrypted per-user credentials, official-endpoint restrictions, and
  the deterministic ATC authority boundary for every future provider.

Provider names describe areas for investigation, not endorsements, confirmed integrations, or
release-date commitments. Cloud users and contributors supply their own accounts and API keys;
StormATC will not distribute shared credentials.

### Complete gate-to-gate ATC

- Strengthen delivery, ground, tower, departure, approach, and handoff transitions.
  **Done:** Delivery→Ground, Ground→Tower, Tower→Departure, Departure→Center (leaving the terminal
  area), Center/Departure→Approach, Approach→Tower, Tower→Ground and Tower→Approach after a
  go-around. The engine now notices the aircraft's own actions with the simulator running (it
  used to do so only from the mock simulator's buttons) and speaks the handoff.
- Expand taxi, hold-short, runway-occupancy, go-around, and missed-approach behaviour.
  *Partly done:* takeoff clearance is refused for traffic on short final to either end of the
  runway; a go-around is answered by the controller who has the aircraft, climbs 2000 ft above the
  aerodrome by default, and is handed to Approach.
  Runway crossings: a pilot held short of a runway on the taxi route asks ("demandons à traverser")
  and is cleared to cross, or told to hold when the runway is occupied or traffic is on short final.
  Still missing: "line up and wait" is not modelled (the tower only clears the takeoff, or holds
  the aircraft short), so there is no alignment step to refuse or delay.
- Make controller ownership and frequency guidance unambiguous.
  **Done:** when a request goes to the wrong controller the reply now gives the frequency
  to call ("contactez Tour 118,700"), not only the name.
- Add more fictional routes and aerodromes for automated scenarios.
  **Done:** two more invented aerodromes (Storm Kilo STMK, Storm Tango STMT: east-west runway, a
  taxiway crossing it to a remote stand) and a second demonstration flight, STORM456 STMK→STMT
  (set `STORMATC_SAMPLE_FLIGHT=STORM456-STMK-STMT` to use it).

### User experience and accessibility

- Finish keyboard navigation, focus states, screen-reader names, and high-DPI testing.
- Improve first-run guidance and provider setup errors.
- Make device, microphone, joystick PTT, and radio state easier to understand.
- Add safe import/export for non-secret preferences.

### Simulator reliability

- Exercise reconnects, aircraft changes, flight reloads, pauses, and simulator shutdown.
- Add captured, licence-safe test fixtures around the SimConnect adapter.
- Improve diagnostics without collecting telemetry or secrets.

### Project health

- Grow domain and provider failure-path test coverage.
- Add installer update testing and signed-release support.
- Keep dependencies locked, audited, and small.
- Build a group of reviewers and maintainers rather than relying on one person.

## Good first contributions

These are useful areas for newcomers. Open or claim an issue before starting a change larger than
a few lines.

- Add a focused unit test for a callsign, number, runway, or frequency spelling variant.
- Improve an unclear error message or documentation example.
- Add automation IDs or accessible names to a UI control.
- Add a fictional transcript fixture that reproduces one parsing failure.
- Improve XML documentation on a public API.
- Add a failure-path test for an existing provider.
- Verify a page at 100%, 125%, and 150% Windows scaling and report exact problems.

Maintainers should label scoped newcomer work with `good first issue` and unassigned work with
`help wanted`.

## Out of scope

- Real-world operational ATC, navigation, or flight planning.
- Shipping API keys, provider accounts, or paid access with the application.
- Copying another ATC product's code, assets, prompts, UI, voices, or proprietary behaviour.
- Allowing a language model to bypass deterministic ATC validation.
- Bundling unlicensed navigation data or SDK files.
