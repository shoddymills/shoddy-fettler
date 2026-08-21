using System.Text;

namespace Fettler.Core;

/// <summary>
/// Where a stretch of a rendered document begins, in the document's own
/// terms: "page 7", "Sheet2", a paragraph number.
/// </summary>
public sealed record Landmark(int Line, string Label);

/// <summary>
/// A document rendered to text, and the marks that say where each stretch
/// of that text came from.
///
/// <para><b>The rendering is not the file, which is the whole reason the
/// landmarks are here.</b> A line number into a rendering nobody can see
/// is not a citation. It cannot be checked against the document, and it
/// moves the moment the renderer changes. A page or a sheet is a thing
/// the person holding the document can actually turn to.</para>
/// </summary>
public sealed record Rendering(string Text, IReadOnlyList<Landmark> Landmarks)
{
    /// <summary>
    /// The label covering a 1-based rendered line, or null when the line
    /// sits before the first mark or the reader set none.
    ///
    /// <para>A walk rather than a search: landmarks are pages and sheets,
    /// so there are tens or hundreds of them, and a binary search here
    /// would be cleverness bought with nothing.</para>
    /// </summary>
    public string? Cite(int line)
    {
        string? label = null;

        foreach (Landmark mark in Landmarks)
        {
            if (mark.Line > line) break;
            label = mark.Label;
        }

        return label;
    }
}

/// <summary>
/// A file type Fettler renders to text in order to READ it, and never
/// writes.
///
/// <para><b>This is a table, not a plugin loader.</b> Adding a reader
/// means adding a class here and an entry in
/// <see cref="Documents.Readers"/>, compiled in and tested like the rest.
/// Nothing is discovered at run time and no assembly is loaded from a
/// path. That refusal is the same one the rest of Fettler makes: running
/// a DECLARED, NAMED command already needs an explicit <c>execute</c>
/// grant, so a loader that ran unnamed code in process at full trust
/// would be a hole straight through the boundary this tool exists to
/// draw. For a format Fettler does not ship, the honest form is the one
/// that already exists - a declared converter task, gated by
/// <c>execute</c>.</para>
///
/// <para><b>Read only, and that is a property of the format rather than
/// a policy.</b> Rendering throws away everything that is not text, so
/// writing a rendering back would not be an edit, it would be a
/// demolition. <c>write</c>, <c>edit</c> and <c>replace</c> refuse these
/// kinds by name.</para>
/// </summary>
public interface IDocumentReader
{
    /// <summary>The extensions this reader claims, lowercase and with the
    /// dot, matched the way <see cref="Typed.KindOf"/> matches: by what
    /// the caller and their tools already agree the file is.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>What <c>read</c> reports the file as, so a caller is told
    /// what they were handed rather than being given text of unstated
    /// origin.</summary>
    FileKind Kind { get; }

    /// <summary>
    /// The document as text, with the marks saying where each part came
    /// from.
    ///
    /// <para>The bytes are passed in rather than read here, so that one
    /// read serves the cache, the hash and the rendering.</para>
    /// </summary>
    Result<Rendering> Render(ContainedPath path, byte[] content);
}

/// <summary>
/// The readers Fettler ships, and the small cache in front of them.
/// </summary>
public static class Documents
{
    /// <summary>
    /// Every reader, in one list. <b>This is the extension point.</b>
    /// </summary>
    static readonly IDocumentReader[] Readers = [new PdfReader(), new XlsxReader(), new DocxReader()];

    /// <summary>
    /// How large a document may be before <c>search</c> declines to
    /// render it.
    ///
    /// <para>Rendering is orders of magnitude dearer than decoding text,
    /// and a search that sweeps a tree meets whatever is in it. The cap
    /// keeps one enormous file from turning a search into a hang - and
    /// what it skipped is reported rather than quietly dropped, because
    /// a clean-looking zero is the wrong answer to give somebody whose
    /// document was never opened.</para>
    /// </summary>
    public const long SearchByteCap = 32L * 1024 * 1024;

    /// <summary>How many renderings are kept.</summary>
    public const int CacheEntries = 24;

    /// <summary>A rendering longer than this is used and not kept, so one
    /// enormous document cannot evict everything else.</summary>
    public const int CacheCharCap = 2_000_000;

    /// <summary>
    /// What a cached rendering is keyed on.
    ///
    /// <para><b>Length and modification time, not a content hash.</b> The
    /// hash would be exact and would cost a full read of the file on
    /// every lookup, which is most of what the cache is here to save. The
    /// gap is a file rewritten to the same length within the resolution
    /// of its own timestamp; that is not a shape reached by accident, and
    /// the alternative pays for it on every call.</para>
    /// </summary>
    readonly record struct Stamp(string Path, long Bytes, long Modified);

    static readonly object Gate = new();
    static readonly Dictionary<Stamp, Rendering> Cached = new();
    static readonly Queue<Stamp> Order = new();

    /// <summary>The reader claiming this path, or null if none does.</summary>
    public static IDocumentReader? For(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension.Length == 0) return null;

        foreach (IDocumentReader reader in Readers)
            foreach (string claimed in reader.Extensions)
                if (string.Equals(claimed, extension, StringComparison.Ordinal))
                    return reader;

        return null;
    }

    /// <summary>Every extension any reader claims, for the help text and
    /// the tool descriptions, so those cannot drift from the table.</summary>
    public static IReadOnlyList<string> Extensions()
    {
        var all = new List<string>();
        foreach (IDocumentReader reader in Readers) all.AddRange(reader.Extensions);
        all.Sort(StringComparer.Ordinal);
        return all;
    }

    /// <summary>
    /// Render a document, through the cache.
    /// </summary>
    public static Result<Rendering> Render(ContainedPath path, IDocumentReader reader)
    {
        Stamp stamp = StampOf(path);

        if (stamp.Path is not null)
            lock (Gate)
                if (Cached.TryGetValue(stamp, out Rendering? already))
                    return Result<Rendering>.Ok(already);

        Result<byte[]> raw = TextIo.ReadBytes(path);
        if (!raw.IsOk) return raw.Carry<Rendering>();

        Result<Rendering> rendered = reader.Render(path, raw.Value);
        if (!rendered.IsOk) return rendered;

        if (stamp.Path is not null && rendered.Value.Text.Length <= CacheCharCap) Keep(stamp, rendered.Value);

        return rendered;
    }

    static Stamp StampOf(ContainedPath path)
    {
        try
        {
            var info = new FileInfo(path.Full);
            if (!info.Exists) return default;
            return new Stamp(path.Full, info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not being able to stamp it is not a failure to read it: the
            // rendering still happens, it simply is not cached.
            return default;
        }
    }

    static void Keep(Stamp stamp, Rendering rendering)
    {
        lock (Gate)
        {
            if (Cached.ContainsKey(stamp)) return;

            Cached[stamp] = rendering;
            Order.Enqueue(stamp);

            while (Order.Count > CacheEntries) Cached.Remove(Order.Dequeue());
        }
    }

    /// <summary>Empties the cache. For tests, which need a cold start to
    /// prove the cache is not what made them pass.</summary>
    public static void Forget()
    {
        lock (Gate)
        {
            Cached.Clear();
            Order.Clear();
        }
    }
}

/// <summary>
/// A PDF as text, page by page, with each page announced so a citation
/// can name one.
///
/// <para>Text extraction only. A PDF that carries no text layer - a scan
/// - yields nothing, and the answer says so rather than returning an
/// empty file that reads as an empty document.</para>
/// </summary>
sealed class PdfReader : IDocumentReader
{
    public IReadOnlyList<string> Extensions => [".pdf"];

    public FileKind Kind => FileKind.Pdf;

    public Result<Rendering> Render(ContainedPath path, byte[] content)
    {
        // Lines are accumulated rather than appended to one buffer so a
        // landmark's line number is exact by construction. Counting
        // newlines in extracted text afterwards would be the same
        // arithmetic done less reliably.
        var lines = new List<string>();
        var marks = new List<Landmark>();
        int pages = 0, withText = 0;

        try
        {
            using UglyToad.PdfPig.PdfDocument document = UglyToad.PdfPig.PdfDocument.Open(content);
            pages = document.NumberOfPages;

            foreach (UglyToad.PdfPig.Content.Page page in document.GetPages())
            {
                marks.Add(new Landmark(lines.Count + 1, $"page {page.Number} of {pages}"));
                lines.Add($"=== page {page.Number} of {pages} ===");

                string body = page.Text;
                if (body.Trim().Length > 0) withText++;

                foreach (Line line in TextIo.SplitLines(body)) lines.Add(line.Text);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            // PdfPig throws several exception types for a malformed file
            // and does not document a closed set, so this is deliberately
            // broad - a PDF Fettler cannot read must be a refusal, never
            // a crash that takes the MCP server down with it.
            return Result<Rendering>.Fail(Outcome.Refused,
                "this PDF could not be read: " + e.Message, path.Display);
        }

        if (pages > 0 && withText == 0)
            return Result<Rendering>.Fail(Outcome.Refused,
                $"this PDF has {pages} page(s) and no text layer at all, so it is probably a scan; "
                + "nothing here does character recognition", path.Display);

        var text = new StringBuilder();
        foreach (string line in lines) text.Append(line).Append('\n');

        return Result<Rendering>.Ok(new Rendering(text.ToString(), marks));
    }
}
