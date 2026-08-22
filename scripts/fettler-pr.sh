#!/usr/bin/env bash
# MAINTAINER TOOL - pushes the current branch and opens a pull request.
# Assumes write access and the GitHub CLI. Run from anywhere inside the repo.
#
#   ./scripts/fettler-pr.sh                     push the branch and open a pull request
#   ./scripts/fettler-pr.sh -Draft              open it as a draft
#   ./scripts/fettler-pr.sh -Title "..."        set the title (default: the first commit's subject)
#   ./scripts/fettler-pr.sh -Web                open the finished pull request in a browser
#
# The twin of fettler-pr.ps1 and equivalent to it, down to the spelling of the flags:
# -Draft, -Title and -Web on both sides, deliberately.
#
# THIS IS WHERE THE AUTOMATION STOPS, deliberately. Everything either side of
# the pull request is a script - ./scripts/fettler-branch.sh cuts, ./scripts/fettler-checkin.sh commits,
# ./scripts/fettler-branch.sh land tidies up, ./scripts/fettler-ship.sh tags - but the merge itself is a
# person reading a diff and a green CI run and deciding. Nothing here merges,
# and ./scripts/fettler-branch.sh land refuses on a branch whose pull request is not merged,
# so there is no back way round that decision.
#
# It is re-runnable. If a pull request for this branch already exists it
# pushes the new commits and prints the existing one rather than failing.
#
# Guards: inside a repository; the tree is clean, so what is reviewed is what
# is committed; on a feature/* or bug/* branch; something to review; and
# `gh` is installed and authenticated.
set -euo pipefail

draft=0
web=0
title=""
while [ $# -gt 0 ]; do
    case "$1" in
        -Draft) draft=1; shift ;;
        -Web)   web=1; shift ;;
        -Title) title=${2:-}; shift 2 ;;
        *)      echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

fail() {
    echo "STOPPED: $1" >&2
    [ $# -gt 1 ] && echo "         $2" >&2
    exit 1
}

g() {
    echo "> git $*"
    git "$@" || fail "git $* failed (exit $?)."
}

# ---- preconditions ----
git rev-parse --is-inside-work-tree >/dev/null 2>&1 || fail "not inside a git repository."

command -v gh >/dev/null 2>&1 || fail \
    "the GitHub CLI (gh) is not installed." \
    "Install it, or push the branch yourself and open the pull request on github.com."

gh auth status >/dev/null 2>&1 || fail "gh is not authenticated." "Run: gh auth login"

git rev-parse -q --verify MERGE_HEAD >/dev/null 2>&1 \
    && fail "a merge is in progress - finish or abort it first."

# A dirty tree here is the quiet way to get a review of something other than
# what you meant: the reviewer sees the commits, not your working copy.
[ -z "$(git status --porcelain)" ] || fail \
    "the working tree is dirty." \
    "Commit with ./scripts/fettler-checkin.sh - a pull request reviews commits, not your working copy."

branch=$(git rev-parse --abbrev-ref HEAD)
case "$branch" in
    feature/*|bug/*) ;;
    *) fail "current branch is '$branch' - a pull request is opened from a feature/* or bug/* branch." \
            "Cut one with ./scripts/fettler-branch.sh feature NAME." ;;
esac

g fetch origin --prune

ahead=$(git rev-list --count "origin/main..HEAD")
[ "$ahead" != "0" ] || fail "no commits on $branch beyond origin/main - there is nothing to review."

# main may have moved underneath this branch. That is not fatal - GitHub will
# say so on the pull request - but saying it here, before the reviewer is
# invited, is cheaper for everyone.
behind=$(git rev-list --count "HEAD..origin/main")
if [ "$behind" != "0" ]; then
    echo
    echo "NOTE: main has $behind commit(s) this branch does not have."
    echo "      ./scripts/fettler-branch.sh sync brings them in, and is worth doing first."
fi

echo
echo "$ahead commit(s) on $branch to review:"
git log --oneline origin/main..HEAD

# ---- push ----
echo
echo "> git push -u origin $branch"
git push -u origin "$branch" || fail \
    "push rejected - origin/$branch has commits you do not have." \
    "git pull, then re-run."

# ---- the pull request ----
# Re-runnable on purpose: pushing more commits to a branch that already has a
# pull request is the normal way to answer review comments, and that must not
# be an error.
if existing=$(gh pr view "$branch" --json url,state 2>/dev/null); then
    url=$(printf '%s' "$existing" | sed -n 's/.*"url":"\([^"]*\)".*/\1/p')
    state=$(printf '%s' "$existing" | sed -n 's/.*"state":"\([A-Z]*\)".*/\1/p')
    if [ -n "$url" ]; then
        echo
        echo "Pull request already open (${state:-UNKNOWN}) - the new commits are on it."
        echo "  $url"
        [ "$web" = "1" ] && gh pr view "$branch" --web >/dev/null
        exit 0
    fi
fi

if [ -z "$title" ]; then
    # The first commit's subject is very nearly always the right title, and a
    # title somebody has to invent twice is a title that goes stale.
    title=$(git log --format=%s "origin/main..HEAD" | tail -n 1)
fi
[ -n "$title" ] || title="$branch"

body_file=$(mktemp)
trap 'rm -f "$body_file"' EXIT
{
    printf 'Merging `%s`.\n\n' "$branch"
    git log --format='- %s' "origin/main..HEAD"
    printf '\n---\nChecked before opening:\n\n'
    printf -- '- `./scripts/fettler-build.sh test` - Fettler.Tests and burler.Tests\n'
    printf -- '- `./scripts/fettler-build.sh check` - twins, permissions, docs, errors\n\n'
    printf 'CI runs both again on Windows and macOS.\n'
} > "$body_file"

gh_args=(pr create --base main --head "$branch" --title "$title" --body-file "$body_file")
[ "$draft" = "1" ] && gh_args+=(--draft)

echo
echo "> gh ${gh_args[*]}"
created=$(gh "${gh_args[@]}") || fail "gh pr create failed."

echo
echo "Pull request opened. Nothing is merged."
echo "  $created"
echo
echo "Next: review it, wait for CI, and merge it on GitHub."
echo "Then: ./scripts/fettler-branch.sh land"

[ "$web" = "1" ] && gh pr view "$branch" --web >/dev/null
exit 0
