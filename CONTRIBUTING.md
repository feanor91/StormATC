# Contributing to StormATC

Thanks for wanting to help. StormATC is a free, open-source project and contributions of every
size are welcome.

## Ways to contribute

You do not need to be an experienced C# developer. Useful contributions include:

- reproducing a speech or readback failure with a redacted transcript;
- testing a release with a different microphone, accent, joystick, simulator state, or display
  scale;
- improving UI design, accessibility, documentation, or error messages;
- writing tests around an existing behaviour;
- reviewing pull requests and answering contributor questions; and
- implementing scoped issues labelled `good first issue` or `help wanted`.

Questions and early ideas belong in GitHub Discussions. Reproducible bugs and agreed work belong
in Issues. Draft pull requests are welcome for early feedback.

## Before you start

Read [docs/architecture.md](docs/architecture.md). One rule shapes almost every decision in this
codebase:

> The simulator determines what is physically true. The deterministic ATC engine determines what
> is permitted. The language model helps understand the pilot and express approved instructions
> naturally.

Most review comments on a first pull request come down to that boundary being crossed.

## Originality

Everything here must be original work.

**Do not copy source code, assets, branding, user-interface layouts, audio, documentation, or
proprietary behaviour from Pilot2ATC, BeyondATC, SayIntentions, or any other commercial
application.** Learning from published ICAO or national phraseology documents is fine; reproducing
another product's implementation, wording, or look is not.

**Do not contribute real navigation data.** The sample aerodromes are fictional and must stay
fictional. If you add sample data, invent it, mark it fictional in a `$comment` block, and use an
ICAO prefix that is not assigned to any real region.

## Getting set up

For the fastest verified setup on Windows:

```powershell
.\scripts\Setup-Development.ps1
```

Built-in mocks support routine development and automated testing without contacting external
services. If your change affects a live integration, test it with your own Microsoft Flight
Simulator installation, hardware, service account, and API key where applicable. Maintainers do
not issue or share credentials. Never place a credential in source code, logs, issues, or pull
requests.

See [docs/contributor-quickstart.md](docs/contributor-quickstart.md) for forking, branching, and
opening your first pull request.

Before committing, also run:

```powershell
.\scripts\Test-RepositorySafety.ps1
```

It scans commit candidates without printing matching credential values. Never commit
`usersettings.json`, `secrets.dat`, `.env` files, raw microphone recordings, diagnostics bundles,
or local SimConnect SDK binaries.

## Where things go

| If you are changing… | It belongs in |
| --- | --- |
| What ATC permits, phraseology, value objects | `StormATC.Domain` |
| The transmission pipeline, abstractions, options | `StormATC.Application` |
| A provider: simulator, SimBrief, OpenRouter, Deepgram, storage | `StormATC.Infrastructure` |
| The window, a page, a view model | `StormATC.Desktop` |

`StormATC.Domain` has no package references and must keep none. If a domain change seems to need
one, the change probably belongs a layer up.

## Coding standards

- Nullable reference types are enabled and the null-safety warnings are errors.
- File-scoped namespaces; `_camelCase` private fields; `PascalCase` everything public.
- Every public type and member carries an XML doc comment.
- Async all the way down. **Never** use `.Result` or `.Wait()`, and never block the UI thread.
- No static service locators and no global mutable state. Everything is constructor-injected.
- No business logic in view models. They observe state and forward commands.
- Do not swallow exceptions. Handle them at a boundary, log with a correlation id, and surface
  something the user can act on.
- Prefer a small, well-named private method over a comment explaining a block.

Comments should say *why*, not *what*. If a piece of code looks odd but is deliberate, the comment
explaining the reason is the valuable part.

## Tests

- New ATC behaviour needs domain tests. That is the part of the system a bug hurts most.
- New provider code needs a test that covers the failure path, not only the success path.
- Tests that need the network, an API key, or a simulator must be traited so they do not run by
  default:

```csharp
[Trait("Category", "Integration")]
[Trait("Category", "RequiresApiKey")]
```

- **Do not delete or weaken a test to get a green run.** If a test is wrong, fix the test and say
  in the pull request why the old expectation was wrong.

## Pull requests

1. For non-trivial work, comment on or open an issue before implementation so effort is not
   duplicated.
2. One concern per pull request.
3. `dotnet build` must produce zero warnings, and `dotnet test` must be green.
4. The repository safety check must pass.
5. Say what you changed and why. If you changed ATC behaviour, say what published procedure it
   reflects.
6. Add screenshots for visible UI changes and test at 100% and 150% Windows scaling.
7. If you touched anything in the security-commitments table in [SECURITY.md](SECURITY.md), say so
   explicitly.

By submitting a contribution, you agree that it is licensed under the repository's MIT licence
and confirm that you have the right to submit it.

## AI-assisted contributions

AI tools are allowed, but the human contributor remains responsible for every submitted line.
Disclose material AI assistance in the pull request, review generated code, and run the same tests
as a hand-written change. Never paste API keys, private reports, proprietary source, licensed SDK
binaries, or another product's code into a model.

Large generated rewrites without a clear problem, review, or verification will not be merged.

## Claims about testing

Be precise about what you actually verified:

- Do not say MSFS integration was tested unless you had a real MSFS connection.
- Do not say a cloud provider was tested unless you had valid credentials.
- "Builds and unit tests pass" is a perfectly good statement, and an honest one.

## Prompts

Prompt files in `prompts/` are loaded from disk at runtime, so you can edit and reload them
without rebuilding — use **Reload prompts from disk** on the Diagnostics page. A prompt may
change *how* a controller speaks. It may never change *what* the controller is permitted to
authorise; that lives in the engine. See [docs/prompt-system.md](docs/prompt-system.md).

## Scope reminder

StormATC is an entertainment product for flight simulation only. It must not be used for
real-world aviation, navigation, flight planning, or air traffic control. Contributions that
present it as anything else will not be accepted.
