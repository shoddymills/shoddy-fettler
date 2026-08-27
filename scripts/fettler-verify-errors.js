// MAINTAINER TOOL - read-only. Every outcome and exit code the tools can
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
// here. Two files per program, joined:
//
//   ExitCodes.cs   `public const int Name = N;`     - the numbers
//   ExitCodes.cs   `Outcome.Name => "spelling",`    - the JSON names
//
// The join is what makes this worth running. Either half alone looks fine
// when they disagree: a constant renamed without its NameOf entry, or an
// outcome whose name is documented against the wrong number, both pass a
// check that only reads one file.
//
// TWO PROGRAMS answer to this check, each documented in its own lane - the
// site keeps the fettle and pick pages isolated on purpose, so a pick
// reader learns what exit 13 means without leaving the pick lane. Two
// tables is an invitation to drift, which is exactly why BOTH are held to
// the source row by row here, and why the alignment is asserted from the
// source rather than from either page: pick's stated rule is "fettle's
// numbers where the words match" - a script driving both tools branches
// one way - and a spelling the two programs share must carry the same
// number in each, because reusing a number for a different word would
// break the very scripts the alignment is for.
//
// Exit 0 and "EVERY OUTCOME IS DOCUMENTED" is the pass.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");
const read = f => fs.readFileSync(path.join(root, f), "utf8");

let bad = 0;
const fail = msg => { console.log(msg); bad++; };

// ---- one program's surface: numbers, spellings, and the enum honoured ----
// `_ => "fault"` is the default arm and carries no Outcome member, so it is
// picked up separately below. An outcome that reaches neither Of() nor
// NameOf() falls to that arm and is reported as `fault`, which tells a
// caller nothing and is almost never what was meant.
function surface(exitCodesFile, outcomeFile) {
  const exitCodes = read(exitCodesFile);
  const outcomes = read(outcomeFile);

  const numberOf = {};
  for (const m of exitCodes.matchAll(/public const int (\w+)\s*=\s*(\d+);/g))
    numberOf[m[1]] = m[2];

  if (!Object.keys(numberOf).length)
    fail(exitCodesFile + ": no exit codes found");

  const spellingOf = {};
  for (const m of exitCodes.matchAll(/Outcome\.(\w+)\s*=>\s*"([a-z-]+)"/g))
    spellingOf[m[1]] = m[2];

  for (const m of outcomes.matchAll(/^\s{4}(\w+) = \d+,/gm)) {
    const member = m[1];
    if (!(member in spellingOf))
      fail(outcomeFile + ": Outcome." + member +
           " has no entry in NameOf - it would report as 'fault'");
    if (!(member in numberOf))
      fail(outcomeFile + ": Outcome." + member +
           " has no exit code - it would report as " + numberOf.Fault);
  }

  return { numberOf, spellingOf };
}

const fettler = surface("Fettler/Cli/ExitCodes.cs", "Fettler/Core/Outcome.cs");
const picker = surface("Picker/Cli/ExitCodes.cs", "Picker/Core/Outcome.cs");

// ---- the alignment: fettle's numbers where the words match ----
// Both directions, because either program could be the one that moved.
for (const [member, spelling] of Object.entries(picker.spellingOf)) {
  const number = picker.numberOf[member];
  if (number === undefined) continue;   // already reported above
  const other = Object.entries(fettler.spellingOf)
    .find(([, s]) => s === spelling);
  if (other && fettler.numberOf[other[0]] !== undefined
      && fettler.numberOf[other[0]] !== number)
    fail("'" + spelling + "' is exit code " + fettler.numberOf[other[0]] +
         " in fettle and " + number + " in pick - the alignment the docs " +
         "promise is broken");
}

// ---- the tables, one per program, each found in its own lane ----
// WITHIN a lane the page is NOT named here. It has moved once already, and
// a check that pins content to a filename turns a docs reorganisation into
// a build failure - which is how a gate ends up being edited to suit the
// pages instead of the pages being held to the gate. So per program: find
// the lane's page carrying rows of this shape, and insist there is exactly
// ONE, because two copies inside a lane drift apart in silence.
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
  .map(f => ({ file: f, page: "docs/" + f, rows: [...read("docs/" + f).matchAll(ROW)] }))
  .filter(c => c.rows.length);

const where = [];

for (const [who, s, inLane] of [
  ["fettle", fettler, f => !f.startsWith("pick")],
  ["pick", picker, f => f.startsWith("pick")],
]) {
  const mine = carriers.filter(c => inLane(c.file));

  if (!mine.length) {
    fail("no page in the " + who + " lane carries an exit-code table - restyled, or dropped?");
    continue;
  }

  if (mine.length > 1)
    fail("the " + who + " exit-code table is on more than one page (" +
         mine.map(c => c.page).join(", ") + ") - two copies drift apart in silence");

  const { page, rows } = mine[0];
  const documented = new Set(rows.map(m => m[1] + " " + (m[2] || "")));
  const documentedNumbers = new Set(rows.map(m => m[1]));

  for (const [member, spelling] of Object.entries(s.spellingOf)) {
    const number = s.numberOf[member];
    if (number === undefined) continue;   // already reported above
    if (!documented.has(number + " " + spelling))
      fail(page + ": exit code " + number + " (" + spelling + ", raised by " +
           who + ") is not in the table");
  }

  // The default arm still has to appear, because a caller can certainly
  // meet it - only its outcome cell is allowed to be a dash.
  if (!documentedNumbers.has(s.numberOf.Fault))
    fail(page + ": exit code " + s.numberOf.Fault +
         " (the internal fault) is not in the table");

  // ---- and nothing is documented that this program cannot produce ----
  // The reverse direction, which is the half a hand-written check always
  // forgets: a code retired from the source and left on the page sends a
  // reader looking for a failure that can no longer happen. Each lane's
  // table is earned by ITS OWN program alone - fettle's rows on pick's
  // page would promise outcomes pick never raises.
  const real = new Set();
  for (const [member, spelling] of Object.entries(s.spellingOf))
    if (s.numberOf[member] !== undefined)
      real.add(s.numberOf[member] + " " + spelling);
  if (s.numberOf.Fault !== undefined) real.add(s.numberOf.Fault + " ");

  for (const row of documented)
    if (!real.has(row))
      fail(page + ": the table documents '" + row.trim() + "', which " +
           who + " cannot produce");

  where.push(documented.size + " " + who + " codes in " + page);
}


if (bad === 0) {
  console.log("EVERY OUTCOME IS DOCUMENTED (" + where.join(", ") + ")");
  process.exit(0);
}
console.log("\n" + bad + " problem(s).");
process.exit(1);
