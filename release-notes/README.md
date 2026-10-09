# Release notes

One file per release, named for its tag: `v1.0.0` becomes `v1.0.0.md`.

**The whole file becomes the body of the GitHub Release.** Do not repeat the
version as a heading. The release is already titled with it.

**Write it on the branch, and commit it before the tag is pushed.** The
Release workflow checks out the *tag* and reads only what that commit
contains, so notes written afterwards are invisible to it.
`fettler-ship.ps1` refuses to tag without the file.

## What goes in one

Lead with what a user notices, in this order:

1. **Breaking changes.** First and unmissable, with what to do about each.
2. **Fixed bugs that bit someone.** Say what went wrong, not just what was
   changed.
3. **New behaviour.** New verbs, flags, permissions, screen categories.
4. **Everything else.** Briefly, or not at all.

Skip anything a user cannot observe. A refactor with no behavioural change
does not belong here. That is what the commit history is for.

## Two Fettler-specific things worth calling out

- **A new or changed exit code.** Scripts branch on these, so a change is
  breaking even when nothing else is. `fettler-verify-errors.js` will not
  let one ship undocumented, but the notes are where somebody finds out.
- **Anything touching the boundary.** A new permission, a change to what a
  scope grants, a new rule file. People have written `.fettler.json` against
  the old behaviour.

## Example

```markdown
## Breaking

- `fettle exec` now refuses a path outside a tree with exit `4`
  (`outside-root`) rather than exit `8` (`refused`). Scripts branching on
  `8` for this case need updating.

## Fixed

- A read of a file whose last line had no newline reported the wrong line
  count, so an `--insert-after` aimed at the end of the file landed one line
  early.

## New

- `read` accepts `--tail N`, for a log whose length you do not know.
```
