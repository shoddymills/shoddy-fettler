#!/usr/bin/env bash
# Build Fettler: restore, build, test, check and publish.
#
#   ./scripts/fettler-build.sh              restore + build (Debug)
#   ./scripts/fettler-build.sh test         build + run Fettler.Tests, burler.Tests and Picker.Tests
#   ./scripts/fettler-build.sh check        the verifiers - twins, permissions, docs, errors
#   ./scripts/fettler-build.sh release      Release build - what a client should launch
#   ./scripts/fettler-build.sh publish [V]  self-contained single-file binaries, one
#                           archive per OS per program; V names them
#                           (fettle-V-RID, burler-V-RID, pick-V-RID)
#
# The twin of fettler-build.ps1 and equivalent to it. This repository needs
# NOTHING built first: Fettler references no project outside its own
# tree, so there is no staged binary to publish and nothing to weave.
# The scripts live in scripts/; everything they build lives one level up.
# Work from the repository root, so a caller's own working directory never
# decides where artifacts land.
set -euo pipefail
cd "$(dirname "$0")/.."

command=${1:-build}
version=${2:-}

run() {
    echo "> dotnet $*"
    dotnet "$@"
}

node_check() {
    echo "> node scripts/$1"
    node "scripts/$1"
}

case "$command" in
    build)   run build Fettler.slnx ;;
    test)
        # All of them, every time. burler is a separate project because
        # its ONNX dependency may not enter Fettler's package allowlist,
        # Picker is a separate program with its own allowlist entirely -
        # and a lane whose later test projects are only run when somebody
        # remembers is a lane with untested halves.
        run test Fettler.Tests
        run test burler.Tests
        run test Picker.Tests
        ;;
    check)
        # The gates that are not tests. All Node, all fast, and all of them
        # catch a class of fault the compiler and the suite cannot see:
        #
        #   twins        a .ps1 with no .sh, or two that have drifted apart.
        #                The twin nobody runs is the twin that rots, and it
        #                rots silently, because the person who would notice
        #                is on the other operating system.
        #   permissions  the executable bit on every tracked script, checked
        #                in git's index rather than the filesystem. A script
        #                committed without +x runs fine on Windows, where
        #                NTFS has no bit to check, and fails on the Linux
        #                runner with "Permission denied", exit 126.
        #   docs         the site against the sources: one nav bar, no
        #                orphan pages, every internal link resolving, no
        #                mojibake, and the screen section still naming every
        #                category the code has.
        #   errors       every outcome and exit code the tool can raise is
        #                documented on the page that promises to list them.
        #
        # verify-permissions STAGES its fix rather than only reporting, so
        # it is the one command here that can leave the tree different from
        # how it found it. That is deliberate: reporting a wrong bit and
        # making the reader go and fix it by hand is how four of them once
        # shipped at once.
        node_check fettler-verify-twins.js
        node_check fettler-verify-permissions.js
        node_check fettler-verify-docs.js
        node_check fettler-verify-errors.js
        ;;
    release) run build Fettler.slnx -c Release ;;
    publish)
        # R2.3: a self-contained single-file fettle per OS, so the target
        # machine needs no .NET install and no repository checkout.
        #
        # The windows RIDs ship as .zip; the unix ones as .tar.gz, because
        # the executable bit only survives a tar. Cut here on a unix runner
        # the bit is real, which is why a release attaches these and not
        # the ones a Windows machine makes.
        suffix=""
        [ -n "$version" ] && suffix="-$version"
        pub="artifacts/publish"

        # A release names its version on the command line and CI passes the
        # tag; without one this builds whatever Directory.Build.props says,
        # which is the development default and not a release number.
        stamp=""
        [ -n "$version" ] && stamp="-p:Version=$version"

        # THREE programs, each its own archive. burler is optional and is
        # only wanted by somebody who has switched the disclosure screen
        # on, so folding it into fettle's archive would make every
        # download pay for ONNX Runtime to get a feature most trees never
        # use. fettle looks for it BESIDE ITSELF, so the two unpack into
        # one directory when both are wanted. pick is its own tool with
        # its own configuration, wanted by nobody who did not ask for a
        # database boundary - same argument, its own archive.
        for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64; do
            for program in fettle burler pick; do
                dir="$pub/$program/$rid"

                # burler bundles ONNX Runtime, which is native per RID.
                # Without this the natives land beside the executable
                # instead of inside it, and an archive carrying only the
                # one file would be missing them.
                native=""
                [ "$program" = "burler" ] && native="-p:IncludeNativeLibrariesForSelfExtract=true"

                run publish "$program" -c Release -r "$rid" --self-contained \
                    -p:PublishSingleFile=true ${native:+"$native"} ${stamp:+"$stamp"} -o "$dir"

                # Two licence obligations, and both are met IN THE ARCHIVE -
                # not merely in the repository, which a person downloading a
                # release never sees. Apache-2.0 asks the licence and any
                # NOTICE to travel with a distributed binary, and fettle
                # bundles PdfPig into the one file it ships; burler bundles
                # ONNX Runtime and Microsoft.ML.Tokenizers, both MIT. MIT
                # asks its own copyright notice to be in every copy, and this
                # archive is a copy. An obligation nobody can read has not
                # been met.
                cp NOTICE "$dir/NOTICE"
                cp LICENSE "$dir/LICENSE"

                case "$rid" in
                    win-*)
                        (cd "$dir" && zip -q "../../$program$suffix-$rid.zip" "$program.exe" NOTICE LICENSE)
                        ;;
                    *)
                        chmod +x "$dir/$program"
                        tar -czf "$pub/$program$suffix-$rid.tar.gz" -C "$dir" "$program" NOTICE LICENSE
                        ;;
                esac
            done
        done
        ls -1 "$pub" | grep -E '^(fettle|burler|pick)' | sed 's/^/  -> /'
        ;;
    *)
        echo "unknown command: $command (build | test | check | release | publish)" >&2
        exit 1
        ;;
esac
