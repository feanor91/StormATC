[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern("^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$")]
    [string] $Version = "0.1.2",

    [Parameter()]
    [string] $OutputDirectory = "artifacts\release",

    [Parameter()]
    [switch] $AcknowledgeSimConnectLicenseReview
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$allowedOutputPrefix = $repositoryRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not $outputRoot.StartsWith(
        $allowedOutputPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must resolve inside the StormATC repository."
}

if (-not $AcknowledgeSimConnectLicenseReview) {
    throw "Before packaging Microsoft SDK files, review the SDK licences installed with your MSFS SDK and rerun with -AcknowledgeSimConnectLicenseReview. This switch records that review; it does not grant redistribution rights."
}

$managedSimConnect = $env:MSFS_SIMCONNECT_PATH
if ([string]::IsNullOrWhiteSpace($managedSimConnect)) {
    $managedSimConnect = "C:\MSFS SDK\SimConnect SDK\lib\managed\Microsoft.FlightSimulator.SimConnect.dll"
}

if (-not (Test-Path -LiteralPath $managedSimConnect -PathType Leaf)) {
    throw "Microsoft.FlightSimulator.SimConnect.dll was not found. Install the MSFS SDK or set MSFS_SIMCONNECT_PATH before creating a release."
}

$nativeSimConnect = $env:MSFS_SIMCONNECT_NATIVE_PATH
if ([string]::IsNullOrWhiteSpace($nativeSimConnect)) {
    $managedDirectory = Split-Path -Parent $managedSimConnect
    $nativeCandidates = @(
        (Join-Path $managedDirectory "SimConnect.dll"),
        (Join-Path (Split-Path -Parent $managedDirectory) "SimConnect.dll"))
    $nativeSimConnect = $nativeCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($nativeSimConnect) -or
    -not (Test-Path -LiteralPath $nativeSimConnect -PathType Leaf)) {
    throw "The native SimConnect.dll was not found. Set MSFS_SIMCONNECT_NATIVE_PATH before creating a release."
}

& (Join-Path $PSScriptRoot "Test-RepositorySafety.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "Repository safety validation failed."
}

Push-Location $repositoryRoot
try {
    dotnet restore StormATC.sln --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Package restore failed."
    }

    dotnet restore src\StormATC.Desktop\StormATC.Desktop.csproj `
        -r win-x64 `
        --locked-mode `
        -p:NuGetLockFilePath=packages.win-x64.lock.json `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Windows runtime restore failed."
    }

    dotnet test StormATC.sln -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Release tests failed."
    }

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

    $staging = Join-Path $outputRoot "StormATC-v$Version-win-x64"
    if (Test-Path -LiteralPath $staging) {
        $resolvedStaging = [System.IO.Path]::GetFullPath($staging)
        if (-not $resolvedStaging.StartsWith(
                $outputRoot.TrimEnd('\') + '\',
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a staging directory outside the release output."
        }

        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }

    # The test build above produces symbol-bearing Release assemblies. Clean before publishing
    # so MSBuild cannot reuse those incremental outputs when the final package disables symbols.
    dotnet clean src\StormATC.Desktop\StormATC.Desktop.csproj `
        -c Release `
        -r win-x64 `
        --nologo `
        -p:MsfsSimConnectPath="$managedSimConnect" `
        -p:MsfsSimConnectNativePath="$nativeSimConnect"

    if ($LASTEXITCODE -ne 0) {
        throw "Release clean failed."
    }

    dotnet publish src\StormATC.Desktop\StormATC.Desktop.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --no-restore `
        --nologo `
        -p:Version=$Version `
        -p:ContinuousIntegrationBuild=true `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -p:MsfsSimConnectPath="$managedSimConnect" `
        -p:MsfsSimConnectNativePath="$nativeSimConnect" `
        -o "$staging"

    if ($LASTEXITCODE -ne 0) {
        throw "Release publish failed."
    }

    $requiredFiles = @(
        "StormATC.exe",
        "Microsoft.FlightSimulator.SimConnect.dll",
        "SimConnect.dll",
        "appsettings.json")

    foreach ($file in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $file) -PathType Leaf)) {
            throw "Release validation failed: $file is missing from the package."
        }
    }

    if (Test-Path -LiteralPath (Join-Path $staging "appsettings.Development.json")) {
        throw "Release validation failed: development configuration was published."
    }

    $forbiddenPackageFiles = @("secrets.dat", "usersettings.json", ".env")
    foreach ($file in $forbiddenPackageFiles) {
        if (Get-ChildItem -LiteralPath $staging -Recurse -File |
            Where-Object { $_.Name.Equals($file, [System.StringComparison]::OrdinalIgnoreCase) }) {
            throw "Release validation failed: local state '$file' entered the package."
        }
    }

    $publishedSymbols = @(
        Get-ChildItem -LiteralPath $staging -Recurse -File -Filter "*.pdb")
    if ($publishedSymbols.Count -gt 0) {
        throw "Release validation failed: debug symbol files entered the package."
    }

    $stormAtcAssemblies = @(
        "StormATC.exe",
        "StormATC.dll",
        "StormATC.Application.dll",
        "StormATC.Domain.dll",
        "StormATC.Infrastructure.dll")

    foreach ($assembly in $stormAtcAssemblies) {
        $assemblyPath = Join-Path $staging $assembly
        if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
            continue
        }

        # Release binaries must not disclose the developer's local workspace. Decode only for
        # this check and never print the matched path.
        $assemblyText = [System.Text.Encoding]::UTF8.GetString(
            [System.IO.File]::ReadAllBytes($assemblyPath))

        if ($assemblyText.IndexOf(
                $repositoryRoot,
                [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Release validation failed: a StormATC binary contains the local repository path."
        }
    }

    $zipPath = Join-Path $outputRoot "StormATC-v$Version-win-x64.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zipPath

    $hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    $checksumPath = "$zipPath.sha256"
    Set-Content -LiteralPath $checksumPath -Value "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($zipPath))"

    Write-Host "Release created:"
    Write-Host "  $zipPath"
    Write-Host "  $checksumPath"
}
finally {
    Pop-Location
}
