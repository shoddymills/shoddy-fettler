#!/usr/bin/env bash
# Put the fettle you just built where the machine will actually find it.
#
#   ./scripts/fettler-install.sh                  build, then install over the one on PATH
#   ./scripts/fettler-install.sh -To DIR          install into DIR instead
#   ./scripts/fettler-install.sh -Stop            end the servers still on the old build
#   ./scripts/fettler-install.sh -DryRun          say what would happen and touch nothing
#   ./scripts/fettler-install.sh -Program pick    the SQL tool instead of fettle
#   ./scripts/fettler-install.sh -Program burler  the sidecar instead of fettle
#
# The twin of fettler-install.ps1 and equivalent to it, down to the spelling of the
# flags: -To, -Stop, -DryRun and -Program on both sides, deliberately. A
# pair that took -Yes in PowerShell and -y in the shell, while the docs
# said they "take the same arguments", is one of the two drifts
# fettler-verify-twins.js was written for.
#
# WHERE IT INSTALLS IS DISCOVERED, NOT INVENTED: the directory holding the
# `fettle` already on PATH, so this replaces the binary the machine is
# already using rather than adding a second one for it to choose between.
# With nothing on PATH it falls back to ~/.local/bin, the directory the
# install page names, and says it has to be added.
#
# WHAT IT COPIES is a self-contained single file, published for this
# machine's own runtime identifier - the same shape a release ships, so
# what you test locally is what other people get. It is NOT the framework
# dependent build under artifacts/bin, which is a directory of assemblies
# and cannot be copied as one file.
#
# A RUNNING COPY DOES NOT STOP THE INSTALL. The obvious shape of this
# script - kill everything, then copy - is the wrong one, so the reason is
# written down here rather than rediscovered:
#
#   The new file is written beside the old one and renamed onto it.
#   rename(2) is atomic, so no reader ever sees a half-written binary, and
#   a process already running the old one keeps the inode it opened and
#   carries on undisturbed.
#
#   A plain `cp` over the destination would NOT do: it writes through the
#   existing inode, and a running process reading its own image out of
#   that inode is entitled to crash. The rename replaces the directory
#   entry and leaves the old inode intact until the last user of it exits.
#
# The write therefore always succeeds and nothing has to be ended to make
# room for it. The earlier shape had a race that this does not: a client
# that restarts a dead stdio server puts a new process on the file between
# the kill and the copy, and the install then fails for no reason a reader
# could see.
#
# WHAT A RUNNING COPY DOES MEAN is that it goes on serving the old build
# until it is restarted, because a process does not reload its own image.
# That is what -Stop is for, and it happens AFTER the new file is in place
# and has answered --version: without it you are told which pids are still
# on the old build, with it they are ended. A failed build can therefore
# never leave this machine with nothing that works.
#
# IT CANNOT RESTART AN MCP SERVER, and does not pretend to. A stdio server
# is a child of the client that launched it, talking down a pipe that
# client owns; nothing outside can hand it a new one. Install, then
# reconnect from the client - in Claude Code that is /mcp.
#
# DO NOT RUN THIS THROUGH `fettle run`. With -Stop the task would be a
# child of the very server it is ending, so the kill would take the task
# with it before it could report what it did. It refuses when it sees
# that, but a terminal is the right place for it regardless - which is
# also why it is not a declared task.
set -euo pipefail
cd "$(dirname "$0")/.."
root=$(pwd)

announce() { echo "$1"; }

stop_with() {
    echo "STOPPED: $1" >&2
    [ $# -gt 1 ] && echo "         $2" >&2
    exit 1
}

# ---- arguments ----
to=""
program="fettle"
do_stop=0
dry_run=0

while [ $# -gt 0 ]; do
    case "$1" in
        -To)      to="${2:-}"; shift 2 ;;
        -Program) program="${2:-}"; shift 2 ;;
        -Stop)    do_stop=1; shift ;;
        -DryRun)  dry_run=1; shift ;;
        # Refused rather than ignored, which is the rule fettle itself
        # keeps: a misspelt flag that takes a value swallows the argument
        # after it, and the run then proceeds with complete confidence.
        *) stop_with "'$1' is not an argument this takes." \
                     "Take -To DIR, -Program NAME, -Stop, -DryRun." ;;
    esac
done

case "$program" in
    fettle|burler|pick) ;;
    *) stop_with "'$program' is not a program in this repository." \
                 "It is fettle, burler or pick." ;;
esac

# ---- 1. this machine's runtime identifier ----
case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux)  os=linux ;;
    *) stop_with "$(uname -s) is not an operating system this publishes for." ;;
esac
case "$(uname -m)" in
    arm64|aarch64) arch=arm64 ;;
    x86_64|amd64)  arch=x64 ;;
    *) stop_with "$(uname -m) is not an architecture this publishes for." ;;
esac
rid="$os-$arch"

# ---- 2. where it goes ----
if [ -n "$to" ]; then
    # Made absolute where it already exists, so the PATH comparison at the
    # end compares two spellings of the same shape rather than reporting a
    # relative -To as "not on PATH" every time.
    dir="$to"
    [ -d "$dir" ] && dir=$(cd "$dir" && pwd)
    announce "installing where asked: $dir"
else
    found=$(command -v "$program" 2>/dev/null || true)
    if [ -n "$found" ]; then
        dir=$(cd "$(dirname "$found")" && pwd)
        announce "found $program on PATH at $found"
    else
        dir="$HOME/.local/bin"
        announce "nothing called $program is on PATH; falling back to $dir"
    fi
fi
destination="$dir/$program"

# An install killed between the copy and the rename leaves one of these
# behind. Sweeping at the start of the next run keeps them from
# accumulating; best effort, because one we cannot remove is not this
# run's problem to solve.
sweep_landings() {
    [ -d "$dir" ] || return 0
    for old in "$dir/$program".new-*; do
        [ -e "$old" ] && rm -f "$old" 2>/dev/null || true
    done
}
sweep_landings

# ---- 3. is a copy of it running ----
# Collected before the build so the report at the end can name what is
# still on the old build. Nothing here is ended in order to install.
#
# The image behind a pid, where the system will say. Linux answers through
# /proc; macOS puts the full path in comm. Where neither does, it is not a
# copy we could have replaced anyway.
exe_of() {
    if [ -r "/proc/$1/exe" ]; then
        readlink -f "/proc/$1/exe" 2>/dev/null || true
    else
        ps -o comm= -p "$1" 2>/dev/null || true
    fi
}

parent_of() {
    ps -o ppid= -p "$1" 2>/dev/null | tr -d ' ' || true
}

# Whether the process we are about to end is one we are running inside of.
# Bounded rather than while-true: a parent chain should be a handful deep,
# and a cycle in it must not become a hang in a script whose whole job is
# to finish and get out of the way.
running_inside() {
    local candidate="$1" walk=$$ i=0
    while [ -n "$walk" ] && [ "$walk" -gt 0 ] && [ "$i" -lt 24 ]; do
        [ "$walk" = "$candidate" ] && return 0
        walk=$(parent_of "$walk")
        i=$((i + 1))
    done
    return 1
}

wanted=$(cd "$dir" 2>/dev/null && pwd || echo "$dir")/"$program"
running=""
for pid in $(pgrep -x "$program" 2>/dev/null || true); do
    where=$(exe_of "$pid")
    [ "$where" = "$wanted" ] && running="$running $pid"
done
running=$(echo "$running" | tr -s ' ' | sed 's/^ //;s/ $//')

# Only -Stop can kill, so only -Stop can kill the thing we are running
# inside of. Without it this is a perfectly ordinary install and the
# ancestry does not matter.
if [ "$do_stop" -eq 1 ]; then
    for pid in $running; do
        if running_inside "$pid"; then
            stop_with "-Stop would end the $program this is running inside (pid $pid)." \
                      "Run it from a terminal instead - a task cannot outlive the server hosting it."
        fi
    done
fi

# ---- 4. build the thing that will be copied ----
staging="$root/artifacts/install/$rid"
staged="$staging/$program"

# burler carries native ONNX libraries, and a single file without them is a
# single file that cannot start. fettler-build.sh says the same thing at its own
# publish step.
native=""
[ "$program" = "burler" ] && native="-p:IncludeNativeLibrariesForSelfExtract=true"

if [ "$dry_run" -eq 1 ]; then
    announce "would publish $program for $rid, then install it to $destination"
    [ -e "$destination" ] && announce "would write $program.new-$$ beside it and rename that onto it"
    if [ -n "$running" ]; then
        if [ "$do_stop" -eq 1 ]; then
            announce "would then end pid(s):$running"
        else
            announce "would then report pid(s) still on the old build:$running"
        fi
    fi
    announce "nothing was written"
    exit 0
fi

announce "> dotnet publish $program -c Release -r $rid --self-contained -p:PublishSingleFile=true"
# shellcheck disable=SC2086
dotnet publish "$program" -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true $native -o "$staging" \
    || stop_with "the build failed." "Nothing was installed."

[ -f "$staged" ] || stop_with "the build produced no $program at $staged."

# ---- 5. put it in place, without ending anything ----
# Write beside it, then rename onto it: atomic, and it leaves a running
# process holding the old inode rather than reading a file changing under
# it. See the note at the head of this script for why not a plain cp.
mkdir -p "$dir"
landing="$dir/$program.new-$$"
cp "$staged" "$landing" || stop_with "could not write $landing." "The previous build was left where it was."
chmod +x "$landing"
mv -f "$landing" "$destination" || {
    rm -f "$landing" 2>/dev/null || true
    stop_with "could not install $program." "The previous build was left where it was."
}

# ---- 6. prove the thing that landed actually runs ----
# A copy that reports success and produced an unrunnable file is the state
# this is least able to notice any other way. NOT written as a pipe into
# head: a pipeline reports the exit status of its LAST command, so
# `$exe --version | head` succeeds whenever head does - which is always -
# and a binary that printed an error and exited 1 would be reported back
# as the version it "installed". Capture first, take the line after.
if ! reported=$("$destination" --version 2>&1); then
    stop_with "$destination ran but failed --version." "It said: $(printf '%s\n' "$reported" | head -n 1)"
fi
reported=$(printf '%s\n' "$reported" | head -n 1)

echo ""
echo "installed $reported"
echo "          $destination"

# ---- 7. end what is still on the old build, if asked ----
# After the install, deliberately, and never as a way of making room for
# it. Ending servers first and then finding the tree does not compile
# leaves the machine with nothing working at all.
if [ "$do_stop" -eq 1 ] && [ -n "$running" ]; then
    for pid in $running; do
        announce "ending pid $pid"
        kill "$pid" 2>/dev/null || announce "  pid $pid had already gone"
    done
    sleep 1
fi

# ---- 8. what is still a person's job ----
now=$(command -v "$program" 2>/dev/null || true)
if [ "$now" != "$destination" ]; then
    echo ""
    echo "NOT ON PATH: $dir is not what '$program' resolves to."
    echo "             Add it and open a new shell, or register the absolute"
    echo "             path with: $program setup CLIENT --local --command \"$destination\""
fi

if [ -n "$running" ]; then
    echo ""
    if [ "$do_stop" -eq 1 ]; then
        echo "Those processes have been ended. Reconnect the server from your"
        echo "client - in Claude Code, /mcp. Nothing here can do that for you: a"
        echo "stdio server belongs to the client that launched it."
    else
        echo "STILL ON THE OLD BUILD: pid(s)$running."
        echo "             A process does not reload its own image, so these go on"
        echo "             serving what they started with. Restart them from your"
        echo "             client - in Claude Code, /mcp - or re-run with -Stop."
    fi
fi
