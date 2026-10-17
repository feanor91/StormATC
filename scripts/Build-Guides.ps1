[CmdletBinding()]
param(
    [Parameter()]
    [string[]] $Guides = @("third-party-downloads", "french-and-local-voice"),

    [Parameter()]
    [string] $EdgePath
)

# Turns the user guides in docs\*.md into PDFs (docs\guides\*.pdf), which the application ships
# and opens from the settings page. The Markdown stays the source: rerun this after editing a
# guide and commit the PDFs. Needs Microsoft Edge (installed with Windows 10 and 11).

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$docs = Join-Path $repositoryRoot "docs"
$outputDirectory = Join-Path $docs "guides"
New-Item -ItemType Directory -Force $outputDirectory | Out-Null

if ([string]::IsNullOrWhiteSpace($EdgePath)) {
    $EdgePath = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($EdgePath)) {
    throw "Microsoft Edge was not found. Pass its location with -EdgePath."
}

function ConvertTo-InlineHtml([string] $text) {
    $html = [System.Net.WebUtility]::HtmlEncode($text)
    $html = [regex]::Replace($html, '`([^`]+)`', '<code>$1</code>')
    $html = [regex]::Replace($html, '\*\*([^*]+)\*\*', '<strong>$1</strong>')
    # Links to other guides or files mean nothing on paper: keep the text only.
    $html = [regex]::Replace($html, '\[([^\]]+)\]\((https?://[^)]+)\)', '<a href="$2">$1</a>')
    $html = [regex]::Replace($html, '\[([^\]]+)\]\([^)]*\)', '$1')
    # The guides that ship with the application are PDFs next to each other.
    $html = [regex]::Replace($html, '\b(third-party-downloads|french-and-local-voice)\.md\b', '$1.pdf')
    $html = [regex]::Replace($html, '&lt;(https?://[^&]+)&gt;', '<a href="$1">$1</a>')
    return $html
}

function ConvertTo-GuideHtml([string[]] $lines) {
    $body = New-Object System.Text.StringBuilder
    $listType = $null
    $inCode = $false
    $paragraph = New-Object System.Collections.Generic.List[string]

    function Close-Paragraph {
        if ($paragraph.Count -gt 0) {
            [void] $body.AppendLine("<p>" + (ConvertTo-InlineHtml ($paragraph -join " ")) + "</p>")
            $paragraph.Clear()
        }
    }

    function Close-List {
        if ($null -ne $script:listType) {
            [void] $body.AppendLine("</$($script:listType)>")
            $script:listType = $null
        }
    }

    $script:listType = $null
    $index = 0
    while ($index -lt $lines.Count) {
        $line = $lines[$index]

        if ($line -match '^```') {
            Close-Paragraph; Close-List
            if ($inCode) {
                [void] $body.AppendLine("</code></pre>")
                $inCode = $false
            } else {
                [void] $body.Append("<pre><code>")
                $inCode = $true
            }

            $index++
            continue
        }

        if ($inCode) {
            [void] $body.AppendLine([System.Net.WebUtility]::HtmlEncode($line))
            $index++
            continue
        }

        if ($line -match '^\s*$') {
            Close-Paragraph; Close-List
            $index++
            continue
        }

        if ($line -match '^(#{1,4})\s+(.*)$') {
            Close-Paragraph; Close-List
            $level = $Matches[1].Length
            [void] $body.AppendLine("<h$level>" + (ConvertTo-InlineHtml $Matches[2]) + "</h$level>")
            $index++
            continue
        }

        if ($line -match '^\|' -and ($index + 1) -lt $lines.Count -and $lines[$index + 1] -match '^\|[\s:|-]+\|\s*$') {
            Close-Paragraph; Close-List
            $headers = $line.Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() }
            [void] $body.AppendLine("<table><thead><tr>")
            foreach ($header in $headers) {
                [void] $body.AppendLine("<th>" + (ConvertTo-InlineHtml $header) + "</th>")
            }

            [void] $body.AppendLine("</tr></thead><tbody>")
            $index += 2
            while ($index -lt $lines.Count -and $lines[$index] -match '^\|') {
                # Split on pipes outside code spans.
                $cells = [regex]::Split($lines[$index].Trim().Trim('|'), '\|(?=(?:[^`]*`[^`]*`)*[^`]*$)')
                [void] $body.AppendLine("<tr>")
                foreach ($cell in $cells) {
                    [void] $body.AppendLine("<td>" + (ConvertTo-InlineHtml $cell.Trim()) + "</td>")
                }

                [void] $body.AppendLine("</tr>")
                $index++
            }

            [void] $body.AppendLine("</tbody></table>")
            continue
        }

        if ($line -match '^\s*[-*]\s+(.*)$' -or $line -match '^\s*\d+\.\s+(.*)$') {
            Close-Paragraph
            $type = if ($line -match '^\s*\d+\.') { "ol" } else { "ul" }
            if ($script:listType -ne $type) {
                Close-List
                [void] $body.AppendLine("<$type>")
                $script:listType = $type
            }

            $item = [regex]::Replace($line, '^\s*(?:[-*]|\d+\.)\s+', '')
            # A wrapped line of the same item continues it.
            while (($index + 1) -lt $lines.Count -and $lines[$index + 1] -match '^\s{2,}\S' -and
                   $lines[$index + 1] -notmatch '^\s*(?:[-*]|\d+\.)\s') {
                $index++
                $item += " " + $lines[$index].Trim()
            }

            [void] $body.AppendLine("<li>" + (ConvertTo-InlineHtml $item) + "</li>")
            $index++
            continue
        }

        Close-List
        $paragraph.Add($line.Trim())
        $index++
    }

    Close-Paragraph; Close-List
    return $body.ToString()
}

$style = @"
body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 10.5pt; line-height: 1.45; color: #1d2430; margin: 0; }
h1 { font-size: 20pt; color: #0f3a5f; border-bottom: 2px solid #0f3a5f; padding-bottom: 4px; }
h2 { font-size: 14.5pt; color: #0f3a5f; margin-top: 20px; }
h3 { font-size: 12pt; color: #25557f; margin-top: 14px; }
code { font-family: Consolas, monospace; font-size: 9.5pt; background: #eef2f6; padding: 0 3px; border-radius: 3px; }
pre { background: #f3f5f8; border: 1px solid #d5dbe3; border-radius: 4px; padding: 8px 10px; white-space: pre-wrap; word-break: break-all; page-break-inside: avoid; }
pre code { background: none; padding: 0; }
table { border-collapse: collapse; width: 100%; margin: 8px 0 12px; font-size: 9.5pt; }
th, td { border: 1px solid #c9d1db; padding: 4px 7px; text-align: left; vertical-align: top; word-break: break-word; }
th { background: #e6edf4; }
tr { page-break-inside: avoid; }
a { color: #0b5cad; }
@page { size: A4; margin: 16mm 15mm; }
"@

$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("stormatc-guides-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $temporary | Out-Null
try {
    foreach ($guide in $Guides) {
        $source = Join-Path $docs "$guide.md"
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Guide not found: $source"
        }

        $lines = [System.IO.File]::ReadAllLines($source, [System.Text.Encoding]::UTF8)
        $html = "<!doctype html><html lang=`"fr`"><head><meta charset=`"utf-8`"><style>$style</style></head><body>" +
            (ConvertTo-GuideHtml $lines) + "</body></html>"
        $htmlPath = Join-Path $temporary "$guide.html"
        [System.IO.File]::WriteAllText($htmlPath, $html, (New-Object System.Text.UTF8Encoding($false)))

        $pdfPath = Join-Path $outputDirectory "$guide.pdf"
        if (Test-Path -LiteralPath $pdfPath) {
            Remove-Item -LiteralPath $pdfPath -Force
        }

        $arguments = @(
            "--headless", "--disable-gpu", "--no-pdf-header-footer",
            "--user-data-dir=$(Join-Path $temporary 'profile')",
            "--print-to-pdf=$pdfPath",
            ([System.Uri] $htmlPath).AbsoluteUri)
        $process = Start-Process -FilePath $EdgePath -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
        if (-not (Test-Path -LiteralPath $pdfPath)) {
            throw "Edge did not produce $pdfPath (exit code $($process.ExitCode))."
        }

        Write-Host "Created $pdfPath ($([int]((Get-Item $pdfPath).Length / 1KB)) KB)"
    }
}
finally {
    Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
}
