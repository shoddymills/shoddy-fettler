#!/usr/bin/env pwsh
# MAINTAINER TOOL - stages everything and commits it. Writes history; pushes
# nothing. Run from anywhere inside the repo.
#
#   ./scripts/fettler-checkin.ps1 -Message "what changed"
#   ./scripts/fettler-checkin.ps1 -Message "..." -Force      allow it on main
#
# This exists so the procedure is reachable as a script rather than as a raw
# `git add -A; git commit -m ...` composed inside a shell and buried in a
# configuration file, where nobody working at a terminal would find it and no
# ordinary review would see it.
#
# IT REFUSES ON main, and that is the point of it rather than a courtesy.
# main receives merges and nothing else; work happens on a branch. A rule only
# a person can remember gets broken, so it is checked here instead, and
# -Force is the deliberate exception that has to be typed out.
#
# IT PUSHES NOTHING. Committing and publishing are separate decisions, and
# this script only makes the first. `./scripts/fettler-pr.ps1` is what puts the work where
# anyone else can see it.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Message,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

function Fail([string]$Msg, [string]$Remedy = '') {
    Write-Host "STOPPED: $Msg" -ForegroundColor Red
    if ($Remedy) { Write-Host "         $Remedy" -ForegroundColor Yellow }
    exit 1
}

# git says perfectly ordinary things on stderr, and Windows PowerShell turns
# each of those into a NativeCommandError the moment a caller captures both
# streams. Under 'Stop' that terminates a run that was going fine. The exit
# code is the only honest signal a native program gives.
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

Gq rev-parse --is-inside-work-tree > $null
if ($LASTEXITCODE -ne 0) { Fail 'not inside a git repository.' }

# Work from the repository root. `git ls-files` below reports paths relative
# to the current directory, and `git add` is handed those paths - so running
# this from a subdirectory would stage the wrong thing, or nothing.
Set-Location "$(Gq rev-parse --show-toplevel)".Trim()

if (-not $Message.Trim()) { Fail 'the message is empty; say what changed.' }

$branch = Gq rev-parse --abbrev-ref HEAD
if ($LASTEXITCODE -ne 0) { Fail 'could not read the current branch.' }
$branch = "$branch".Trim()

if (($branch -eq 'main' -or $branch -eq 'master') -and -not $Force) {
    Fail "on $branch, which takes merges and not commits." `
         'Cut a branch with ./scripts/fettler-branch.ps1 feature NAME, or pass -Force if you mean it.'
}

Gq rev-parse -q --verify MERGE_HEAD > $null
if ($LASTEXITCODE -eq 0) { Fail 'a merge is in progress - finish or abort it first.' }

# Nothing to commit is not a failure worth a stack trace, but it must not be
# silence either: a task that reports success having done nothing is how you
# come to believe work is saved when it is not.
$dirty = Gq status --porcelain
if (-not $dirty) { Fail 'nothing to commit - the tree is clean.' }

# A .sh git has never seen is staged 100644 unless it is added with the bit
# set, and fettler-verify-permissions.js cannot cover it: that check walks TRACKED
# files, so the window between writing a script and adding it is invisible to
# it. Four scripts once shipped through that window and one took a release
# workflow down with "Permission denied", exit 126.
#
# `git update-index --chmod=+x` is the wrong tool here - it refuses a path git
# does not yet have, with a message about a missing --add option that reads
# like an unrelated fault. `git add --chmod=+x` does both at once.
$fresh = @(Gq ls-files --others --exclude-standard |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -like '*.sh' })
foreach ($f in $fresh) { G add --chmod=+x -- $f }

G add -A
G commit -m $Message

Write-Host ''
Write-Host "Committed on $branch. Nothing has been pushed." -ForegroundColor Green
Write-Host "When the work is ready: ./scripts/fettler-pr.ps1"
