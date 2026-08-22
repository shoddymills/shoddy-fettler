# Contributing to Fettler

Thanks for your interest in improving Fettler. Contributions of all sizes —
bug reports, docs, tests, and tool work — are welcome.

## License of contributions

Fettler is released under the [MIT License](LICENSE). By submitting a
contribution, you agree that your contribution is licensed under the same
MIT License, and you certify that you have the right to submit it under
that license (see the [Developer Certificate of Origin](https://developercertificate.org/)).

## How to contribute

1. Open an issue to discuss anything non-trivial before you start.
2. Fork, then cut a branch:
   ```powershell
   ./scripts/fettler-branch.ps1 feature NAME
   ```
3. Make your change and commit it:
   ```powershell
   ./scripts/fettler-checkin.ps1 -Message "what changed"
   ```
   It pushes nothing, refuses on `main`, and adds any new `.sh` with its
   executable bit already set.
4. Build, test and check locally:
   ```powershell
   ./scripts/fettler-build.ps1 test     # Fettler.Tests AND burler.Tests
   ./scripts/fettler-build.ps1 check    # twins, permissions, docs, errors
   ```
   CI runs exactly these two commands, so a green pair here means a green
   pipeline there.
5. Open the pull request:
   ```powershell
   ./scripts/fettler-pr.ps1
   ```

Every script has a `.sh` twin taking the same arguments, flags included.
**Run the `.ps1` on Windows** - it is the half that actually ships there, and
the twin that goes unexercised is the twin that rots.

## Three constraints that are not style preferences

Each is asserted by a test *and* by CI, so breaking one fails the push. If
your change needs to relax one, say so in the issue first — each was written
down after the thing it prevents actually happened.

- **No `ProjectReference` that leaves this repository**, and none between
  `Fettler` and `burler` in either direction. The wire format is the whole
  contract between those two, and each side carries its own copy of the shapes
  that cross it, proven equivalent by a protocol test on each side rather than
  by a shared assembly.
- **A `PackageReference` only from the allowlist** in `Fettler.csproj` — today
  `PdfPig` alone. `fettle` publishes self-contained and single-file per OS, so
  a package with native assets produces an archive that is missing pieces.
  Adding one means editing the csproj comment, `FrontEndTests.Allowed`,
  `ci.yml`'s allowlist, `THIRD-PARTY-NOTICES.md`, and `NOTICE` if its licence
  asks.
- **`Fettler.Core` may not name `System.Text.Json`, `System.Console` or an
  exit code.** A core that knows about a protocol has already stopped being
  one.

## A few things worth knowing

- **Every script ships twice**, `.ps1` and `.sh`, and the two must offer the
  same verbs with the same spellings — flags included. `fettler-verify-twins.js`
  checks it, because the twin nobody runs is the twin that rots.
- **Scripts are prefixed `fettler-`, and no task or script is named with a
  bare git word** — the committing task is `checkin`, not `commit`. An
  assistant's permission layer reads these names, and a bare git word can be
  refused as if it were the raw command it resembles. Keep the prefix when
  adding a script or a declared task.
- **Scripts need their executable bit set in git's index.** `./scripts/fettler-build.ps1
  check` stages the fix for you; a filesystem `chmod` will not do, because a
  checkout with `core.filemode=false` makes one invisible to git.

## Documentation

The site under `docs/` is checked against the source: `fettler-verify-docs.js`
asserts that every internal link and fragment resolves, that every page
carries the same nav bar, and that the disclosure screen's section names every
category the code has. `fettler-verify-errors.js` asserts that every outcome and exit
code the tool can raise appears in the table that promises to list them.

**Neither check names the page it looks on.** Both find it - the page with
the screen section, the page with the exit-code table - and fail if there is
more than one, because two copies of a table drift apart in silence. So
moving a section between pages is a documentation decision, not a build
failure, and a gate is never edited to suit the pages.

A new page means adding it to the nav on every other page — that is what the
nav check is for.

## Code of Conduct

This project has adopted a [Code of Conduct](CODE_OF_CONDUCT.md). By
participating, you are expected to uphold it.
