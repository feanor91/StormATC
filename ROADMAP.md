# StormATC roadmap

StormATC is in an early community-release stage. This roadmap communicates direction; it is not a
promise of dates or a claim that an item is already assigned.

## Current priorities

### Speech and readback reliability

- Improve aviation-number, callsign, waypoint, runway, SID, squawk, and frequency normalisation.
- Expand accent-tolerant phrase matching without training on or storing a user's raw voice.
- Make partial readbacks and corrections feel natural instead of repetitive.
- Add repeatable transcript fixtures for real failure cases with all personal data removed.

### Provider choice and local AI

- Keep OpenRouter and Deepgram as optional integrations rather than hard dependencies.
- Explore direct model adapters for providers such as OpenAI, Anthropic, DeepSeek, and other
  compatible APIs.
- Add more speech-to-text and text-to-speech provider choices behind the existing interfaces.
- Investigate local and fully offline speech recognition and text-to-speech, including controller
  voices that do not require a Deepgram account.
- Preserve explicit consent, encrypted per-user credentials, official-endpoint restrictions, and
  the deterministic ATC authority boundary for every future provider.

Provider names describe areas for investigation, not endorsements, confirmed integrations, or
release-date commitments. Cloud users and contributors supply their own accounts and API keys;
StormATC will not distribute shared credentials.

### Complete gate-to-gate ATC

- Strengthen delivery, ground, tower, departure, approach, and handoff transitions.
- Expand taxi, hold-short, runway-occupancy, go-around, and missed-approach behaviour.
- Make controller ownership and frequency guidance unambiguous.
- Add more fictional routes and aerodromes for automated scenarios.

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
