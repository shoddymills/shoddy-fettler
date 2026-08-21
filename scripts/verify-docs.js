// MAINTAINER TOOL - read-only. Checks the site against the sources.
//
//   node scripts/verify-docs.js        (run from anywhere; finds the repo from its own path)
//
// Rebuilds ground truth from the tree on every run rather than comparing the
// pages against a list somebody has to remember to update. Five checks:
//
//   navigation - every page carries the same nav bar. Adding a page means
//                adding it to every other page, and this is what says you
//                missed one.
//   links      - every internal href AND src resolves: the file exists, and
//                if a link carries a #fragment, something on that page has
//                that id. A link to a section that was renamed is the
//                commonest way a site rots, and nothing else notices; a src
//                is here because the two figures live in media/ as their own
//                files now, and a page referencing one that has been renamed
//                shows a broken image and says nothing.
//   orphans    - every page is reachable from another page. A page nothing
//                links to is a page nobody will read.
//   screen     - the disclosure screen's section on the overview page still
//                names every category the code has, and the exit-code table
//                gives `screened` as the number ExitCodes.cs declares.
//   encoding   - no tracked text file carries UTF-8 that has been round-
//                tripped through CP1252. The repository this came from
//                shipped ~400 mangled characters past a gate that had no
//                such check. Nothing else notices: the damage sits in
//                comments and prose, so the compiler and the tests are all
//                perfectly satisfied.
//
// Exit 0 and "THE SITE MATCHES THE SOURCES" is the pass. Any mismatch prints
// what it found against what it wanted, and exits 1.
//
// Deliberately NOT checked: counts in prose. A page should not say "six
// pages" at all - a number that has to be revised every time the site grows
// is a divergence waiting to happen.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");
const docs = path.join(root, "docs");

const read = f => fs.readFileSync(path.join(root, f), "utf8");
let bad = 0;
const fail = msg => { console.log(msg); bad++; };

// ---- the pages ----
const pages = fs.readdirSync(docs)
  .filter(f => f.endsWith(".html"))
  .sort();

// ---- navigation: one nav bar, copied into every page ----
// The bar is two <nav class="docnav"> rows - "meta" beside the wordmark,
// "main" beneath - so every row is collected and the items compared as one
// sequence. Checking only the first would leave the second row unguarded.
//
// Items may carry a class of their own, and the current page is a
// <span class="here"> rather than an <a>, so both forms are matched and only
// the TEXT is compared. Otherwise every page would differ from every other
// by exactly the item naming itself.
{
  const navs = {};
  for (const p of pages) {
    if (p === "404.html") continue;   // deliberately self-contained, no nav
    const rows = [...read("docs/" + p)
      .matchAll(/<nav class="docnav[^"]*"[^>]*>([\s\S]*?)<\/nav>/g)];
    if (!rows.length) { fail("docs/" + p + ": no nav bar"); continue; }
    const key = rows
      .flatMap(r => [...r[1].matchAll(/<(?:a href="[^"]+"[^>]*|span class="here[^"]*")>([^<]+)</g)])
      .map(x => x[1].trim()).join(" | ");
    (navs[key] = navs[key] || []).push(p);
  }
  if (Object.keys(navs).length > 1) {
    const majority = Object.entries(navs).sort((a, b) => b[1].length - a[1].length)[0][0];
    for (const [key, where] of Object.entries(navs))
      if (key !== majority)
        fail("nav differs on " + where.join(", ") + ":\n  has  " + key + "\n  want " + majority);
  }
}

// ---- links: every internal href resolves, fragment included ----
{
  const ids = {};
  for (const p of pages)
    ids[p] = new Set([...read("docs/" + p).matchAll(/\bid="([^"]+)"/g)].map(m => m[1]));

  const linkedTo = new Set();

  for (const p of pages) {
    const text = read("docs/" + p);
    // href and src alike: a stylesheet, a script or a figure that has been
    // renamed is exactly as broken as a dead link, and rather harder to see.
    for (const m of text.matchAll(/\b(?:href|src)="([^"]+)"/g)) {
      const href = m[1];
      // Off-site, in-page, and non-page links are somebody else's problem.
      if (/^(https?:|mailto:|#|\/)/.test(href)) continue;
      const [file, frag] = href.split("#");
      if (!file) continue;
      if (!file.endsWith(".html")) {
        if (!fs.existsSync(path.join(docs, file)))
          fail("docs/" + p + ": links to " + file + ", which is not there");
        continue;
      }
      if (!pages.includes(file)) {
        fail("docs/" + p + ": links to " + file + ", which is not there");
        continue;
      }
      if (file !== p) linkedTo.add(file);
      if (frag && !ids[file].has(frag))
        fail("docs/" + p + ": links to " + file + "#" + frag + ", and nothing there has that id");
    }
  }

  // ---- orphans ----
  for (const p of pages) {
    if (p === "index.html" || p === "404.html") continue;
    if (!linkedTo.has(p)) fail("docs/" + p + ": nothing links to this page");
  }
}

// ---- the disclosure screen: docs/index.html against the sources ----
// The screen is configuration a person writes and a refusal they meet, so
// its documentation is part of the feature rather than a description of one.
{
  const page = read("docs/index.html");

  // The SECTION, not the page: a category word turning up in some unrelated
  // paragraph would satisfy a whole-page search while the section that has
  // to list them stayed silent.
  const start = page.indexOf('id="screen"');
  const screen = start === -1 ? null
    : page.slice(start, (i => i === -1 ? page.length : i)(page.indexOf("<h2", start + 1)));

  if (screen === null) {
    fail('docs/index.html: MISSING the screen section (id="screen")');
  } else {
    // Every category the code can screen for, named in the page. One added
    // in the code and documented nowhere is a category nobody can discover;
    // one removed leaves a page promising a screen that no longer exists.
    const words = [...read("Fettler/Core/Screen.cs")
      .matchAll(/Screened\.\w+ => "([a-z]+)"/g)].map(m => m[1]);

    if (!words.length) fail("verify-docs: no screen categories found in Screen.cs");

    for (const word of words)
      if (!screen.includes("<code>" + word + "</code>"))
        fail("docs/index.html #screen: the '" + word + "' category is undocumented");
  }

  // The exit code the screen actually raises, in the table that promises to
  // list every one. A number quoted in prose and changed in the code is the
  // kind of fact that stops being true without anybody noticing.
  const declared = /public const int Screened = (\d+);/.exec(read("Fettler/Cli/ExitCodes.cs"));

  if (!declared) {
    fail("verify-docs: no Screened exit code in ExitCodes.cs");
  } else if (!read("docs/install.html")
      .includes("<td>" + declared[1] + "</td><td><code>screened</code></td>")) {
    fail("docs/install.html: the exit-code table does not give screened as " + declared[1]);
  }
}

// ---- encoding: no UTF-8 round-tripped through CP1252 ----
// The signature is a byte sequence that is valid UTF-8 but reads as the
// CP1252 rendering of a character nobody types: an em dash arriving as three
// characters, and on a second pass as eight.
//
// A file that is legitimately ABOUT the fault - a release note explaining
// one - opts out by carrying the marker below anywhere in its text. Reach
// for that before reaching for deleting the check.
{
  const MARKER = "verify-docs: mojibake is the subject here";
  const SIGNS = [
    "â", "â", "â", "â",
    "â", "â", "Ã©", "Ã¨",
    "Ã¢", "Â ", "Â·", "└ó",
  ];
  const SKIP = new Set(["node_modules", ".git", "bin", "obj", "artifacts", "published", "smoke"]);
  const TEXT = /\.(cs|js|mjs|json|md|html|css|txt|yml|yaml|ps1|sh|csproj|slnx|props)$/i;

  (function walk(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      if (entry.name.startsWith(".") && entry.name !== ".github") continue;
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) { if (!SKIP.has(entry.name)) walk(full); continue; }
      if (!TEXT.test(entry.name)) continue;
      const rel = path.relative(root, full).split(path.sep).join("/");
      const text = fs.readFileSync(full, "utf8");
      if (text.includes(MARKER)) continue;
      for (const sign of SIGNS)
        if (text.includes(sign)) {
          fail(rel + ": carries UTF-8 round-tripped through CP1252 (found " +
               JSON.stringify(sign) + ")");
          break;
        }
    }
  })(root);
}

if (bad === 0) {
  console.log("THE SITE MATCHES THE SOURCES (" + pages.length + " pages)");
  process.exit(0);
}
console.log("\n" + bad + " problem(s).");
process.exit(1);
