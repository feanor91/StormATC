[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName PresentationFramework

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$solutionPath = Join-Path $repositoryRoot "StormATC.sln"
$releaseExecutable = Join-Path $repositoryRoot (
    "src\StormATC.Desktop\bin\Release\net10.0-windows10.0.19041.0\StormATC.exe")
$launcherLogDirectory = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) (
    "StormATC")
$buildOutputLog = Join-Path $launcherLogDirectory "launcher-build.log"
$buildErrorLog = Join-Path $launcherLogDirectory "launcher-build-error.log"

function Show-LauncherError {
    param([Parameter(Mandatory)][string]$Message)

    [System.Windows.MessageBox]::Show(
        $Message,
        "StormATC launcher",
        [System.Windows.MessageBoxButton]::OK,
        [System.Windows.MessageBoxImage]::Error) | Out-Null
}

try {
    if (-not (Test-Path -LiteralPath $solutionPath)) {
        throw "StormATC.sln was not found at $solutionPath."
    }

    # Release DLLs cannot be replaced while this repository's app is running. Close only the
    # StormATC instance belonging to this checkout, and do so through the normal window-close
    # path so the host can stop speech, SimConnect, and persistence services cleanly.
    $repositoryPrefix = $repositoryRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    foreach ($process in @(Get-Process -Name "StormATC" -ErrorAction SilentlyContinue)) {
        $processPath = $null
        try {
            $processPath = $process.Path
        }
        catch {
            continue
        }

        if ([string]::IsNullOrWhiteSpace($processPath) -or
            -not $processPath.StartsWith(
                $repositoryPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        if (-not $process.CloseMainWindow()) {
            throw "StormATC is already running and could not be closed safely. Close it and click the shortcut again."
        }

        if (-not $process.WaitForExit(8000)) {
            throw "StormATC is still shutting down. Wait a moment, then click the shortcut again."
        }
    }

    $dotnet = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        throw "The .NET SDK is not installed or is not on PATH. Install the .NET 10 SDK, then try again."
    }

    New-Item -ItemType Directory -Path $launcherLogDirectory -Force | Out-Null

    # Build on every click. MSBuild remains incremental, so unchanged launches are quick while
    # source, prompt, resource, and project changes are always reflected in the executable.
    $build = Start-Process `
        -FilePath $dotnet.Source `
        -ArgumentList @(
            "build",
            "StormATC.sln",
            "-c",
            "Release",
            "--nologo") `
        -WorkingDirectory $repositoryRoot `
        -WindowStyle Hidden `
        -Wait `
        -PassThru `
        -RedirectStandardOutput $buildOutputLog `
        -RedirectStandardError $buildErrorLog

    if ($build.ExitCode -ne 0) {
        throw "The latest StormATC source did not build. See:`n$buildOutputLog`n$buildErrorLog"
    }

    if (-not (Test-Path -LiteralPath $releaseExecutable)) {
        throw "The build succeeded, but StormATC.exe was not found at $releaseExecutable."
    }

    # Use the Windows shell so the GUI process is fully detached from this hidden launcher.
    # This keeps the shortcut responsive even when it is started by another terminal or tool.
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $releaseExecutable
    $startInfo.WorkingDirectory = Split-Path -Parent $releaseExecutable
    $startInfo.UseShellExecute = $true

    $launchedProcess = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $launchedProcess) {
        throw "Windows could not start StormATC.exe."
    }

    $launchedProcess.Dispose()
    exit 0
}
catch {
    Show-LauncherError -Message (
        "StormATC could not start the latest version.`n`n" + $_.Exception.Message)
    exit 1
}
