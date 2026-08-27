// MAINTAINER TOOL - every .ps1 has its .sh, and the two offer the same verbs.
//
//   node scripts/fettler-verify-twins.js
//
// EVERY SCRIPT IN THIS REPOSITORY SHIPS TWICE, and only one of the pair ever
// gets run on any given machine. That is the whole problem: the twin nobody
// runs is the twin that rots, and it rots silently, because the person who
// would notice is on the other operating system. A release once had to fix
// "native stderr fatal in every PowerShell script" - a fault that had sat
// there through several releases for exactly this reason.
//
// What was here before was a grep in the workflow:
//
//     for verb in build test release publish; do
//       grep -q "$verb" fettler/build.ps1 || exit 1
//
// That passes on the word appearing ANYWHERE, a comment included. It covered
// one lane out of six, and it could not have caught either of the two real
// drifts that were in the tree when this was written:
//
//   * a tutorial shipped build.sh with NO build.ps1 at all - on a repo
//     whose own rule is that the .ps1 is the half that actually gets run.
//   * two release scripts took -Yes in PowerShell and -y in the shell,
//     while the docs said the twins "take the same arguments".
//
// TWO CHECKS, and they are deliberately dull ones:
//
//   1. Pairing. A .ps1 with no .sh, or a .sh with no .ps1, unless it is on
//      the allowlist below with its reason written down.
//   2. Verbs. The command list in each file's OWN header comment - the usage
//      block a reader learns the surface from - must be the same on both.
//
// A THIRD CHECK WAS WRITTEN AND TAKEN OUT, which is worth recording because
// it looked obviously correct. It asserted that every verb a header
// advertises appears somewhere in the code below, so a command could not be
// deleted and left advertised. Run against this tree it produced twenty-two
// findings and not one of them was a defect:
//
//   * scripts/shoddy.ps1 documents doctor, preflight, package, publish,
//     status and clean and mentions none of them below the header, because
//     it was a thin launcher that forwarded @args elsewhere. That is the
//     design the whole file exists to explain.
//   * "./build.sh  restore + build" and "./build.ps1  draw the spiral" put
//     an ordinary English word where a verb sits, and no parser distinguishes
//     those without understanding the sentence.
//
// A check that reports working code as broken gets switched off within a
// week, and then it is not protecting anything at all. Pairing and parity
// are both mechanical, and between them they catch the two drifts that were
// actually in this tree.
//
// FLAG PARITY IS ALSO NOT CHECKED, for the same reason: comparing -Yes
// against -y means knowing that PowerShell resolves the short form by prefix
// and a shell case statement does not. The two are now spelt the same on
// both sides by hand, and keeping them so is a reviewer's job.
//
// The filesystem is walked rather than `git ls-files`, and that is not an
// oversight. A script written and not yet added is invisible to the index,
// so an index-based check passes on the exact tree where a new twin is
// missing - which is the moment this most needs to speak up. driver.mjs
// wrote the same lesson down about fettler-verify-permissions.js.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");

// A script with no twin, and why. A name here is a decision on the record.
//
// EMPTY ON PURPOSE, and worth keeping empty. The obvious candidate is
// make-sitemap, which runs only on the Pages runner and is therefore always
// Linux - the reasoning that put it on this list in the repository Fettler
// came from. But "no Windows caller today" is a fact about the workflow, not
// about the script, and it stops being true the first time somebody wants to
// regenerate a sitemap locally before pushing. It has a twin here instead.
const SINGLETONS = {};

const SKIP = new Set([
  "node_modules", ".git", "bin", "obj", "artifacts", "published", "smoke",
]);

function walk(dir, found = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name.startsWith(".") && entry.name !== ".github") continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (SKIP.has(entry.name)) continue;
      walk(full, found);
    } else if (entry.name.endsWith(".ps1") || entry.name.endsWith(".sh")) {
      found.push(path.relative(root, full).split(path.sep).join("/"));
    }
  }
  return found;
}

// The leading comment block only, which is where every script in this tree
// states its usage. Reading the whole file would pick up section headers and
// bury the surface in commentary about the code.
function header(text) {
  const out = [];
  for (const line of text.split("\n")) {
    const t = line.trim();
    if (t.startsWith("#!")) continue;
    if (t.startsWith("#")) { out.push(t.replace(/^#\s?/, "")); continue; }
    if (t === "") { if (out.length) break; continue; }
    break;
  }
  return out;
}

// Verbs, as the header advertises them.
//
// TWO RULES, both there to keep prose out of the answer:
//
//   The line must BEGIN with the invocation. "build.ps1 at the root refuses
//   a mill that has only one of the pair" is a sentence about the script;
//   "./build.ps1 test   run the suite" is the surface. Only the second
//   starts with the name.
//
//   The verb must be followed by two or more spaces, a bracket, or the end
//   of the line - the column break before a description. That is what
//   separates "./build.ps1 test     run the headless checks" from
//   "./build.ps1          draw the spiral in a window", where the word in
//   the verb column is the first word of the description.
//
// It errs towards missing a verb rather than inventing one: "new NAME" is
// not counted, because NAME follows a single space. A verb missed on both
// sides still compares equal, and a check that under-reports is worth more
// than one that cries wolf.
function verbs(text, file) {
  const base = path.basename(file).replace(/\./g, "\\.");
  const pattern = new RegExp(`^(?:\\S*[\\\\/])?${base}\\s+([A-Za-z][A-Za-z0-9-]*)(?=\\s{2,}|\\s*$|\\s\\[)`);
  const found = new Set();
  for (const line of header(text)) {
    const m = line.match(pattern);
    if (m) found.add(m[1].toLowerCase());
  }
  return found;
}

const scripts = walk(root);
const problems = [];
const seen = new Set();

for (const file of scripts) {
  const isPs = file.endsWith(".ps1");
  const stem = file.slice(0, -(isPs ? 4 : 3));
  if (seen.has(stem)) continue;
  seen.add(stem);

  const ps = stem + ".ps1";
  const sh = stem + ".sh";
  const hasPs = fs.existsSync(path.join(root, ps));
  const hasSh = fs.existsSync(path.join(root, sh));

  if (!hasPs || !hasSh) {
    const missing = hasPs ? sh : ps;
    const present = hasPs ? ps : sh;
    if (SINGLETONS[present]) continue;
    problems.push(`${present} has no twin: ${missing} is missing`);
    continue;
  }

  const psText = fs.readFileSync(path.join(root, ps), "utf8");
  const shText = fs.readFileSync(path.join(root, sh), "utf8");

  const psVerbs = verbs(psText, ps);
  const shVerbs = verbs(shText, sh);

  for (const v of psVerbs) {
    if (!shVerbs.has(v)) problems.push(`${sh} does not offer '${v}', which ${ps} documents`);
  }
  for (const v of shVerbs) {
    if (!psVerbs.has(v)) problems.push(`${ps} does not offer '${v}', which ${sh} documents`);
  }
}

// ---- the screen twins: two programs, one set of detectors ----
// Picker carries a copy of Fettler's tier-one screen, because no assembly
// is ever shared between the programs - the same reasoning that gives the
// burler wire a copy of its shapes on each side. A copy is the honest
// design, and this is its price: the copies can drift, and a pattern that
// drifts is a leak on one side that the other side's tests still pass.
// So the twin rule the scripts live under applies to them too, and this
// is where it is enforced, because this file is where twins are held equal.
//
// Compared: the category words (the NameOf switch), and each detector's
// name, pattern text and Luhn flag out of the Structural array. The prose
// around them is deliberately NOT compared - each program explains the
// screen in its own domain's words, and that is drift nobody is hurt by.
function screenSurface(file) {
  const text = fs.readFileSync(path.join(root, file), "utf8");

  const categories = [...text.matchAll(/Screened\.\w+ => "([a-z]+)"/g)].map(m => m[1]);

  // The Structural array, entry by entry: each starts at a lowercase
  // new( - the New(@"...") helper calls inside an entry are capitalised,
  // so they stay inside their entry rather than splitting it.
  const block = /static readonly Detector\[\] Structural[\s\S]*?\n    \];/.exec(text);
  const detectors = {};
  if (block)
    for (const entry of block[0].split(/\bnew\(/).slice(1)) {
      const name = /^"([a-z-]+)"/.exec(entry);
      if (!name) continue;
      const pattern = [...entry.matchAll(/@"((?:[^"]|"")*)"/g)].map(m => m[1]).join("");
      detectors[name[1]] = pattern + (/Luhn:\s*true/.test(entry) ? "  [luhn]" : "");
    }

  return { categories, detectors };
}

{
  const A = "Fettler/Core/Screen.cs";
  const B = "Picker/Core/Screen.cs";

  if (!fs.existsSync(path.join(root, B))) {
    problems.push(`${A} has no twin: ${B} is missing`);
  } else {
    const a = screenSurface(A);
    const b = screenSurface(B);

    // An extractor that silently found nothing would hold two empty
    // surfaces equal forever, which is worse than no check at all.
    if (!a.categories.length || !Object.keys(a.detectors).length)
      problems.push(`${A}: found no screen surface - the extractor is broken, not the code`);
    if (!b.categories.length || !Object.keys(b.detectors).length)
      problems.push(`${B}: found no screen surface - the extractor is broken, not the code`);

    if (a.categories.join(" ") !== b.categories.join(" "))
      problems.push(`the screen twins disagree about the categories: ${A} has [` +
        a.categories.join(", ") + `], ${B} has [` + b.categories.join(", ") + `]`);

    for (const name of Object.keys(a.detectors))
      if (!(name in b.detectors))
        problems.push(`${B} has no '${name}' detector, which ${A} carries`);
      else if (a.detectors[name] !== b.detectors[name])
        problems.push(`the '${name}' detector has drifted between ${A} and ${B}:\n` +
          `    ${A}: ${a.detectors[name]}\n    ${B}: ${b.detectors[name]}`);
    for (const name of Object.keys(b.detectors))
      if (!(name in a.detectors))
        problems.push(`${A} has no '${name}' detector, which ${B} carries`);

    seen.add("Fettler/Core/Screen.cs + Picker/Core/Screen.cs");
  }
}

if (problems.length === 0) {
  console.log(`ALL TWINS AGREE (${seen.size} pair(s))`);
  process.exit(0);
}

console.log("TWINS HAVE DRIFTED:");
for (const p of problems) console.log(" - " + p);
process.exit(1);
