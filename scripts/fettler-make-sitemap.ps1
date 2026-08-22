#!/usr/bin/env pwsh
# Regenerate the docs sitemap.
#
#   ./scripts/fettler-make-sitemap.ps1 [DOCSDIR] [BASEURL]
#
# Every *.html under DOCSDIR except 404.html gets a <url> entry; index.html
# becomes the directory URL. <lastmod> is the file's last git commit date, so
# it tracks real changes - run with fetch-depth: 0 in CI. A file that is
# dirty or untracked falls back to its mtime, so a local run before a commit
# still stamps today.
#
# THE BASE URL IS DEFINED IN ONE PLACE, here and in the .sh twin, and the
# Pages workflow passes no override. When the site's address is settled,
# this is the line to change.
#
# The twin of fettler-make-sitemap.sh. The Pages runner is Linux and calls the .sh;
# this half exists so a sitemap can be regenerated and eyeballed on the
# machine the site is actually written on, before anything is pushed.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$DocsDir = 'docs',
    [Parameter(Position = 1)][string]$BaseUrl = 'https://shoddymills.github.io/shoddy-fettler/'
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$docs = Join-Path $root $DocsDir
if (-not (Test-Path $docs)) {
    Write-Host "STOPPED: no such directory: $docs" -ForegroundColor Red
    exit 1
}

$out = Join-Path $docs 'sitemap.xml'
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('<?xml version="1.0" encoding="UTF-8"?>')
$lines.Add('<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">')

# Sorted by ordinal so the file is byte-identical to what the .sh twin
# writes; `LC_ALL=C sort` there is the same ordering.
$pages = Get-ChildItem $docs -Recurse -File -Filter '*.html' |
    Where-Object { $_.Name -ne '404.html' } |
    Sort-Object { $_.FullName.Substring($docs.Length).Replace('\', '/') } -CaseSensitive

foreach ($page in $pages) {
    $rel = $page.FullName.Substring($docs.Length).TrimStart('\', '/').Replace('\', '/')

    if ($rel -eq 'index.html')            { $loc = $BaseUrl }
    elseif ($rel.EndsWith('/index.html')) { $loc = $BaseUrl + $rel.Substring(0, $rel.Length - 'index.html'.Length) }
    else                                  { $loc = $BaseUrl + $rel }

    # git writes its "not tracked" complaint to stderr and answers with an
    # exit code; judge it on the code alone. Windows PowerShell turns a
    # native program's redirected stderr into error records otherwise, and
    # an untracked file is an ordinary answer here, not a failure.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        git -C $root ls-files --error-unmatch $page.FullName 2>&1 | Out-Null
        $tracked = ($LASTEXITCODE -eq 0)
        $dirty = $true
        if ($tracked) {
            $status = git -C $root status --porcelain -- $page.FullName 2>&1
            $dirty = -not [string]::IsNullOrWhiteSpace(($status | Out-String))
        }
        if ($tracked -and -not $dirty) {
            $mod = (git -C $root log -1 --format=%cs -- $page.FullName 2>&1 | Out-String).Trim()
        } else {
            $mod = $page.LastWriteTime.ToString('yyyy-MM-dd')
        }
    } finally { $ErrorActionPreference = $prev }

    if ([string]::IsNullOrWhiteSpace($mod)) { $mod = $page.LastWriteTime.ToString('yyyy-MM-dd') }

    $lines.Add("  <url><loc>$loc</loc><lastmod>$mod</lastmod></url>")
}

$lines.Add('</urlset>')

# LF and no BOM, matching what the twin writes and what .gitattributes
# pins. Set-Content would use the platform ending and a UTF-8 BOM.
$text = ($lines -join "`n") + "`n"
[System.IO.File]::WriteAllText($out, $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "wrote $out"
