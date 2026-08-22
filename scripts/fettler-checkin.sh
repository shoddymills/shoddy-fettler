#!/usr/bin/env bash
# MAINTAINER TOOL - stages everything and commits it. Writes history; pushes
# nothing. Run from anywhere inside the repo.
#
#   ./scripts/fettler-checkin.sh -Message "what changed"
#   ./scripts/fettler-checkin.sh -Message "..." -Force      allow it on main
#
# The twin of fettler-checkin.ps1 and equivalent to it, down to the spelling of the
# flags: -Message and -Force on both sides, deliberately. A pair that took
# -Force in PowerShell and -f in the shell, while the docs called them
# identical, is one of the two drifts fettler-verify-twins.js was written for.
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
# this script only makes the first. `./scripts/fettler-pr.sh` is what puts the work where
# anyone else can see it.
set -euo pipefail

message=""
force=0
while [ $# -gt 0 ]; do
    case "$1" in
        -Message) message=${2:-}; shift 2 ;;
        -Force)   force=1; shift ;;
        *)        echo "unknown argument: $1" >&2; exit 2 ;;
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

git rev-parse --is-inside-work-tree >/dev/null 2>&1 || fail "not inside a git repository."

# Work from the repository root. `git ls-files` below reports paths relative
# to the current directory, and `git add` is handed those paths - so running
# this from a subdirectory would stage the wrong thing, or nothing.
cd "$(git rev-parse --show-toplevel)"

[ -n "$(printf '%s' "$message" | tr -d '[:space:]')" ] \
    || fail "the message is empty; say what changed."

branch=$(git rev-parse --abbrev-ref HEAD) || fail "could not read the current branch."

if { [ "$branch" = "main" ] || [ "$branch" = "master" ]; } && [ "$force" = "0" ]; then
    fail "on $branch, which takes merges and not commits." \
         "Cut a branch with ./scripts/fettler-branch.sh feature NAME, or pass -Force if you mean it."
fi

git rev-parse -q --verify MERGE_HEAD >/dev/null 2>&1 \
    && fail "a merge is in progress - finish or abort it first."

# Nothing to commit is not a failure worth a stack trace, but it must not be
# silence either: a task that reports success having done nothing is how you
# come to believe work is saved when it is not.
[ -n "$(git status --porcelain)" ] || fail "nothing to commit - the tree is clean."

# A .sh git has never seen is staged 100644 unless it is added with the bit
# set, and fettler-verify-permissions.js cannot cover it: that check walks TRACKED
# files, so the window between writing a script and adding it is invisible to
# it. Four scripts once shipped through that window and one took a release
# workflow down with "Permission denied", exit 126.
#
# `git update-index --chmod=+x` is the wrong tool here - it refuses a path git
# does not yet have, with a message about a missing --add option that reads
# like an unrelated fault. `git add --chmod=+x` does both at once.
while IFS= read -r f; do
    [ -n "$f" ] && g add --chmod=+x -- "$f"
done < <(git ls-files --others --exclude-standard | grep '\.sh$' || true)

g add -A
g commit -m "$message"

echo
echo "Committed on $branch. Nothing has been pushed."
echo "When the work is ready: ./scripts/fettler-pr.sh"
