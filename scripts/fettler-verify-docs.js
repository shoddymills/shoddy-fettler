// MAINTAINER TOOL - read-only. Checks the site against the sources.
//
//   node scripts/fettler-verify-docs.js        (run from anywhere; finds the repo from its own path)
//
// Rebuilds ground truth from the tree on every run rather than comparing the
// pages against a list somebody has to remember to update. Seven checks:
//
//   navigation - every page carries the same nav bar, AND no page's bar
//                links to the page it is sitting on. Adding a page means
//                adding it to every other page, and this is what says you
//                missed one. A page that links to itself has lost the
//                "here" marker saying where the reader is - which is
//                exactly how a page made by copying another one arrives,
//                and is invisible to a comparison that reads only the text.
//   order      - the prev/next chain joins up: a page's own two nav bars
//                agree with each other, and if A's next is B then B's
//                previous is A. Every link still resolves when this rots,
//                so the link check stays perfectly happy while a reader
//                walks past a page or round in a circle.
//   links      - every internal href AND src resolves: the file exists, and
//                if a link carries a #fragment, something on that page has
//                that id. A link to a section that was renamed is the
//                commonest way a site rots, and nothing else notices; a src
//                is here because the two figures live in media/ as their own
//                files now, and a page referencing one that has been renamed
//                shows a broken image and says nothing.
//   orphans    - every page is reachable from another page. A page nothing
//                links to is a page nobody will read.
//   screen     - the disclosure screen's section names every category the
//                code has, and some exit-code table on the site gives
//                `screened` as the number ExitCodes.cs declares. Neither
//                half names the page it looks on - it finds it, so moving a
//                section between pages is a docs decision and not a gate
//                failure.
//   encoding   - no tracked text file carries UTF-8 that has been round-
//                tripped through CP1252. The repository this came from
//                shipped ~400 mangled characters past a gate that had no
//                such check. Nothing else notices: the damage sits in
//                comments and prose, so the compiler and the tests are all
//                perfectly satisfied.
//   ascii      - a docs page, a docs SVG and a root Markdown file carry no
//                character above 0x7F. Every special character is written
//                as an entity (&mdash; &rsquo; &#9654;), which is plain
//                ASCII and survives any console, editor or pipeline. The
//                mojibake check above catches one round trip and no other;
//                this rule needs no signature. The same opt-out marker
//                applies. C# sources stay out of scope, and so does
//                Markdown below the root (release notes).
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

// ---- navigation: no page's bar links to the page it is on ----
// The comparison above reads only the TEXT of each item, because the current
// page is a <span class="here"> rather than an <a> and every page would
// otherwise differ from every other by exactly the item naming itself. That
// blindness has a cost: a page whose "here" marker was left pointing at the
// wrong item passes it. So say the rule directly - a page does not link to
// itself - which is the shape that fault always takes.
for (const p of pages) {
  if (p === "404.html") continue;
  for (const bar of read("docs/" + p)
      .matchAll(/<nav class="docnav[^"]*"[^>]*>([\s\S]*?)<\/nav>/g))
    if (bar[1].includes('href="' + p + '"')) {
      fail("docs/" + p + ": its own nav bar links to it - the here marker is on the wrong item");
      break;
    }
}

// ---- lanes: the main bar shows ONE program, and it is the page's own ----
// The identity check above compares only item TEXT, and both lanes
// deliberately use the same words - so it cannot see a pick page
// carrying fettle's items, which would pass every other check and be
// wrong on sight. Say the lane rules directly: exactly one main bar per
// page, every href in it inside one lane, and the lane matching the
// page's own name (shared pages - index, heritage, authorship - carry
// the fettle lane, the first program).
for (const p of pages) {
  if (p === "404.html") continue;
  const text = read("docs/" + p);
  const main = [...text.matchAll(/<nav class="docnav main"[^>]*>([\s\S]*?)<\/nav>/g)];
  if (main.length !== 1) {
    fail("docs/" + p + ": expected exactly one main bar, found " + main.length);
    continue;
  }
  const hrefs = [...main[0][1].matchAll(/href="([^"#]+)"/g)].map(m => m[1]);
  const lane = p.startsWith("pick") ? "pick" : "fettle";
  for (const href of hrefs)
    if (!href.startsWith(lane))
      fail("docs/" + p + ": its main bar links to " + href +
           ", which is not in the " + lane + " lane");
}

// ---- lanes: outside the switcher, a lane page never leaves its lane ----
// The switcher is the ONE deliberate crossover, and it jumps to the same
// section on the other side. Every other cross-lane link is a reader
// silently changing programs mid-page - which is how the screening pages
// once sent a pick reader to fettle's instructions - so the lanes are
// isolated: each has its own copy of every page, and a fact both programs
// share is stated twice rather than linked across. Shared pages (index,
// heritage, authorship, and the self-contained 404) belong to both
// programs and may link either lane.
{
  const shared = new Set(["index.html", "heritage.html", "authorship.html", "404.html"]);
  for (const p of pages) {
    if (shared.has(p)) continue;
    const text = read("docs/" + p)
      .replace(/<nav class="docnav switch"[\s\S]*?<\/nav>/g, "");
    const crossing = p.startsWith("pick") ? /href="(fettle[^"]*)"/g : /href="(pick[^"]*)"/g;
    for (const m of text.matchAll(crossing))
      fail("docs/" + p + ": links across the lanes to " + m[1] +
           " outside the switcher - the lanes are isolated on purpose");
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
// ---- order: the prev/next chain joins up ----
// The pages carry a linear "Fettler, in order" chain in a pagenav bar at the
// top and another at the foot. Two ways it rots, both silent, and both have
// happened here: the two bars on one page saying different things, and A
// pointing forward to B while B points back past A to somewhere else. Every
// link still resolves either way, so nothing else notices.
//
// Pages with no pagenav at all - heritage, authorship, 404 - are not in the
// chain and are skipped rather than reported.
{
  const endsOf = bar => {
    const out = {};
    for (const a of bar.matchAll(/<a\b([^>]*)>/g)) {
      const dir = /class="(prev|next)"/.exec(a[1]);
      const href = /href="([^"#]+)"/.exec(a[1]);
      if (dir && href) out[dir[1]] = href[1];
    }
    return out;
  };

  const chain = {};
  for (const p of pages) {
    const bars = [...read("docs/" + p)
      .matchAll(/<nav class="pagenav[^"]*"[^>]*>([\s\S]*?)<\/nav>/g)];
    if (!bars.length) continue;

    const shapes = bars.map(b => {
      const e = endsOf(b[1]);
      return (e.prev || "nothing") + "  <-  " + p + "  ->  " + (e.next || "nothing");
    });

    if (new Set(shapes).size > 1)
      fail("docs/" + p + ": its nav bars disagree about the reading order:\n    " +
           [...new Set(shapes)].join("\n    "));

    chain[p] = endsOf(bars[0][1]);
  }

  for (const [p, e] of Object.entries(chain)) {
    if (e.next && chain[e.next] && chain[e.next].prev !== p)
      fail("docs/" + p + ": its next is " + e.next + ", whose previous is " +
           (chain[e.next].prev || "nothing") + " - the chain does not join up");
    if (e.prev && chain[e.prev] && chain[e.prev].next !== p)
      fail("docs/" + p + ": its previous is " + e.prev + ", whose next is " +
           (chain[e.prev].next || "nothing") + " - the chain does not join up");
  }
}

// ---- the disclosure screen, against the sources ----
// The screen is configuration a person writes and a refusal they meet, so
// its documentation is part of the feature rather than a description of one.
//
// WHICH page carries the section is not pinned here. It has moved once
// already, and a check naming a filename turns a docs reorganisation into a
// build failure - which is how a gate ends up edited to suit the pages
// instead of the pages being held to the gate. Find the page with
// id="screen", and insist there is exactly one: two of them drift apart.
{
  const carriers = pages.filter(p => read("docs/" + p).includes('id="screen"'));

  if (!carriers.length) {
    fail('no page carries the screen section (id="screen")');
  } else {
    if (carriers.length > 1)
      fail('the screen section (id="screen") is on more than one page (' +
           carriers.map(p => "docs/" + p).join(", ") + ") - two copies drift apart");

    const where = "docs/" + carriers[0];
    const text = read(where);

    // The SECTION, not the page: a category word turning up in some unrelated
    // paragraph would satisfy a whole-page search while the section that has
    // to list them stayed silent.
    const start = text.indexOf('id="screen"');
    const end = text.indexOf("<h2", start + 1);
    const screen = text.slice(start, end === -1 ? text.length : end);

    // Every category the code can screen for, named in the section. One added
    // in the code and documented nowhere is a category nobody can discover;
    // one removed leaves a page promising a screen that no longer exists.
    const words = [...read("Fettler/Core/Screen.cs")
      .matchAll(/Screened\.\w+ => "([a-z]+)"/g)].map(m => m[1]);

    if (!words.length) fail("verify-docs: no screen categories found in Screen.cs");

    for (const word of words)
      if (!screen.includes("<code>" + word + "</code>"))
        fail(where + ' #screen: the \'' + word + "' category is undocumented");
  }

  // The exit code the screen actually raises, in the table that promises to
  // list every one - on whichever page keeps that table. fettler-verify-errors.js
  // holds that table to the source row by row; this asks only that the
  // screen's own number is in it, so a screen documented as a feature always
  // has the code a caller branches on written down beside it.
  const declared = /public const int Screened = (\d+);/.exec(read("Fettler/Cli/ExitCodes.cs"));

  if (!declared) {
    fail("verify-docs: no Screened exit code in ExitCodes.cs");
  } else {
    const row = "<td>" + declared[1] + "</td><td><code>screened</code></td>";
    if (!pages.some(p => read("docs/" + p).includes(row)))
      fail("no exit-code table on the site gives screened as " + declared[1]);
  }
}


// ---- encoding: no UTF-8 round-tripped through CP1252 or CP437/850 ----
// The signature is a byte sequence that is valid UTF-8 but reads as the
// CP1252 rendering of a character nobody types: an em dash arriving as three
// characters, and on a second pass as eight.
//
// The 437/850 family is the same fault through a Windows console codepage
// instead of a Windows text codepage. It is here because the design
// transcript arrived with every em dash as "ΓÇö" - and this check, as it
// stood, was satisfied.
//
// A file that is legitimately ABOUT the fault - a release note explaining
// one - opts out by carrying the marker below anywhere in its text. Reach
// for that before reaching for deleting the check.
{
  const MARKER = "verify-docs: mojibake is the subject here";
  const SIGNS = [
    "ΓÇö", "ΓÇô", "ΓÇÖ", "ΓÇ£", "ΓÇ¥", "ΓÇÿ",
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
          fail(rel + ": carries UTF-8 round-tripped through a legacy codepage (found " +
               JSON.stringify(sign) + ")");
          break;
        }
    }
  })(root);
}

// ---- ascii: no character above 0x7F in a page, a docs SVG or a root .md ----
{
  const MARKER = "verify-docs: mojibake is the subject here";
  const files = [];
  for (const p of pages) files.push("docs/" + p);
  (function svgs(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) { svgs(full); continue; }
      if (entry.name.endsWith(".svg"))
        files.push(path.relative(root, full).split(path.sep).join("/"));
    }
  })(docs);
  for (const entry of fs.readdirSync(root))
    if (entry.endsWith(".md")) files.push(entry);

  for (const rel of files) {
    const text = read(rel);
    if (text.includes(MARKER)) continue;
    text.split("\n").forEach((line, i) => {
      const m = line.match(/[^\x00-\x7f]/u);
      if (m) {
        const cp = m[0].codePointAt(0).toString(16).toUpperCase().padStart(4, "0");
        fail(rel + ":" + (i + 1) + ": character U+" + cp +
             " - write it as an entity; the docs are ASCII");
      }
    });
  }
}

if (bad === 0) {
  console.log("THE SITE MATCHES THE SOURCES (" + pages.length + " pages)");
  process.exit(0);
}
console.log("\n" + bad + " problem(s).");
process.exit(1);
