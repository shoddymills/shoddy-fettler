

<!-- fettler:begin -->
## Working on files here

Use the Fettler tools (`mcp__fettler__*`) for file work: find, search, read,
write, edit, move, copy and delete. Call `roots` first. It lists the trees,
what each allows, and which tree an unqualified path lands in.

**There is no working directory.** Paths resolve against the trees. `cd` and
`Set-Location` do nothing for Fettler. `read` returns a hash. Pass it as
`expect` on an edit, and the edit is refused if the file changed.

**Every answer stops at 40,000 characters** and says what it left out. Read
the end of a long log with `read` and `tail`.

If something looks wrong, run `fettle doctor`.

**Guardrails are on.** The built-in Read, Write, Edit, NotebookEdit, Grep and
Glob are denied. Use the Fettler tools for every file operation.

**A tree may be read-only.** A scope inside a tree may allow more or less
than the tree does. Running a declared task needs `execute`, which is never
a default.

`.fettler.json` and `.fettler.local.json` say what Fettler may do. Fettler
does not write them. A person edits them.
<!-- fettler:end -->
