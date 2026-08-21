#!/usr/bin/env bash
# Cut a release: tag main and push the tag. CI does the rest.
#
#   ./scripts/ship.sh X.Y.Z         tag vX.Y.Z on main and push it
#   ./scripts/ship.sh X.Y.Z -Yes    the same, without the confirmation prompt
#
# The twin of ship.ps1 and equivalent to it, down to the spelling of the
# flag: -Yes on both sides, deliberately. A pair that took -Yes in
# PowerShell and -y in the shell, while the docs said they "take the same
# arguments", is one of the two drifts verify-twins.js was written for.
#
# THIS SCRIPT DOES EXACTLY TWO MUTATING THINGS: it creates a tag and it
# pushes that tag. Everything else it does is refuse. There is no version
# bump to commit, no release branch to cut, and nothing to merge back,
# because the version is not stored in a file - release.yml reads it from
# the tag and passes it to the build.
#
# PUSHING THE TAG IS THE MOMENT IT SHIPS. A tag someone may have fetched
# is never moved: if the workflow fails, fix forward with a new patch
# version.
# The scripts live in scripts/; the repository they release is one level up.
set -euo pipefail
cd "$(dirname "$0")/.."

version=${1:-}
assume_yes=${2:-}

stop() {
    echo "STOPPED: $1" >&2
    [ $# -gt 1 ] && echo "         $2" >&2
    exit 1
}

# ---- 1. the version must be a version ----
case "$version" in
    [0-9]*.[0-9]*.[0-9]*) ;;
    *) stop "'$version' is not an exact X.Y.Z version." \
            "Pre-release and build suffixes are not supported here." ;;
esac
printf '%s' "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' \
    || stop "'$version' is not an exact X.Y.Z version." \
            "Pre-release and build suffixes are not supported here."

tag="v$version"
notes="release-notes/$tag.md"

# ---- 2. this must be a git repository, with no merge half-done ----
git rev-parse --git-dir >/dev/null 2>&1 || stop "this is not a git repository."
[ -f "$(git rev-parse --git-dir)/MERGE_HEAD" ] \
    && stop "a merge is in progress." "Finish or abort it first."

# ---- 3. LOCAL STATE IS NOT EVIDENCE ABOUT REMOTE STATE. Fetch first. ----
# Every check below about what origin has is worthless without this: refs
# go stale the moment somebody else pushes, and nothing announces it.
echo "> git fetch --prune --tags"
git fetch --prune --tags || stop "could not reach origin."

# ---- 4. on main, clean, and level with origin ----
branch=$(git rev-parse --abbrev-ref HEAD)
[ "$branch" = "main" ] \
    || stop "on '$branch', not main." "A release is cut from main. Merge your branch first."

[ -z "$(git status --porcelain)" ] \
    || stop "the working tree is dirty." \
            "Commit or stash before tagging - the tag must name a commit that exists."

behind=$(git rev-list --count main..origin/main)
ahead=$(git rev-list --count origin/main..main)
[ "$behind" = "0" ] || stop "main is $behind commit(s) behind origin/main." "Pull first."
[ "$ahead" = "0" ] || stop "main is $ahead commit(s) ahead of origin/main." \
                           "Push first - CI has not seen these commits."

commit=$(git rev-parse HEAD)
short=$(git rev-parse --short=8 HEAD)

# ---- 5. the tag must be free, locally AND on origin ----
git rev-parse --verify --quiet "refs/tags/$tag" >/dev/null \
    && stop "$tag already exists locally." "Pick the next version. A tag is never moved."
[ -z "$(git ls-remote --tags origin "refs/tags/$tag")" ] \
    || stop "$tag already exists on origin." \
            "Pick the next version. A tag someone may have fetched is never moved."

# ---- 6. the notes must exist, and be COMMITTED ----
# release.yml checks out the tag and reads only what that commit contains,
# so notes written afterwards are invisible to it and the release body
# falls back to a list of merge subjects.
[ -f "$notes" ] \
    || stop "$notes does not exist." \
            "The release body comes from this file. Write it, commit it, push it, then ship."
git ls-files --error-unmatch "$notes" >/dev/null 2>&1 \
    || stop "$notes is not committed." \
            "The workflow reads the tagged commit; an untracked file is not in it."

# ---- 7. CI must already be green on this exact commit ----
# The proof is CI's, not this script's. Re-running the suite here would be
# a second opinion from a machine that is not the one that builds the
# release, and the earlier design's local 'receipt store' is exactly the
# thing that once printed a pass describing yesterday's tree.
if command -v gh >/dev/null 2>&1; then
    if state=$(gh run list --commit "$commit" --json conclusion,name,status 2>/dev/null); then
        case "$state" in
            *'"conclusion":"failure"'*)
                stop "CI is red on $short." "Fix it on a branch and merge before tagging." ;;
            *'"status":"in_progress"'*|*'"status":"queued"'*)
                stop "CI is still running on $short." \
                     "Wait for it. The tag should name a commit already proven." ;;
            *'"conclusion":"success"'*)
                echo "CI is green on $short." ;;
            *)
                echo "NOTE: no finished CI run found for $short." ;;
        esac
    else
        echo "NOTE: could not ask GitHub about CI for $short - continuing without that check."
    fi
else
    echo "NOTE: gh is not installed, so CI's verdict on this commit was not checked."
fi

# ---- 8. say what is about to happen, then ask ----
previous=$(git describe --tags --abbrev=0 2>/dev/null || true)
echo
echo "  tag        $tag"
echo "  commit     $short  $(git log -1 --format=%s)"
echo "  notes      $notes"
[ -n "$previous" ] && echo "  since      $previous"
echo "  publishes  fettle and burler, 6 RIDs each, 12 archives"
echo
echo "  Pushing the tag is the moment it ships, and a tag is never moved."
echo

if [ "$assume_yes" != "-Yes" ]; then
    printf 'Tag and push? (y/N) '
    read -r answer
    case "$answer" in
        y|Y) ;;
        *) echo "Nothing was pushed."; exit 0 ;;
    esac
fi

# ---- 9. the two mutating operations ----
echo "> git tag -a $tag"
git tag -a "$tag" -m "$tag" || stop "could not create the tag."

echo "> git push origin $tag"
git push origin "$tag" || stop \
    "could not push the tag; it exists locally and nothing has shipped." \
    "Delete it with 'git tag -d $tag' and try again, or push it yourself."

echo
echo "$tag is public. Watch Actions -> Release."
