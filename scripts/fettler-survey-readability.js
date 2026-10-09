// MAINTAINER TOOL - read-only. Scores the prose of the documentation site
// and the root Markdown files against the readability standard
// (shoddy-planning/requirements/doc-readability-standard.md).
//
//   node scripts/fettler-survey-readability.js              the table, every page
//   node scripts/fettler-survey-readability.js --worst      sorted by grade, worst first
//   node scripts/fettler-survey-readability.js --csv        the same rows as CSV
//   node scripts/fettler-survey-readability.js PATH ...     only the files named
//
// Report only. It is not a gate and nothing in fettler-build.* or ci.yml runs it.
// Ported from the shoddy repository's scripts/survey-readability.js. The
// two sites share a layout (docs/ plus root Markdown). This copy differs
// in its header and in one addition: after the table it lists every
// sentence over 40 words, so the writer can find and split each one.
// The standard says the targets are a cheap check between real-reader
// tests, and that the test that counts is a beginner succeeding.
//
// Prose only, as the standard asks. Code blocks, inline code, navigation,
// headings, scripts and styles are stripped before anything is counted,
// so a syllable count on ScribblerSetInterval cannot skew a page. Tables
// on the HTML pages stay in, because a reference row is a sentence someone
// reads; the README's HTML galleries come out, because a caption is not.
// What the formulas cannot know is which technical terms a page defines.
// Those still count as long words here, so a reference page scores a
// little worse than it reads.
//
// Columns:
//   words sents  prose size
//   avg   max    words per sentence, mean and longest   (target 15-20, 40)
//   >40          sentences past the hard ceiling
//   para         sentences per paragraph, mean          (target 2-4)
//   FK           Flesch-Kincaid grade                   (target <= 8)
//   FRE          Flesch Reading Ease                    (target >= 60)
//   Fog          Gunning Fog index                      (target <= 10)
//   dash         em dashes in the raw file, entity or character
//   inten        honestly / frankly / genuinely / truly / really
//   buzz         the standard's banned words
//   nonascii     characters above 0x7F in the raw file
//
// After the table: every sentence over 40 words, with its file, its word
// count and its opening words, so each one can be found and split. Then
// every character above 0x7F found in the pages, the root Markdown and the
// SVGs under docs/, by code point, with its HTML entity name where one
// exists, how many, and where. That is the list an entity pass has to
// convert, and a gate that refuses such bytes wants it empty.
//
// The syllable count is the usual English heuristic (vowel groups, a
// silent trailing e, -le endings) and is wrong on some words in both
// directions. Treat every number here as a comparison between pages and
// across time, not as a fact about one sentence.
//
// This file is ASCII on purpose. The few characters above 0x7F it has to
// recognise are built from their code points with ch() below, so a console
// round trip on the wrong code page cannot mangle this file the way it has
// mangled some of the pages it measures.

const fs = require("fs");
const path = require("path");
const root = path.resolve(__dirname, "..");

const ch = c => String.fromCharCode(c);

const args = process.argv.slice(2);
const flags = new Set(args.filter(a => a.startsWith("--")));
const named = args.filter(a => !a.startsWith("--"));

// ---- which files ----------------------------------------------------

function walk(dir, out) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) { if (e.name !== "media") walk(p, out); }
    else if (e.name.endsWith(".html") && e.name !== "404.html") out.push(p);
  }
  return out;
}

const rel = p => path.relative(root, p).replace(/\\/g, "/");

let files;
if (named.length) {
  files = named.map(n => path.resolve(root, n));
} else {
  files = walk(path.join(root, "docs"), []);
  for (const f of fs.readdirSync(root))
    if (f.endsWith(".md")) files.push(path.join(root, f));
}
files.sort((a, b) => rel(a).localeCompare(rel(b)));

// ---- entities -------------------------------------------------------
// Quotes and apostrophes decode to their ASCII forms so that "don&rsquo;t"
// stays one word and a sentence can end in a closing quote. Accented
// letters decode to the bare letter so the word survives. Every other
// entity (&mdash;, &rarr;, &times;, &nbsp; ...) becomes a space, which
// keeps two words apart rather than gluing them. Raw typographic quotes
// in the text are normalised the same way.

const ENTITIES = {
  amp: "&", lt: "<", gt: ">", quot: ch(0x22), apos: "'", nbsp: " ",
  rsquo: "'", lsquo: "'", rdquo: ch(0x22), ldquo: ch(0x22),
  eacute: "e", auml: "a",
};

const CURLY_SINGLE = new RegExp("[" + ch(0x2018) + ch(0x2019) + "]", "g");
const CURLY_DOUBLE = new RegExp("[" + ch(0x201c) + ch(0x201d) + "]", "g");

function decode(s) {
  return s
    .replace(/&#x([0-9a-f]+);/gi, (m, h) => String.fromCodePoint(parseInt(h, 16)))
    .replace(/&#(\d+);/g, (m, d) => String.fromCodePoint(+d))
    .replace(/&([A-Za-z]+);/g, (m, n) => (n in ENTITIES ? ENTITIES[n] : " "))
    .replace(CURLY_SINGLE, "'")
    .replace(CURLY_DOUBLE, ch(0x22));
}

// ---- prose extraction -----------------------------------------------

// A blockquote is somebody else's words quoted verbatim (the devlog quotes
// the author's messages as written), so it is not the site's prose and is
// not scored.
const STRIP_HTML = ["script", "style", "pre", "code", "nav", "footer", "title",
                    "blockquote", "h1", "h2", "h3", "h4", "h5", "h6"];

function proseOfHtml(html) {
  let s = html;
  for (const tag of STRIP_HTML)
    s = s.replace(new RegExp("<" + tag + "\\b[\\s\\S]*?</" + tag + ">", "gi"), " ");
  s = s.replace(/<div class="topbar">[\s\S]*?<\/div><\/div>/, " ");
  s = s.replace(/<!--[\s\S]*?-->/g, " ");
  // paragraph boundaries survive as blank lines
  s = s.replace(/<\/(p|li|td|th|div|blockquote|dt|dd|tr)>/gi, "\n\n");
  s = s.replace(/<br\s*\/?>/gi, "\n");
  s = s.replace(/<[^>]+>/g, " ");
  return decode(s);
}

function proseOfMarkdown(md) {
  let s = md.replace(/\r\n/g, "\n");
  s = s.replace(/```[\s\S]*?```/g, "\n\n");             // fenced code
  s = s.replace(/<!--[\s\S]*?-->/g, " ");
  s = s.replace(/<table[\s\S]*?<\/table>/gi, "\n\n");   // README's galleries
  s = s.replace(/<picture[\s\S]*?<\/picture>/gi, " ");
  s = s.replace(/`[^`\n]*`/g, " ");                     // inline code
  s = s.replace(/!\[[^\]]*\]\([^)]*\)/g, " ");          // images
  s = s.replace(/\[([^\]]*)\]\([^)]*\)/g, "$1");        // links keep their text
  s = s.replace(/^#{1,6} .*$/gm, "");                   // headings
  s = s.replace(/^\|.*$/gm, "");                        // table rows
  s = s.replace(/^[-*_]{3,}\s*$/gm, "");                // horizontal rules
  s = s.replace(/^(    |\t).*$/gm, "");                 // indented code
  s = s.replace(/^\s*[-*+] /gm, "");                    // bullet markers
  s = s.replace(/^\s*\d+\. /gm, "");                    // numbered markers
  s = s.replace(/[*_]{1,3}([^*_\n]+)[*_]{1,3}/g, "$1"); // emphasis
  s = s.replace(/<\/(p|li|td|th|div|tr|sub)>/gi, "\n\n");
  s = s.replace(/<[^>]+>/g, " ");                       // any HTML left
  return decode(s);
}

// ---- sentences and words --------------------------------------------

function wordsOf(s) {
  return s.split(/\s+/)
    .map(w => w.replace(/^[^A-Za-z0-9]+|[^A-Za-z0-9]+$/g, ""))
    .filter(w => /[A-Za-z]/.test(w));
}

// One entry per paragraph; each entry is a list of sentences; each
// sentence is its list of words. A split happens after . ! ? (and a
// closing quote or bracket) when the next thing starts like a sentence,
// so "e.g. the" and "vs. the" stay joined and "U.S. Copyright" does not,
// which is the usual trade.
function paragraphsOf(text) {
  const out = [];
  for (const para of text.split(/\n\s*\n/)) {
    const flat = para.replace(/\s+/g, " ").trim();
    if (!flat) continue;
    const parts = flat.split(/(?<=[.!?]["')\]]?) (?=["'(\[]?[A-Z0-9])/);
    const sents = [];
    for (const p of parts) {
      const words = wordsOf(p);
      if (words.length) sents.push(words);
    }
    if (sents.length) out.push(sents);
  }
  return out;
}

function syllables(word) {
  let w = word.toLowerCase().replace(/[^a-z]/g, "");
  if (!w) return 0;
  if (w.length <= 3) return 1;
  w = w.replace(/(?:[^laeiouy]es|ed|[^laeiouy]e)$/, "");
  w = w.replace(/^y/, "");
  const groups = w.match(/[aeiouy]{1,2}/g);
  return Math.max(1, groups ? groups.length : 1);
}

// ---- scoring ---------------------------------------------------------

const DASH  = new RegExp("&mdash;|" + ch(0x2014), "g");
const INTEN = /\b(honestly|frankly|genuinely|truly|really)\b/gi;
const BUZZ  = /\b(load-bearing|leverage|robust|seamless(?:ly)?|delve|landscape|unlock)\b/gi;
const NONASCII = /[^\x00-\x7f]/gu;

const count = (s, re) => (s.match(re) || []).length;

function score(file) {
  const raw = fs.readFileSync(file, "utf8");
  const text = file.endsWith(".md") ? proseOfMarkdown(raw) : proseOfHtml(raw);
  const paras = paragraphsOf(text);
  const sents = paras.flat();
  let words = 0, syl = 0, complex = 0, over40 = 0, max = 0;
  const long = [];
  for (const s of sents) {
    words += s.length;
    if (s.length > 40) { over40++; long.push(s); }
    if (s.length > max) max = s.length;
    for (const w of s) {
      const k = syllables(w);
      syl += k;
      if (k >= 3 && !w.includes("-")) complex++;
    }
  }
  return {
    file: rel(file), words, sents: sents.length, paras: paras.length,
    syl, complex, over40, max, long,
    dash: count(raw, DASH), inten: count(raw, INTEN),
    buzz: count(raw, BUZZ), nonascii: count(raw, NONASCII),
  };
}

// The three formulas, over any set of totals, so the site can be scored
// as one text as well as page by page.
function formulas(t) {
  const S = t.sents || 1, W = t.words || 1;
  const avg = t.words / S, spw = t.syl / W;
  return {
    avg,
    para: t.sents / (t.paras || 1),
    fk:  0.39 * avg + 11.8 * spw - 15.59,
    fre: 206.835 - 1.015 * avg - 84.6 * spw,
    fog: 0.4 * (avg + 100 * (t.complex / W)),
  };
}

const rows = files.filter(f => fs.existsSync(f)).map(score).map(r => Object.assign(r, formulas(r)));
if (flags.has("--worst")) rows.sort((a, b) => b.fk - a.fk);

// ---- output ----------------------------------------------------------

const cols = [
  ["page",     r => r.file,           34, "l"],
  ["words",    r => r.words,           6],
  ["sents",    r => r.sents,           5],
  ["avg",      r => r.avg.toFixed(1),  5],
  ["max",      r => r.max,             4],
  [">40",      r => r.over40,          4],
  ["para",     r => r.para.toFixed(1), 5],
  ["FK",       r => r.fk.toFixed(1),   5],
  ["FRE",      r => r.fre.toFixed(0),  4],
  ["Fog",      r => r.fog.toFixed(1),  5],
  ["dash",     r => r.dash,            5],
  ["inten",    r => r.inten,           5],
  ["buzz",     r => r.buzz,            4],
  ["nonascii", r => r.nonascii,        8],
];

if (flags.has("--csv")) {
  console.log(cols.map(c => c[0]).join(","));
  for (const r of rows) console.log(cols.map(c => c[1](r)).join(","));
} else {
  const pad = (v, n, side) => (side === "l" ? String(v).padEnd(n) : String(v).padStart(n));
  console.log(cols.map(c => pad(c[0], c[2], c[3])).join(" "));
  for (const r of rows) console.log(cols.map(c => pad(c[1](r), c[2], c[3])).join(" "));

  const tot = { words: 0, sents: 0, paras: 0, syl: 0, complex: 0, over40: 0,
                dash: 0, inten: 0, buzz: 0, nonascii: 0 };
  for (const r of rows) for (const k of Object.keys(tot)) tot[k] += r[k];
  const site = formulas(tot);
  const meets = p => rows.filter(p).length;

  console.log("");
  console.log(rows.length + " pages, " + tot.words + " words, " + tot.sents +
              " sentences, " + tot.over40 + " over 40 words");
  console.log("site as one text: FK " + site.fk.toFixed(1) + "  FRE " + site.fre.toFixed(0) +
              "  Fog " + site.fog.toFixed(1) + "  avg " + site.avg.toFixed(1) +
              " words/sentence  para " + site.para.toFixed(1));
  console.log("pages meeting target: FK<=8 " + meets(r => r.fk <= 8) +
              "  FRE>=60 " + meets(r => r.fre >= 60) +
              "  Fog<=10 " + meets(r => r.fog <= 10) +
              "  avg 15-20 " + meets(r => r.avg >= 15 && r.avg <= 20) +
              "  none over 40 " + meets(r => r.over40 === 0) +
              "  of " + rows.length);
  console.log("em dashes " + tot.dash + ", intensifiers " + tot.inten +
              ", banned words " + tot.buzz + ", non-ASCII characters " + tot.nonascii);

  // ---- sentences over 40 words ---------------------------------------
  // A bullet list or a table cell with no full stop reads to the splitter
  // as one sentence, so an entry here may be a list rather than prose.
  // Either way the fix is the same: end each item with a full stop, or
  // split the sentence.
  const longs = rows.flatMap(r => r.long.map(s => ({ file: r.file, s })));
  if (longs.length) {
    console.log("");
    console.log("sentences over 40 words (file, words, opening words):");
    for (const { file, s } of longs)
      console.log("  " + file.padEnd(34) + String(s.length).padStart(4) + "  " +
                  s.slice(0, 12).join(" ") + " ...");
  }

  // ---- non-ASCII inventory ------------------------------------------
  const NAMES = {
    0xa0: "nbsp", 0xa3: "pound", 0xa7: "sect", 0xa9: "copy", 0xb0: "deg",
    0xb7: "middot", 0xbd: "frac12", 0xd7: "times", 0xe2: "acirc", 0xe4: "auml",
    0xe9: "eacute", 0xf6: "ouml", 0xfc: "uuml", 0x2013: "ndash", 0x2014: "mdash",
    0x2018: "lsquo", 0x2019: "rsquo", 0x201c: "ldquo", 0x201d: "rdquo",
    0x2026: "hellip", 0x2032: "prime", 0x2033: "Prime", 0x2190: "larr",
    0x2192: "rarr", 0x2264: "le", 0x2265: "ge", 0x2260: "ne", 0x221e: "infin",
    0x03c0: "pi", 0x00b1: "plusmn", 0x2122: "trade", 0x2022: "bull",
  };
  function walkSvg(dir, out) {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, e.name);
      if (e.isDirectory()) walkSvg(p, out);
      else if (e.name.endsWith(".svg")) out.push(p);
    }
    return out;
  }
  const inv = new Map();   // code point -> { n, files }
  const scanned = files.filter(f => fs.existsSync(f));
  if (!named.length) walkSvg(path.join(root, "docs"), scanned);
  for (const f of scanned) {
    const raw = fs.readFileSync(f, "utf8");
    for (const c of raw) {
      const cp = c.codePointAt(0);
      if (cp < 0x80) continue;
      let e = inv.get(cp);
      if (!e) { e = { n: 0, files: new Set() }; inv.set(cp, e); }
      e.n++;
      e.files.add(rel(f));
    }
  }
  if (inv.size) {
    console.log("");
    console.log("non-ASCII characters, by code point (pages, root Markdown, docs SVGs):");
    for (const [cp, e] of [...inv.entries()].sort((a, b) => b[1].n - a[1].n)) {
      const hex = "U+" + cp.toString(16).toUpperCase().padStart(4, "0");
      const name = cp in NAMES ? "&" + NAMES[cp] + ";" : "";
      const list = [...e.files];
      const where = list.slice(0, 3).join(", ") + (list.length > 3 ? " +" + (list.length - 3) + " more" : "");
      console.log("  " + hex.padEnd(8) + name.padEnd(10) + String(e.n).padStart(6) +
                  "  " + list.length + " file" + (list.length === 1 ? "" : "s") + ": " + where);
    }
  }
}
