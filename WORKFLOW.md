# Branch to tag, start to finish

The maintainer's path for one piece of work: cut a branch, build the thing,
prove it, ship it. Every command here is the Windows `.ps1`; the `.sh` twin
takes the same arguments, flags included.

**Every script is prefixed `fettler-`, and no task or script is named with a
bare git word** — the committing task is `checkin`, not `commit`. An
assistant's permission layer reads these names, and a bare git word can be
refused as if it were the raw git command; the prefix stops the pattern-match.

**Every step is a script except one.** The pull request is a person reading a
diff and a green CI run and deciding, and nothing here automates that. The
scripts stop either side of it.

This page is the sequence. The reference behind each step — and the reasoning
that made it this short — is [RELEASING.md](RELEASING.md).

| | | |
|---|---|---|
| 1 | **Branch** | `./scripts/fettler-branch.ps1 feature NAME` |
| 2 | **Work** | edit, `./scripts/fettler-build.ps1`, `./scripts/fettler-checkin.ps1 -Message "..."` |
| 3 | **Prove** | `./scripts/fettler-build.ps1 test` then `./scripts/fettler-build.ps1 check` |
| 4 | **Notes** | `release-notes/vX.Y.Z.md`, on the branch, committed |
| 5 | **Review** | `./scripts/fettler-pr.ps1` — **then a person merges it on GitHub** |
| 6 | **Land** | `./scripts/fettler-branch.ps1 land` |
| 7 | **Ship** | `./scripts/fettler-ship.ps1 X.Y.Z` |
| 8 | **Watch** | Actions → Release |

---

## 1 — Branch

```powershell
./scripts/fettler-branch.ps1 feature NAME     # -> feature/NAME
./scripts/fettler-branch.ps1 bug NAMENN       # -> bug/NAMENN, for a fix to something released
```

It fetches, refuses a name already taken locally or on origin, cuts from an
up-to-date `main`, and switches you to it. A leading `feature/` or `bug/` is
stripped, so both spellings work.

**No work happens on `main`.** It receives merges and nothing else — and
`scripts/fettler-checkin.ps1` refuses there rather than trusting anyone to remember.

**A fix branch is numbered per origin feature.** `bug/reinard01` is the first
fix to work that shipped from `feature/reinard`, so `bug reinard` refuses
where `bug reinard01` is meant. Which numbers are taken:

```powershell
git --no-pager log --oneline main --grep="Merge pull request"
```

## 2 — Work

```powershell
./scripts/fettler-build.ps1                            # restore + build (Debug)
dotnet test Fettler.Tests                      # one project, while you are iterating
./scripts/fettler-checkin.ps1 -Message "what changed"  # stage everything and commit
```

`scripts/fettler-checkin.ps1` **pushes nothing.** Committing and publishing are separate
decisions and it only makes the first. It also refuses on an empty message,
on a clean tree, and mid-merge — and it adds any new `.sh` with `--chmod=+x`,
because `verify-permissions` walks *tracked* files and cannot see a script
that has never been added.

**Write the tests first**, from what the change must be true of, and never
edit a test to make code pass — if it fails, either the code is wrong or the
requirement was, and both are worth knowing.

If `main` moves while you work:

```powershell
./scripts/fettler-branch.ps1 sync     # merge main into your branch, deliberately, tree clean
```

Far better here than as a surprise conflict inside the pull request.

## 3 — Prove

```powershell
./scripts/fettler-build.ps1 test     # Fettler.Tests AND burler.Tests
./scripts/fettler-build.ps1 check    # twins, permissions, docs, errors
```

Both, and both every time. Together they are what CI runs, so a green pair
here means a green pipeline there.

**`check` can leave the tree different from how it found it.**
`verify-permissions` stages a missing executable bit rather than only
reporting it — `git status` will show the correction, and it ships in whatever
commit you make next. CI runs the same script with `--check`, which fixes
nothing and fails instead.

## 4 — Release notes

**On the branch, before you ship, and committed.** The Release workflow checks
out the *tag* and reads only what that commit contains, so notes written
afterwards are invisible to it.

One file per release, named for its tag: `release-notes/v1.0.0.md`. The whole
file becomes the body of the GitHub Release. Format and an example are in
[release-notes/README.md](release-notes/README.md).

`scripts/fettler-ship.ps1` refuses to tag without this file, so forgetting it costs a re-run,
not a bad release.

## 5 — Review

```powershell
./scripts/fettler-pr.ps1              # push the branch and open a pull request
./scripts/fettler-pr.ps1 -Draft       # open it as a draft
./scripts/fettler-pr.ps1 -Web         # and open it in a browser
```

It pushes, opens the pull request against `main`, titles it from your first
commit, and fills the body with the commit list. It refuses on a dirty tree —
a reviewer sees your commits, not your working copy — and warns if `main` has
moved ahead of you.

**It is re-runnable.** Answering review comments means more commits on the
same branch, so a second run pushes them and prints the existing pull request
rather than failing.

**Then stop, and use GitHub.** Read the diff, wait for CI to go green on both
Windows and macOS, and merge it yourself. Nothing in this repository will do
that for you, and `land` below refuses on a branch whose pull request is not
merged — so there is no back way round it.

## 6 — Land

```powershell
./scripts/fettler-branch.ps1 land
```

After the merge, this returns you to an up-to-date `main` and deletes the
branch locally and on origin.

**It asks GitHub whether the work actually landed before deleting anything.**
If `gh` reports the pull request merged, it proceeds. If not, it falls back to
asking git whether every commit is reachable from `origin/main` — which is the
honest answer for a merge commit and a false negative for a **squash** merge,
so in that case it explains itself and asks rather than deciding.

## 7 — Ship

```powershell
./scripts/fettler-ship.ps1 1.0.0
```

That is the whole release. **There is no version to bump, no release branch to
cut, and nothing to merge back** — the version comes from the tag.

`scripts/fettler-ship.ps1` does exactly two mutating things: it creates a tag and it pushes
that tag. Everything else it does is refuse:

| It refuses when | Because |
|---|---|
| the version is not an exact `X.Y.Z` | it ends up stamped into a binary |
| you are not on `main` | a release is cut from `main` |
| the tree is dirty | the tag must name a commit that exists |
| `main` and `origin/main` differ | CI has not seen what you are tagging |
| the tag exists, locally or on origin | a tag is never moved |
| `release-notes/vX.Y.Z.md` is missing or uncommitted | the workflow reads the tagged commit |
| CI is red or still running on that commit | the proof is CI's, not this script's |

It fetches first, before any of the checks about origin — local refs go stale
the moment somebody else pushes, and nothing announces it.

## 8 — Watch, then verify

Actions → **Release**. It rebuilds from the tag on a clean Linux runner, runs
both suites again, publishes 12 archives — `fettle` and `burler`, six RIDs
each — and composes the release body from your notes file.

Three things it asserts before publishing, each of which has broken a release
somewhere:

- **the unix archives carry the executable bit** — it only survives a tar made
  on a filesystem that has one, and a `fettle` that arrives without it does
  not run;
- **the binary reports the version on the archive** — proving the tag reached
  `-p:Version=` rather than the build falling back to the development default;
- **every archive carries `NOTICE` and `LICENSE`** — an obligation nobody can
  read has not been met.

```powershell
git ls-remote --tags origin    # the tag is public
```

**If the workflow fails, the tag is already public.** Fix forward with a new
patch version. Never move a tag someone may have fetched.

Docs deploy themselves: a merge touching `docs/**` triggers the Pages
workflow. Nothing to do.

---

## Every script, at a glance

| Script | What it does | Mutates |
|---|---|---|
| `scripts/fettler-build.ps1` | build, test, check, release, publish | build output |
| `scripts/fettler-branch.ps1 feature\|bug` | cut a branch off an up-to-date `main` | a local branch |
| `scripts/fettler-branch.ps1 sync` | merge `main` into the current branch | your branch |
| `scripts/fettler-checkin.ps1` | stage everything and commit | **history**, locally |
| `scripts/fettler-pr.ps1` | push and open a pull request | **origin**, and opens a PR |
| `scripts/fettler-branch.ps1 land` | return to `main`, delete the merged branch | **deletes** a branch |
| `scripts/fettler-ship.ps1` | tag and push the tag | **publishes** a release |
| `scripts/fettler-make-sitemap.ps1` | regenerate `docs/sitemap.xml` | one file |

The four verifiers under `scripts/` are read-only, except
`fettler-verify-permissions.js`, which stages a fix.

## When it goes wrong

| Symptom | First move |
|---|---|
| `scripts/fettler-checkin.ps1` refuses: on main | That is the rule working. `./scripts/fettler-branch.ps1 feature NAME`, or `-Force` if you truly mean it. |
| `scripts/fettler-pr.ps1` refuses: dirty tree | Commit first — a review reads commits, not your working copy. |
| `scripts/fettler-pr.ps1`: push rejected | Someone else pushed to your branch. `git pull`, then re-run. |
| `scripts/fettler-branch.ps1 land` cannot confirm the merge | Usually a squash merge. It explains and asks; answer `y` if you merged it. |
| `scripts/fettler-branch.ps1 sync` hits conflicts | Resolve, `git add`, `git commit`. Nothing else changed. |
| `scripts/fettler-ship.ps1` says CI is still running | Wait. The tag should name a commit already proven. |
| `scripts/fettler-ship.ps1` says the notes are not committed | Commit and push them, then re-run. Nothing was tagged. |
| The tag was created but the push failed | It is local only and nothing has shipped. `git tag -d vX.Y.Z` and retry. |
| The Release workflow failed after publishing | Fix forward with a new patch version. |
| `verify-permissions` keeps reporting the same file | You ran a filesystem `chmod`. Use the script — a checkout with `core.filemode=false`, the default on Windows, makes a raw `chmod` invisible to git. |

## What is where

| | |
|---|---|
| This page | the sequence |
| [RELEASING.md](RELEASING.md) | the reference behind every step, and why it is this short |
| [CONTRIBUTING.md](CONTRIBUTING.md) | outside contributors: fork, PR, licence |
| [CLAUDE.md](CLAUDE.md) | repo rules for an assistant working here |
