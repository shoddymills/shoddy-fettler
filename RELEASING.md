# Releasing Fettler

How a version gets cut, in full, and why the procedure is as short as it is.
**If you want the sequence rather than the reasoning, that is
[WORKFLOW.md](WORKFLOW.md), and it is the page to follow.**

---

## The short version

```powershell
./scripts/fettler-build.ps1 test     # both suites
./scripts/fettler-build.ps1 check    # twins, permissions, docs, errors
# write release-notes/vX.Y.Z.md, commit it, merge to main with CI green
./scripts/fettler-ship.ps1 1.0.0     # tag and push - CI publishes
```

Every script has a `.sh` twin taking the same arguments, for Linux and macOS.
**On Windows run the `.ps1`.** It is the path that ships there, and the twin
that goes unexercised is the twin that rots. `fettler-verify-twins.js`
exists because that has happened.

Every script is prefixed `fettler-`, and no task or script is named with a
bare git word. The task that commits is `checkin`, not `commit`. An
assistant's permission layer reads these names, and a release attempt once
stalled when a task named `commit` was refused as if it were the raw git
command. Keep the prefix on anything new.

## The tag is the version

This is the central decision, and everything short about the procedure
follows from it.

**Nothing in this repository records a release number.**
`Directory.Build.props` carries `1.0.0` as a development default. A release
takes its number from the git tag, and `release.yml` passes it to
`dotnet publish -p:Version=`.

**Everything a version-in-a-file needs, this does not need.** No bump commit.
No `release/VX.Y.Z` branch to cut. No merge back. No half-finished sequence to
recover from, because there is no sequence. Pushing the tag is the whole of
it, and a step that does not exist cannot fail with the previous ones already
done.

**The cost, stated plainly:** a locally built `fettle` reports `1.0.0`
whatever branch it came from. That is accurate, because a local build is not
a release. `release.yml` asserts that a downloaded archive's binary reports
the version printed on the archive, so the number a user sees is never a
guess.

## There is no gate harness

Fettler is two executables and two test projects.
`./scripts/fettler-build.ps1 test` is the entire suite and it runs in
minutes.

So there is no step registry, no receipt store keyed to the working tree, and
no `--resume`. Those exist to avoid re-running an hour of work, and there is
no hour of work here. A receipt store also has a failure mode worth avoiding.
One keyed on the commit SHA alone will happily print a pass, in a tenth of a
second, describing a tree from before the morning's edits.

**What replaces it is CI's own verdict.** `scripts/fettler-ship.ps1` asks
GitHub whether the commit it is about to tag passed, and refuses if it did
not. That is a better answer than a local receipt for the same reason a build
artefact is better than a build log. It is the machine that does the work,
reporting on the actual work.

If `gh` is not installed the check is skipped with a warning rather than
failing. An unavailable tool should not block a release. The workflow runs
both suites again on the tagged commit anyway, so nothing ships unproven.

## There is one way to ship

`scripts/fettler-ship.ps1` has no primitive underneath it that skips the
checks. This matters more than it sounds. The previous arrangement had two
entry points that both tagged and pushed, one of which quietly skipped the
proof, and the documentation needed a comparison table warning you off the
wrong one. A second door into a release is a door somebody uses at 2am.

**Pushing the tag is the moment it ships.** `scripts/fettler-ship.ps1` prints
what is about to happen and asks before doing it. Add `-Yes` from a
non-interactive shell. It is spelled the same way in both twins,
deliberately. A pair that took `-Yes` in PowerShell and `-y` in the shell
while the docs called them identical is one of the two drifts
`fettler-verify-twins.js` was written for.

## No path filters on CI

`ci.yml` runs on every push and every pull request, with no `paths` or
`paths-ignore`.

This is deliberate and it is a reaction to a specific failure. Every lane in
the previous repository was path-filtered, so a change outside a lane's paths
ran none of its suites. `Directory.Build.props`, which sets the version every
binary reports, matched no filter at all. Three of four test projects were
proved only by workflows a given push might never trigger.

The cost is a doc typo rebuilding on two operating systems for nothing. That
is a few runner-minutes. When the choice is between wasting a runner and not
knowing, waste the runner.

## What the release asserts before it publishes

Each of these is a check because the thing it checks has gone wrong
somewhere:

**The unix archives carry the executable bit.** It only survives a tar made
on a filesystem that has one. An archive cut on Windows cannot record it at
all, and a `fettle` that arrives without it does not run. This is why the
release job is a Linux runner and why that matters rather than being
incidental. R6.9 is Fettler's own clause about exactly this bit, so shipping
it wrong would be a poor joke. `burler` is checked too. It is launched by
`fettle` as a child process, and one that arrives without its bit turns every
screened read into a refusal that blames the sidecar for not starting.

**The binary reports the version on the archive.** Before a version was
stamped at all, every binary reported the MSBuild default of `1.0.0` while
the repository shipped something else, which made the smoke test report a
figure that meant nothing.

**Every archive carries `NOTICE` and `LICENSE`.** Apache-2.0 asks the licence
and any NOTICE to travel with a distributed binary, and `fettle` bundles
PdfPig into the single file it ships. MIT asks its copyright notice to be in
every copy, and an archive is a copy. An obligation that stops at the
repository has not reached the person who downloaded a release.

## The executable bit, before it reaches CI

`fettler-verify-permissions.js` checks the bit on every tracked script **in
git's index**, not on the filesystem. A checkout with `core.filemode=false`,
the default on Windows, makes a raw `chmod` invisible to git no matter what
the filesystem did.

It has two modes on purpose. Locally it **stages the fix**, because reporting
a wrong bit and making the reader go and correct it by hand is how four of
them once shipped at once. One of those took down a release with
`Permission denied`, exit 126. In CI it runs with `--check`, which fixes
nothing and fails instead. A runner's index is thrown away at the end of the
job. A fixing run there would repair the bit, print a cheerful line, exit 0,
and let the fault sail through.
**A gate that repairs the evidence is not a gate.**

## Release notes

**Where:** one file per release in [`release-notes/`](release-notes/), named
for its tag. `v1.0.0` becomes `release-notes/v1.0.0.md`. The whole file
becomes the body of the GitHub Release.

**When: before you tag, and committed.** The workflow checks out the tag and
reads only what that commit contains. `scripts/fettler-ship.ps1` refuses to
tag without the file, so this is a re-run rather than a bad release. If a tag
is pushed by hand without one, the workflow falls back to a list of commit
subjects. That is never empty, and never worth linking to.

## If a release fails

**The tag is already public.** Fix forward with a new patch version. Never
move a tag someone may have fetched, and never delete one. A consumer who
fetched it has it, and a moved tag means two different builds answer to the
same number.

If `scripts/fettler-ship.ps1` created the tag but the push failed, nothing
has shipped. The tag is local only, and `git tag -d vX.Y.Z` puts you back
where you started. The script says so when it happens.
