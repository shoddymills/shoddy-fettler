using System.Text;

namespace Fettler.Core;

/// <summary>
/// One change to one file. R5.4 is why there is more than an exact
/// unique string here: requiring global uniqueness fails constantly in a
/// tree of small, repetitive definitions, so a match can be scoped to a
/// line range and disambiguation does not mean quoting ever more
/// surrounding context.
/// </summary>
public abstract record Edit
{
    /// <summary>Replace text, optionally only within a range of lines.</summary>
    public sealed record Replace(string Find, string With, int? FromLine = null, int? ToLine = null, bool All = false) : Edit;

    /// <summary>Insert after a line; line 0 puts it at the top.</summary>
    public sealed record InsertAfter(int Line, string Text) : Edit;

    /// <summary>Delete a range of lines, both ends included.</summary>
    public sealed record DeleteLines(int From, int To) : Edit;
}

/// <summary>
/// The edits for one file, and what the caller believes the file to be.
///
/// <para><c>ExpectedHash</c> is R5.5 satisfied through R5.9: the check is
/// on content, but the caller proves the content with a hash instead of
/// echoing the file back. The guarantee is identical and the cost is
/// sixty characters rather than the whole file, which is the difference
/// between a check that gets used and one that gets left out.</para>
/// </summary>
public sealed record FileEdits(string Path, string? ExpectedHash, IReadOnlyList<Edit> Edits);

/// <summary>What one edit would do, for the dry run of R5.6.</summary>
public sealed record PlannedEdit(string Path, int Index, int Line, string Description);

/// <summary>What happened to one file.</summary>
public sealed record EditedFile(
    ContainedPath Path,
    int EditsApplied,
    string Hash,
    bool Written,
    bool MixedEndings,
    IReadOnlyList<string> MetadataLost);

/// <summary>What the batch did, or would have done.</summary>
public sealed record EditAnswer(
    IReadOnlyList<EditedFile> Files,
    IReadOnlyList<PlannedEdit> Plan,
    bool DryRun);

/// <summary>
/// The editing of R5.
///
/// <para><b>Every edit resolves against the file as read, before any of
/// them is applied (R5.2)</b>, so edits cannot perturb each other's
/// addressing: an edit at line 150 means line 150 of the file the caller
/// saw, not of whatever the earlier edits left behind.</para>
///
/// <para><b>The batch is all-or-nothing, and now across files
/// (R5.3, R5.7).</b> Renaming a type touches every file that names it,
/// and a batch scoped to one file turns that into one refusal boundary
/// per file - a failure part-way leaves the tree half-edited with
/// nothing recording where it stopped. Every edit in every file is
/// resolved first; only when the whole set resolves does anything get
/// written. Where the filesystem cannot make the write itself atomic
/// across files, and it cannot, the answer names exactly which files
/// were replaced.</para>
/// </summary>
public static class Editor
{
    readonly record struct Resolved(int Start, int Length, string Replacement, int Line, string Description);

    public static Result<EditAnswer> Apply(Roots roots, IReadOnlyList<FileEdits> batch,
                                          bool dryRun, bool allowCredential = false)
    {
        if (batch.Count == 0)
            return Result<EditAnswer>.Fail(Outcome.Invalid, "the batch is empty");

        // ---- resolve everything first (R5.2, R5.3, R5.7) ----

        var planned = new List<(TextFile File, List<Resolved> Edits, string NewText)>();
        var plan = new List<PlannedEdit>();

        foreach (FileEdits fileEdits in batch)
        {
            // An edit changes an existing file, so it is always update -
            // there is no branch on existence here, because an edit to a
            // file that is not there is a NotFound from the read below
            // rather than a creation.
            Result<ContainedPath> path = roots.Resolve(fileEdits.Path, Permission.Update);
            if (!path.IsOk) return path.Carry<EditAnswer>();

            Result<TextFile> read = TextIo.Read(path.Value);
            if (!read.IsOk) return read.Carry<EditAnswer>();
            TextFile file = read.Value;

            // R5.5 and R5.8: the check and the write it guards are one
            // step, and this is the check. Nothing below writes until
            // every file in the batch has passed it.
            if (fileEdits.ExpectedHash is { } expected
                && !expected.Equals(file.Hash, StringComparison.OrdinalIgnoreCase))
                return Result<EditAnswer>.Fail(Outcome.Stale,
                    "the file changed since it was read; read it again and rebuild the edit",
                    path.Value.Display);

            var resolved = new List<Resolved>();
            for (int i = 0; i < fileEdits.Edits.Count; i++)
            {
                Result<Resolved> one = ResolveOne(file, fileEdits.Edits[i], i + 1);
                if (!one.IsOk) return one.Carry<EditAnswer>();

                resolved.Add(one.Value);
                plan.Add(new PlannedEdit(path.Value.Display, i + 1, one.Value.Line, one.Value.Description));
            }

            Result<string> merged = Merge(file, resolved, path.Value);
            if (!merged.IsOk) return merged.Carry<EditAnswer>();

            planned.Add((file, resolved, merged.Value));
        }

        if (dryRun)
            return Result<EditAnswer>.Ok(new EditAnswer([], plan, DryRun: true));

        // ---- then write, reporting exactly what was replaced (R5.7) ----

        var written = new List<EditedFile>();
        foreach ((TextFile file, List<Resolved> edits, string newText) in planned)
        {
            Result<Saved> saved = SafeWrite.Text(
                file.Path, newText, file.EncodingName, file.LineEnding, overwrite: true,
                allowCredential: allowCredential);

            if (!saved.IsOk)
                return Result<EditAnswer>.Fail(new Failure(
                    saved.Failure!.Outcome,
                    written.Count == 0
                        ? $"nothing was written: {saved.Failure.Message}"
                        : $"{written.Count} file(s) were already replaced before this one failed: {saved.Failure.Message}",
                    file.Path.Display));

            written.Add(new EditedFile(
                file.Path, edits.Count, saved.Value.Hash, Written: true,
                saved.Value.MixedEndings, saved.Value.MetadataLost));
        }

        return Result<EditAnswer>.Ok(new EditAnswer(written, plan, DryRun: false));
    }

    // ---- resolving one edit against the file as read ----

    static Result<Resolved> ResolveOne(TextFile file, Edit edit, int index) => edit switch
    {
        Edit.Replace r => ResolveReplace(file, r, index),
        Edit.InsertAfter i => ResolveInsert(file, i, index),
        Edit.DeleteLines d => ResolveDelete(file, d, index),
        _ => Result<Resolved>.Fail(Outcome.Invalid, $"edit {index} is of no kind this understands"),
    };

    static Result<Resolved> ResolveReplace(TextFile file, Edit.Replace r, int index)
    {
        if (r.Find.Length == 0)
            return Result<Resolved>.Fail(Outcome.Invalid, $"edit {index} looks for nothing");

        int[] starts = LineStarts(file.Lines);

        int from = 0;
        int to = file.Text.Length;

        if (r.FromLine is { } f)
        {
            if (f < 1 || f > file.Lines.Count)
                return Result<Resolved>.Fail(Outcome.Invalid,
                    $"edit {index} scopes from line {f}, and the file has {file.Lines.Count}");
            from = starts[f - 1];
        }

        if (r.ToLine is { } t)
        {
            if (t < 1 || t > file.Lines.Count)
                return Result<Resolved>.Fail(Outcome.Invalid,
                    $"edit {index} scopes to line {t}, and the file has {file.Lines.Count}");
            to = starts[t];
        }

        if (from > to)
            return Result<Resolved>.Fail(Outcome.Invalid, $"edit {index} scopes a range that runs backwards");

        // Every occurrence that fits entirely inside the scoped region.
        // Overlapping matches are not counted twice: the scan resumes
        // after the match it just took.
        List<int> Scan(string needle)
        {
            var hits = new List<int>();
            for (int at = from; at <= to - needle.Length;)
            {
                int hit = file.Text.IndexOf(needle, at, to - at, StringComparison.Ordinal);
                if (hit < 0) break;
                hits.Add(hit);
                at = hit + needle.Length;
            }
            return hits;
        }

        // Defect 1.1: an anchor spanning more than one line carries
        // whatever endings the caller's shell produced, and a CRLF anchor
        // never matches an LF file no matter how right it looks. The
        // exact match is tried FIRST, so a caller who genuinely means
        // CRLF still wins on a CRLF file; only when that finds nothing is
        // the anchor retried in the file's own endings. A fallback, not a
        // change of primary semantics.
        string find = r.Find;
        List<int> found = Scan(find);

        if (found.Count == 0)
        {
            string retried = InTheFilesEndings(file, r.Find);
            if (!retried.Equals(r.Find, StringComparison.Ordinal))
            {
                List<int> again = Scan(retried);
                if (again.Count > 0) { find = retried; found = again; }
            }
        }

        if (found.Count == 0)
            return Result<Resolved>.Fail(Outcome.NotFound,
                $"edit {index} found no \"{Shorten(r.Find)}\"" + (r.FromLine is null ? "" : " in the lines it was scoped to"));

        // R5.3: ambiguity is a refusal, not a guess. R5.4's line range is
        // the way out that does not mean quoting more context.
        if (found.Count > 1 && !r.All)
            return Result<Resolved>.Fail(Outcome.Conflict,
                $"edit {index} matched {found.Count} places; scope it with a line range, or say to replace all");

        if (r.All)
        {
            // Replacing every occurrence is one span over the whole
            // scoped region, so it cannot overlap another edit in a way
            // the overlap check would miss.
            string region = file.Text[from..to];
            return Result<Resolved>.Ok(new Resolved(
                from, to - from,
                region.Replace(find, InTheFilesEndings(file, r.With), StringComparison.Ordinal),
                LineOf(starts, found[0]),
                $"replace {found.Count} x \"{Shorten(r.Find)}\" -> \"{Shorten(r.With)}\""));
        }

        return Result<Resolved>.Ok(new Resolved(
            found[0], find.Length, InTheFilesEndings(file, r.With), LineOf(starts, found[0]),
            $"replace \"{Shorten(r.Find)}\" -> \"{Shorten(r.With)}\""));
    }

    static Result<Resolved> ResolveInsert(TextFile file, Edit.InsertAfter i, int index)
    {
        if (i.Line < 0 || i.Line > file.Lines.Count)
            return Result<Resolved>.Fail(Outcome.Invalid,
                $"edit {index} inserts after line {i.Line}, and the file has {file.Lines.Count}");

        int[] starts = LineStarts(file.Lines);
        string ending = TextIo.EndingText(file.LineEnding);

        // Inserting after a last line that has no ending has to supply
        // one first, or the new text joins the old line rather than
        // following it.
        string incoming = InTheFilesEndings(file, i.Text);

        if (i.Line == file.Lines.Count && !file.FinalNewline && file.Lines.Count > 0)
            return Result<Resolved>.Ok(new Resolved(
                file.Text.Length, 0, ending + incoming, i.Line, "insert after (adding a final newline first)"));

        return Result<Resolved>.Ok(new Resolved(
            i.Line == 0 ? 0 : starts[i.Line], 0, incoming + ending, i.Line, "insert after"));
    }

    static Result<Resolved> ResolveDelete(TextFile file, Edit.DeleteLines d, int index)
    {
        if (d.From < 1 || d.To < d.From || d.To > file.Lines.Count)
            return Result<Resolved>.Fail(Outcome.Invalid,
                $"edit {index} deletes lines {d.From}-{d.To}, and the file has {file.Lines.Count}");

        int[] starts = LineStarts(file.Lines);
        int start = starts[d.From - 1];
        int end = starts[d.To];

        return Result<Resolved>.Ok(new Resolved(
            start, end - start, string.Empty, d.From, $"delete {d.To - d.From + 1} line(s)"));
    }

    // ---- merging resolved edits ----

    static Result<string> Merge(TextFile file, List<Resolved> edits, ContainedPath path)
    {
        var ordered = new List<Resolved>(edits);
        ordered.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Length.CompareTo(b.Length));

        for (int i = 1; i < ordered.Count; i++)
        {
            Resolved previous = ordered[i - 1];
            Resolved current = ordered[i];

            // Adjacency is not overlap: an edit ending exactly where the
            // next begins leaves no character claimed twice, and two
            // zero-length inserts at one point are two lines arriving in
            // a stated order rather than a contradiction.
            if (previous.Start + previous.Length > current.Start)
                return Result<string>.Fail(Outcome.Conflict,
                    $"two edits cover the same text (around line {current.Line}); nothing written",
                    path.Display);
        }

        // Applied from the end, so an earlier edit's offsets are still
        // the offsets of the file as read (R5.2).
        string text = file.Text;
        for (int i = ordered.Count - 1; i >= 0; i--)
        {
            Resolved e = ordered[i];
            text = string.Concat(text.AsSpan(0, e.Start), e.Replacement, text.AsSpan(e.Start + e.Length));
        }

        return Result<string>.Ok(text);
    }

    /// <summary>
    /// Text the caller supplied, written in the line endings the file
    /// actually uses.
    ///
    /// <para><b>Text an edit inserts is normalised; text already in the
    /// file is not.</b> A caller composing a replacement cannot be
    /// expected to know what a file's endings are, and on Windows the
    /// shell decides for them - a PowerShell here-string is CRLF whatever
    /// the file is. Splicing that in verbatim leaves a file disagreeing
    /// with itself by one line, which nothing notices until a version
    /// control system does. That happened in this repository, to this
    /// file's own project, and is why this exists.</para>
    ///
    /// <para>Existing lines keep their own endings, so R10.2's
    /// byte-identical round trip still holds: reading a mixed file and
    /// writing it back unchanged changes nothing. Only what an edit
    /// brings in is made to match.</para>
    /// </summary>
    static string InTheFilesEndings(TextFile file, string text)
    {
        if (text.Length == 0) return text;

        string ending = TextIo.EndingText(file.LineEnding);
        var rewritten = new StringBuilder(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                rewritten.Append(ending);
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (c == '\n') rewritten.Append(ending);
            else rewritten.Append(c);
        }

        return rewritten.ToString();
    }

    // ---- line arithmetic ----

    /// <summary>Character offset of each line's first character, with one
    /// extra entry for the end of the file, so <c>starts[n]</c> is always
    /// where line n+1 would begin.</summary>
    static int[] LineStarts(IReadOnlyList<Line> lines)
    {
        int[] starts = new int[lines.Count + 1];
        int at = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            starts[i] = at;
            at += lines[i].Text.Length + lines[i].Ending.Length;
        }
        starts[lines.Count] = at;
        return starts;
    }

    static int LineOf(int[] starts, int offset)
    {
        for (int i = 0; i < starts.Length - 1; i++)
            if (offset < starts[i + 1]) return i + 1;
        return Math.Max(1, starts.Length - 1);
    }

    static string Shorten(string s)
    {
        string oneLine = s.Replace("\r", "").Replace("\n", "\\n");
        return oneLine.Length <= 40 ? oneLine : oneLine[..37] + "...";
    }
}
