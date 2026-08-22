// MAINTAINER TOOL - read-only. Every outcome and exit code the tool can
// raise is documented on the page that promises to list them.
//
//   node scripts/fettler-verify-errors.js
//
// R3.5 is the clause this serves: each distinct failure class gets its own
// exit code so a script can branch on it without parsing text. That promise
// is only worth anything if the numbers are WRITTEN DOWN somewhere a caller
// can find, and stay correct. A new outcome added to the enum and left out
// of the table is a code somebody's script will meet and not recognise.
//
// GROUND TRUTH IS THE SOURCE, rebuilt on every run and never a list kept
// here. Two files, joined:
//
//   ExitCodes.cs   `public const int Name = N;`     - the numbers
//   ExitCodes.cs   `Outcome.Name => "spelling",`    - the JSON names
//
// The join is what makes this worth running. Either half alone looks fine
// when they disagree: a constant renamed without its NameOf entry, or an
// outcome whose name is documented against the wrong number, both pass a
// check that only reads one file.
//
// Exit 0 and "EVERY OUTCOME IS DOCUMENTED" is the pass.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");
const read = f => fs.readFileSync(path.join(root, f), "utf8");

let bad = 0;
const fail = msg => { console.log(msg); bad++; };

const exitCodes = read("Fettler/Cli/ExitCodes.cs");
const outcomes = read("Fettler/Core/Outcome.cs");

// ---- the numbers ----
const numberOf = {};
for (const m of exitCodes.matchAll(/public const int (\w+)\s*=\s*(\d+);/g))
  numberOf[m[1]] = m[2];

if (!Object.keys(numberOf).length) {
  console.log("verify-errors: no exit codes found in ExitCodes.cs");
  process.exit(1);
}

// ---- the JSON spellings ----
// Taken from NameOf, which is the map a caller reading --json actually
// meets. `_ => "fault"` is the default arm and carries no Outcome member,
// so it is picked up separately below.
const spellingOf = {};
for (const m of exitCodes.matchAll(/Outcome\.(\w+)\s*=>\s*"([a-z-]+)"/g))
  spellingOf[m[1]] = m[2];

// ---- every member of the enum is accounted for ----
// An outcome that reaches neither Of() nor NameOf() falls to the default
// arm and is reported as `fault`, which tells a caller nothing and is
// almost never what was meant.
for (const m of outcomes.matchAll(/^\s{4}(\w+) = \d+,/gm)) {
  const member = m[1];
  if (!(member in spellingOf))
    fail("Outcome." + member + " has no entry in ExitCodes.NameOf - it would report as 'fault'");
  if (!(member in numberOf))
    fail("Outcome." + member + " has no exit code in ExitCodes - it would report as " + numberOf.Fault);
}

// ---- the table, wherever the site keeps it ----
// The page is NOT named here. It has moved once already, and a check that
// pins content to a filename turns a docs reorganisation into a build
// failure - which is how a gate ends up being edited to suit the pages
// instead of the pages being held to the gate. So: find the page carrying
// rows of this shape, and insist there is exactly ONE. Two copies of the
// table is itself the fault worth catching, because they drift apart in
// silence and a reader believes whichever one they landed on.
//
// The row shape is fixed and asserted here, so a table restyled into a
// different shape fails loudly rather than silently matching nothing and
// passing.
//
// The outcome cell is EITHER a <code>spelling</code> OR an em dash. The dash
// is not a gap in the documentation: exit code 1 is the default arm, which
// belongs to no Outcome member, so there is no outcome word to put there and
// printing one would invent a member that does not exist.
const ROW = /<td>(\d\d?)<\/td><td>(?:<code>([a-z-]+)<\/code>|&mdash;)<\/td>/g;

const carriers = fs.readdirSync(path.join(root, "docs"))
  .filter(f => f.endsWith(".html"))
  .sort()
  .map(f => ({ page: "docs/" + f, rows: [...read("docs/" + f).matchAll(ROW)] }))
  .filter(c => c.rows.length);

if (!carriers.length) {
  console.log("no page in docs/ carries an exit-code table - restyled, or dropped?");
  process.exit(1);
}

if (carriers.length > 1)
  fail("the exit-code table is on more than one page (" +
       carriers.map(c => c.page).join(", ") + ") - two copies drift apart in silence");

const page = carriers[0].page;
const rows = carriers[0].rows;

const documented = new Set(rows.map(m => m[1] + " " + (m[2] || "")));
const documentedNumbers = new Set(rows.map(m => m[1]));

for (const [member, spelling] of Object.entries(spellingOf)) {
  const number = numberOf[member];
  if (number === undefined) continue;   // already reported above
  if (!documented.has(number + " " + spelling))
    fail(page + ": exit code " + number + " (" + spelling + ") is not in the table");
}

// The default arm still has to appear, because a caller can certainly meet
// it - only its outcome cell is allowed to be a dash.
if (!documentedNumbers.has(numberOf.Fault))
  fail(page + ": exit code " + numberOf.Fault + " (the internal fault) is not in the table");

// ---- and nothing is documented that the tool cannot produce ----
// The reverse direction, which is the half a hand-written check always
// forgets: a code retired from the source and left on the page sends a
// reader looking for a failure that can no longer happen.
const real = new Set(Object.entries(spellingOf)
  .filter(([m]) => numberOf[m] !== undefined)
  .map(([m, s]) => numberOf[m] + " " + s));
real.add(numberOf.Fault + " ");

for (const row of documented)
  if (!real.has(row))
    fail(page + ": the table documents '" + row.trim() + "', which the tool cannot produce");


if (bad === 0) {
  console.log("EVERY OUTCOME IS DOCUMENTED (" + real.size + " exit codes, in " + page + ")");
  process.exit(0);
}
console.log("\n" + bad + " problem(s).");
process.exit(1);
