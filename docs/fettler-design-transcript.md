# Design conversation - Fettler

**Date:** 16 August 2026
**Repository context:** `c:\github\shoddy`, branch `feature/gildersome`
**Participants:** Stephen Vincent Foster, Claude (Opus 5)

**Outcome:** an enhancement request at
`C:\github\shoddy-planning\requirements\backlog\shoddy-source-tools-enhancement.md`
specifying **Fettler**, a C# file/search/edit tool with an MCP front end
and a CLI front end.

**On fidelity.** User messages are reproduced verbatim. Assistant
responses are reproduced in substance, with the tables, examples and
conclusions intact; some long passages are trimmed rather than padded.
Tool calls are summarised in italics rather than shown as full payloads.

---

## 1. User

> throughout our work, we've consantly been hit by bash and windows issues when using tools to develop shoddy.
>
> please prepare for me a list of the essential commands/tools/etc you use to search for files and their contents, to read file contents, to create files or change existing files. when i say files, i mean source files of any type or language, not binary. and anything else you use frequently to help me write code.

## 2. Assistant

*Ran two verification searches against `machines/` to confirm how `.shoddy` files are actually matched.*

Verified that `Glob` and `Grep` both take forward-slash patterns and Windows absolute paths, and both return backslash paths - no shell involved either way.

**The headline:** every file operation has a dedicated tool that never touches a shell. Glob, Grep, Read, Edit and Write are Node-level calls into the filesystem and ripgrep. No quoting rules, no `/dev/null` vs `NUL`, no encoding default, no `&&`. The bash/Windows pain comes entirely from reaching for `Bash` or `PowerShell` to do something one of these five already does.

| Purpose | Tool | Notes |
|---|---|---|
| Find files by path | `Glob` | `machines/**/*.shoddy`; results sorted by mtime |
| Which files contain a pattern | `Grep` (`files_with_matches`) | cheapest way to narrow before reading |
| Matching lines | `Grep` (`content`) | `-n`, `-A`/`-B`/`-C` context |
| Read | `Read` | absolute path; `cat -n` output; `offset`/`limit` for large files |
| Targeted change | `Edit` | must have Read first; `old_string` must be byte-exact and unique |
| New file / full replacement | `Write` | overwriting an unread file is refused |

Notes that mattered: ripgrep regex (not grep), `.shoddy` has no ripgrep `type` so filtering is `glob: "*.shoddy"`, and never `Get-ChildItem -Recurse`, `Select-String`, `Get-Content`, `Set-Content` (which defaults to ANSI on this box).

**PowerShell 5.1 traps:** no `&&`/`||`; no ternary/`??`/`?.`; `2>&1` on a native exe wraps stderr as `NativeCommandError` and sets `$?` false even on exit 0; here-strings must close at column 0; `-ErrorAction SilentlyContinue` still exits 1.

**The rule that would have saved most of the trouble:** if the operation is find, read, or change a text file - no shell, ever. The shell is for verbs that produce output: build, test, run, git. On this machine that shell is PowerShell, and the script to run is the `.ps1`.

---

## 3. User

> i also have this desire that anyone using shoddy -- including with claude, not need anything else. no python. no wsl. no bash. nothing else required.
>
> please consider all of the actions you perform on source code files...which you listed above....and design a single, cohesive, comprehensive tool, or suite of tools that would be written in shoddy. be holestic. don't be afraid to suggest shoddy enhancements, such as file operations. don't just recreate what's there....this is your chance to tell me how you'd like to work with files.

## 4. Assistant

*Read `machines/file.shoddy` in full; launched two background exploration agents (one mapping the builtin file/OS surface, one mapping the text/string surface); read `mills/halifax/mill.manifest`, `machines/terminal.shoddy`, `hosts/mcp/README.md`, `hosts/mcp/CAPABILITIES.md`, `src/Shoddy.Runtime/FileRoot.cs`, `mills/sparky/mill.manifest`; grepped for capability declarations, sum-type syntax and record syntax.*

Key findings surfaced during the research:

- `file.shoddy` is 92 lines: eight whole-file builtins and three words. No directories, no rename, no copy, no metadata by path, no atomic write.
- The capability manifest already gates file access by glob pattern (`"write": ["halifaxrc", "*.calc", "*.tape"]`).
- `Shoddy.Runtime.FileRoot` already enforces a root boundary for every file word - `..`, absolute paths and symlinks anywhere along the path - and every host inherits it.
- Sparky is the precedent: a mill whose *user is a model*, hand-rolled JSON-RPC over stdio, self-contained single-file publish per OS.
- No `Eval` or compile word exists, so Shoddy cannot verify Shoddy from inside the language.

*This turn was interrupted before the design was delivered.*

---

## 5. User *(interrupting)*

> ok...that's enough. i'm asking a purely functionaly language to do a lot of side-effect stuff. that's not a good fit for the language.
>
> Our goal of a single install can be met with c#. same requirements, different target. what's that plan?

## 6. Assistant

Agreed - I was pushing effects into a language built to keep them at the edges.

*Verified `hosts/mcp` project layout and read `FileRoot.cs`, confirming it is pure `System.IO` with no Shoddy dependencies.*

*This turn was interrupted before the plan was delivered.*

---

## 7. User *(interrupting)*

> let's keep this 100% csharp. no shoddy. but part of the repo. to keep our single-install goal.

## 8. Assistant

**The shape:** one more MCP server, in this repo, 100% C# - `read`/`write`/`edit`/`find`/`search`/`run` over stdio. Most of the hard parts already exist in `hosts/mcp`.

Proposed layout: shared protocol in `src/Shoddy.Mcp.Protocol`, shared path boundary in `src/Shoddy.Paths`, tools in `hosts/mcp/Shoddy.Tools`, executable reusing `sparky`.

**Five design calls worth making deliberately:**

- **Edits are planned, then applied.** All anchors resolved against the file as read; conflicts reported before anything is written; one write.
- **Staleness checked by content, not a session flag.** Today's "must Read before Edit" proves I looked, not that the file is unchanged.
- **Anchors are scoped, not just unique strings.** Uniqueness fails constantly in a codebase of four-line bodies.
- **Encoding and line endings survive the round trip.** Shoddy's `ReadLines` does a blanket `Replace(c, "\r", "")` and `WriteLines` always writes LF plus a trailing newline - a one-line edit rewrites every line ending.
- **Writes are atomic.** Temp file, then `File.Move(..., overwrite: true)`. Not expressible in Shoddy at all, which is independent evidence C# is the right target.

**On the shell:** `run` uses `Process.Start` with `ArgumentList` - an array - never a command string. No quoting layer, no dialect. Caveat stated plainly: tasks still invoke `pwsh` and `node`, so the machine still needs them; full independence is a separate, larger job.

Phasing given as six phases, phase 0 (extraction) first because it makes the rest cheap and is the only phase touching shipping code.

---

## 9. User

> would this be both mcp and scripted (from ps or sh)?

## 10. Assistant

Yes - and the CLI mode pays for itself fastest.

- **This repo already runs the pattern.** `scripts/shoddy.ps1` and `.sh` are thin launchers over `scripts/gate/driver.mjs`, which exists so "the logic lives once and the twins cannot drift."
- **Every line of logic out of the twins is a line that can't drift.** The release that had to fix *native stderr being fatal in every PowerShell script* is exactly the failure this prevents.
- **It makes the tools testable.** Driving a stdio JSON-RPC session to check a glob is miserable; `sparky find "machines/**/*.shoddy"` is not.

Shape: one core knowing nothing of protocol or console, an MCP front end, a CLI front end. The split must exist from phase 1 or it's a retrofit.

What the CLI needs that MCP doesn't: distinct exit codes, a `--json` mode, and **the complete result including failures on stdout** - because PowerShell 5.1 wraps redirected native stderr into `NativeCommandError` and flips `$?` false even on exit 0. If a script ever needs `2>&1`, the bug is rebuilt.

---

## 11. User

> no dependencies on shoddy... including its runtime. i don't want these two things coupled together.
> the implementation can use whatever fileio mechanims work best.
> also, what about file-level operations? move, copy, delete, rename, create
> also, must work on windows and mac.
> please write an enhancement request, save in github\shoddy-planning\requirmenents (see folders for pattern). you have permission to write there. not a lot of design, focus on requirements.

## 12. Assistant

*Listed the requirements folder; read `shoddy-sparky-mcp-enhancement.md` and `shoddy-nuget-publication-enhancement.md` to match the house pattern; wrote the new document.*

Pattern identified: `[Enhancement]` title, a ground-truth table read from the tree, numbered `R*` clauses, acceptance criteria, out of scope, and decisions taken with the rejected option recorded. New requests go in `backlog/`.

Document written to `requirements/backlog/shoddy-source-tools-enhancement.md`, with:

- **R1 as the hard constraint** - no reference to any Shoddy assembly, plus a test inspecting built assemblies. `FileRoot` re-implemented, not shared: referencing it couples the tool to the runtime, moving it couples the runtime to the tool.
- **R6 for file-level operations** - no wildcards on anything destructive, no implicit overwrite, recursive delete asked for explicitly, move reports whether it was atomic.
- **R9 for platform** - Windows and macOS equal, with seven named hazards each having a wrong answer that works on one platform.

Flagged: `shoddy-planning` sits on `main`, the write was authorised in that message, nothing was committed.

---

## 13. User

> for naming, consider the tools use and the source of the shoddy name. think maintenance in a 1875 shoddy mill.

## 14. Assistant

*Read `west_riding_naming.md` and grepped `docs/heritage.html` for the naming tradition; confirmed `fettler` unclaimed in both repositories.*

**Fettler.**

In a West Riding woollen mill the fettler kept the carding engines in order - stripping matted fibre and dirt out of the card clothing, cleaning, setting, repairing. He produced no cloth. His whole job was keeping the machines that produced cloth in a state where they could.

- **It makes nothing.** The devil tears source into tokens, the weave builds, the mill runs. This tool keeps the material they work on in order.
- **"To fettle" is the literal English for what it does** - and survives as *in fine fettle*, so it needs no glossary.
- **The connection is real inside the existing naming.** `scribbler` is already named for scribbling, the first carding step. The fettler is the trade that maintained the carding engines.

Sub-choice: project and server **Fettler**, executable **`fettle`** - commands read as imperatives.

Runners-up and why they lost: **Millwright** (maintains machinery, not material; long, reads American), **Mender** (exact for editing, undersells the rest), **Overlooker** (supervision, not repair), **Tuner** (overloaded in software), **Mungo** (already taken - `mungo-caverns` is a mill).

---

## 15. User

> agreed. include that in the requirements.

## 16. Assistant

*Six targeted edits folding the name through the document.*

- Title and provenance paragraph in Sparky's shape.
- R2.1 names the lane `hosts/fettler` and adds that no project in it carries a `Shoddy.` prefix - the prefix would claim the kinship R1 exists to deny.
- R2.6 settles the two spellings.
- R11.5 requires a "Where the Name Comes From" section.
- Out of scope loses "a name is the maintainer's to give".
- Decisions taken gains the naming decision plus the five candidates that lost.

Noted that renaming the file to `shoddy-fettler-enhancement.md` would match the sparky precedent but needs a delete, so it needs explicit go-ahead.

---

## 17. User

> looking good. add examples for each command.

## 18. Assistant

*Added a `## The commands` section; discovered and closed a gap.*

Added twelve verbs in one table with an example each, worked forms, and the `.ps1`/`.sh` twins side by side reading from stdout with no `2>&1`.

**The gap it exposed:** there was no requirements section for `find` or `search` at all. The opening prose promised both and the acceptance criteria assumed them, but nothing in between said what they must do. Writing the examples is what surfaced it. Added as R4.6-R4.10, with three clauses that earn their place:

- **Glob semantics must be stated, not inherited** - both required platforms are case-insensitive, so deferring to the OS behaves differently from not.
- **A result must be usable as another verb's input without reformatting** - this is what makes it a set rather than a collection.
- **Truncation must be reported** - a silently capped search that reads as complete is worse than an error.

Also corrected a `copy` example writing to `/tmp`, which the containment rule would refuse.

---

## 19. User

> remove all the referencs to shoddy...just the requirements for this thing. simply say 'all c#', or whatever.

## 20. Assistant

*Full rewrite of the document.*

- Every reference to the other project removed. The only surviving "mill" mentions are in the naming rationale, explaining where Fettler comes from.
- **R1 became "Implementation and independence"** - written entirely in C#, no reference to any other project here, shares the repository and release and nothing else. The section arguing why it wasn't written in another language deleted outright.
- **Ground truth rewritten from scratch** - from facts about another codebase to nine verifiable platform behaviours: the PowerShell 5.1 stderr trap, case-insensitive filesystems, macOS filename normalization, Windows reserved names and path limits, open-file locking, and `File.Move` having two atomicity guarantees depending on volume.

---

## 21. User *(mid-turn)*

> your forgot to add examples for every command

## 22. Assistant

Correct - the worked forms covered six of twelve.

*Replaced the section with one worked example per verb: `find`, `search`, `read`, `write`, `edit`, `new`, `mkdir`, `move`, `copy`, `delete`, `tasks`, `run`.*

Each captioned with what it pins and which clause it comes from, and headed by a note that the examples pin **the information each answer must carry, not its layout** - otherwise invented output formatting reads as specification.

Several show the refusal path rather than the happy one, which is where the requirements have teeth:

```
$ fettle delete "artifacts/*.tmp"
refused: delete does not accept a pattern; name one path

$ fettle edit src/Fettler/Glob.cs --script edits.json
refused: edit 2 matched 4 places in src/Fettler/Glob.cs; nothing written

$ fettle move src/glob.cs src/Glob.cs
moved src/glob.cs -> src/Glob.cs  atomic: yes  case-only rename
```

---

## 23. User

> is a file-watcher useful in the mcp? to notify you of changes you may not otherwise know about...

## 24. Assistant

Yes - but almost certainly not as a watcher, and the reasoning inverts the obvious answer.

**The problem is real.** R5.5 refuses an edit against a file that changed since it was read, which catches staleness *at the moment of writing*. It misses the worse case: read a file, reason about it for several turns, never write - nothing ever checks, and the answer is built on content that stopped being true.

**Push is the wrong shape.** MCP can push, but whether a client surfaces a notification to the model is the client's business and unobservable from the server; and there is no process between turns to interrupt.

**The recommendation: stat what was read, not watch the tree.** The set of files with stale beliefs attached is exactly the set that has been read. Remember path, size and mtime per session; re-stat that set on any subsequent call; report divergence.

| | Stat what was read | Recursive watcher |
|---|---|---|
| Cross-platform reliability | A stat is a stat | `FileSystemWatcher` overflows under bursts on Windows; recursive watching on macOS has a long history of fd and API trouble |
| Noise from own writes | None - it knows what it wrote | Every edit reports itself unless carefully attributed |
| Noise from a build | None - nobody read `obj/` | Thousands of events without a scoping rule |
| What it misses | Files never read - no beliefs held about them | Whatever the OS dropped, silently |

The watcher watches an entire tree to report on a subset that could be checked directly.

**Three clauses if it goes in:** advisory never authoritative; mtime *and* size together; Fettler's own writes are not news.

**Where a real watcher would earn its place:** driving something reactive - rebuild on save, a live gate. That's a different product.

Recommendation: add the read-set staleness check, mark it advisory, and put a genuine file watcher in Out of scope with the reasoning recorded.

---

## 25. User

> provide a transcript of this conversation, as an md file please

## 26. Assistant

This document.

---

## Artifacts produced

| Artifact | Path | State |
|---|---|---|
| Enhancement request | `C:\github\shoddy-planning\requirements\backlog\shoddy-source-tools-enhancement.md` | written, uncommitted |
| This transcript | `<scratch>\fettler-design-transcript.md` | written |

## Open items at the close of the conversation

- **The file-watcher clauses are not yet written in.** The recommendation was given and agreed to nothing; the requirements document does not yet mention staleness notification.
- **The requirements file is still named `shoddy-source-tools-enhancement.md`.** Renaming it to `shoddy-fettler-enhancement.md` would match the sparky precedent but requires a delete.
- **Nothing was committed** in either repository at any point in this conversation.
