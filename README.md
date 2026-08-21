# Fettler

**File, search and edit tools for a model and a script. One program, two
front ends, and no shell between it and the file.**

An assistant given Fettler cannot go wandering. Every path it touches must sit
inside a *tree* — a folder you named ahead of time. Outside those trees
everything is refused: writing, reading, listing, even learning that a file
exists. There is no current directory to change, no path can climb out of a
tree, and there is no shell to sneak past it with. So it cannot wander into a
sibling project, an old copy, or your home directory, read the wrong thing,
and then answer from it with complete confidence.

**And nothing it does changes how _you_ work.** Your shell, your file
explorer, your editor and your git stay exactly as they were.

| | |
|---|---|
| **One file to install** | Self-contained and single-file, per OS. The target machine needs no .NET and no checkout. |
| **Two front ends, one program** | `fettle serve` speaks MCP to an assistant; `fettle read src/A.cs` answers a script. Same operations, same boundary. |
| **Every answer is the next call's argument** | `search` hands back `tree:path:line:column`; `read` numbers its lines and states a hash; `edit` asks for exactly those. Refused edits stop happening. |
| **It reads more than source** | Notebooks as cells, spreadsheets as rows with their formulas, Word as paragraphs under their headings, PDF as text per page, images as facts — and inside archives without unpacking them. |
| **Many calls become one** | `read` takes several paths. `search` takes several patterns. `batch` takes a whole sequence, applied all or not at all. |
| **A screen you can switch on** | Refuses to disclose regulated data out of a scope you name — and refuses rather than serving it unscreened if the screen cannot run. |

**[Read the docs](https://shoddymills.github.io/shoddy-fettler/)**
&nbsp;·&nbsp;
[Quick start](https://shoddymills.github.io/shoddy-fettler/quickstart.html)
&nbsp;·&nbsp;
[Installing it](https://shoddymills.github.io/shoddy-fettler/install.html)
&nbsp;·&nbsp;
[In a project](https://shoddymills.github.io/shoddy-fettler/setup.html)

The project, the lane and the MCP server are **Fettler**. The thing a
person or a script types is **`fettle`**, the imperative — because
`fettle move a.txt b.txt` reads as an instruction and `fettler move`
does not. Both spellings are correct and neither is a typo.

## The fettled workstation

<p align="center">
  <a href="https://shoddymills.github.io/shoddy-fettler/diagram.html">
    <img src="docs/media/disk-map.svg" width="900"
         alt="A map of a developer's disk and the network beyond it. The whole machine is hatched, meaning out of bounds by default; a handful of lit blocks are the entire list of what is reachable. One repository is opened at one folder and shut at the next. The files that define the boundary are sealed inside the very tree that grants everything else. Two sanctioned network exits lead from one project only - and a Dropbox folder has its own sync door that no task opens and nothing here can close.">
  </a>
  <br>
  <a href="https://shoddymills.github.io/shoddy-fettler/media/disk-map.svg">⛶ Full screen</a>
</p>

**The disk, and every way off it.** The hatched ground is the whole machine,
and it is the default; the lit blocks are the entire list, not a summary of
it. A repository can be opened at one folder and shut at the next. The files
that define the boundary are sealed inside the very tree that grants
everything else. And the two exits open from one room only, because that is
the single place permitted to run anything at all. **Then there is Dropbox** —
a way off the machine that no task opens and nothing here can close, which is
why sealing that folder is not about privacy but about shutting a route that
would bypass every other line on the map.

<p align="center">
  <a href="https://shoddymills.github.io/shoddy-fettler/diagram.html">
    <img src="docs/media/workstation.svg" width="900"
         alt="A component diagram of three boundaries. First the model, whose eleven built-in tools are every one of them struck through as denied: six file tools, three shells, and two output readers. Second the Fettler MCP server, whose boundary comes from .fettler.json and which refuses to write the files that govern it or the model. Third the filesystem, visible only as the declared trees, with everything else refused as nonexistent.">
  </a>
  <br>
  <a href="https://shoddymills.github.io/shoddy-fettler/media/workstation.svg">⛶ Full screen</a>
</p>

**A workstation in fine fettle.** The client denies all eleven of its own
built-ins — the six file tools, the three shells, and the two output readers —
so the Fettler server is the only route to disk. **`Monitor` is a shell**,
running what it is handed in the same environment `Bash` does, so denying
`Bash` and leaving it open would deny the word and not the thing.
**`BashOutput` and `TaskOutput` are readers**: they start nothing and write
nothing, but they hand back the output of work already done, and those bytes
reached the model without passing the tree boundary, the secret scan or the
disclosure screen.

**[Both diagrams full size, with the reasoning behind every line](https://shoddymills.github.io/shoddy-fettler/diagram.html)**

## What is here

| Project | What it holds |
|---|---|
| `Fettler/Core` | the operations: containment, text, writes, glob, find, search, edit, file operations, tasks |
| `Fettler/Cli` | the command-line front end — verbs, exit codes, and both output modes |
| `Fettler/Mcp` | the MCP front end — stdio JSON-RPC, hand-rolled over `System.Text.Json` |
| `fettle` | the executable, and the whole of it is wiring |
| `burler` | the disclosure screen's model host — a **second** executable, for the reason below |
| `Fettler.Tests` | the proof; every item in R10 of the requirements is an assertion here |
| `burler.Tests` | burler's own proof — its own project, so each side of the pipe asserts the wire independently |
| `docs/` | the published site, deployed to Pages from `main` |
| `scripts/` | the verifiers and the sitemap generator — all Node, all read-only bar one |
| `release-notes/` | one file per tag; the whole file becomes the release body |

## Building and working on it

**This repository is set up for Fettler, and builds itself with itself.**
`.fettler.json` declares one tree and the tasks below, so an assistant working
here reaches the build through `run` and never through a shell. That is the
tool's own argument applied to the tool: `run` executes only what the
configuration declares, never composes a command line, and needs the tree to
grant `execute`, which is never a default.

**None of that is required to build it.** Every task is a script in the root
of this repository, and a terminal is a first-class way in. The two routes run
the same file with the same arguments — there is no privileged path.

**This repository needs nothing built first.** Fettler references no project
outside its own tree, so there is no staged binary to publish and nothing to
weave. `scripts/build.ps1 test` is the whole suite and it runs in minutes: no gate
harness, no receipt store, and no `--resume`, because there is nothing long
enough to be worth resuming.

### From a terminal

```powershell
./scripts/build.ps1               # restore + build (Debug)
./scripts/build.ps1 test          # Fettler.Tests AND burler.Tests — both, always
./scripts/build.ps1 check         # the verifiers: twins, permissions, docs, errors
./scripts/build.ps1 release       # Release build
./scripts/build.ps1 publish 1.0.0 # self-contained single-file per OS, per program
```

The git workflow is scripted too, end to end apart from the merge:

```powershell
./scripts/branch.ps1 feature NAME              # cut a branch off an up-to-date main
./scripts/commit.ps1 -Message "what changed"   # stage and commit; pushes nothing
./scripts/pr.ps1                               # push and open a pull request
                                       #   <- a person merges it on GitHub
./scripts/branch.ps1 land                      # return to main, delete the branch
./scripts/ship.ps1 1.0.0                       # tag and push — CI publishes
```

Every script has a `.sh` twin taking the same arguments, flags included, and
`verify-twins.js` fails the build if the two drift apart. **On Windows run the
`.ps1`** — it is the half that actually ships there, and the twin that goes
unexercised is the twin that rots.

[WORKFLOW.md](WORKFLOW.md) is that sequence in full.

### From an assistant, or any MCP client

The same commands, as declared tasks. From a terminal:

```powershell
fettle tasks              # what is declared, and what each would actually launch
fettle run test           # run one
```

From an MCP client the tools are `tasks` and `run`, taking the same names. **A
task takes no arguments from its caller** — name and timeout are the only
inputs, and anything else is refused rather than ignored. A variant is a
second declared task, or a value the configuration declares in braces.

| Task | Runs | Mutates |
|---|---|---|
| `build` | `scripts/build.ps1 build` | build output |
| `test` | `scripts/build.ps1 test` | build output |
| `check` | `scripts/build.ps1 check` | stages an executable bit, if one is wrong |
| `build-release` | `scripts/build.ps1 release` | build output |
| `publish` | `scripts/build.ps1 publish {release-version}` | build output |
| `verify-twins` | `scripts/verify-twins.js` | nothing |
| `verify-permissions` | `scripts/verify-permissions.js` | stages an executable bit |
| `verify-docs` | `scripts/verify-docs.js` | nothing |
| `verify-errors` | `scripts/verify-errors.js` | nothing |
| `sitemap` | `scripts/make-sitemap.ps1` | `docs/sitemap.xml` |
| `feature` | `scripts/branch.ps1 feature {feature-branch}` | a local branch |
| `bug` | `scripts/branch.ps1 bug {bug-branch}` | a local branch |
| `sync` | `scripts/branch.ps1 sync` | your branch |
| `commit` | `scripts/commit.ps1 -Message {commit-note}` | **history**, locally |
| `pr` | `scripts/pr.ps1` | **origin**, and opens a pull request |
| `pr-draft` | `scripts/pr.ps1 -Draft` | as above, as a draft |
| `land` | `scripts/branch.ps1 land -Yes` | **deletes** a merged branch |
| `ship` | `scripts/ship.ps1 {release-version} -Yes` | **publishes** a release |

The braced names are **replacements** — values the configuration supplies, so
a branch name or a version never has to be composed into a command line by
whoever is calling. They live in `.fettler.local.json`, which is gitignored,
so today's branch name stays out of the diff. An undeclared name is refused
and the configuration does not load, which is why that file is required here
rather than optional.

**The last five write something you cannot take back.** They are declared
because the whole procedure is meant to be reachable, not because they should
be run casually — `land` under `-Yes` refuses a branch it cannot confirm was
merged, and `ship` refuses on anything but a clean `main` whose CI is green.

## What is pinned

- **`net10.0`**, pinned by `global.json` to an SDK floor of 10.0.302 with
  `rollForward: latestMinor`.
- **No `ProjectReference` that leaves this repository, ever**, and none
  between `Fettler` and `burler` in either direction. The wire format is
  the whole contract between those two, and each side carries its own copy
  of the shapes that cross it, proven equivalent by a protocol test on each
  side rather than by a shared assembly.
- **The version is not in a file.** `Directory.Build.props` carries a
  development default; a release takes its number from the git tag, which
  is why there is no version-bump commit and no release branch to cut and
  merge back.
- **A `PackageReference` only from a tested allowlist.** Each entry must
  meet all of: a permissive licence (MIT, Apache-2.0, BSD) and never
  copyleft; pure managed with no native assets, because `fettle` publishes
  self-contained and single-file per OS; a minimal transitive closure; and
  active maintenance. The allowlist today is one package, **PdfPig**
  (Apache-2.0, pure managed, zero transitive dependencies), and it is
  there because the assistant's own reader is denied outright — which
  makes `fettle` the only reader, so a developer holding a PDF would
  otherwise be stranded. **Nothing else built here uses it** — `burler`
  included — and `fettle` ships as its own archive carrying copies of
  `NOTICE` and `LICENSE`, so the Apache-2.0 and MIT obligations reach
  whoever downloaded it rather than stopping at the repository.
- **The allowlist is tested from outside.** `FrontEndTests` reads the
  built assemblies' references and names all seven PdfPig assemblies
  explicitly, so adding a package means changing a test on purpose rather
  than watching one stop failing. A constraint that is not tested lasts
  until somebody is in a hurry.
- **The MCP protocol is hand-rolled.** A preview-versioned SDK would be
  this tool's most volatile dependency sitting on its least stable
  layer; a few hundred lines is the cheaper exposure.

## Registering it with an MCP client

`serve` chooses the MCP front end. Selection by the name the executable
was invoked under is the Unix convention and rests on a mechanism not in
ordinary use on Windows, so it is a subcommand instead.

```json
{
  "mcpServers": {
    "fettler": {
      "command": "fettle",
      "args": ["serve"]
    }
  }
}
```

**No root or config flag appears there, and none can.** The server learns
its trees at launch, from the `.fettler.json` in the folder it starts in,
before the model has said anything — and no tool schema carries a way to
name a different one. **Every call then re-reads that same file**,
overlay included, so an edit binds on the very next request: a granted
task appears, a revoked permission takes, and no restart is involved. A
malformed edit refuses every request, naming the fault, until it is
fixed --glob never falling back to the last good reading, since a boundary
held wider than the file states is the one direction this must never
fail in. Deleting the file refuses too, rather than searching again:
the file found at launch is the file, for the life of the server.

**A tree declares its own boundary**, so nobody types one onto every
command. `fettle` takes the nearest `.fettler.json` at or above the
current directory:

```json
{
  "trees": {
    "work": { "path": "." },
    "requirements": {
      "path": "../shoddy-requirements",
      "can": ["list", "read"],
      "scopes": {
        "backlog":           { "can": ["list", "read", "create", "update", "rename"] },
        "backlog/cancelled": { "can": [] }
      }
    }
  }
}
```

**Its paths are relative to the file, never to whoever is asking** — which
is the whole point of it. Read against the caller's working directory a
relative path names a different tree from every directory; read against
the file's own directory it names one tree from everywhere, so the same
command means the same thing three levels down and the file can be checked
in.

The seven permissions are `list read create update rename delete execute`.
The tree the file sits in gets all but `execute`; **every other tree gets
`list read`** unless `can` says more; **`execute` is never a default
anywhere**. A scope *replaces* what its tree grants rather than adding to
it, so it can take a permission away — and a scope with no `list` is not
there at all as far as `find` and `search` are concerned.

**`--root PATH` grants `list read` and nothing else, with no override.** It
stays because the server has to be launched somehow and ad-hoc reading is
useful; it cannot hand anybody write access, and --glob having no file behind
it --glob it is the one boundary a running server never re-reads. To write to a tree you put a
file in it, which is a deliberate act by a person inside the tree it
governs — and the act a caller cannot perform, because `.fettler.json`
and `.fettler.local.json` are refused by every write path at every
permission level. That refusal has its own outcome, `governed`, and its
own exit code, 11. Two names cover the trees and the tasks alike, since
one file declares both.

The same refusal covers the files that say what the **assistant** may do:
`.mcp.json`, `.claude/settings.json`, `.claude/settings.local.json` and
`.vscode/mcp.json`. No boundary has to be breached to reach one, because
a project's assistant configuration lives in the project - so the file
governing the assistant and a file the assistant may write were the same
file until this existed. A model that can edit its own deny list has no
deny list. Matched on the last two segments, so a bare `settings.json`
and `.vscode/settings.json` stay ordinary; `CLAUDE.md` is left out
because it persuades rather than permits. `setup` still writes all of
them, because it uses plain file IO and resolves no path through the
boundary.

## A write that would put a secret on disk

Refused, with its own outcome `credential` and its own exit code, 12.
**It judges the diff, not the file**: only a credential absent from the
previous content stops a write, so a config that already holds a key
stays editable - which is the difference between a rule people keep and a
rule people switch off.

Two tiers. Structural patterns match credentials whose shape their issuer
fixed: AWS key ids, GitHub and Slack tokens, Stripe live keys, Google API
keys, PEM private-key headers. The second tier is a guess and gated hard
because of it - a secret-shaped name assigned a long, high-entropy,
whitespace-free value that is not a reference, so an empty password and a
value naming an environment variable both pass.

**The refusal never repeats what it found.** A line number and a detector
name only: a message naming the secret would write it into the log and
the transcript, which is worse than the write it was refusing.

**`--allow-credential` is offered by the command line and by nothing
else.** No MCP tool carries it and a test asserts none ever will, for the
same reason `setup` is never offered: deciding a high-entropy string is
not a secret is a judgement about your own file, and a person makes it.

**It is a safety net, not a boundary.** Base64 the value or split the
string and every rule here is defeated, by accident as easily as on
purpose. It stops the ordinary accident of pasting a real key into a
config, not an adversary.

`--config PATH` names a different file and keeps full expressiveness.
`--no-config` turns the search off. Any `--root` **replaces** the file
rather than adding to it, because merging would leave a caller unable to
narrow the boundary. A `.fettler.local.json` beside the checked-in file
*is* merged over it — that is one tree's configuration layered on itself
rather than a caller overriding a tree — and it is gitignored, so a
machine's own trees need not be checked in.

**Declaring nothing at all is a refusal**, not a fallback to the current
directory. A working directory usually sits *above* the tree somebody
meant, so the old fallback silently widened the boundary at exactly the
moment nobody had stated one.

A malformed configuration is refused, never fallen back from, and the old
`roots` shape is refused with the migration named rather than
reinterpreted — a root granted everything, and reading it as a tree would
grant more than the file says.

`fettle roots` answers what is declared, what may be done in each, and
which file said so:

```
$ fettle roots
work          /work/shoddy  (default)
              can: list read create update rename delete
requirements  /work/shoddy-requirements
              can: list read
              backlog  can: list read create update rename
              backlog/cancelled  can: nothing

declared by /work/shoddy/.fettler.json
```

Every path must resolve inside one of them. A move between two trees works
and says that it crossed, because moving a file between two trust domains
is something the caller is entitled to see.

## The disclosure screen, and why there are two executables

**Off unless a tree asks for it.** Most trees never switch it on.

Fettler reads PDF, Excel and Word — exactly the formats regulated
personal data lives in — and everything it returns lands in a
transcript. A tree may declare a `screen`, and then **any detection in a
screened category refuses the whole response**:

```json
{
  "trees": {
    "records": { "path": "../records", "can": ["list", "read"], "screen": ["identifiers", "clinical"] }
  },
  "models": "../screening-models"
}
```

`true` screens all four categories — `identifiers`, `clinical`, `legal`,
`scientific`. A bare list includes; a list of `-` words excludes from the
full set; mixing the two in one list is refused when the file is read,
because `["identifiers", "-scientific"]` has two readings that differ by
two whole categories. A scope's `screen` replaces the tree's, and a scope
that says nothing inherits it.

`phi`, `pii` and `sci` are **refused rather than translated**, with a
message naming what to write instead — `identifiers` for the pattern
tier, `clinical` for model screening of clinical text, `scientific` for
the scientific model. Each names a regulation rather than what runs, and
so promises coverage that only arrives once a model is installed;
translating them silently would keep them alive in configurations
indefinitely, still over-promising.

It is the credential net pointed the other way — that one judges a write
going in and refuses an introduced secret; this judges a payload coming
out and refuses a detected entity. **It judges the payload, not the
file**, so a document holding a record number on one page stays readable
everywhere else, and the refusal carries a category and a count and
never what it found.

**Two tiers.** The first is structural and runs in process with no model
and no new dependency: a social security number, a card that passes
Luhn, a phone number, an email address, and — under their own labels — a
medical record number and a date of birth. **That list is the whole of
`identifiers`, and the whole of what the screen catches out of the
box.** Names, addresses, conditions and free text are the model tier, or
nothing. The one honest sentence: out of the box the screen catches
structured identifiers; everything else is caught only by a model you
install, and the manifest plus `roots` name exactly which model that is.

The second is **`burler`**, a separate program, and the separation is
the interesting part. BERT inference needs ONNX Runtime, which ships
native per-RID binaries; `Fettler.csproj` admits only pure managed
packages so that `fettle` can publish self-contained and single-file,
and `FrontEndTests` asserts that reference set from outside. So the
inference went behind a pipe instead, and **the allowlist did not
change**. Neither project references the other in either direction: the
wire is the whole contract, and a protocol test on each side asserts it
independently.

**No model weights ship, ever.** Quantised, they run to hundreds of MB
against a `fettle` download measured in single-digit MB. Three
categories can take one; `identifiers` never does. A person downloads
what they want and names the directory. **Until that name exists the
first tier is the whole screen** and a clean payload is served; once it
exists, a screened category with no model refuses, because somebody has
now said they expect it to be there. `fettle roots` reports which state
each category is in, naming the checkpoint and revision from the
manifest where a model is installed. Everything
else that can go wrong — a sidecar that will not start, a malformed
answer, an inference that outran its clock — refuses too. There is no
path through it that serves unscreened content.

> **It is a safety net and not a boundary.** Models miss entities, and
> the patterns miss anything written a way they do not expect — a number
> split across a line break defeats every one of them. A clean verdict is
> evidence of absence and never a certificate of it. **FCRA and FERPA are
> not covered categories**, only the structured identifiers in them.

## Is it wired up?

```
fettle doctor
```

Two jobs in one verb: whether Fettler is registered with each assistant
client — Claude Code, Claude Desktop, VS Code Copilot, the GitHub Copilot
agent — at both the global and the repository level, and what on this
machine still lets the boundary be gone round. It never writes, never
prints a secret, and reads only a compiled-in list of well-known
configuration paths. `fettle setup CLIENT --local` writes the wiring;
`--dry-run` shows it first. See [`docs/setup.html`](docs/setup.html).


## Line endings

**Text an edit brings in is rewritten into the file's own line endings.**
A caller composing a replacement cannot be expected to know a file's
endings, and on Windows the shell decides for them — a PowerShell
here-string is CRLF whatever the file is. Splicing that in verbatim leaves
a file disagreeing with itself by one line, which nothing notices until a
version control system does.

**Text already in the file is left exactly as it was**, so a mixed file
read and written back unchanged is still byte-identical. Only what the
edit brings in is made to match.

**`read` says when a file disagrees with itself**, marking it `(MIXED)`
next to the dominant ending, and setting `line_ending_mixed` in the
machine-readable answer. Reporting only the dominant ending is what hid a
stray CRLF here for two commits.

## Arguments it will not guess at

**A flag no verb knows is refused, never ignored.** Ignoring one does not
produce a failure, it produces a *wrong answer*: a flag taking a value also
swallows the argument after it, so a misspelt `--gob "*.cs"` eats the
pattern and the search then runs against the whole tree and reports what it
found with complete confidence.

**A pattern may arrive as bytes rather than as an argument.**
`--pattern-file PATH` and `--pattern-stdin` take one pattern per line —
one line is one string, the same idea a task's `run` array states as one
element per argument. The pattern is the
argument a shell is likeliest to spoil — quotes, backslashes and a leading
`--` all mean something to somebody first — and the damage is silent,
because a mangled pattern does not fail, it matches something else.

**A leading byte-order mark on a stream is taken off.** A BOM is a fact
about a file's encoding, not a character in its content, and it belongs at
the head of a file or nowhere. Windows PowerShell emits one whether asked
to or not; keeping it made a search pattern match nothing and spliced an
invisible character into the middle of a source file. A mark further along
is left alone.


## The verbs, and their exit codes

`find`, `search`, `read`, `write`, `edit`, `replace`, `new`, `mkdir`,
`move`, `copy`, `delete`, `exec`, `extract`, `roots`, `tasks`, `run`,
`batch`, `doctor`, `setup` — and `serve`, which is not an operation.
**The CLI verb and the MCP tool name are the same word**, and both front
ends reach the operations through one dispatcher, so a capability
available to a model and not to a script is unwritable rather than merely
forbidden.

**`read --tail N`** is the end of a file, counted backwards. A log is the
one file read backwards, and without it reaching the end of one costs two
calls — a read to learn the line count, then arithmetic to name a range.
A tool that cannot cheaply answer "what did it just say" sends its caller
to a shell, which is the thing this one exists to make unnecessary. It is
not combinable with `--from`/`--to`: those are a different question, and
answering one of them by precedence is a wrong answer rather than an
error.

**Documents are rendered and never written.** A `.pdf` reads as text page
by page; a `.xlsx`/`.xlsm` as one line per row, sheet by sheet; a
`.docx`/`.docm` as its paragraphs and tables, with its headings kept as
headings. **`search` looks inside all of them by default**, and a hit
carries the page, sheet or heading it was found under, so a result is
somewhere a person can actually turn to rather than a line number into a
rendering nobody can see. `--no-documents` leaves them shut, and either
way the answer says how many were not looked inside: a file that was
never opened is not a file with nothing in it.

The office formats add no dependency at all. Both are a zip full of XML,
reachable with `System.IO.Compression` and `System.Xml`, which are in the
shared framework - so the R1.2 allowlist is still the one package it was.
Only PDF needs PdfPig. What they get is real: **formulas as well as the
values they last worked out to**, because a search for `VLOOKUP` is a
question about how a sheet works and would find nothing in the results,
and **dates rendered as dates**, because Excel stores one as a day count
and a workbook full of them is otherwise a workbook nobody can search.

**And they are refused by `write`, `edit` and `replace`, by name.** What
comes back for one is a rendering, not the file - the sheets, the
formulas, the tables and every piece of structure are left behind in the
making of it. Writing that text back would not edit the document, it
would replace it with a fraction of itself still wearing a name that says
it is a document. The refusal sits in the one function every verb that
writes text arrives at, so there is no verb to forget. `extract` is
deliberately not caught by it: that writes a document's own bytes.

**An image, an archive and a lone `.gz` are refused by the same three
verbs**, for the plainer reason that they are not text at all. `edit` and
`replace` never needed telling — both read the file as text before they
change anything, and R4.5 stops them there — but `write --overwrite`
reads only to *learn* the encoding and carries on when that read fails,
so it was the one route by which a line of text could take a PNG's place.
`move`, `copy` and `delete` do not inspect content and are the way to
shift one of these. A notebook is deliberately not in this list: a
`.ipynb` **is** JSON, so text written at one is the file, however much
`read` renders it as cells.

**Archives are read and never written.** A `.zip`, `.tar`, `.tar.gz` or
`.tgz` reads as its manifest — every member, its size, and whether it
carries the execute bit — and `--member NAME` reads one entry out of it,
decoded by the ordinary rules, without unpacking anything. A lone `.gz` is
one file wearing a coat and reads as what is underneath. Neither
`System.IO.Compression` nor `System.Formats.Tar` is a package: both are in
the shared framework, so the R1.2 allowlist is untouched.

**`extract ARCHIVE --into DIR`** is the one that writes, and it is refused
*whole* or not at all. Every member resolves through the boundary before a
byte is written, so zip slip is an ordinary outside-root refusal; a member
escaping only the *destination* (`out/../elsewhere.txt`, which the
boundary is perfectly happy with) is refused too; a link member is refused
rather than skipped, because a skip produces an extraction that looks
complete and is not; and the executable bit is carried, which is R6.9 and
the reason the release tars are cut on a Linux runner. Producing an
archive is deliberately not here: that is a build output, and building is
what a declared task and `run` are for.

**`setup` is the one deliberate exception**, and a test asserts it stays
one. It writes the client configuration files — including the one naming
the command that launches the server — so a model holding it could point
its own boundary at a different binary, or grant back the permissions the
deny list removes. Parity is a rule about capability, not about reach:
scaffolding a machine is a person's act, performed once, in a terminal.
`doctor` *is* offered, because a model diagnosing its own wiring beats
asking somebody to open a terminal for it.

| Code | Meaning |
|---|---|
| 0 | ok |
| 1 | an unexpected internal fault |
| 2 | invalid — the request did not make sense |
| 3 | not-found |
| 4 | outside-root |
| 5 | target-exists |
| 6 | stale — the file changed since it was read |
| 7 | conflict — ambiguous or overlapping edits |
| 8 | refused — Fettler declined, for a stated reason |
| 9 | **denied — the operating system declined** |
| 10 | timed-out |
| 11 | **governed — the path names a file that says what this tool may do** |
| 12 | **credential - the write would have ADDED a secret the file did not already carry** |

**8 and 9 are deliberately different.** Refused means Fettler declined;
denied means the platform did, for an operation Fettler was perfectly
willing to perform — a sandbox, a restricted account, a read-only
attribute, a file another process holds open. Collapsing them sends a
caller to debug a boundary that is working.

### Worked twins

The whole point of the CLI is that a `.ps1` and a `.sh` become
launchers, and neither redirects stderr anywhere:

```powershell
# scripts/list-types.ps1
$out = fettle search "^public sealed" --glob "src/**/*.cs" --json
if ($LASTEXITCODE -ne 0) { throw "search failed: $out" }
$out
```

```sh
# scripts/list-types.sh
out=$(fettle search '^public sealed' --glob 'src/**/*.cs' --json) || {
  printf '%s\n' "$out" >&2; exit 1;
}
printf '%s\n' "$out"
```

In machine-readable mode the complete result — **failures included** —
is on stdout. That is not a preference: Windows PowerShell 5.1 turns a
native program's redirected stderr into `NativeCommandError` records and
sets `$?` false even on exit code 0, so any design needing `2>&1` to
retrieve an answer is broken on the primary development platform.

## Declaring tasks

`run` executes only what the `.fettler.json` declares, and never composes
a shell command:

```json
{
  "trees": {
    "work": { "path": ".", "can": ["list","read","create","update","rename","delete","execute"] }
  },
  "tasks": {
    "build": { "run": "pwsh -File scripts/build.ps1 build", "cwd": "." },
    "test":  { "run": "pwsh -File scripts/build.ps1 test",  "cwd": "." },
    "sign":  { "run": "signtool sign /f \"C:\\keys\\my cert.pfx\" bin/app.exe" }
  }
}
```

**The whole grammar:** whitespace separates arguments; a double-quoted
span is one argument, and the quotes are removed; two double quotes
inside a quoted span are one literal quote; **a backslash is never an
escape**, so a Windows path is written once rather than doubled. Nothing
else is special — no single quotes, no variables, no globbing, no
redirection, no operators — and an unclosed quote is refused. The string
is split once and the program is launched with the resulting list.

**A task takes no arguments from its caller.** `run` accepts a name and a
timeout and nothing else; an extra word is refused rather than dropped,
on the command line and over MCP alike. What runs is exactly what the
file says, and a variant is a second declared task.

`cwd` is optional and defaults to the top of the default tree; it is
checked for containment like any other path. **A relative declared path
is written with forward slashes** — the git convention — and a backslash
in one is refused, since on macOS and Linux it is an ordinary character
in a name rather than a separator. An absolute path names one machine
either way and is left alone.

**That declaration is trusted input, and this says so out loud.**
Containment guards paths; it does not and cannot guard a command list,
which is read from a file inside the very root Fettler was pointed at.
Whoever can write it chooses what a caller — a model included — can
execute. The alternative is an allowlist maintained outside the
repository, which puts the declaration somewhere the repository's own
contributors cannot change alongside the build it belongs to. Point
Fettler at a tree you trust.

## What it deliberately does not do

Version control. Networked transport. A trash or undo store. Binary
editing. Language-aware editing. Permissions beyond the one bit git
tracks. Creating links. Setting a timestamp to a value the caller names.

Each of those is refused in the requirements with the reasoning
recorded, so a later reader does not re-open the question by accident.

## Where the name comes from

A **fettler** kept the carding engines in order — stripping matted fibre
and dirt out of the card clothing, cleaning, setting, repairing. He made
no cloth. His whole job was keeping the machines that made cloth in a
state where they could, which is this tool's relationship to everything
around it: it compiles nothing, runs nothing, and produces no program of
its own. *To fettle* is the plain English for it — to put right, to make
ready — and it survives in ordinary use as *in fine fettle*, so the name
carries its own meaning without a glossary.
