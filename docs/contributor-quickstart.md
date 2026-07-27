# Contributor quickstart

You can contribute to most of StormATC without Microsoft Flight Simulator, the MSFS SDK, a
microphone, or any API key. The deterministic engine, mock simulator, typed-transmission path, and
test suite work offline.

## What you need

- Windows 10 or 11 on an x64 computer
- Git
- .NET 10 SDK
- Visual Studio 2022 or another editor with C# support

Fork the repository on GitHub, clone your fork using the URL shown under **Code**, and enter the
checkout:

```powershell
git clone YOUR-FORK-URL
cd StormATC
```

Run the contributor setup check:

```powershell
.\scripts\Setup-Development.ps1
```

It checks the operating system and SDK, scans the checkout for accidental credentials, restores
locked dependencies, builds the solution with zero warnings, and runs every offline test.

Launch the newest source build with:

```powershell
.\scripts\Start-StormATC-Latest.ps1
```

The shortcut is convenient, but a normal editor launch or `dotnet run` also works.

## Pick a change

Look for issues labelled `good first issue` or `help wanted`, or choose a small item in
[ROADMAP.md](../ROADMAP.md). Comment on an issue before investing in a substantial implementation
so work is not duplicated.

For a first pull request, a focused test or error-message improvement is often more valuable than
a large new provider.

## Make a branch

```powershell
git switch -c feature/short-description
```

Keep one concern in the branch. Add or update tests with the implementation. Before pushing:

```powershell
.\scripts\Test-RepositorySafety.ps1
dotnet build StormATC.sln -c Release --nologo
dotnet test StormATC.sln -c Release --no-build --nologo
```

Push the branch to your fork and open a pull request against `main`. Draft pull requests are
welcome when you want early design feedback.

## Where to begin reading

1. [CONTRIBUTING.md](../CONTRIBUTING.md) — project rules and review expectations
2. [architecture.md](architecture.md) — the simulator/engine/model authority boundary
3. [atc-state-machine.md](atc-state-machine.md) — session phases and controller ownership
4. [development.md](development.md) — configuration and common coding tasks
5. [prompt-system.md](prompt-system.md) — safe prompt edits

Questions that are not yet actionable bugs belong in GitHub Discussions. Specific bugs and
approved work belong in Issues.
