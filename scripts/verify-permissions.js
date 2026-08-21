// MAINTAINER TOOL - checks (and fixes) the executable bit on every tracked
// script, using git's own index rather than the filesystem.
//
//   node scripts/verify-permissions.js            check, and FIX what is wrong
//   node scripts/verify-permissions.js --check    check only, and fail if it is
//
// TWO MODES, because the two callers want opposite things.
//
// Locally, fixing is the point: reporting a wrong bit and making the reader
// go and correct it by hand is how four of them once shipped at once. So the
// bare form stages the correction and exits 0 - it has left the tree right.
//
// In CI there is nothing to fix. A runner's index is thrown away at the end
// of the job, so a fixing run there would repair the bit, print a cheerful
// line, exit 0, and let the fault sail through to the release that honors it
// literally. --check is therefore the CI form: it changes nothing and exits 1
// on the first wrong bit. A gate that repairs the evidence is not a gate.
//
// A script committed without +x runs fine on Windows, where NTFS has no
// execute bit to check, and only fails once something honors the bit
// literally - the Linux CI runner, WSL, a Mac. v1.8.0 shipped four scripts
// this way; one of them broke the Release workflow (mills/devils-dust/build.sh,
// "Permission denied", exit 126) before the rest were found by hand.
//
// Finds every tracked file that opens with a shebang line and is missing
// +x, then fixes it directly in the index: `git update-index --chmod=+x`,
// not a filesystem chmod, because a checkout with core.filemode=false (the
// default on Windows) makes a raw chmod invisible to git regardless of
// what the filesystem actually did.
//
// ALL EXECUTABLE BITS OK is the pass (nothing to fix). FIXED N EXECUTABLE
// BIT(S) means the correction is now staged - `git status` will show it;
// commit it like any other change. Either way this exits 0: once it has
// run, the tree is correct.

const { execSync } = require("child_process");
const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");
const checkOnly = process.argv.includes("--check");

const lines = execSync("git ls-files -s", { cwd: root, encoding: "utf8" })
  .trim().split("\n").filter(Boolean);
const fixed = [];

for (const line of lines) {
  const m = line.match(/^(\d+) [0-9a-f]+ \d+\t(.+)$/);
  if (!m) continue;
  const [, mode, file] = m;
  if (mode === "120000") continue; // symlink - no exec bit to check
  let head;
  try {
    const fd = fs.openSync(path.join(root, file), "r");
    const buf = Buffer.alloc(2);
    fs.readSync(fd, buf, 0, 2, 0);
    fs.closeSync(fd);
    head = buf.toString("utf8");
  } catch { continue; } // unreadable in the working tree - not this check's problem
  if (head !== "#!" || mode === "100755") continue;
  if (!checkOnly) execSync(`git update-index --chmod=+x -- "${file}"`, { cwd: root });
  fixed.push(file);
}

if (fixed.length === 0) {
  console.log("ALL EXECUTABLE BITS OK");
  process.exit(0);
}

if (checkOnly) {
  console.log(fixed.length + " SCRIPT(S) MISSING THE EXECUTABLE BIT:");
  for (const f of fixed) console.log(" - " + f);
  console.log("\nRun `node scripts/verify-permissions.js` (no --check) to stage the fix,");
  console.log("then commit it. A raw filesystem chmod will not do: a checkout with");
  console.log("core.filemode=false, the default on Windows, makes one invisible to git.");
  process.exit(1);
}

console.log("FIXED " + fixed.length + " EXECUTABLE BIT(S):");
for (const f of fixed) console.log(" - " + f);
console.log("\nThe correction is STAGED, not committed - `git status` will show it.");
process.exit(0);
