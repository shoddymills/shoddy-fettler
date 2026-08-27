# Third-party notices

This repository distributes three programs. This file is the
developer-facing summary of what rides along inside each of them.

**[`NOTICE`](NOTICE) is the file that matters legally**, and it is the one
that travels: `scripts/fettler-build.ps1 publish` copies `NOTICE` and `LICENSE` into every
archive, because an obligation that stops at the repository has not reached
the person who downloaded a release. This file is a convenience for anyone
reading the source; it does not ship.

## What is in each archive

| Package | Version | Licence | Ships in | Why |
|---|---|---|---|---|
| [PdfPig](https://github.com/UglyToad/PdfPig) | 0.1.15 | Apache-2.0 | `fettle` | reads PDF documents as text |
| [Microsoft.ML.OnnxRuntime](https://github.com/microsoft/onnxruntime) | 1.20.1 | MIT | `burler` | runs the screening models |
| [Microsoft.ML.Tokenizers](https://github.com/dotnet/machinelearning) | 1.0.0 | MIT | `burler` | the WordPiece tokenizer a BERT-family model needs |
| [Microsoft.SqlServer.TransactSql.ScriptDom](https://github.com/microsoft/SqlScriptDOM) | 180.59.2 | MIT | `pick` | parses the one SELECT the query gate accepts |
| [Microsoft.Data.SqlClient](https://github.com/dotnet/SqlClient) | 7.0.2 | MIT | `pick` | the connection to SQL Server |

PdfPig has no transitive dependencies at all; the two `burler` packages
bring only their own native runtime assets; ScriptDom brings nothing.
**Microsoft.Data.SqlClient is the one wide closure in the repository** -
the Microsoft.IdentityModel and Microsoft.Extensions families,
`Microsoft.SqlServer.Server`, `Microsoft.Bcl.Cryptography` and a handful
of `System.*` packages - accepted because a first-party SQL Server driver
has no slimmer substitute, and every package in it is Microsoft-authored
and MIT. Its native Windows SNI library is present in the dependency
graph and **never used and never shipped**: `pick` pins the managed
network path on all three operating systems, and its archive carries the
single executable alone.

## The allowlists, and why they are short

`pick` carries its own allowlist in `Picker.csproj`, in the same form and
under the same tests as `fettle`'s: today it admits exactly the two
packages in the table above, and its csproj comment records the
managed-SNI pin and the fallback that would ever relax it.

`fettle` publishes **self-contained and single-file per operating system**, so
a package with native assets does not merely complicate the build - it
produces an archive that is missing pieces. Every entry admitted to
`Fettler.csproj` must meet all four of:

- a permissive licence (MIT, Apache-2.0, BSD) and never copyleft;
- pure managed, with no native assets;
- a minimal transitive closure;
- actively maintained.

**The allowlist is enforced from outside the code**, in two places that fail
the push rather than the release:

- `FrontEndTests` reads the *built assemblies'* references, so a package that
  arrives transitively is caught as surely as one written into the csproj.
- `ci.yml` reads the *project files*, so a reference added and not yet built
  is caught in the same push that adds it.

Adding a package therefore means changing a test on purpose, rather than
watching one quietly stop failing. That is the point.

## Why `burler` exists at all

ONNX Runtime ships native per-RID binaries, which the allowlist above
disqualifies. Rather than widen a rule that has a stated reason, the
inference lives in a **second executable** that `fettle` talks to over a
pipe. Neither program references the other, in either direction; the wire
format is the whole contract between them, and each side carries its own
copy of the shapes that cross it, proven equivalent by a protocol test on
each side rather than by a shared assembly.

## Model weights are never distributed

The screening models `burler` runs are **not in this repository and not in
any archive**. Four quantised models run about 400 MB against a `fettle`
download measured in single-digit MB, so a person places them in a models
directory named in `.fettler.local.json`, and `burler` refuses to start a
category whose model or manifest is missing.

Their licences and provenance live in that directory's own manifest rather
than here, because a licence obligation binds on redistribution and none
occurs.
