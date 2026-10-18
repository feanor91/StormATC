[CmdletBinding()]
param()

# Compiles msfs-wasm\test\route_json_test.cpp with the desktop C++ compiler (Visual Studio) and runs
# it: the JSON the WASM module publishes is checked without the simulator.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$sdk = $env:MSFS2024_SDK
if ([string]::IsNullOrWhiteSpace($sdk)) { $sdk = "C:\MSFS 2024 SDK\" }
$headers = Join-Path $sdk "WASM\include"

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ([string]::IsNullOrWhiteSpace($vs)) { throw "Visual Studio with the C++ tools was not found." }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("stormatc-wasm-test-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $source = Join-Path $repositoryRoot "msfs-wasm\test\route_json_test.cpp"
    $exe = Join-Path $work "route_json_test.exe"
    $installer = Split-Path -Parent $vswhere
    $command = "set `"PATH=%PATH%;$installer`" && `"$vcvars`" >nul && cl /nologo /EHsc /W3 /std:c++17 /I`"$headers`" /Fo`"$work\\`" /Fe`"$exe`" `"$source`" && `"$exe`""
    & cmd /c $command
    if ($LASTEXITCODE -ne 0) { throw "The module's JSON tests failed." }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

# The compiled module, run against a fake simulator (needs Node.js).
$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    & node (Join-Path $repositoryRoot "msfs-wasm\test\module_host_test.js")
    if ($LASTEXITCODE -ne 0) { throw "The compiled module failed its host test." }
}
else {
    Write-Warning "Node.js was not found: the compiled module was not run against the fake simulator."
}
