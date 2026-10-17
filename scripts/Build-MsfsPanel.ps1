[CmdletBinding()]
param(
    [Parameter()]
    [string] $OutputDirectory = "artifacts\msfs-panel",

    # Compile the panel definition again with the MSFS SDK and refresh the committed .spb.
    # Needed only after editing msfs-panel\package\PackageSources; MSFS must be running.
    [Parameter()]
    [switch] $Recompile,

    [Parameter()]
    [string] $PackageTool
)

# Builds the MSFS toolbar package (stormatc-ingamepanel): a folder to drop into MSFS's
# Community folder (the StormATC settings page can do that for you).
#
# The panel's page, script and style are plain files. The only compiled part is the panel
# definition (InGamePanel_StormATC.spb, made from PackageSources\InGamePanels\*.xml by the SDK's
# fspackagetool). It is committed in msfs-panel\prebuilt so that the package can be built, and
# the application released, without the SDK. manifest.json and layout.json are written here.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$source = Join-Path $repositoryRoot "msfs-panel"
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$allowedPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $outputRoot.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must resolve inside the StormATC repository."
}

$spbName = "InGamePanel_StormATC.spb"
$prebuiltSpb = Join-Path $source "prebuilt\$spbName"

if ($Recompile) {
    if ([string]::IsNullOrWhiteSpace($PackageTool)) {
        $PackageTool = @(
            $env:MSFS_SDK_PACKAGETOOL,
            "C:\MSFS 2024 SDK\Tools\bin\fspackagetool.exe",
            "C:\MSFS SDK\Tools\bin\fspackagetool.exe") |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
            Select-Object -First 1
    }

    if ([string]::IsNullOrWhiteSpace($PackageTool)) {
        throw "fspackagetool.exe was not found. Install the MSFS SDK or pass -PackageTool."
    }

    if (-not (Get-Process -Name "FlightSimulator2024", "FlightSimulator" -ErrorAction SilentlyContinue)) {
        throw "Start Microsoft Flight Simulator first: fspackagetool compiles inside the running simulator."
    }

    $compile = Join-Path $outputRoot "_compile"
    if (Test-Path -LiteralPath $compile) {
        Remove-Item -LiteralPath $compile -Recurse -Force
    }

    New-Item -ItemType Directory -Force $compile | Out-Null
    Copy-Item -Path (Join-Path $source "package\*") -Destination $compile -Recurse -Force

    $project = Join-Path $compile "stormatc-ingamepanel.xml"
    $compiled = Join-Path $compile "Packages\stormatc-ingamepanel\InGamePanels\$spbName"

    # fspackagetool writes progress to stderr, which PowerShell would turn into errors. When
    # the simulator is running it attaches to it and may wait for ever after the work is done,
    # so wait for the .spb and stop the tool.
    $process = Start-Process -FilePath $PackageTool `
        -ArgumentList @("`"$project`"", "-rebuild", "-nopause", "-outputtoseparateconsole") `
        -WorkingDirectory $compile -PassThru -NoNewWindow

    $deadline = (Get-Date).AddSeconds(420)
    while (-not $process.HasExited -and (Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $compiled)) {
        Start-Sleep -Milliseconds 500
    }

    Start-Sleep -Seconds 3
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path -LiteralPath $compiled)) {
        throw "fspackagetool did not produce $spbName."
    }

    New-Item -ItemType Directory -Force (Split-Path -Parent $prebuiltSpb) | Out-Null
    Copy-Item -LiteralPath $compiled -Destination $prebuiltSpb -Force
    Remove-Item -LiteralPath $compile -Recurse -Force
    Write-Host "Refreshed $prebuiltSpb"
}

if (-not (Test-Path -LiteralPath $prebuiltSpb)) {
    # The application still builds and releases without the package; only the settings page's
    # install button then has nothing to copy.
    Write-Warning "$prebuiltSpb is missing, so no MSFS panel package was built. Run this script with -Recompile while MSFS is running."
    return
}

$destination = Join-Path $outputRoot "stormatc-ingamepanel"
if (Test-Path -LiteralPath $destination) {
    Remove-Item -LiteralPath $destination -Recurse -Force
}

# html_ui from the package sources, plus the page and style shared with the standalone page.
New-Item -ItemType Directory -Force $destination | Out-Null
Copy-Item -Path (Join-Path $source "package\html_ui") -Destination $destination -Recurse -Force
$panelFolder = Join-Path $destination "html_ui\InGamePanels\StormATC"
foreach ($name in "panel-core.js", "panel.css") {
    Copy-Item -LiteralPath (Join-Path $source "web\$name") -Destination $panelFolder -Force
}

New-Item -ItemType Directory -Force (Join-Path $destination "InGamePanels") | Out-Null
Copy-Item -LiteralPath $prebuiltSpb -Destination (Join-Path $destination "InGamePanels\$spbName") -Force

# layout.json lists every file with its size and its time as a Windows FILETIME.
$root = [System.IO.Path]::GetFullPath($destination).TrimEnd('\') + '\'
$files = @(Get-ChildItem -LiteralPath $destination -Recurse -File | Sort-Object FullName)
$entries = foreach ($file in $files) {
    [ordered]@{
        path = $file.FullName.Substring($root.Length).Replace('\', '/')
        size = $file.Length
        date = $file.LastWriteTimeUtc.ToFileTimeUtc()
    }
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText(
    (Join-Path $destination "layout.json"),
    ([ordered]@{ content = @($entries) } | ConvertTo-Json -Depth 5),
    $utf8)

$totalSize = ($files | Measure-Object -Property Length -Sum).Sum
$manifest = [ordered]@{
    dependencies = @()
    content_type = "MISC"
    title = "StormATC panel"
    manufacturer = "StormATC"
    creator = "StormATC"
    package_version = "0.1.0"
    minimum_game_version = "1.9.18"
    minimum_compatibility_version = "9.18.0.250"
    export_type = "Community"
    builder = "Microsoft Flight Simulator 2024"
    package_order_hint = "UI_PATCH"
    release_notes = [ordered]@{ neutral = [ordered]@{ LastUpdate = ""; OlderHistory = "" } }
    total_package_size = "$totalSize"
}

[System.IO.File]::WriteAllText(
    (Join-Path $destination "manifest.json"),
    ($manifest | ConvertTo-Json -Depth 5),
    $utf8)

Write-Host "MSFS panel package: $destination"
