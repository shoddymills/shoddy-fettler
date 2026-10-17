

<!-- fettler:begin -->
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

If something looks wrong, run `fettle doctor`.

**Guardrails are on.** The built-in Read, Write, Edit, NotebookEdit, Grep and
Glob are denied. Use the Fettler tools for every file operation.

**A tree may be read-only.** A scope inside a tree may allow more or less
than the tree does. Running a declared task needs `execute`, which is never
a default.

`.fettler.json` and `.fettler.local.json` say what Fettler may do. Fettler
does not write them. A person edits them.
<!-- fettler:end -->
