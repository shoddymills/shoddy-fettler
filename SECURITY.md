# Security Policy

Fettler is a bounded file, search and edit tool for AI assistants and
scripts. **Its whole purpose is to be a boundary**, so a way around that
boundary is a security issue rather than a bug, and worth reporting as one.

Particularly interesting:

* A path that resolves outside every declared tree and is not refused.
* A write to `.fettler.json` or `.fettler.local.json` that succeeds. Those
  are the files that say what the tool may do, and it must never write them,
  at any permission level.
* A scope or tree that grants more than it declares.
* A task that runs without `execute` granted, or that takes an argument from
  its caller.
* A payload leaving a screened scope without being screened.
* A credential entering a file through a write that should have refused it.

## Reporting a vulnerability

Please **do not** open a public issue for security problems.

Report suspected vulnerabilities privately to **stephen@shoddymills.dev**, or use
GitHub's [private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
("Report a vulnerability" under the Security tab).

Please include:

* A description of the issue and its impact.
* Steps to reproduce. A `.fettler.json` and the command that got past it is
  ideal.
* The affected version or commit (`fettle --version`).

You can expect an acknowledgement within a few days. Once a fix is available,
we will coordinate disclosure and credit reporters who wish to be named.

## What is out of scope

**Fettler bounds itself, and nothing else.** A shell command, an allowed
built-in editor, or a person with the file open reads and writes as normal.
The boundary sits on Fettler's own answers, and only there. That is the
design rather than a hole, and
[what it defends against](https://shoddymills.github.io/shoddy-fettler/#threat)
draws the line explicitly. `fettle doctor` is what finds the ways round on a
given machine.

**Model weights are not distributed by this project.** The screening models
`burler` runs are downloaded and placed by whoever uses them. Their
provenance is their own.

## Supported versions

Only the latest release is supported. Fixes go forward in a new patch
version. A published tag is never moved.
