using System.Text.RegularExpressions;

namespace Fettler.Core;

/// <summary>Whether the caller gave text to find or an expression to
/// match. R4.8 requires the answer say which it was given, because the
/// two behave differently on the same characters.</summary>
public enum PatternKind
{
    Literal,
    Regex,
}

/// <summary>What a search answers with.</summary>
public enum SearchMode
{
    /// <summary>Each matching line, with its text.</summary>
    Hits,

    /// <summary>Each file with a match, once, and how many lines in it
    /// matched.</summary>
    Files,

    /// <summary>How many lines matched, and in how many files. No text,
    /// so nothing about the size of the text can stop it: the count is
    /// exact.</summary>
    Count,
}

/// <summary>
/// Where a cut-down line sits in the whole one: the column its text
/// starts at, and the length of the line it was cut from.
/// </summary>
public sealed record Clip(int FromColumn, int LineLength);

/// <summary>A line near a hit, carried when context was asked for.</summary>
public sealed record ContextLine(int Number, string Text, Clip? Clip = null);

/// <summary>
/// One matching line as a record rather than a line of text the caller
/// has to re-parse (R4.8), carrying enough to address the same line in
/// <c>edit</c> without reformatting (R4.9).
/// </summary>
public sealed record Hit(
    ContainedPath Path,
    int Line,
    int Column,
    string Text,
    IReadOnlyList<ContextLine> Context,

    /// <summary>Where the hit sits in a rendered document's own terms -
    /// "page 7", "Sheet2" - or null for an ordinary text file, whose
    /// line number already IS the citation.</summary>
    string? Cite = null,

    /// <summary>Set when the line was too long to send whole, and
    /// <c>Text</c> is the window around the match.</summary>
    Clip? Clip = null)
{
    /// <summary>Where an answer that stops after this hit carries on
    /// from, as its <c>next</c>.</summary>
    public string Position => $"{Path.Display}:{Line}";
}

/// <summary>One file in a search that names files only.</summary>
public sealed record MatchedFile(ContainedPath Path, int Hits);

/// <summary>What a search found, and what it did not get to.</summary>
public sealed record SearchAnswer(
    IReadOnlyList<Hit> Hits,
    int Files,
    int FilesSearched,
    bool Truncated,
    PatternKind Kind,
    IReadOnlyList<string> Patterns,
    int Excluded,

    /// <summary>Documents in the sweep that were NOT looked inside, because
    /// rendering was turned off, because the file was over the cap, or
    /// because it could not be rendered at all. Reported rather than
    /// dropped: a clean-looking zero is the wrong answer to give somebody
    /// whose document was never opened.</summary>
    int DocumentsSkipped = 0,

    /// <summary>How many files the glob matched, before binaries and
    /// documents were set aside. Beside <c>FilesSearched</c> it tells a
    /// glob that matched nothing from files that held no text.</summary>
    int FilesMatched = 0,

    SearchMode Mode = SearchMode.Hits,

    /// <summary>The files a <see cref="SearchMode.Files"/> search names.</summary>
    IReadOnlyList<MatchedFile>? Matched = null,

    /// <summary>Every matching line, for a <see cref="SearchMode.Count"/>
    /// search.</summary>
    int Total = 0,

    /// <summary>Where to carry on from when the answer stopped short:
    /// pass it back as <c>after</c>.</summary>
    string? Next = null,

    /// <summary>The matching lines of screened files, for a search that
    /// sends no text. A count or a list of files still says that a
    /// screened file holds what the pattern asked for, so the screen
    /// judges those lines exactly as it would judge the hits.</summary>
    IReadOnlyList<Hit>? Disclosed = null);

/// <summary>A place to start a search after: a file, and in it the line
/// after which hits come next. Line 0 means the whole file.</summary>
public sealed record SearchCursor(string Path, int Line)
{
    /// <summary>A <c>next</c> read back: <c>PATH:LINE</c> for hits,
    /// <c>PATH</c> for files. Null when it is neither.</summary>
    public static SearchCursor? Parse(string after, SearchMode mode)
    {
        if (after.Length == 0) return null;
        if (mode == SearchMode.Files) return new SearchCursor(after, int.MaxValue);

        int colon = after.LastIndexOf(':');
        if (colon <= 0) return null;

        return int.TryParse(after[(colon + 1)..], System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out int line) && line >= 1
            ? new SearchCursor(after[..colon], line)
            : null;
    }
}

/// <summary>
/// The search of R4.8, bounded by R4.10 and matched under R4.13.
///
/// <para><b>The dialect is .NET's, named as such (R4.13).</b> It is
/// neither PCRE nor ripgrep's, and a caller writing from habit meets the
/// differences silently unless told. Implementing a PCRE-compatible
/// dialect to meet those habits would be a parser and a lifetime of
/// divergence; naming the one in use costs a line of documentation.</para>
///
/// <para><b>Every expression runs under a bound.</b> A pattern arrives
/// from a model or a script and is not known to be well-behaved, so
/// matching uses the non-backtracking engine where the pattern allows
/// it and an explicit timeout where it does not. Without that, one
/// catastrophically backtracking pattern consumes the tool that was
/// asked to run it - a denial of service Fettler performs on itself.</para>
/// </summary>
public static class Searcher
{
    /// <summary>The bound for patterns the non-backtracking engine will
    /// not take. Long enough that no honest pattern notices, short
    /// enough that a runaway one is an error rather than a hang.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    public static Result<Regex> Compile(string pattern, PatternKind kind, bool caseSensitive)
    {
        string source = kind == PatternKind.Literal ? Regex.Escape(pattern) : pattern;

        RegexOptions options = RegexOptions.CultureInvariant;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;

        try
        {
            // The non-backtracking engine cannot run away, and is the
            // right answer whenever it will take the pattern at all.
            return Result<Regex>.Ok(new Regex(source, options | RegexOptions.NonBacktracking));
        }
        catch (NotSupportedException)
        {
            // Lookaround and backreferences are the common reasons, and
            // they are ordinary things to write. Those patterns get the
            // timeout instead: R4.13 asks for one bound or the other,
            // and refusing a lookahead outright would be a worse tool.
            try
            {
                return Result<Regex>.Ok(new Regex(source, options, MatchTimeout));
            }
            catch (ArgumentException e)
            {
                return Result<Regex>.Fail(Outcome.Invalid, $"the pattern will not compile: {e.Message}");
            }
        }
        catch (ArgumentException e)
        {
            return Result<Regex>.Fail(Outcome.Invalid, $"the pattern will not compile: {e.Message}");
        }
    }

    /// <summary>
    /// Search a set of files for any of several patterns (R4.15).
    ///
    /// <para>One hit per matching line, at the earliest column any
    /// pattern matched. Reporting a line once per pattern would return
    /// the same line several times for a caller who asked one question
    /// in several ways, and R9.2's no-double-match reasoning applies to
    /// a line as much as to a file.</para>
    ///
    /// <para><b>Bounded by size as well as count.</b> <c>limit</c> bounds
    /// the number of hits, and one hit can be a thousand characters of
    /// markup, so the hits also stop at the room the meter gives. No hit
    /// is served whatever it costs: a line too long to send is cut to a
    /// window around its match, and the hit says where the window
    /// starts. An answer that stops short says where to carry on.</para>
    /// </summary>
    public static Result<SearchAnswer> Search(
        IReadOnlyList<FoundFile> files,
        IReadOnlyList<string> patterns,
        PatternKind kind,
        bool caseSensitive,
        int context,
        int limit,
        int excluded,
        bool documents = true,
        SearchMode mode = SearchMode.Hits,
        SearchCursor? after = null,
        IMeter? meter = null)
    {
        if (patterns.Count == 0)
            return Result<SearchAnswer>.Fail(Outcome.Invalid, "no pattern was given");

        var compiled = new List<Regex>(patterns.Count);
        foreach (string p in patterns)
        {
            Result<Regex> r = Compile(p, kind, caseSensitive);
            if (!r.IsOk) return r.Carry<SearchAnswer>();
            compiled.Add(r.Value);
        }

        IMeter measure = meter ?? PlainMeter.Default;
        int room = measure.Room;

        var hits = new List<Hit>();
        var disclosed = new List<Hit>();
        var matched = new List<MatchedFile>();
        var filesWithHits = new HashSet<string>(StringComparer.Ordinal);
        int searched = 0, skipped = 0, spent = 0, total = 0;
        bool truncated = false;
        string? next = null;

        foreach (FoundFile file in files)
        {
            // A page starts after the place the last one stopped. Files
            // come in path order, so every file before the cursor's was
            // on an earlier page, and so were the lines up to its line.
            int startAfter = 0;
            if (after is not null)
            {
                int order = string.CompareOrdinal(file.Path.Display, after.Path);
                if (order < 0 || (order == 0 && mode == SearchMode.Files)) continue;
                if (order == 0) startAfter = after.Line;
            }

            IReadOnlyList<Line> lines;
            Rendering? rendering = null;

            if (Documents.For(file.Path.Full) is { } reader)
            {
                // A document has to be rendered before there is anything
                // to match against. Without this, `read` could open a PDF
                // and `search` could not find one - the same file both
                // readable and, as far as any search could tell, empty.
                if (!documents || file.Bytes > Documents.SearchByteCap) { skipped++; continue; }

                Result<Rendering> made = Documents.Render(file.Path, reader);

                // One unreadable document does not fail the sweep. It is
                // counted, so the answer can say the search was not as
                // complete as it looks.
                if (!made.IsOk) { skipped++; continue; }

                rendering = made.Value;
                lines = TextIo.SplitLines(rendering.Text);
            }
            else
            {
                Result<TextFile> read = TextIo.Read(file.Path);

                // A binary file in the path of a search is not an error: it
                // is a file with nothing to find in it. R4.5 refuses to read
                // it as text and the search moves on.
                if (!read.IsOk) continue;
                lines = read.Value.Lines;
            }

            searched++;
            int inFile = 0;

            for (int i = startAfter; i < lines.Count; i++)
            {
                (int index, int length) = EarliestMatch(compiled, lines[i].Text);
                if (index < 0) continue;

                inFile++;
                total++;

                if (mode != SearchMode.Hits)
                {
                    if (file.Path.Screen != Screened.None)
                        disclosed.Add(new Hit(file.Path, i + 1, index + 1,
                            Window(lines[i].Text, index, length).Text, []));
                    continue;
                }

                if (limit > 0 && hits.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                (string text, Clip? clip, int anchor, int width) = Window(lines[i].Text, index, length);
                var hit = new Hit(
                    file.Path,
                    i + 1,                                  // sequences are 1-based here as they are in Shoddy
                    index + 1,
                    text,
                    ContextAround(lines, i, context, anchor, width, clip is not null),
                    rendering?.Cite(i + 1),
                    clip);

                if (!Fits(ref hit, measure, room, spent, first: hits.Count == 0))
                {
                    truncated = true;
                    break;
                }

                spent += measure.Hit(hit);
                hits.Add(hit);
                filesWithHits.Add(file.Path.Full);
            }

            if (mode == SearchMode.Count && inFile > 0) filesWithHits.Add(file.Path.Full);

            if (mode == SearchMode.Files && inFile > 0)
            {
                int cost = measure.Matched(file.Path, inFile);
                bool full = limit > 0 && matched.Count >= limit;

                if (full || (matched.Count > 0 && spent + cost + measure.After(file.Path) > room))
                {
                    truncated = true;
                    next = matched[^1].Path.Display;
                    break;
                }

                spent += cost;
                matched.Add(new MatchedFile(file.Path, inFile));
            }

            if (truncated) break;
        }

        if (truncated && mode == SearchMode.Hits && hits.Count > 0) next = hits[^1].Position;

        return Result<SearchAnswer>.Ok(new SearchAnswer(
            hits,
            mode == SearchMode.Files ? matched.Count : filesWithHits.Count,
            searched, truncated, kind, patterns, excluded, skipped, files.Count,
            mode, matched, total, next, disclosed));
    }

    /// <summary>
    /// Whether a hit fits in what is left, with room for the position to
    /// carry on from after it.
    ///
    /// <para>The first hit of an answer always goes, so an answer always
    /// moves forward. If it does not fit with its context, the context
    /// lines farthest from it are dropped until it does. Without context
    /// it is a window of at most <see cref="Budget.MatchCap"/> plus twice
    /// <see cref="Budget.MatchWindow"/> characters and a path, which is
    /// far inside the ceiling.</para>
    /// </summary>
    static bool Fits(ref Hit hit, IMeter meter, int room, int spent, bool first)
    {
        if (spent + meter.Hit(hit) + meter.After(hit) <= room) return true;
        if (!first) return false;

        var context = new List<ContextLine>(hit.Context);
        while (context.Count > 0 && meter.Hit(hit with { Context = context }) + meter.After(hit) > room)
        {
            int farthest = Math.Abs(context[0].Number - hit.Line) >= Math.Abs(context[^1].Number - hit.Line)
                ? 0 : context.Count - 1;
            context.RemoveAt(farthest);
        }

        hit = hit with { Context = context };
        return true;
    }

    /// <summary>
    /// The part of a matching line a hit carries: all of it when it is
    /// short, otherwise the match and <see cref="Budget.MatchWindow"/>
    /// characters either side. Returns where the window starts and how
    /// wide it is, so the context lines can be cut to the same columns.
    /// </summary>
    static (string Text, Clip? Clip, int Anchor, int Width) Window(string line, int index, int length)
    {
        int keep = Math.Min(length, Budget.MatchCap);
        int start = Math.Max(0, index - Budget.MatchWindow);
        int end = Math.Min(line.Length, index + keep + Budget.MatchWindow);

        if (start == 0 && end == line.Length)
            return (line, null, 0, (2 * Budget.MatchWindow) + Budget.MatchCap);

        (start, end) = Whole(line, start, end);
        return (line[start..end], new Clip(start + 1, line.Length), start, end - start);
    }

    /// <summary>A window moved outwards, if it would split a surrogate
    /// pair at either end.</summary>
    static (int Start, int End) Whole(string line, int start, int end)
    {
        if (start > 0 && char.IsLowSurrogate(line[start])) start--;
        if (end < line.Length && char.IsLowSurrogate(line[end])) end++;
        return (start, end);
    }

    /// <summary>The earliest match any pattern makes in a line: where it
    /// starts and how long it is, or -1 when nothing matched.</summary>
    static (int Index, int Length) EarliestMatch(List<Regex> patterns, string line)
    {
        int best = -1, length = 0;
        foreach (Regex r in patterns)
        {
            Match m;
            try { m = r.Match(line); }
            catch (RegexMatchTimeoutException) { continue; }

            if (m.Success && (best < 0 || m.Index < best))
            {
                best = m.Index;
                length = m.Length;
            }
        }
        return (best, length);
    }

    /// <summary>
    /// The lines around a hit, each cut to the same columns as the hit
    /// where it is long enough, or to the same width from its own start
    /// where it is not. A context line is as able to be a megabyte of
    /// markup as the hit itself.
    /// </summary>
    static IReadOnlyList<ContextLine> ContextAround(
        IReadOnlyList<Line> lines, int index, int context, int anchor, int width, bool clipped)
    {
        if (context <= 0) return [];

        var around = new List<ContextLine>();
        for (int i = Math.Max(0, index - context); i <= Math.Min(lines.Count - 1, index + context); i++)
        {
            string text = lines[i].Text;
            int start = clipped ? Math.Min(anchor, Math.Max(0, text.Length - width)) : 0;
            int end = Math.Min(text.Length, start + width);

            if (start == 0 && end == text.Length)
            {
                around.Add(new ContextLine(i + 1, text));
                continue;
            }

            (start, end) = Whole(text, start, end);
            around.Add(new ContextLine(i + 1, text[start..end], new Clip(start + 1, text.Length)));
        }

        return around;
    }
}
