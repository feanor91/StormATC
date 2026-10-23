<#
.SYNOPSIS
    Updates the VAC charts in StormATC's "Cartes VAC" folder from the SIA eAIP of the current AIRAC cycle.

.DESCRIPTION
    The SIA publishes a new AIRAC cycle every 28 days. This script finds the cycle in effect today (or the one
    you name), downloads the chart of every aerodrome that has a PDF in the folder, and replaces a file only
    when the new chart is different (SHA-256). With -IncludeMissing it also downloads the charts of the
    aerodromes listed in docs/aerodromes-france.csv that the folder does not have yet.

    The charts are the SIA's: they are kept as published, and the cycle date is written to _cycle.txt in the
    folder so the source and date can be quoted. Flight simulation only: never use for real navigation.

.PARAMETER Directory
    The charts folder. Default: "Cartes VAC" in the StormATC installation, or in this repository.

.PARAMETER Cycle
    The AIRAC effective date, such as 2026-10-01. Default: the cycle in effect today.

.PARAMETER IncludeMissing
    Also download the charts of the aerodromes in docs/aerodromes-france.csv that are not in the folder.

.PARAMETER DryRun
    Report what would change without writing anything.

.EXAMPLE
    .\scripts\Update-VacCharts.ps1 -DryRun
    .\scripts\Update-VacCharts.ps1
    .\scripts\Update-VacCharts.ps1 -IncludeMissing
#>
[CmdletBinding()]
param(
    [string] $Directory,
    [string] $Cycle,
    [switch] $IncludeMissing,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot

if (-not $Directory) {
    $installed = Join-Path $env:LOCALAPPDATA 'Programs\StormATC\Cartes VAC'
    $Directory = if (Test-Path -LiteralPath $installed) { $installed } else { Join-Path $repository 'Cartes VAC' }
}
if (-not (Test-Path -LiteralPath $Directory)) {
    throw "The charts folder does not exist: $Directory"
}

# AIRAC effective dates are 28 days apart; 2026-10-01 is one of them.
$reference = [datetime]::new(2026, 10, 1)
if ($Cycle) {
    $effective = [datetime]::ParseExact($Cycle, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
} else {
    $days = [math]::Floor(([datetime]::UtcNow.Date - $reference).TotalDays / 28) * 28
    $effective = $reference.AddDays($days)
}

function Get-ChartUrl([datetime] $date, [string] $icao) {
    $folder = 'eAIP_' + $date.ToString('dd_MMM_yyyy', [Globalization.CultureInfo]::InvariantCulture).ToUpperInvariant()
    "https://www.sia.aviation-civile.gouv.fr/media/dvd/$folder/Atlas-VAC/PDF_AIPparSSection/VAC/AD/AD-2.$icao.pdf"
}

$headers = @{ 'User-Agent' = 'StormATC-VAC-update (personal flight simulation use)' }

function Test-Chart([datetime] $date, [string] $icao) {
    try {
        $null = Invoke-WebRequest -Uri (Get-ChartUrl $date $icao) -Method Head -Headers $headers -UseBasicParsing
        return $true
    } catch {
        return $false
    }
}

# The cycle is probed with a chart that exists everywhere; if it is not online, say so rather than guess.
if (-not (Test-Chart $effective 'LFPL')) {
    throw "The AIRAC cycle $($effective.ToString('yyyy-MM-dd')) is not online at the SIA (yet). Pass -Cycle with another date."
}
Write-Host "AIRAC cycle: $($effective.ToString('yyyy-MM-dd')) -> $Directory"

$wanted = [System.Collections.Generic.SortedSet[string]]::new()
Get-ChildItem -LiteralPath $Directory -Filter 'AD-2.*.pdf' | ForEach-Object {
    if ($_.BaseName -match '^AD-2\.(L[FG][A-Z]{2})$') { [void] $wanted.Add($Matches[1]) }
}
if ($IncludeMissing) {
    $list = Join-Path $repository 'docs\aerodromes-france.csv'
    if (-not (Test-Path -LiteralPath $list)) { throw "The list of aerodromes is missing: $list" }
    Import-Csv -LiteralPath $list -Delimiter ';' | ForEach-Object {
        if ($_.OACI -match '^L[FG][A-Z]{2}$') { [void] $wanted.Add($_.OACI) }
    }
}

$updated = 0; $added = 0; $same = 0; $absent = 0
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('vac-' + [guid]::NewGuid().ToString('N') + '.pdf')
try {
    foreach ($icao in $wanted) {
        $target = Join-Path $Directory "AD-2.$icao.pdf"
        try {
            Invoke-WebRequest -Uri (Get-ChartUrl $effective $icao) -Headers $headers -UseBasicParsing -OutFile $temporary
        } catch {
            $absent++
            continue
        }
        $bytes = [IO.File]::ReadAllBytes($temporary)
        if ($bytes.Length -lt 4 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne '%PDF') { $absent++; continue }

        if (-not (Test-Path -LiteralPath $target)) {
            $added++
            Write-Host "  new      $icao"
            if (-not $DryRun) { Copy-Item -LiteralPath $temporary -Destination $target }
        } elseif ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
            $updated++
            Write-Host "  changed  $icao"
            if (-not $DryRun) { Copy-Item -LiteralPath $temporary -Destination $target -Force }
        } else {
            $same++
        }
        Start-Sleep -Milliseconds 400   # one chart at a time, and gently
    }
} finally {
    Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
}

if (-not $DryRun) {
    $note = "Source: SIA, AIP France, AIRAC cycle effective $($effective.ToString('yyyy-MM-dd')). Charts kept as published. Updated $(Get-Date -Format 'yyyy-MM-dd')."
    Set-Content -LiteralPath (Join-Path $Directory '_cycle.txt') -Value $note -Encoding UTF8
}

$verb = if ($DryRun) { 'would be' } else { 'were' }
Write-Host "$updated changed, $added new, $same unchanged, $absent not published by the SIA ($verb written)."
if (($updated + $added) -gt 0 -and -not $DryRun) {
    Write-Host 'Profiles extracted from charts can be refreshed with: python scripts/extract_vac_profiles.py "<folder>" --overwrite'
}
