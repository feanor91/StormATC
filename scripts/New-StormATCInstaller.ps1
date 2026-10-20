[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern("^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$")]
    [string] $Version,

    [Parameter()]
    [string] $OutputDirectory = "artifacts\release",

    [Parameter()]
    [string] $InnoCompilerPath,

    [Parameter()]
    [string] $SigningCertificateThumbprint,

    [Parameter()]
    [switch] $AcknowledgeSimConnectLicenseReview
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# The version is the one in Directory.Build.props unless one is given.
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (& (Join-Path $PSScriptRoot "Get-StormAtcVersion.ps1") | Select-Object -First 1)
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$staging = Join-Path $outputRoot "StormATC-v$Version-win-x64"
$installerScript = Join-Path $repositoryRoot "installer\StormATC.iss"
$zipPath = Join-Path $outputRoot "StormATC-v$Version-win-x64.zip"
$setupPath = Join-Path $outputRoot "StormATC-Setup-v$Version-win-x64.exe"
$checksumsPath = Join-Path $outputRoot "SHA256SUMS-v$Version.txt"
$numericVersion = ([regex]::Match($Version, "^\d+\.\d+\.\d+").Value) + ".0"

function Resolve-InnoCompiler {
    if (-not [string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
        $explicitPath = [System.IO.Path]::GetFullPath($InnoCompilerPath)
        if (-not (Test-Path -LiteralPath $explicitPath -PathType Leaf)) {
            throw "ISCC.exe was not found at '$explicitPath'."
        }

        return $explicitPath
    }

    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $programFilesX86 = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFilesX86)
    $programFiles = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFiles)
    $localPrograms = Join-Path (
        [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)) "Programs"
    $candidates = @(
        (Join-Path $localPrograms "Inno Setup 6\ISCC.exe"),
        (Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe"),
        (Join-Path $programFiles "Inno Setup 6\ISCC.exe"))

    $found = $candidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($found)) {
        throw "Inno Setup 6 is required. Install it with: winget install --id JRSoftware.InnoSetup --exact"
    }

    return $found
}

function Resolve-SignTool {
    $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $found = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1

        if (-not [string]::IsNullOrWhiteSpace($found)) {
            return $found
        }
    }

    throw "signtool.exe was not found. Install the Windows SDK before using code signing."
}

function Invoke-CodeSigning {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if ([string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
        return
    }

    $signTool = Resolve-SignTool
    & $signTool sign `
        /sha1 $SigningCertificateThumbprint `
        /fd SHA256 `
        /tr "http://timestamp.digicert.com" `
        /td SHA256 `
        $Path

    if ($LASTEXITCODE -ne 0) {
        throw "Code signing failed for '$Path'."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "The signature on '$Path' did not validate: $($signature.StatusMessage)"
    }
}

if (-not $AcknowledgeSimConnectLicenseReview) {
    throw "Review the SimConnect SDK licences first, then pass -AcknowledgeSimConnectLicenseReview. The switch records your review; it does not grant redistribution rights."
}

if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) {
    throw "The installer definition was not found at '$installerScript'."
}

$innoCompiler = Resolve-InnoCompiler

& (Join-Path $PSScriptRoot "Publish-StormATC.ps1") `
    -Version $Version `
    -OutputDirectory $OutputDirectory `
    -AcknowledgeSimConnectLicenseReview

if ($LASTEXITCODE -ne 0) {
    throw "The application package could not be created."
}

$appExecutable = Join-Path $staging "StormATC.exe"
if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) {
    throw "The published StormATC executable is missing."
}

if (-not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    Invoke-CodeSigning -Path $appExecutable

    if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zipPath
}

if (Test-Path -LiteralPath $setupPath -PathType Leaf) {
    Remove-Item -LiteralPath $setupPath -Force
}

& $innoCompiler `
    /Qp `
    "/DAppVersion=$Version" `
    "/DNumericVersion=$numericVersion" `
    "/DSourceDir=$staging" `
    "/DOutputDir=$outputRoot" `
    $installerScript

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed to compile the installer."
}

if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "The installer compiler succeeded, but '$setupPath' was not created."
}

Invoke-CodeSigning -Path $setupPath

$releaseFiles = @($setupPath, $zipPath)
$checksumLines = foreach ($file in $releaseFiles) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Release artifact '$file' is missing."
    }

    $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($file))"
}

Set-Content -LiteralPath $checksumsPath -Value $checksumLines -Encoding UTF8

Write-Host "Windows release created:"
Write-Host "  $setupPath"
Write-Host "  $zipPath"
Write-Host "  $checksumsPath"

if ([string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    Write-Warning "The app and installer are unsigned. Windows SmartScreen may show an unknown-publisher warning."
}
