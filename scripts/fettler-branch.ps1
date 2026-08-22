#!/usr/bin/env pwsh
# MAINTAINER TOOL - creates branches, and deletes them locally and on origin.
# Assumes write access. Run from anywhere inside the repo.
#
#   ./scripts/fettler-branch.ps1 feature NAME    cut feature/NAME off an up-to-date main
#   ./scripts/fettler-branch.ps1 bug NAMENN      cut bug/NAMENN off an up-to-date main
#   ./scripts/fettler-branch.ps1 sync            bring main's commits into the current branch
#   ./scripts/fettler-branch.ps1 land [-Yes]     after the PR is merged: return to main and
#                                delete the branch, local and origin
#
# WHAT THIS DOES NOT DO IS MERGE. The pull request is the gate, and it is a
# human one: `./scripts/fettler-pr.ps1` opens it, a person reviews it and presses the button,
# and `land` only tidies up afterwards. `land` REFUSES on a branch whose pull
# request is not merged, so it cannot be used to skip the review by another
# name.
#
# Guards, in the order they are checked: inside a repository; no merge in
# progress; the tree is clean. Then per verb - `feature` and `bug` refuse a
# name already taken locally or on origin; a bug name must end in two digits,
# numbered per origin feature, so bug/reinard01 is the first fix to work that
# shipped from feature/reinard; `sync` and `land` refuse on main and on a
# branch that is neither feature/* nor bug/*.
#
# A leading `feature/` or `bug/` on NAME is stripped, so both spellings work.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command,
    [Parameter(Position = 1)][string]$Name,
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'

function Fail([string]$Msg, [string]$Remedy = '') {
    Write-Host "STOPPED: $Msg" -ForegroundColor Red
    if ($Remedy) { Write-Host "         $Remedy" -ForegroundColor Yellow }
    exit 1
}

# git says perfectly ordinary things on stderr - "Already on 'main'",
# "Switched to branch", the whole of a push's progress. Run plainly that is
# harmless, but the moment a CALLER redirects (`./scripts/fettler-branch.ps1 land 2>&1 | tee
# log.txt`, or any runner that captures both streams) Windows PowerShell wraps
# each stderr line in a NativeCommandError, and under $ErrorActionPreference =
# 'Stop' that terminates the run halfway through. The exit code is the only
# honest signal a native program gives, so judge every call on that alone.
function Gq {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { git @args } finally { $ErrorActionPreference = $prev }
}

# Gq's twin for calls whose failure should stop the script rather than be
# inspected. Everything a precondition asks - "is the tree dirty?" - uses Gq,
# because there a non-zero answer IS the answer.
function G {
    Write-Host ("> git " + ($args -join ' ')) -ForegroundColor Cyan
    Gq @args
    if ($LASTEXITCODE -ne 0) { Fail "git $($args -join ' ') failed (exit $LASTEXITCODE)." }
}

# The one cut, shared by 'feature' and 'bug'. The prefix is the only thing
# the two verbs disagree about.
function Cut([string]$Branch) {
    # LOCAL REFS ARE NOT EVIDENCE ABOUT ORIGIN. They go stale the moment
    # somebody else pushes and nothing announces it, so both checks below are
    # worthless without this.
    G fetch origin --prune

    Gq show-ref --verify --quiet "refs/heads/$Branch"
    if ($LASTEXITCODE -eq 0) { Fail "$Branch already exists locally." }
    Gq show-ref --verify --quiet "refs/remotes/origin/$Branch"
    if ($LASTEXITCODE -eq 0) { Fail "$Branch already exists on origin." }

    G checkout main
    G pull --ff-only origin main
    G checkout -b $Branch

    Write-Host ''
    Write-Host "On $Branch, cut from an up-to-date main." -ForegroundColor Green
    Write-Host "Work, then: ./scripts/fettler-checkin.ps1 -Message ""what changed"""
}

function CurrentBranch() {
    $b = Gq rev-parse --abbrev-ref HEAD
    if ($LASTEXITCODE -ne 0) { Fail 'could not read the current branch.' }
    return "$b".Trim()
}

function RequireWorkBranch([string]$Verb) {
    $b = CurrentBranch
    if ($b -notlike 'feature/*' -and $b -notlike 'bug/*') {
        Fail "current branch is '$b' - $Verb only runs from a feature/* or bug/* branch."
    }
    return $b
}

# ---- preconditions, before any verb ----
Gq rev-parse --is-inside-work-tree > $null
if ($LASTEXITCODE -ne 0) { Fail 'not inside a git repository.' }

Gq rev-parse -q --verify MERGE_HEAD > $null
if ($LASTEXITCODE -eq 0) { Fail 'a merge is in progress - finish or abort it first.' }

Gq diff --quiet
if ($LASTEXITCODE -ne 0) {
    Fail 'unstaged changes present.' 'Commit them with ./scripts/fettler-checkin.ps1, or stash them. See git status.'
}
Gq diff --cached --quiet
if ($LASTEXITCODE -ne 0) {
    Fail 'staged-but-uncommitted changes present.' 'Commit them with ./scripts/fettler-checkin.ps1, or unstage them.'
}

switch ($Command) {

    'feature' {
        if (-not $Name) { Fail 'usage: ./scripts/fettler-branch.ps1 feature NAME' }
        $Name = $Name -replace '^feature/', ''
        if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
            Fail "a branch name may use letters, digits, . _ - only (got '$Name')."
        }
        Cut "feature/$Name"
    }

    'bug' {
        if (-not $Name) { Fail 'usage: ./scripts/fettler-branch.ps1 bug NAMENN   (bug/<origin-feature>NN, e.g. bug reinard01)' }
        $Name = $Name -replace '^bug/', ''
        if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
            Fail "a branch name may use letters, digits, . _ - only (got '$Name')."
        }
        # The number is not decoration: the name says which shipped feature
        # the fix belongs to and which fix it is, so bug/reinard refuses
        # where bug/reinard01 is meant.
        if ($Name -notmatch '\d\d$') {
            Fail "a bug branch is bug/<origin-feature>NN, numbered per origin feature (got '$Name')." `
                 "Taken numbers: git --no-pager log --oneline main --grep=""Merge pull request"""
        }
        Cut "bug/$Name"
    }

    'sync' {
        # main has moved and this branch has not. Doing it here, deliberately
        # and while the tree is clean, is far better than discovering it as a
        # conflict inside the pull request.
        $branch = RequireWorkBranch 'sync'
        G fetch origin --prune

        $behind = [int](Gq rev-list --count "HEAD..origin/main")
        if ($behind -eq 0) {
            Write-Host ''
            Write-Host "$branch already has everything on main. Nothing to do." -ForegroundColor Green
            exit 0
        }

        Write-Host ''
        Write-Host "Bringing $behind commit(s) from main into $branch." -ForegroundColor Yellow
        Write-Host "> git merge origin/main" -ForegroundColor Cyan
        Gq merge origin/main -m "Merge main into $branch"
        if ($LASTEXITCODE -ne 0) {
            Fail "conflicts merging main into $branch." `
                 "Resolve them, 'git add' the files, and 'git commit'. Nothing else has changed."
        }

        Write-Host ''
        Write-Host "$branch is now up to date with main." -ForegroundColor Green
    }

    'land' {
        # AFTER the pull request is merged, and only then. This deletes a
        # branch, so it asks GitHub whether the work is actually on main
        # before throwing anything away.
        $branch = RequireWorkBranch 'land'
        G fetch origin --prune

        $merged = $false
        $how = ''

        $gh = Get-Command gh -ErrorAction SilentlyContinue
        if ($gh) {
            $prev = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try { $state = & gh pr view $branch --json state 2>&1 | Out-String } finally { $ErrorActionPreference = $prev }
            if ($LASTEXITCODE -eq 0 -and $state -match '"state":"MERGED"') {
                $merged = $true; $how = 'its pull request is merged'
            } elseif ($LASTEXITCODE -eq 0 -and $state -match '"state":"(OPEN|CLOSED)"') {
                Fail "the pull request for $branch is $($Matches[1].ToLower()), not merged." `
                     "Merge it on GitHub first - landing is not a way round the review."
            }
        }

        # No gh, or no pull request found. Fall back to asking git whether
        # every commit on this branch is already reachable from origin/main.
        # This is the honest answer for a merge commit; a SQUASHED merge
        # rewrites the commits, so it will say no even when the work landed -
        # which is why it only warns rather than deciding.
        if (-not $merged) {
            $ahead = [int](Gq rev-list --count "origin/main..$branch")
            if ($ahead -eq 0) { $merged = $true; $how = 'every commit on it is already on origin/main' }
        }

        if (-not $merged) {
            Write-Host ''
            Write-Host "Cannot confirm $branch has been merged." -ForegroundColor Yellow
            Write-Host "  gh reported no merged pull request, and commits on this branch are" -ForegroundColor Yellow
            Write-Host "  not all reachable from origin/main. If the PR was SQUASH-merged this" -ForegroundColor Yellow
            Write-Host "  is expected and the branch is safe to delete." -ForegroundColor Yellow
            Write-Host ''
            if (-not $Yes) {
                $a = Read-Host "Delete $branch anyway? (y/N)"
                if ($a -notmatch '^(y|yes)$') { Write-Host 'Nothing was deleted.'; exit 0 }
            } else {
                Fail "will not delete an unconfirmed branch under -Yes." `
                     "Run it without -Yes to decide interactively."
            }
            $how = 'you confirmed it'
        }

        Write-Host ''
        Write-Host "Landing $branch ($how)." -ForegroundColor Green

        G checkout main
        G pull --ff-only origin main
        # -D rather than -d: a squash-merged branch is not "merged" as far as
        # git is concerned, and -d would refuse it. The check above is what
        # decides whether deleting is safe; git's own is too narrow here.
        G branch -D $branch

        Gq show-ref --verify --quiet "refs/remotes/origin/$branch"
        if ($LASTEXITCODE -eq 0) {
            G push origin --delete $branch
        } else {
            Write-Host "origin/$branch was already gone." -ForegroundColor DarkGray
        }

        Write-Host ''
        Write-Host "DONE - on main, and $branch is deleted locally and on origin." -ForegroundColor Green
    }

    default {
        Write-Host 'usage: ./scripts/fettler-branch.ps1 feature NAME    cut feature/NAME off an up-to-date main'
        Write-Host '       ./scripts/fettler-branch.ps1 bug NAMENN      cut bug/NAMENN off an up-to-date main'
        Write-Host "       ./scripts/fettler-branch.ps1 sync            bring main's commits into the current branch"
        Write-Host '       ./scripts/fettler-branch.ps1 land [-Yes]     after the PR is merged: return to main and delete the branch'
        exit 2
    }
}
