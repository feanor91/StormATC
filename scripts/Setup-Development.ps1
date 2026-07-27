[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solutionPath = Join-Path $repositoryRoot "StormATC.sln"

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    & $script:DotNetPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$isWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)
if (-not $isWindows) {
    throw "The complete StormATC desktop solution currently requires Windows."
}

if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "StormATC.sln was not found. Run this script from a complete repository checkout."
}

$dotnet = Get-Command "dotnet" -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw "The .NET 10 SDK is required. Install it from https://dotnet.microsoft.com/download."
}

$script:DotNetPath = $dotnet.Source
$sdkVersion = (& $script:DotNetPath --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch "^10\.") {
    throw "StormATC requires the .NET 10 SDK. Detected version: '$sdkVersion'."
}

Push-Location $repositoryRoot
try {
    Write-Host "StormATC contributor setup"
    Write-Host "  Repository: $repositoryRoot"
    Write-Host "  .NET SDK:   $sdkVersion"

    & (Join-Path $PSScriptRoot "Test-RepositorySafety.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "Repository safety validation failed."
    }

    Invoke-DotNet -Arguments @(
        "restore",
        "StormATC.sln",
        "--locked-mode",
        "--nologo")

    Invoke-DotNet -Arguments @(
        "build",
        "StormATC.sln",
        "-c",
        "Debug",
        "--no-restore",
        "--nologo")

    Invoke-DotNet -Arguments @(
        "test",
        "StormATC.sln",
        "-c",
        "Debug",
        "--no-restore",
        "--no-build",
        "--nologo")

    $simConnectPath = $env:MSFS_SIMCONNECT_PATH
    if ([string]::IsNullOrWhiteSpace($simConnectPath)) {
        $simConnectPath =
            "C:\MSFS SDK\SimConnect SDK\lib\managed\Microsoft.FlightSimulator.SimConnect.dll"
    }

    if (Test-Path -LiteralPath $simConnectPath -PathType Leaf) {
        Write-Host "  SimConnect:  SDK detected; real MSFS connector can be built."
    }
    else {
        Write-Host "  SimConnect:  SDK not detected; offline mock connector remains available."
    }

    Write-Host ""
    Write-Host "Contributor setup passed. Start the latest app with:"
    Write-Host "  .\scripts\Start-StormATC-Latest.ps1"
}
finally {
    Pop-Location
}
