<!-- fettler:begin -->
## Working on files here

Use the Fettler tools (`mcp__fettler__*`) for every file operation: find,
search, read, write, edit, move, copy, delete. They are the route, not an
option, and the built-in Read, Write, Edit, NotebookEdit, Grep and Glob are
denied so that habit cannot quietly take over.

**There is no working directory.** Paths resolve against declared trees, so
`cd` and `Set-Location` do nothing for Fettler and reaching for one is a sign
the wrong tool is being used. Ask `roots` first: it says which trees are open,
what may be done in each, and which one an unqualified path lands in.

**A tree may be read-only**, and a scope inside it may grant more or less than
the tree does. Running a declared task needs `execute`, which is never granted
by default.

`.fettler.json` and `.fettler.local.json` say what this tool may do - the
trees it may touch and the tasks it may run - and it does not write them.
A person edits those.

Run `fettle doctor` if anything here looks wrong.
<!-- fettler:end -->
