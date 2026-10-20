<#
.SYNOPSIS
Prints the version StormATC is built as: the <Version> in Directory.Build.props.

.DESCRIPTION
Directory.Build.props is the only place the version is written by hand. The packaging scripts read
it from here, so a release is always built with the version the source says, and
New-Release.ps1 changes it in one step.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$props = Join-Path $PSScriptRoot "..\Directory.Build.props"
$version = (Select-Xml -Path $props -XPath "//Version").Node.InnerText.Trim()
if ($version -notmatch "^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$") {
    throw "The <Version> in Directory.Build.props ('$version') is not a semantic version."
}

$version
