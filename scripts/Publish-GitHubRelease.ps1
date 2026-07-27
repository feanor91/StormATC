[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern("^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$")]
    [string] $Version,

    [Parameter()]
    [ValidatePattern("^[^/\s]+/[^/\s]+$")]
    [string] $Repository,

    [Parameter()]
    [string] $OutputDirectory = "artifacts\release",

    [Parameter()]
    [switch] $Publish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$tag = "v$Version"
$setupPath = Join-Path $outputRoot "StormATC-Setup-v$Version-win-x64.exe"
$zipPath = Join-Path $outputRoot "StormATC-v$Version-win-x64.zip"
$checksumsPath = Join-Path $outputRoot "SHA256SUMS-v$Version.txt"
$assets = @($setupPath, $zipPath, $checksumsPath)

foreach ($asset in $assets) {
    if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) {
        throw "Release asset '$asset' is missing. Run New-StormATCInstaller.ps1 first."
    }
}

$expectedHashes = @{}
foreach ($line in Get-Content -LiteralPath $checksumsPath) {
    if ($line -notmatch "^([0-9a-fA-F]{64})\s{2}(.+)$") {
        throw "The checksum file contains an invalid line."
    }

    $expectedHashes[$Matches[2]] = $Matches[1].ToLowerInvariant()
}

foreach ($asset in @($setupPath, $zipPath)) {
    $name = [System.IO.Path]::GetFileName($asset)
    if (-not $expectedHashes.ContainsKey($name)) {
        throw "The checksum file does not contain '$name'."
    }

    $actual = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expectedHashes[$name]) {
        throw "Checksum validation failed for '$name'. Rebuild the release."
    }
}

function Resolve-GitHubCli {
    $command = Get-Command "gh" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $programFiles = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFiles)
    $localPrograms = Join-Path (
        [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)) "Programs"
    $candidates = @(
        (Join-Path $programFiles "GitHub CLI\gh.exe"),
        (Join-Path $localPrograms "GitHub CLI\gh.exe"))
    $found = $candidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($found)) {
        throw "GitHub CLI is required. Install it with: winget install --id GitHub.cli --exact"
    }

    return $found
}

$gh = Resolve-GitHubCli

& $gh auth status
if ($LASTEXITCODE -ne 0) {
    throw "GitHub CLI is not signed in. Run: gh auth login"
}

Push-Location $repositoryRoot
try {
    $tagsAtHead = @(git tag --points-at HEAD)
    if ($tagsAtHead -notcontains $tag) {
        throw "HEAD is not tagged '$tag'. Commit the release, then run: git tag -a $tag -m `"StormATC $tag`""
    }

    $repoArguments = @()
    if (-not [string]::IsNullOrWhiteSpace($Repository)) {
        $repoArguments = @("--repo", $Repository)
    }

    $releaseJson = & $gh release view $tag @repoArguments --json isDraft
    if ($LASTEXITCODE -ne 0) {
        & $gh release create $tag `
            @repoArguments `
            --draft `
            --verify-tag `
            --generate-notes `
            --title "StormATC $tag"

        if ($LASTEXITCODE -ne 0) {
            throw "The draft GitHub release could not be created."
        }
    }
    else {
        $release = $releaseJson | ConvertFrom-Json
        if (-not $release.isDraft) {
            throw "GitHub release '$tag' is already published. Refusing to replace its artifacts."
        }
    }

    & $gh release upload $tag @assets @repoArguments --clobber
    if ($LASTEXITCODE -ne 0) {
        throw "Release asset upload failed."
    }

    if ($Publish) {
        & $gh release edit $tag @repoArguments --draft=false --latest
        if ($LASTEXITCODE -ne 0) {
            throw "The assets uploaded, but the draft could not be published."
        }

        Write-Host "StormATC $tag is published on GitHub."
    }
    else {
        Write-Host "Artifacts uploaded to the $tag draft. Review it on GitHub before publishing."
    }
}
finally {
    Pop-Location
}
