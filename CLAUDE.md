# Fettler

File, search and edit tools for a model and a script. One program, two front
ends, no shell between it and the file.

**The project, the lane and the MCP server are Fettler. The thing a person
or a script types is `fettle`**, the imperative, because
`fettle move a.txt b.txt` reads as an instruction and `fettler move` does
not. Both spellings are correct and neither is a typo.

## This is a Windows machine. Run the `.ps1`

Every script here ships twice: `fettler-build`, `fettler-branch`,
`fettler-checkin`, `fettler-pr`, `fettler-ship`, `fettler-install` and
`fettler-make-sitemap`, each with a `.ps1` and a `.sh`.
**Run the `.ps1`, every time, without being asked.** Git Bash exists on this
machine and the `.sh` twins run under it perfectly well, which is exactly why
this keeps happening. Reaching for `.sh` works, so nothing stops it. But the
`.ps1` is the path that ships here, and running the other one means it goes
untested release after release.

**If the `.ps1` is refused, stop.** Say which command was refused and wait.
Switching to the `.sh` twin to get past a denial defeats the point of the
denial.

## Every name carries the `fettler-` prefix, deliberately

Every script in `scripts/` is named `fettler-*`, and no script or task is
named with a bare git word. The task that commits is `checkin`, not
`commit`, and it runs `fettler-checkin.ps1`. An assistant's permission layer
reads these names, and a release attempt once stalled because a task named
`commit` was refused as if it were the raw git command. When adding a script
or a declared task, keep the `fettler-` prefix and pick a name that is not a
git verb.

## The commands

```powershell
./scripts/fettler-build.ps1                            # restore + build (Debug)
./scripts/fettler-build.ps1 test                       # Fettler.Tests AND burler.Tests. Both, always
./scripts/fettler-build.ps1 check                      # the verifiers: twins, permissions, docs, errors
./scripts/fettler-build.ps1 release                    # Release build
./scripts/fettler-build.ps1 publish 1.0.0              # self-contained single-file per OS, per program
./scripts/fettler-install.ps1                          # install over the fettle on PATH; NOT a task
./scripts/fettler-install.ps1 -Stop                    # ...and end the servers still on the old build

./scripts/fettler-branch.ps1 feature NAME              # cut a branch off an up-to-date main
./scripts/fettler-branch.ps1 sync                      # merge main into the current branch
./scripts/fettler-checkin.ps1 -Message "what changed"  # stage everything and commit; pushes nothing
./scripts/fettler-pr.ps1                               # push and open a pull request
./scripts/fettler-branch.ps1 land                      # after the PR merges: delete the branch
./scripts/fettler-ship.ps1 1.0.0                       # the one way to cut a release
```

`scripts/fettler-build.ps1 test` is the whole suite and it runs in minutes.
There is no gate harness, no receipt store and no `--resume`, because there
is nothing long enough to be worth resuming.

## What is here

| Project | What it holds |
|---|---|
| `Fettler/Core` | the operations: containment, text, writes, glob, find, search, edit, file operations, tasks |
| `Fettler/Cli` | the command-line front end: verbs, exit codes, both output modes |
| `Fettler/Mcp` | the MCP front end: stdio JSON-RPC over `System.Text.Json` |
| `fettle` | the executable, and the whole of it is wiring |
| `burler` | the disclosure screen's model host, a **second** executable |
| `Picker/Core` | the SQL boundary: verbs, grants, columns, the resolver, the ScriptDom query gate, the screen twin |
| `Picker/Cli` / `Picker/Mcp` | pick's two front ends, on Fettler's pattern |
| `pick` | a **third** executable. SELECT and DESCRIBE inside declared SQL Server databases, with its own MCP server |
| `Fettler.Tests` | the proof. Every item in R10 of the requirements is an assertion here |
| `burler.Tests` | burler's own proof, in its own project, so each side of the pipe asserts the wire independently |
| `Picker.Tests` | Picker's proof, pure against a catalog of literals, plus LocalDB tests that skip with a reason |
| `docs/` | the published site |
| `scripts/` | the verifiers, all Node, all read-only except `verify-permissions` |

**The project is Picker and the thing a person or a script types is
`pick`.** That is the fettle/fettler arrangement, after the rag pickers who
picked and graded the stock by hand and altered none of it. Its own
configuration is `.picker.json` + `.picker.local.json`. Its own requirements
are `picker-enhancement.md` in the planning tree. It shares the repository,
the release and the version number with the rest, and nothing else. There
is no ProjectReference in any direction. It has its own package allowlist
(ScriptDom and Microsoft.Data.SqlClient, managed SNI pinned), and its own
copy of the burler wire and the tier-one screen patterns, proven equivalent
by test.

## The invariants. Do not quietly relax these

- **No `ProjectReference` that leaves this repository, ever**, and none
  between `Fettler` and `burler` in either direction. The wire format is the
  whole contract between those two. Each side carries its own copy of the
  shapes that cross it, proven equivalent by a protocol test on each side
  rather than by a shared assembly.
- **A `PackageReference` only from the allowlist** in the owning csproj:
  `PdfPig` alone for `fettle`, and ScriptDom + `Microsoft.Data.SqlClient`
  for `pick` (in `Picker.csproj`). Adding one means editing the csproj
  comment, the lane's `FrontEndTests.Allowed`, `ci.yml`'s allowlist,
  `THIRD-PARTY-NOTICES.md`, and `NOTICE` if its licence asks. The CI
  workflow greps `NOTICE` for the literal strings `fettle alone` and
  `pick alone`, so both wordings matter.
- **`Fettler.Core` may not name `System.Text.Json`, `System.Console` or an
  exit code.** A core that knows about a protocol has already stopped being
  one. This is asserted by test.
- **The version is not in a file.** `Directory.Build.props` carries a
  development default. A release takes its number from the git tag. Never
  "bump the version" as a step. There is nothing to bump.

## Documentation style

The site under `docs/` and the root Markdown are written in plain language:
short sentences, short paragraphs, one idea per sentence, active voice, no
em dashes, no intensifiers, terms defined at first use, ASCII source with
entities. The docs verifier, `fettler-verify-docs.js`, fails on a
non-ASCII character. The `survey-readability` task measures every page. Every user-facing message the programs print follows the
same rule: the first sentence says what happened, the second says what to
do. The reasoning stays in a source comment.

## The release path

Feature branch to shipped, in full. **Every step is a script except the
merge**, which is a person reading a diff and a green CI run.

| # | Step | Command |
|---|---|---|
| 1 | Branch | `./scripts/fettler-branch.ps1 feature NAME`. The user names it |
| 2 | Work | `./scripts/fettler-build.ps1`, then `./scripts/fettler-checkin.ps1 -Message "..."` |
| 3 | Test | `./scripts/fettler-build.ps1 test` |
| 4 | Check | `./scripts/fettler-build.ps1 check` |
| 5 | Notes | write `release-notes/vX.Y.Z.md`, committed on the branch |
| 6 | Review | `./scripts/fettler-pr.ps1`. **Then a person merges it on GitHub** |
| 7 | Land | `./scripts/fettler-branch.ps1 land` |
| 8 | Ship | `./scripts/fettler-ship.ps1 X.Y.Z` |
| 9 | Publish | Actions, then Release |

**Never run `scripts/fettler-checkin.ps1`, `scripts/fettler-pr.ps1`,
`scripts/fettler-branch.ps1 land` or `scripts/fettler-ship.ps1` unless the
current message says to.** They write history, publish to origin, delete
branches and cut releases respectively. Finish the work, report what
changed, and leave the tree dirty. The choice to make it permanent is the
user's, and it is given per message rather than once.

`scripts/fettler-ship.ps1` does exactly two mutating things: create a tag
and push it. Everything else it does is refuse. **Pushing the tag is the
moment it ships**, and a tag someone may have fetched is never moved. Fix
forward with a new patch version.

The full reference is [RELEASING.md](RELEASING.md). The sequence is
[WORKFLOW.md](WORKFLOW.md).

<!-- fettler:begin -->
## Working on files here

Use the Fettler tools (`mcp__fettler__*`) for every file operation: find,
search, read, write, edit, move, copy and delete. The built-in Read, Write,
Edit, NotebookEdit, Grep and Glob are denied.

**There is no working directory.** Paths resolve against the declared trees.
`cd` and `Set-Location` do nothing for Fettler. Call `roots` first. It lists
the trees, what each allows, and which tree an unqualified path lands in.

**A tree may be read-only.** A scope inside a tree may allow more or less
than the tree does. Running a declared task needs `execute`, which is never
a default.

`.fettler.json` and `.fettler.local.json` say what Fettler may do. Fettler
does not write them. A person edits them.

If something looks wrong, run `fettle doctor`.
<!-- fettler:end -->
