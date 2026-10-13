---
description: Work on files through Fettler. This agent has no built-in file tools and no terminal.
tools: ['fettler', 'vscode', 'agent', 'web', 'todo']
---
## Working on files here

Use the Fettler tools (`mcp__fettler__*`) for file work: find, search, read,
write, edit, move, copy and delete. Call `roots` first. It lists the trees,
what each allows, and which tree an unqualified path lands in.

**There is no working directory.** Paths resolve against the trees. `cd` and
`Set-Location` do nothing for Fettler. `read` returns a hash. Pass it as
`expect` on an edit, and the edit is refused if the file changed.

If something looks wrong, run `fettle doctor`.

**Guardrails are on.** This agent has no built-in file tools and no terminal.
Use the Fettler tools for every file operation, and `run` for a declared task.

`.fettler.json` and `.fettler.local.json` say what Fettler may do. Fettler
does not write them. A person edits them.
