[CmdletBinding()]
param(
    [Parameter()]
    [string] $OutputDirectory = "artifacts\site",

    [Parameter()]
    [ValidatePattern("^https://")]
    [string] $BaseUrl = "https://example.github.io/StormATC",

    [Parameter()]
    [ValidatePattern("^https://github\.com/[^/\s]+/[^/\s]+/?$")]
    [string] $RepositoryUrl = "https://github.com/example/StormATC"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$sourceRoot = Join-Path $repositoryRoot "website"
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$allowedPrefix = $repositoryRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not $outputRoot.StartsWith(
        $allowedPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must resolve inside the StormATC repository."
}

if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot "index.html") -PathType Leaf)) {
    throw "The website source is incomplete."
}

if (Test-Path -LiteralPath $outputRoot) {
    $resolvedOutput = [System.IO.Path]::GetFullPath($outputRoot)
    if (-not $resolvedOutput.StartsWith(
            $allowedPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a website output outside the repository."
    }

    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
Copy-Item -Path (Join-Path $sourceRoot "*") -Destination $outputRoot -Recurse -Force

$assetOutput = Join-Path $outputRoot "assets"
New-Item -ItemType Directory -Path $assetOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot "assets\branding\stormatc-logo.png") `
    -Destination (Join-Path $assetOutput "stormatc-logo.png") `
    -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot "assets\branding\StormATC.ico") `
    -Destination (Join-Path $outputRoot "favicon.ico") `
    -Force

$normalizedBaseUrl = $BaseUrl.TrimEnd("/")
$normalizedRepositoryUrl = $RepositoryUrl.TrimEnd("/")
$textFiles = Get-ChildItem -LiteralPath $outputRoot -Recurse -File |
    Where-Object { $_.Extension -in @(".html", ".xml", ".txt") }
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

foreach ($file in $textFiles) {
    $content = [System.IO.File]::ReadAllText($file.FullName)
    $content = $content.Replace("__BASE_URL__", $normalizedBaseUrl)
    $content = $content.Replace("__REPOSITORY_URL__", $normalizedRepositoryUrl)
    [System.IO.File]::WriteAllText($file.FullName, $content, $utf8NoBom)
}

[System.IO.File]::WriteAllText(
    (Join-Path $outputRoot ".nojekyll"),
    [string]::Empty,
    $utf8NoBom)

$remainingTokens = Get-ChildItem -LiteralPath $outputRoot -Recurse -File |
    Where-Object { $_.Extension -in @(".html", ".xml", ".txt") } |
    Select-String -Pattern "__BASE_URL__|__REPOSITORY_URL__"
if ($remainingTokens) {
    throw "Website build validation failed: an environment token remains."
}

Write-Host "GitHub Pages site built at: $outputRoot"
