#!/usr/bin/env pwsh
# Build Fettler: restore, build, test, check and publish.
#
#   ./scripts/fettler-build.ps1              restore + build (Debug)
#   ./scripts/fettler-build.ps1 test         build + run Fettler.Tests, burler.Tests and Picker.Tests
#   ./scripts/fettler-build.ps1 check        the verifiers - twins, permissions, docs, errors
#   ./scripts/fettler-build.ps1 release      Release build - what a client should launch
#   ./scripts/fettler-build.ps1 publish [V]  self-contained single-file binaries, one
#                            archive per OS per program; V names them
#                            (fettle-V-RID, burler-V-RID, pick-V-RID)
#
# This repository needs NOTHING built first. Fettler references no project
# outside its own tree, so there is no staged binary to publish and nothing
# to weave - the check other build scripts open with would have nothing to
# check.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'build',
    [Parameter(Position = 1)][string]$Version = ''
)
$ErrorActionPreference = 'Stop'

# The scripts live in scripts/; everything they build lives one level up.
# Resolve the repository root once and work from it, so a caller's own
# working directory never decides where artifacts land.
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Run([string[]]$DotnetArgs) {
    Write-Host ("> dotnet " + ($DotnetArgs -join ' ')) -ForegroundColor Cyan
    # dotnet writes restore progress to stderr under some hosts, and
    # Windows PowerShell 5.1 turns a native program's redirected stderr
    # into NativeCommandError records. Judge the call on its exit code
    # alone - the same fault R3.7 exists to keep out of Fettler itself.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { dotnet @DotnetArgs } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "STOPPED: dotnet $($DotnetArgs -join ' ') failed (exit $LASTEXITCODE)." -ForegroundColor Red
        exit 1
    }
}

# NOT called Node, and that is not a style choice. PowerShell resolves a
# command name as alias, then FUNCTION, then cmdlet, then external program,
# and it does so case-insensitively - so a function called Node makes the
# `node` on the next line call this function rather than node.exe. It then
# recurses, and because Join-Path CONCATENATES an absolute second argument
# instead of resetting to it, each turn appends the whole repository path
# again: scripts\C:\...\scripts\C:\...\scripts\ forever, until something
# gives up. It hung for thirty minutes twice before it was found.
#
# The .sh twin was never exposed to this: it calls its function node_check.
# This is exactly the fault that survives when only one of a pair is ever
# run - the twin that works hides the twin that does not.
function NodeCheck([string]$Script) {
    Write-Host "> node scripts/$Script" -ForegroundColor Cyan
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { node (Join-Path $PSScriptRoot $Script) } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "STOPPED: scripts/$Script failed (exit $LASTEXITCODE)." -ForegroundColor Red
        exit 1
    }
}

switch ($Command) {
    'build'   { Run @('build', 'Fettler.slnx') }
    'test'    {
        # All of them, every time. burler is a separate project because
        # its ONNX dependency may not enter Fettler's package allowlist,
        # Picker is a separate program with its own allowlist entirely -
        # and a lane whose later test projects are only run when somebody
        # remembers is a lane with untested halves.
        Run @('test', 'Fettler.Tests')
        Run @('test', 'burler.Tests')
        Run @('test', 'Picker.Tests')
    }
    'check'   {
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
        NodeCheck 'fettler-verify-twins.js'
        NodeCheck 'fettler-verify-permissions.js'
        NodeCheck 'fettler-verify-docs.js'
        NodeCheck 'fettler-verify-errors.js'
    }
    'release' { Run @('build', 'Fettler.slnx', '-c', 'Release') }
    'publish' {
        # R2.3: a self-contained single-file fettle per OS, so the target
        # machine needs no .NET install and no repository checkout.
        #
        # The windows RIDs ship as .zip; the unix ones as .tar.gz, because
        # the executable bit only survives a tar. An archive cut on Windows
        # cannot record that bit at all, so unix archives made here are for
        # inspection - the ones a release attaches are a unix runner's.
        # Fettler of all things should say so out loud: R6.9 is the clause
        # about exactly this bit.
        $suffix = ''
        if ($Version) { $suffix = "-$Version" }
        $pub = Join-Path $root 'artifacts/publish'

        # A release names its version on the command line and CI passes the
        # tag; without one this builds whatever Directory.Build.props says,
        # which is the development default and not a release number.
        $stamp = @()
        if ($Version) { $stamp = @("-p:Version=$Version") }

        # THREE programs, each its own archive. burler is optional and is
        # only wanted by somebody who has switched the disclosure screen
        # on, so folding it into fettle's archive would make every
        # download pay for ONNX Runtime to get a feature most trees never
        # use. fettle looks for it BESIDE ITSELF, so the two unpack into
        # one directory when both are wanted. pick is its own tool with
        # its own configuration, wanted by nobody who did not ask for a
        # database boundary - same argument, its own archive.
        foreach ($rid in @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')) {
            foreach ($program in @('fettle', 'burler', 'pick')) {
                $dir = Join-Path $pub "$program/$rid"

                # burler bundles ONNX Runtime, which is native per RID.
                # Without this the natives land beside the executable
                # instead of inside it, and an archive carrying only the
                # one file would be missing them.
                $native = @()
                if ($program -eq 'burler') { $native = @('-p:IncludeNativeLibrariesForSelfExtract=true') }

                Run (@('publish', $program, '-c', 'Release', '-r', $rid, '--self-contained',
                       '-p:PublishSingleFile=true') + $native + $stamp + @('-o', $dir))

                # Two licence obligations, and both are met IN THE ARCHIVE -
                # not merely in the repository, which a person downloading a
                # release never sees. Apache-2.0 asks the licence and any
                # NOTICE to travel with a distributed binary, and fettle
                # bundles PdfPig into the one file it ships; burler bundles
                # ONNX Runtime and Microsoft.ML.Tokenizers, both MIT. MIT
                # asks its own copyright notice to be in every copy, and this
                # archive is a copy. An obligation nobody can read has not
                # been met.
                Copy-Item (Join-Path $root 'NOTICE') $dir -Force
                Copy-Item (Join-Path $root 'LICENSE') $dir -Force

                if ($rid -like 'win-*') {
                    Compress-Archive -Path @((Join-Path $dir "$program.exe"),
                                             (Join-Path $dir 'NOTICE'),
                                             (Join-Path $dir 'LICENSE')) `
                        -DestinationPath (Join-Path $pub "$program$suffix-$rid.zip") -Force
                } else {
                    tar -czf (Join-Path $pub "$program$suffix-$rid.tar.gz") -C $dir $program NOTICE LICENSE
                    if ($LASTEXITCODE -ne 0) {
                        Write-Host "STOPPED: tar failed for $program on $rid (exit $LASTEXITCODE)." -ForegroundColor Red
                        exit 1
                    }
                }
            }
        }
        Get-ChildItem $pub -File |
            Where-Object { $_.Name -like 'fettle*' -or $_.Name -like 'burler*' -or $_.Name -like 'pick*' } |
            ForEach-Object { Write-Host ("  -> " + $_.Name) }
    }
    default   { Write-Host "unknown command: $Command (build | test | check | release | publish)" -ForegroundColor Red; exit 1 }
}
