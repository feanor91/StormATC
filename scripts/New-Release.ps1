<#
.SYNOPSIS
Raises the version and prepares a release commit and tag.

.DESCRIPTION
Every release has a higher version than the one before it. This script is the way to get one:

  .\scripts\New-Release.ps1                 # 0.2.0 -> 0.2.1 (patch, the default)
  .\scripts\New-Release.ps1 -Bump minor     # 0.2.1 -> 0.3.0
  .\scripts\New-Release.ps1 -Version 1.0.0  # an explicit version, which must be higher

It changes the version in Directory.Build.props and in the application manifest, turns the
"Unreleased" section of CHANGELOG.md into the section of the new version (and opens a new empty
"Unreleased" one), commits that as "Release StormATC X.Y.Z" and creates the annotated tag vX.Y.Z.
With -Push it pushes the branch and the tag; the tag starts the release workflow, which refuses a
tag that does not match the source or is not higher than the previous release.

It does not build or upload anything: see docs/releasing.md.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateSet("patch", "minor", "major")]
    [string] $Bump = "patch",

    [Parameter()]
    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string] $Version,

    [Parameter()]
    [switch] $Push
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
Set-Location $root

function Invoke-Git {
    $output = & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed." }
    $output
}

if (@(Invoke-Git status --porcelain --untracked-files=no).Count -gt 0) {
    throw "The working tree has uncommitted changes. Commit or stash them first: a release commit holds only the version."
}

$current = [version](& (Join-Path $PSScriptRoot "Get-StormAtcVersion.ps1") | Select-Object -First 1 | ForEach-Object { $_ -replace "-.*$", "" })

if ($Version) {
    $next = [version]$Version
}
else {
    switch ($Bump) {
        "major" { $next = [version]::new($current.Major + 1, 0, 0) }
        "minor" { $next = [version]::new($current.Major, $current.Minor + 1, 0) }
        default { $next = [version]::new($current.Major, $current.Minor, $current.Build + 1) }
    }
}

if ($next -le $current) {
    throw "The new version $next must be higher than the current version $current."
}

$tag = "v$next"
if (@(Invoke-Git tag --list $tag).Count -gt 0) {
    throw "The tag $tag already exists."
}

# A release is never lower than the highest tag, whatever the source says.
foreach ($existing in @(Invoke-Git tag --list "v*")) {
    $parsed = $null
    if ([version]::TryParse(($existing.TrimStart("v") -replace "-.*$", ""), [ref]$parsed) -and $parsed -ge $next) {
        throw "The tag $existing is already at or above $tag: a release must be higher than every earlier one."
    }
}

function Set-FileText([string] $path, [scriptblock] $edit) {
    $text = [System.IO.File]::ReadAllText($path)
    $updated = & $edit $text
    if ($updated -eq $text) { throw "Nothing to change in $path." }
    [System.IO.File]::WriteAllText($path, $updated, [System.Text.UTF8Encoding]::new($false))
}

Set-FileText (Join-Path $root "Directory.Build.props") {
    param($t)
    $t = $t -replace "<Version>[^<]*</Version>", "<Version>$next</Version>"
    $t = $t -replace "<AssemblyVersion>[^<]*</AssemblyVersion>", "<AssemblyVersion>$next.0</AssemblyVersion>"
    $t -replace "<FileVersion>[^<]*</FileVersion>", "<FileVersion>$next.0</FileVersion>"
}

Set-FileText (Join-Path $root "src\StormATC.Desktop\app.manifest") {
    param($t)
    $t -replace '(<assemblyIdentity version=")[^"]*(")', "`${1}$next.0`${2}"
}

$changelog = Join-Path $root "CHANGELOG.md"
$today = Get-Date -Format "yyyy-MM-dd"
Set-FileText $changelog {
    param($t)
    if ($t -notmatch "(?m)^## Unreleased\s*$") { throw "CHANGELOG.md has no 'Unreleased' section." }
    $t -replace "(?m)^## Unreleased\s*$", "## Unreleased`n`n## $next - $today"
}

Invoke-Git add Directory.Build.props src/StormATC.Desktop/app.manifest CHANGELOG.md | Out-Null
Invoke-Git commit -m "Release StormATC $next" | Out-Null
Invoke-Git tag -a $tag -m "StormATC $tag" | Out-Null
Write-Host "Version $current -> $next committed and tagged $tag."

if ($Push) {
    Invoke-Git push | Out-Null
    Invoke-Git push origin $tag | Out-Null
    Write-Host "Pushed the branch and $tag."
}
else {
    Write-Host "Push it with: git push; git push origin $tag"
}
