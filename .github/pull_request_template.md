## What changed

<!-- Describe the user-visible outcome. -->

## Why

<!-- Link an issue with "Closes #123", or explain why no issue was needed. -->

## Screenshots

<!-- Required for visible UI changes. Remove this section when it does not apply. -->

## Verification

- [ ] `./scripts/Test-RepositorySafety.ps1`
- [ ] `dotnet restore StormATC.sln --locked-mode`
- [ ] `dotnet build StormATC.sln -c Release --no-restore`
- [ ] `dotnet test StormATC.sln -c Release`
- [ ] The affected UI was checked at 100% and 150% Windows scaling
- [ ] No API key, local settings file, transcript, audio, or SimConnect SDK binary was added

## Flight-simulation safety

- [ ] The change remains simulation-only
- [ ] Model output still passes deterministic validation before affecting session state

## Contributor confirmation

- [ ] This is original work that I have the right to submit under the MIT licence
- [ ] I disclosed material AI assistance and reviewed any generated changes
