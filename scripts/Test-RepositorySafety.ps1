[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

Push-Location $repositoryRoot
try {
    # Git quotes and octal-escapes non-ASCII file names unless told not to, and PowerShell must
    # read the raw bytes as UTF-8: a name such as "étude.md" would otherwise reach the path
    # functions below as a quoted, escaped string and stop the whole check.
    $previousEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    try {
        $candidateFiles = @(
            git -c core.quotepath=false ls-files --cached --others --exclude-standard |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    finally {
        [Console]::OutputEncoding = $previousEncoding
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Git could not enumerate repository files."
    }

    $forbiddenNames = @(
        "secrets.dat",
        "secrets.json",
        "usersettings.json",
        ".env")

    $textExtensions = @(
        ".cs", ".csproj", ".props", ".targets", ".json", ".md", ".ps1",
        ".sln", ".txt", ".xaml", ".xml", ".yaml", ".yml")

    $patterns = @(
        @{
            Name = "private-key marker"
            Pattern = "-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"
        },
        @{
            Name = "provider API key"
            Pattern = "\bsk-(?!(?:test|or-v1-test)-)[A-Za-z0-9_-]{16,}\b"
        },
        @{
            Name = "GitHub token"
            Pattern = "\bgh[opusr]_[A-Za-z0-9_]{20,}\b"
        },
        @{
            Name = "Google API key"
            Pattern = "\bAIza[0-9A-Za-z_-]{20,}\b"
        },
        @{
            Name = "AWS access key"
            Pattern = "\bAKIA[0-9A-Z]{16}\b"
        },
        @{
            Name = "Slack token"
            Pattern = "\bxox[baprs]-[0-9A-Za-z-]{10,}\b"
        })

    $findings = [System.Collections.Generic.List[string]]::new()

    foreach ($relativePath in $candidateFiles) {
        if ($relativePath -eq "scripts/Test-RepositorySafety.ps1") {
            continue
        }

        $leafName = [System.IO.Path]::GetFileName($relativePath)
        if ($forbiddenNames -contains $leafName.ToLowerInvariant()) {
            $findings.Add("$relativePath (forbidden local-state filename)")
            continue
        }

        $extension = [System.IO.Path]::GetExtension($relativePath).ToLowerInvariant()
        if ($textExtensions -notcontains $extension) {
            continue
        }

        $absolutePath = Join-Path $repositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
            continue
        }

        $lineNumber = 0
        foreach ($line in Get-Content -LiteralPath $absolutePath) {
            $lineNumber++
            foreach ($rule in $patterns) {
                if ($line -match $rule.Pattern) {
                    # Report location and rule only. Never echo the matching credential.
                    $findings.Add("$relativePath`:$lineNumber ($($rule.Name))")
                }
            }
        }
    }

    if ($findings.Count -gt 0) {
        $failureMessage =
            "Repository safety check failed. Remove or rotate the suspected secret(s):`n  " +
            ($findings -join "`n  ")
        Write-Error -Message $failureMessage
    }

    Write-Host "Repository safety check passed: no credential-shaped content or local secret files found."
}
finally {
    Pop-Location
}
