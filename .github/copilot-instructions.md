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
