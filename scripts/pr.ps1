#!/usr/bin/env pwsh
# MAINTAINER TOOL - pushes the current branch and opens a pull request.
# Assumes write access and the GitHub CLI. Run from anywhere inside the repo.
#
#   ./scripts/pr.ps1                     push the branch and open a pull request
#   ./scripts/pr.ps1 -Draft              open it as a draft
#   ./scripts/pr.ps1 -Title "..."        set the title (default: the first commit's subject)
#   ./scripts/pr.ps1 -Web                open the finished pull request in a browser
#
# THIS IS WHERE THE AUTOMATION STOPS, deliberately. Everything either side of
# the pull request is a script - ./scripts/branch.ps1 cuts, ./scripts/commit.ps1 commits,
# ./scripts/branch.ps1 land tidies up, ./scripts/ship.ps1 tags - but the merge itself is a
# person reading a diff and a green CI run and deciding. Nothing here merges,
# and ./scripts/branch.ps1 land refuses on a branch whose pull request is not merged,
# so there is no back way round that decision.
#
# It is re-runnable. If a pull request for this branch already exists it
# pushes the new commits and prints the existing one rather than failing.
#
# Guards: inside a repository; the tree is clean, so what is reviewed is what
# is committed; on a feature/* or bug/* branch; something to review; and
# `gh` is installed and authenticated.
[CmdletBinding()]
param(
    [switch]$Draft,
    [string]$Title = '',
    [switch]$Web
)
$ErrorActionPreference = 'Stop'

function Fail([string]$Msg, [string]$Remedy = '') {
    Write-Host "STOPPED: $Msg" -ForegroundColor Red
    if ($Remedy) { Write-Host "         $Remedy" -ForegroundColor Yellow }
    exit 1
}

# See branch.ps1 for why every call is judged on its exit code alone: git
# writes ordinary progress to stderr, and Windows PowerShell turns that into
# a terminating error the moment a caller captures both streams.
function Gq {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { git @args } finally { $ErrorActionPreference = $prev }
}
function G {
    Write-Host ("> git " + ($args -join ' ')) -ForegroundColor Cyan
    Gq @args
    if ($LASTEXITCODE -ne 0) { Fail "git $($args -join ' ') failed (exit $LASTEXITCODE)." }
}
function Ghq {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & gh @args 2>&1 | Out-String } finally { $ErrorActionPreference = $prev }
    return $out.Trim()
}

# ---- preconditions ----
Gq rev-parse --is-inside-work-tree > $null
if ($LASTEXITCODE -ne 0) { Fail 'not inside a git repository.' }

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail 'the GitHub CLI (gh) is not installed.' `
         'Install it, or push the branch yourself and open the pull request on github.com.'
}

Ghq auth status > $null
if ($LASTEXITCODE -ne 0) { Fail 'gh is not authenticated.' 'Run: gh auth login' }

Gq rev-parse -q --verify MERGE_HEAD > $null
if ($LASTEXITCODE -eq 0) { Fail 'a merge is in progress - finish or abort it first.' }

# A dirty tree here is the quiet way to get a review of something other than
# what you meant: the reviewer sees the commits, not your working copy.
if (Gq status --porcelain) {
    Fail 'the working tree is dirty.' `
         'Commit with ./scripts/commit.ps1 - a pull request reviews commits, not your working copy.'
}

$branch = "$(Gq rev-parse --abbrev-ref HEAD)".Trim()
if ($branch -notlike 'feature/*' -and $branch -notlike 'bug/*') {
    Fail "current branch is '$branch' - a pull request is opened from a feature/* or bug/* branch." `
         'Cut one with ./scripts/branch.ps1 feature NAME.'
}

G fetch origin --prune

$ahead = [int](Gq rev-list --count "origin/main..HEAD")
if ($ahead -eq 0) { Fail "no commits on $branch beyond origin/main - there is nothing to review." }

# main may have moved underneath this branch. That is not fatal - GitHub will
# say so on the pull request - but saying it here, before the reviewer is
# invited, is cheaper for everyone.
$behind = [int](Gq rev-list --count "HEAD..origin/main")
if ($behind -gt 0) {
    Write-Host ''
    Write-Host "NOTE: main has $behind commit(s) this branch does not have." -ForegroundColor Yellow
    Write-Host "      ./scripts/branch.ps1 sync brings them in, and is worth doing first." -ForegroundColor Yellow
}

Write-Host ''
Write-Host "$ahead commit(s) on $branch to review:" -ForegroundColor Yellow
Gq log --oneline origin/main..HEAD

# ---- push ----
Write-Host ''
Write-Host "> git push -u origin $branch" -ForegroundColor Cyan
Gq push -u origin $branch
if ($LASTEXITCODE -ne 0) {
    Fail "push rejected - origin/$branch has commits you do not have." `
         'git pull, then re-run.'
}

# ---- the pull request ----
# Re-runnable on purpose: pushing more commits to a branch that already has a
# pull request is the normal way to answer review comments, and that must not
# be an error.
$existing = Ghq pr view $branch --json url,state
if ($LASTEXITCODE -eq 0 -and $existing -match '"url":"([^"]+)"') {
    $url = $Matches[1]
    $state = if ($existing -match '"state":"([A-Z]+)"') { $Matches[1] } else { 'UNKNOWN' }
    Write-Host ''
    Write-Host "Pull request already open ($state) - the new commits are on it." -ForegroundColor Green
    Write-Host "  $url"
    if ($Web) { Ghq pr view $branch --web > $null }
    exit 0
}

if (-not $Title) {
    # The first commit's subject is very nearly always the right title, and a
    # title somebody has to invent twice is a title that goes stale.
    $Title = "$(Gq log --format=%s "origin/main..HEAD" | Select-Object -Last 1)".Trim()
}
if (-not $Title) { $Title = $branch }

$body = @"
Merging ``$branch``.

$( (Gq log --format='- %s' "origin/main..HEAD") -join "`n" )

---
Checked before opening:

- ``./scripts/build.ps1 test`` - Fettler.Tests and burler.Tests
- ``./scripts/build.ps1 check`` - twins, permissions, docs, errors

CI runs both again on Windows and macOS.
"@

$bodyFile = Join-Path ([System.IO.Path]::GetTempPath()) "fettler-pr-$PID.md"
[System.IO.File]::WriteAllText($bodyFile, $body, (New-Object System.Text.UTF8Encoding($false)))

try {
    $ghArgs = @('pr', 'create', '--base', 'main', '--head', $branch,
                '--title', $Title, '--body-file', $bodyFile)
    if ($Draft) { $ghArgs += '--draft' }

    Write-Host ''
    Write-Host ("> gh " + ($ghArgs -join ' ')) -ForegroundColor Cyan
    $created = Ghq @ghArgs
    if ($LASTEXITCODE -ne 0) { Fail "gh pr create failed." $created }
} finally {
    Remove-Item $bodyFile -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Pull request opened. Nothing is merged.' -ForegroundColor Green
Write-Host "  $created"
Write-Host ''
Write-Host 'Next: review it, wait for CI, and merge it on GitHub.'
Write-Host 'Then: ./scripts/branch.ps1 land'

if ($Web) { Ghq pr view $branch --web > $null }
