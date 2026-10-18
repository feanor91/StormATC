[CmdletBinding()]
param(
    [Parameter()]
    [string] $OutputDirectory = "msfs-wasm\prebuilt"
)

# Compiles the StormATC route reader (msfs-wasm\src\module.cpp) to WebAssembly with the toolchain
# of the MSFS SDK: clang-cl and wasm-ld against the WASI sysroot. The result,
# msfs-wasm\prebuilt\stormatc_route.wasm, is committed so that the package and the installer can be
# built without the SDK; run this script again after changing the module's source.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$sdk = $env:MSFS2024_SDK
if ([string]::IsNullOrWhiteSpace($sdk)) { $sdk = "C:\MSFS 2024 SDK\" }
$wasm = Join-Path $sdk "WASM"
$clang = Join-Path $wasm "llvm\bin\clang-cl.exe"
$linker = Join-Path $wasm "llvm\bin\wasm-ld.exe"
$sysroot = Join-Path $wasm "wasi-sysroot"

foreach ($tool in $clang, $linker, $sysroot) {
    if (-not (Test-Path -LiteralPath $tool)) { throw "The MSFS SDK's WASM toolchain was not found: $tool" }
}

$source = Join-Path $repositoryRoot "msfs-wasm\src\module.cpp"
$output = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
New-Item -ItemType Directory -Force $output | Out-Null
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("stormatc-wasm-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null

try {
    $object = Join-Path $work "module.o"
    $compileArguments = @(
        "--target=wasm32-unknown-wasi",
        "/nologo", "/c", "/W3", "/clang:-O2", "/clang:-std=c++17", "/clang:-fno-stack-protector",
        "-fms-extensions", "/GR-", "-Xclang", "-mthread-model", "-Xclang", "single",
        "-D_MSFS_WASM=1", "-D__wasi__", "-D_LIBCPP_HAS_NO_THREADS", "-D_WINDLL", "-D_MBCS", "-DNDEBUG",
        "/clang:--sysroot=$sysroot",
        "-I$(Join-Path $wasm 'include')",
        "-I$(Join-Path $sysroot 'include')",
        "-I$(Join-Path $sdk 'SimConnect SDK\include')",
        "/Fo$object",
        $source)

    # The tools write warnings to stderr, which PowerShell would turn into errors: judge them by exit code.
    $ErrorActionPreference = "Continue"
    & $clang @compileArguments 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "clang-cl failed to compile the module." }

    $library = Join-Path $sysroot "lib\wasm32-wasi"
    $wasmFile = Join-Path $output "stormatc_route.wasm"
    $linkArguments = @(
        "--no-entry", "--allow-undefined", "--export-dynamic", "--export-table",
        "--export=malloc", "--export=free", "--export-if-defined=__wasm_call_ctors",
        # What the simulator's own allocator reports to the host: modules built with the SDK's
        # toolchain export these (the other modules in a Community folder do).
        "--export-if-defined=mallinfo", "--export-if-defined=mchunkit_begin", "--export-if-defined=mchunkit_next",
        "--export-if-defined=get_pages_state", "--export-if-defined=mark_decommit_pages",
        "-L$library",
        $object,
        "-lc",
        (Join-Path $library "libclang_rt.builtins-wasm32.a"),
        "-o", $wasmFile)

    & $linker @linkArguments 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "wasm-ld failed to link the module." }
    $ErrorActionPreference = "Stop"

    Write-Host "Module: $wasmFile ($([int]((Get-Item $wasmFile).Length)) bytes)"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
