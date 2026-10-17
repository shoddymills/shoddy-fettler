---
description: Work on files through Fettler. This agent has no built-in file tools and no terminal.
tools: ['fettler', 'vscode', 'agent', 'web', 'todo']
---
## Working on files here

Use the Fettler tools (`mcp__fettler__*`) for file work: find, search, read,
write, edit, move, copy and delete. Call `roots` first. It lists the trees,
what each allows, and which tree an unqualified path lands in.

**There is no working directory.** Paths resolve against the trees. `cd` and
`Set-Location` do nothing for Fettler.

**Do several things in one call.** `read` takes several paths, `search` takes
several patterns, `edit` takes edits to several files, and `batch` runs a
list of file operations in order. Send calls that do not need each other's
answers together, in one turn, not one after another.

**Feed each answer into the next call.** `read` returns a hash. Pass it as
`expect` on an edit, and the edit is refused if the file changed. A search
hit gives the path and line to read or edit. An answer that stops short
gives `next`. Pass it back as `after`, or as `from` and `column` for a read.

**Every answer stops at 40,000 bytes.** Read the end of a long log with
`read` and `tail`.

**A subagent works under the same boundary.** Do not send one to do what a
refusal stopped. Ask the person instead.

If something looks wrong, run `fettle doctor`.

**Guardrails are on.** This agent has no built-in file tools and no terminal.
Use the Fettler tools for every file operation, and `run` for a declared task.

`.fettler.json` and `.fettler.local.json` say what Fettler may do. Fettler
does not write them. A person edits them.
