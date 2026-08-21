#!/usr/bin/env bash
# MAINTAINER TOOL - creates branches, and deletes them locally and on origin.
# Assumes write access. Run from anywhere inside the repo.
#
#   ./scripts/branch.sh feature NAME    cut feature/NAME off an up-to-date main
#   ./scripts/branch.sh bug NAMENN      cut bug/NAMENN off an up-to-date main
#   ./scripts/branch.sh sync            bring main's commits into the current branch
#   ./scripts/branch.sh land [-Yes]     after the PR is merged: return to main and
#                               delete the branch, local and origin
#
# The twin of branch.ps1 and equivalent to it, down to the spelling of the
# flag: -Yes on both sides, deliberately.
#
# WHAT THIS DOES NOT DO IS MERGE. The pull request is the gate, and it is a
# human one: `./scripts/pr.sh` opens it, a person reviews it and presses the button,
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
set -euo pipefail

command=${1:-}
name=${2:-}
assume_yes=${3:-}
[ "${2:-}" = "-Yes" ] && { assume_yes="-Yes"; name=""; }

fail() {
    echo "STOPPED: $1" >&2
    [ $# -gt 1 ] && echo "         $2" >&2
    exit 1
}

g() {
    echo "> git $*"
    git "$@" || fail "git $* failed (exit $?)."
}

current_branch() { git rev-parse --abbrev-ref HEAD; }

require_work_branch() {
    local b; b=$(current_branch)
    case "$b" in
        feature/*|bug/*) printf '%s' "$b" ;;
        *) fail "current branch is '$b' - $1 only runs from a feature/* or bug/* branch." ;;
    esac
}

# The one cut, shared by 'feature' and 'bug'. The prefix is the only thing
# the two verbs disagree about.
cut_branch() {
    local branch="$1"
    # LOCAL REFS ARE NOT EVIDENCE ABOUT ORIGIN. They go stale the moment
    # somebody else pushes and nothing announces it, so both checks below are
    # worthless without this.
    g fetch origin --prune

    git show-ref --verify --quiet "refs/heads/$branch" \
        && fail "$branch already exists locally."
    git show-ref --verify --quiet "refs/remotes/origin/$branch" \
        && fail "$branch already exists on origin."

    g checkout main
    g pull --ff-only origin main
    g checkout -b "$branch"

    echo
    echo "On $branch, cut from an up-to-date main."
    echo "Work, then: ./scripts/commit.sh -Message \"what changed\""
}

# ---- preconditions, before any verb ----
git rev-parse --is-inside-work-tree >/dev/null 2>&1 || fail "not inside a git repository."
git rev-parse -q --verify MERGE_HEAD >/dev/null 2>&1 \
    && fail "a merge is in progress - finish or abort it first."
git diff --quiet \
    || fail "unstaged changes present." "Commit them with ./scripts/commit.sh, or stash them. See git status."
git diff --cached --quiet \
    || fail "staged-but-uncommitted changes present." "Commit them with ./scripts/commit.sh, or unstage them."

case "$command" in

    feature)
        [ -n "$name" ] || fail "usage: ./scripts/branch.sh feature NAME"
        name=${name#feature/}
        printf '%s' "$name" | grep -Eq '^[A-Za-z0-9][A-Za-z0-9._-]*$' \
            || fail "a branch name may use letters, digits, . _ - only (got '$name')."
        cut_branch "feature/$name"
        ;;

    bug)
        [ -n "$name" ] || fail "usage: ./scripts/branch.sh bug NAMENN   (bug/<origin-feature>NN, e.g. bug reinard01)"
        name=${name#bug/}
        printf '%s' "$name" | grep -Eq '^[A-Za-z0-9][A-Za-z0-9._-]*$' \
            || fail "a branch name may use letters, digits, . _ - only (got '$name')."
        # The number is not decoration: the name says which shipped feature
        # the fix belongs to and which fix it is, so bug/reinard refuses
        # where bug/reinard01 is meant.
        printf '%s' "$name" | grep -Eq '[0-9][0-9]$' \
            || fail "a bug branch is bug/<origin-feature>NN, numbered per origin feature (got '$name')." \
                    "Taken numbers: git --no-pager log --oneline main --grep=\"Merge pull request\""
        cut_branch "bug/$name"
        ;;

    sync)
        # main has moved and this branch has not. Doing it here, deliberately
        # and while the tree is clean, is far better than discovering it as a
        # conflict inside the pull request.
        branch=$(require_work_branch sync)
        g fetch origin --prune

        behind=$(git rev-list --count "HEAD..origin/main")
        if [ "$behind" = "0" ]; then
            echo
            echo "$branch already has everything on main. Nothing to do."
            exit 0
        fi

        echo
        echo "Bringing $behind commit(s) from main into $branch."
        echo "> git merge origin/main"
        git merge origin/main -m "Merge main into $branch" || fail \
            "conflicts merging main into $branch." \
            "Resolve them, 'git add' the files, and 'git commit'. Nothing else has changed."

        echo
        echo "$branch is now up to date with main."
        ;;

    land)
        # AFTER the pull request is merged, and only then. This deletes a
        # branch, so it asks GitHub whether the work is actually on main
        # before throwing anything away.
        branch=$(require_work_branch land)
        g fetch origin --prune

        merged=0
        how=""

        if command -v gh >/dev/null 2>&1; then
            if state=$(gh pr view "$branch" --json state 2>/dev/null); then
                case "$state" in
                    *'"state":"MERGED"'*) merged=1; how="its pull request is merged" ;;
                    *'"state":"OPEN"'*)
                        fail "the pull request for $branch is open, not merged." \
                             "Merge it on GitHub first - landing is not a way round the review." ;;
                    *'"state":"CLOSED"'*)
                        fail "the pull request for $branch is closed, not merged." \
                             "Merge it on GitHub first - landing is not a way round the review." ;;
                esac
            fi
        fi

        # No gh, or no pull request found. Fall back to asking git whether
        # every commit on this branch is already reachable from origin/main.
        # This is the honest answer for a merge commit; a SQUASHED merge
        # rewrites the commits, so it will say no even when the work landed -
        # which is why it only warns rather than deciding.
        if [ "$merged" = "0" ]; then
            ahead=$(git rev-list --count "origin/main..$branch")
            if [ "$ahead" = "0" ]; then
                merged=1; how="every commit on it is already on origin/main"
            fi
        fi

        if [ "$merged" = "0" ]; then
            echo
            echo "Cannot confirm $branch has been merged."
            echo "  gh reported no merged pull request, and commits on this branch are"
            echo "  not all reachable from origin/main. If the PR was SQUASH-merged this"
            echo "  is expected and the branch is safe to delete."
            echo
            if [ "$assume_yes" = "-Yes" ]; then
                fail "will not delete an unconfirmed branch under -Yes." \
                     "Run it without -Yes to decide interactively."
            fi
            printf 'Delete %s anyway? (y/N) ' "$branch"
            read -r answer
            case "$answer" in
                y|Y|yes|YES) ;;
                *) echo "Nothing was deleted."; exit 0 ;;
            esac
            how="you confirmed it"
        fi

        echo
        echo "Landing $branch ($how)."

        g checkout main
        g pull --ff-only origin main
        # -D rather than -d: a squash-merged branch is not "merged" as far as
        # git is concerned, and -d would refuse it. The check above is what
        # decides whether deleting is safe; git's own is too narrow here.
        g branch -D "$branch"

        if git show-ref --verify --quiet "refs/remotes/origin/$branch"; then
            g push origin --delete "$branch"
        else
            echo "origin/$branch was already gone."
        fi

        echo
        echo "DONE - on main, and $branch is deleted locally and on origin."
        ;;

    *)
        echo 'usage: ./scripts/branch.sh feature NAME    cut feature/NAME off an up-to-date main'
        echo '       ./scripts/branch.sh bug NAMENN      cut bug/NAMENN off an up-to-date main'
        echo "       ./scripts/branch.sh sync            bring main's commits into the current branch"
        echo '       ./scripts/branch.sh land [-Yes]     after the PR is merged: return to main and delete the branch'
        exit 2
        ;;
esac
