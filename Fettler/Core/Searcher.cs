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

/// <summary>A line near a hit, carried when context was asked for.</summary>
public sealed record ContextLine(int Number, string Text);

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
    string? Cite = null);

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
    int DocumentsSkipped = 0);

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
    /// </summary>
    public static Result<SearchAnswer> Search(
        IReadOnlyList<FoundFile> files,
        IReadOnlyList<string> patterns,
        PatternKind kind,
        bool caseSensitive,
        int context,
        int limit,
        int excluded,
        bool documents = true)
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

        var hits = new List<Hit>();
        var filesWithHits = new HashSet<string>(StringComparer.Ordinal);
        int searched = 0, skipped = 0;
        bool truncated = false;

        foreach (FoundFile file in files)
        {
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

            for (int i = 0; i < lines.Count; i++)
            {
                int column = EarliestMatch(compiled, lines[i].Text);
                if (column < 0) continue;

                if (limit > 0 && hits.Count >= limit) { truncated = true; break; }

                hits.Add(new Hit(
                    file.Path,
                    i + 1,                                  // sequences are 1-based here as they are in Shoddy
                    column + 1,
                    lines[i].Text,
                    ContextAround(lines, i, context),
                    rendering?.Cite(i + 1)));

                filesWithHits.Add(file.Path.Full);
            }

            if (truncated) break;
        }

        return Result<SearchAnswer>.Ok(new SearchAnswer(
            hits, filesWithHits.Count, searched, truncated, kind, patterns, excluded, skipped));
    }

    static int EarliestMatch(List<Regex> patterns, string line)
    {
        int best = -1;
        foreach (Regex r in patterns)
        {
            Match m;
            try { m = r.Match(line); }
            catch (RegexMatchTimeoutException) { continue; }

            if (m.Success && (best < 0 || m.Index < best)) best = m.Index;
        }
        return best;
    }

    static IReadOnlyList<ContextLine> ContextAround(IReadOnlyList<Line> lines, int index, int context)
    {
        if (context <= 0) return [];

        var around = new List<ContextLine>();
        for (int i = Math.Max(0, index - context); i <= Math.Min(lines.Count - 1, index + context); i++)
            around.Add(new ContextLine(i + 1, lines[i].Text));

        return around;
    }
}
