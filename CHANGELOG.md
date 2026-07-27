# Changelog

All notable user-visible changes are documented here. StormATC uses semantic versioning for
public releases.

## Unreleased

### Added

- Community governance, conduct, roadmap, support, and contributor onboarding.
- One-command contributor environment validation.
- Responsive GitHub Pages landing site with automatic deployment and social link preview.

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
