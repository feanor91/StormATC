# Security policy

## Scope

StormATC is a desktop flight-simulation application. It has no server, no accounts, and no
telemetry. The security surface is therefore small and specific:

- **API keys** held in the operating system secret store
- **Local session data** in a SQLite file under `%LOCALAPPDATA%\StormATC`
- **Outbound requests** to OpenRouter and Deepgram, when the user has configured them
- **Untrusted model output** reaching application state

## Reporting a vulnerability

Please report privately rather than opening a public issue. Use GitHub's
[private vulnerability reporting](https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability)
on this repository.

Include what you did, what happened, and what you expected. A proof of concept helps. You will
get an acknowledgement within a few days; fixes for confirmed issues are prioritised over
features.

## What we consider a vulnerability

- Any path by which an API key can be read from a log, a crash dump, a diagnostics export, an
  exception message, a settings file, or the database
- Any path by which language-model output can change session state without passing domain
  validation
- Any path by which a crafted transcript, flight plan, or aerodrome file can execute code, escape
  the storage directory, or exhaust memory
- Any outbound request carrying data the user did not consent to send

## What we do not consider a vulnerability

- StormATC giving procedurally incorrect ATC instructions. That is a bug, not a vulnerability;
  the application is a game and cannot be used operationally.
- Third-party service pricing, availability, or data handling. Those are governed by the
  agreement between the user and that provider.
- A user deliberately entering their own key into a debugger or a modified build.

## Design commitments

These are enforced in code and in tests, and a change that breaks one is a defect:

| Commitment | Where it lives |
| --- | --- |
| Keys are never written to a settings file | `UserSettingsService.LooksLikeSecret` rejects credential-shaped keys |
| Keys are never written to the database | Nothing in `SqliteSessionRepository` accepts a secret |
| Keys are never logged | `DpapiSecretStore` logs key *names* only |
| Credential-shaped mistakes are redacted at the log sink | `SensitiveDataRedactor` filters formatted messages and exceptions |
| Keys are never exported | `DiagnosticsViewModel.BuildReport` records presence, never value |
| Keys are never shown in the UI | `SettingsViewModel` holds no key field; the value goes straight to the store |
| Keys are stored only on an explicit action | Saving requires a button click; nothing autosaves |
| Keys survive a restart, so a saved key stays saved | `DpapiSecretStore` is registered in every configuration; the in-memory store is used only by tests |
| A key cannot be redirected to another host | `ProviderEndpointPolicy` pins official hosts and provider HTTP clients disable redirects |
| Cloud providers require explicit consent | OpenRouter and Deepgram check `CloudServicesConsentGiven` before reading a key |
| Model output cannot invent a clearance | `PhraseologyValidator` rejects any unapproved number |
| Model output cannot invent an entity | `OpenRouterIntentParser.Validate` re-derives every value |
| Raw audio is not written to disk | The production speech service has no audio-recording path |
| Nothing is contacted until it is configured | Each provider is chosen from its own configuration; `Application:OfflineMode` removes the network-capable ones from the container entirely |

Corresponding tests live in `tests/StormATC.Infrastructure.Tests` and
`tests/StormATC.Application.Tests`.

## Dependencies

`dotnet restore` is expected to complete with no `NU1901`–`NU1904` vulnerability warnings. If a
transitive package is flagged, it is pinned to a fixed version in `Directory.Packages.props`
using central transitive pinning rather than suppressed.

NuGet lock files are committed and CI restores them in locked mode. CI also runs
`scripts/Test-RepositorySafety.ps1`, builds with warnings as errors, runs every test, and lists
vulnerable direct and transitive dependencies.
