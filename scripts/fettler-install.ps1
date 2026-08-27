#!/usr/bin/env pwsh
# Put the fettle you just built where the machine will actually find it.
#
#   ./scripts/fettler-install.ps1                 build, then install over the one on PATH
#   ./scripts/fettler-install.ps1 -To DIR         install into DIR instead
#   ./scripts/fettler-install.ps1 -Stop           end the servers still on the old build
#   ./scripts/fettler-install.ps1 -DryRun         say what would happen and touch nothing
#   ./scripts/fettler-install.ps1 -Program pick   the SQL tool instead of fettle
#   ./scripts/fettler-install.ps1 -Program burler the sidecar instead of fettle
#
# WHERE IT INSTALLS IS DISCOVERED, NOT INVENTED: the directory holding the
# `fettle` already on PATH, so this replaces the binary the machine is
# already using rather than adding a second one for it to choose between.
# With nothing on PATH it falls back to the per-user directory the install
# page names, and says it has to be added.
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
#   Windows refuses to write over a running .exe, but it will happily
#   RENAME one. So the old file is moved aside and the new one copied into
#   the name it vacated. The running process follows its file to the new
#   name and carries on undisturbed; the next launch gets the new build.
#
#   Unix gets the same effect from rename(2): the new file is written
#   beside the old one and moved onto it in a single atomic step, and the
#   running process keeps the inode it already opened.
#
# Either way the write always succeeds and nothing has to be ended to make
# room for it. The earlier shape had a race that this does not: a client
# that restarts a dead stdio server puts a new process on the file between
# the kill and the copy, and the install then fails with a sharing
# violation for no reason a reader could see.
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
[CmdletBinding()]
param(
    [string]$To = '',
    [ValidateSet('fettle', 'burler', 'pick')][string]$Program = 'fettle',
    [switch]$Stop,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'

# $IsWindows DOES NOT EXIST in Windows PowerShell 5.1 - it reads as $null,
# which is falsey. Every test of it below would then quietly answer "not
# Windows", and this would publish a linux binary onto a Windows machine
# and copy it over the one that works. Refuse rather than guess.
if ($PSVersionTable.PSVersion.Major -lt 6) {
    Write-Host "STOPPED: this needs PowerShell 7 or newer (found $($PSVersionTable.PSVersion))." -ForegroundColor Red
    Write-Host "         Run it with pwsh." -ForegroundColor Red
    exit 1
}

# The scripts live in scripts/; what they build lives one level up.
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# NOT called Stop, Copy, Build or Install. PowerShell resolves a command
# name as alias, then FUNCTION, then cmdlet, then external program, and it
# does so case-insensitively - so a function named after the thing it wraps
# shadows that thing and calls itself. fettler-build.ps1 hung for thirty minutes
# twice on exactly that, and the note above `NodeCheck` there is the long
# version. `Stop` is also a real parameter of this script, which is a
# second reason not to make it a function name as well.
function Announce([string]$Line) { Write-Host $Line -ForegroundColor Cyan }

function Abort([string]$Why, [string]$Then = '') {
    Write-Host "STOPPED: $Why" -ForegroundColor Red
    if ($Then) { Write-Host "         $Then" -ForegroundColor Red }
    exit 1
}

function ParentOf([int]$Id) {
    if ($IsWindows) {
        $info = Get-CimInstance Win32_Process -Filter "ProcessId=$Id" -ErrorAction SilentlyContinue
        if ($info) { return [int]$info.ParentProcessId }
        return 0
    }
    $out = (ps -o ppid= -p $Id 2>$null)
    if ($out) { return [int]($out.Trim()) }
    return 0
}

# Whether the process we are about to end is one we are running inside of.
# Bounded rather than while-true: a parent chain should be a handful deep,
# and a cycle in it must not become a hang in a script whose whole job is
# to finish and get out of the way.
function RunningInside([int]$Candidate) {
    $walk = $PID
    for ($i = 0; $i -lt 24 -and $walk -gt 0; $i++) {
        if ($walk -eq $Candidate) { return $true }
        $walk = ParentOf $walk
    }
    return $false
}

function FullName([string]$Path) {
    try { return [IO.Path]::GetFullPath($Path) } catch { return $Path }
}

# ---- 1. this machine's runtime identifier ----
# Built from the OS and the architecture rather than read from
# RuntimeInformation.RuntimeIdentifier, which reports the runtime PowerShell
# itself is on - not necessarily the one the SDK will publish for.
$os = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
$arch = ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture).ToString().ToLowerInvariant()
$rid = "$os-$arch"
$exe = if ($IsWindows) { "$Program.exe" } else { $Program }

# ---- 2. where it goes ----
if ($To) {
    $dir = FullName $To
    Announce "installing where asked: $dir"
}
else {
    $already = Get-Command $Program -CommandType Application -ErrorAction SilentlyContinue |
               Select-Object -First 1
    if ($already) {
        $dir = Split-Path -Parent (FullName $already.Source)
        Announce "found $Program on PATH at $($already.Source)"
    }
    elseif ($IsWindows) {
        # Per program, matching the install pages - except burler, whose
        # home is beside fettle. Installing burler beside pick is -To.
        $fallback = if ($Program -eq 'pick') { 'Programs/pick' } else { 'Programs/fettle' }
        $dir = FullName (Join-Path $env:LOCALAPPDATA $fallback)
        Announce "nothing called $Program is on PATH; falling back to $dir"
    }
    else {
        $dir = FullName (Join-Path $HOME '.local/bin')
        Announce "nothing called $Program is on PATH; falling back to $dir"
    }
}
$destination = Join-Path $dir $exe

# Every install that displaced a running Windows copy leaves one of these
# behind, and it cannot be removed until that process ends. Sweeping at
# the start of the NEXT run is the moment it usually can be, so they do
# not accumulate. Best effort throughout: one still in use is not an
# error, it is simply not this run's to clean up.
function SweepAsides() {
    if (-not (Test-Path $dir)) { return }
    foreach ($old in @(Get-ChildItem -Path $dir -Filter "$exe.old-*" -File -ErrorAction SilentlyContinue)) {
        try { Remove-Item $old.FullName -Force -ErrorAction Stop } catch { }
    }
}
SweepAsides

# ---- 3. is a copy of it running ----
# Collected before the build so the report at the end can name what is
# still on the old build. Nothing here is ended in order to install.
$running = @()
foreach ($p in @(Get-Process -Name $Program -ErrorAction SilentlyContinue)) {
    # A process owned by another account refuses to name its own image, and
    # that is not an error here - it is simply not one we could replace.
    $where = $null
    try { $where = $p.Path } catch { $where = $null }
    if ($where -and (FullName $where) -ieq $destination) { $running += $p }
}

# Only -Stop can kill, so only -Stop can kill the thing we are running
# inside of. Without it this is a perfectly ordinary install and the
# ancestry does not matter.
if ($Stop) {
    foreach ($p in $running) {
        if (RunningInside $p.Id) {
            Abort "-Stop would end the $Program this is running inside (pid $($p.Id))." `
                  "Run it from a terminal instead - a task cannot outlive the server hosting it."
        }
    }
}

# ---- 4. build the thing that will be copied ----
$staging = Join-Path $root "artifacts/install/$rid"
$staged = Join-Path $staging $exe

# burler carries native ONNX libraries, and a single file without them is a
# single file that cannot start. fettler-build.ps1 says the same thing at its own
# publish step.
$native = @()
if ($Program -eq 'burler') { $native = @('-p:IncludeNativeLibrariesForSelfExtract=true') }

if ($DryRun) {
    Announce "would publish $Program for $rid, then install it to $destination"
    if (Test-Path $destination) {
        if ($IsWindows) { Announce "would move the file there aside to $exe.old-$PID and copy into its place" }
        else { Announce "would write $exe.new-$PID beside it and rename that onto it" }
    }
    if ($running.Count -gt 0) {
        if ($Stop) { Announce "would then end pid(s): $($running.Id -join ', ')" }
        else { Announce "would then report pid(s) still on the old build: $($running.Id -join ', ')" }
    }
    Announce "nothing was written"
    exit 0
}

Announce "> dotnet publish $Program -c Release -r $rid --self-contained -p:PublishSingleFile=true"
$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    dotnet publish $Program -c Release -r $rid --self-contained `
        -p:PublishSingleFile=true @native -o $staging
}
finally { $ErrorActionPreference = $prev }

if ($LASTEXITCODE -ne 0) { Abort "the build failed (exit $LASTEXITCODE)." "Nothing was installed." }
if (-not (Test-Path $staged)) { Abort "the build produced no $exe at $staged." }

# ---- 5. put it in place, without ending anything ----
New-Item -ItemType Directory -Force $dir | Out-Null
$aside = ''

if ($IsWindows) {
    # Rename, then copy. Windows forbids overwriting a running image and
    # permits renaming one, which is the whole of why this is two steps
    # rather than a Copy-Item -Force.
    if (Test-Path $destination) {
        $aside = Join-Path $dir "$exe.old-$PID"
        try { [IO.File]::Move($destination, $aside, $false) }
        catch { Abort "could not move the existing $exe aside: $($_.Exception.Message)" "Nothing was installed." }
    }
    try { Copy-Item $staged $destination -Force }
    catch {
        # Put back what was there. A half-done install that leaves no
        # binary at all is the one outcome worse than not installing.
        if ($aside) { try { [IO.File]::Move($aside, $destination, $false) } catch { } }
        Abort "could not copy the new $exe into place: $($_.Exception.Message)" "The previous build was left where it was."
    }
}
else {
    # Write beside it, then rename onto it. rename(2) is atomic, so no
    # reader ever sees a half-written file, and a process already running
    # the old one keeps the inode it opened.
    $landing = Join-Path $dir "$exe.new-$PID"
    try {
        Copy-Item $staged $landing -Force
        chmod +x $landing
        [IO.File]::Move($landing, $destination, $true)
    }
    catch {
        if (Test-Path $landing) { try { Remove-Item $landing -Force } catch { } }
        # ${exe}, not $exe - PowerShell reads `$exe:` as a scope-qualified
        # variable, the way `$env:PATH` is, and refuses to parse the file.
        Abort "could not install ${exe}: $($_.Exception.Message)" "The previous build was left where it was."
    }
}

# ---- 6. prove the thing that landed actually runs ----
# A copy that reports success and produced an unrunnable file is the state
# this is least able to notice any other way. Both halves matter: catch is
# for a file that will not launch at all, $LASTEXITCODE for one that
# launches and fails - without the second, a binary that printed an error
# and exited 1 would be reported as the version it "installed".
# NOT piped into Select-Object -First 1: that stops the pipeline as soon
# as it has its line, which can cut the native command off before its exit
# code is recorded - and the exit code is half of what is being checked
# here. Collect it all, then take the first line.
$said = @()
$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'   # native stderr is not a fatal error here
$LASTEXITCODE = 0
try { $said = @(& $destination --version 2>&1) }
catch {
    if ($aside) { Write-Host "The previous build is still here, as $aside." -ForegroundColor Yellow }
    Abort "$destination was written but will not run: $_"
}
finally { $ErrorActionPreference = $prev }

$reported = if ($said.Count -gt 0) { "$($said[0])" } else { '' }

if ($LASTEXITCODE -ne 0) {
    if ($aside) { Write-Host "The previous build is still here, as $aside." -ForegroundColor Yellow }
    Abort "$destination ran but failed --version (exit $LASTEXITCODE): $reported"
}

Write-Host ""
Write-Host "installed $reported" -ForegroundColor Green
Write-Host "          $destination"

# ---- 7. end what is still on the old build, if asked ----
# After the install, deliberately, and never as a way of making room for
# it. Ending servers first and then finding the tree does not compile
# leaves the machine with nothing working at all.
if ($Stop -and $running.Count -gt 0) {
    foreach ($p in $running) {
        Announce "ending pid $($p.Id)"
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { Announce "  pid $($p.Id) had already gone" }
    }
    Start-Sleep -Milliseconds 400
    SweepAsides   # the aside is usually free the moment its process ends
}

# ---- 8. what is still a person's job ----
$now = Get-Command $Program -CommandType Application -ErrorAction SilentlyContinue |
       Select-Object -First 1
if (-not $now -or (FullName $now.Source) -ine $destination) {
    Write-Host ""
    Write-Host "NOT ON PATH: $dir is not what '$Program' resolves to." -ForegroundColor Yellow
    Write-Host "             Add it and open a new terminal, or register the absolute" -ForegroundColor Yellow
    Write-Host "             path with: $Program setup CLIENT --local --command `"$destination`"" -ForegroundColor Yellow
}

if ($running.Count -gt 0) {
    Write-Host ""
    if ($Stop) {
        Write-Host "Those processes have been ended. Reconnect the server from your" -ForegroundColor Yellow
        Write-Host "client - in Claude Code, /mcp. Nothing here can do that for you: a" -ForegroundColor Yellow
        Write-Host "stdio server belongs to the client that launched it." -ForegroundColor Yellow
    }
    else {
        Write-Host "STILL ON THE OLD BUILD: pid(s) $($running.Id -join ', ')." -ForegroundColor Yellow
        Write-Host "             A process does not reload its own image, so these go on" -ForegroundColor Yellow
        Write-Host "             serving what they started with. Restart them from your" -ForegroundColor Yellow
        Write-Host "             client - in Claude Code, /mcp - or re-run with -Stop." -ForegroundColor Yellow
    }
}
