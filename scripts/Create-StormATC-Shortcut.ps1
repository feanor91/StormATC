[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$launcherPath = Join-Path $PSScriptRoot "Start-StormATC-Latest.ps1"
$iconPath = Join-Path $repositoryRoot "assets\branding\StormATC.ico"
$desktopPath = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::DesktopDirectory)
$shortcutPath = Join-Path $desktopPath "StormATC (Latest).lnk"
$windowsPowerShell = Join-Path $env:SystemRoot (
    "System32\WindowsPowerShell\v1.0\powershell.exe")

foreach ($requiredPath in @($launcherPath, $iconPath, $windowsPowerShell)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required launcher file was not found: $requiredPath"
    }
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $windowsPowerShell
$shortcut.Arguments = (
    '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f
    $launcherPath)
$shortcut.WorkingDirectory = $repositoryRoot
$shortcut.IconLocation = "$iconPath,0"
$shortcut.Description = "Build and start the latest StormATC Release version"
$shortcut.Save()

Write-Output $shortcutPath
