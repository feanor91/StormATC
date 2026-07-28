# Releasing StormATC

StormATC releases contain a per-user Windows installer, a portable ZIP, and SHA-256 checksums.
The installer creates Start menu and desktop shortcuts and includes an uninstaller. It does not
request administrator access and it does not contain API keys, user settings, transcripts, logs,
or microphone recordings.

## One-time GitHub setup

1. On GitHub, create an empty public repository named `StormATC`. Do not add a generated README,
   licence, or `.gitignore` because this checkout already contains them.
2. Run the repository safety check and review what will be committed:

   ```powershell
   .\scripts\Test-RepositorySafety.ps1
   git add .
   git status
   git commit -m "Initial StormATC release"
   git branch -M main
   git remote add origin https://github.com/YOUR-USER/StormATC.git
   git push -u origin main
   ```

3. Install the two release tools:

   ```powershell
   winget install --id JRSoftware.InnoSetup --exact
   winget install --id GitHub.cli --exact
   gh auth login
   ```

4. Under **Settings → Pages**, select **GitHub Actions** as the source. The `GitHub Pages`
   workflow publishes the responsive landing site in `website/` and updates it whenever that
   source changes on `main`.

## Build a release

Review the licence files installed with the exact Microsoft Flight Simulator SDK version that
will supply SimConnect. When that review is complete:

```powershell
.\scripts\New-StormATCInstaller.ps1 `
  -Version 0.1.2 `
  -AcknowledgeSimConnectLicenseReview
```

This performs the repository safety scan, locked restore, full Release tests, self-contained
`win-x64` publish, package-content validation, ZIP creation, installer compilation, and checksum
generation. Normal builds and the `win-x64` publish use separate checked-in NuGet lock graphs so
both paths remain reproducible. The output is placed in `artifacts\release\`:

- `StormATC-Setup-v0.1.2-win-x64.exe` — normal Windows installer
- `StormATC-v0.1.2-win-x64.zip` — portable package
- `SHA256SUMS-v0.1.2.txt` — hashes for both downloads

Do not rename or edit those files after the checksum file is generated.

## Create and publish the GitHub release

Commit the exact source used to build the installer, create an annotated semantic-version tag,
and push both:

```powershell
git add .
git commit -m "Release StormATC 0.1.2"
git push
git tag -a v0.1.2 -m "StormATC v0.1.2"
git push origin v0.1.2
```

The tag workflow validates that source and creates a draft GitHub Release. Upload the locally
built, licence-reviewed artifacts and keep the release as a draft:

```powershell
.\scripts\Publish-GitHubRelease.ps1 -Version 0.1.2
```

Open the draft on GitHub, test its downloaded installer on another Windows PC, review the
generated release notes, and publish it with:

```powershell
.\scripts\Publish-GitHubRelease.ps1 -Version 0.1.2 -Publish
```

The upload script verifies both SHA-256 hashes and requires the matching tag at `HEAD` before it
changes GitHub.

## Code signing

Unsigned installers work, but Windows identifies their publisher as unknown and reputation-based
SmartScreen warnings are likely for a new project. A Git commit signature is not a Windows
executable signature.

After obtaining a Windows code-signing certificate and installing it in the current user's
certificate store, build with its SHA-1 thumbprint:

```powershell
.\scripts\New-StormATCInstaller.ps1 `
  -Version 0.1.2 `
  -SigningCertificateThumbprint YOUR_CERTIFICATE_THUMBPRINT `
  -AcknowledgeSimConnectLicenseReview
```

The script signs and timestamps both `StormATC.exe` and the Setup executable. Never commit an
exported `.pfx`, certificate password, API key, or GitHub token; those filenames and common
credential patterns are blocked by the repository safety scan.

## Updating

Increase the semantic version for every public release. Installing a newer version over the
existing per-user installation updates the app in place while preserving local settings and
encrypted API keys under `%LOCALAPPDATA%\StormATC`.
