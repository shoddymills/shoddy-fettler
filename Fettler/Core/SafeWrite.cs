using System.Text;

namespace Fettler.Core;

/// <summary>What a write did, including what it could not carry over.</summary>
public sealed record Saved(
    ContainedPath Path,
    long Bytes,
    string EncodingName,
    LineEnding LineEnding,
    bool MixedEndings,
    bool FinalNewline,
    string Hash,
    bool Created,
    IReadOnlyList<string> MetadataLost);

/// <summary>
/// The write of R4.3: interrupted at any point it leaves either the
/// original content or the new content, never a truncated file.
///
/// <para><b>The mechanism is stage-and-replace.</b> Content goes to a
/// temporary file beside the destination - beside, so the replace is a
/// rename within one volume rather than a copy across two - is forced to
/// the disk, and only then takes the destination's name. A crash before
/// the rename leaves the original untouched and a stray temporary; a
/// crash after it leaves the new content whole. There is no moment at
/// which the destination holds half of anything.</para>
///
/// <para><b>And it carries the file's metadata across (R4.18).</b> The
/// replace makes a new file wear an old name, and a new file has none of
/// the old one's attributes, alternate data streams or extended
/// attributes unless they are deliberately moved. Left alone this
/// silently strips a Mark-of-the-Web or a quarantine flag on the way
/// past, which is a security marker weakened as a side effect of editing
/// text. What could not be carried is named in <see cref="Saved"/>
/// rather than swallowed.</para>
/// </summary>
public static class SafeWrite
{
    public static Result<Saved> Text(ContainedPath path, string text, string encodingName,
        LineEnding ending, bool overwrite, bool allowCredential = false)
    {
        // A DOCUMENT IS NOT ITS RENDERING, and this is where that stops
        // being a remark and starts being enforced. What `read` and
        // `search` hand back for a workbook or a Word document is text
        // pulled out of it, with the sheets, the formulas, the tables and
        // every piece of structure left behind. Writing that text back
        // would not edit the file, it would replace it with a fraction of
        // itself, still wearing the name that says it is a document.
        //
        // Here rather than in each of write, edit and replace, for the
        // same reason the credential screen is here: every verb that puts
        // TEXT anywhere arrives at this one function, so there is no verb
        // to forget. `extract` is deliberately not caught by it - that
        // writes a document's own bytes, which is a perfectly good way to
        // get one onto the disk.
        if (Documents.For(path.Full) is { } reader)
            return Result<Saved>.Fail(Outcome.Refused,
                $"this is read as a {Typed.NameOf(reader.Kind)}, and what read and search give back "
                + "for one is a rendering rather than the file; writing that text here would leave "
                + "the name and destroy the document", path.Display);

        // THE SAME ARGUMENT, ONE STEP WIDER. An image, an archive and a
        // lone gzip are not text either. `read` gives back an image's
        // facts and an archive's manifest - neither of which is the file
        // - so text written at one of those names is never the file
        // going back, it is the file gone.
        //
        // `edit` and `replace` were never exposed to this: both read the
        // file as text before they change anything and R4.5 stops them
        // there. `write --overwrite` reads only to LEARN the encoding
        // (Bench.WriteAsync), keeps going when that read fails, and
        // reached the disk. A PNG replaced by a line of text is the same
        // loss as a workbook replaced by its own rendering, and it had
        // no refusal in front of it.
        //
        // A notebook is deliberately NOT here. A `.ipynb` IS JSON, so
        // text written at one is the file however much `read` renders it
        // as cells - and refusing it would make notebooks the one text
        // format this tool cannot write.
        FileKind kind = Typed.KindOf(path.Full);
        if (kind is FileKind.Image or FileKind.Archive or FileKind.Gzip)
            return Result<Saved>.Fail(Outcome.Refused,
                $"this is read as {Article(kind)} and is not text; writing text here would leave "
                + "the name and destroy the file. The file-level operations of R6 - move, copy, "
                + "delete - do not inspect content and are the way to shift one of these",
                path.Display);

        byte[] bytes = TextIo.Encode(text, encodingName);
        // The text goes down with the bytes: a UTF-16 file re-encodes to
        // something the credential screen could not decode, and a screen
        // that silently skipped every file in a second encoding would be
        // worse than none, because nothing would say it had.
        Result<Saved> saved = Bytes(path, bytes, overwrite, allowCredential, text);
        if (!saved.IsOk) return saved;

        // Defect 1.2: `read` says when a file disagrees with itself about
        // line endings, and `write` said nothing - so a file could be
        // made mixed by a write and only found out about on the next
        // read, or by version control two commits later. The mixture is
        // computed from what was actually written rather than from what
        // was asked for.
        IReadOnlyList<Line> lines = TextIo.SplitLines(text);
        return Result<Saved>.Ok(saved.Value with
        {
            EncodingName = encodingName,
            LineEnding = ending,
            MixedEndings = TextIo.Mixed(lines),
            FinalNewline = lines.Count > 0 && lines[^1].Ending.Length > 0,
        });
    }

    /// <summary>"an image", "an archive", "a gzip". A refusal is read by
    /// a person at the moment something they asked for did not happen,
    /// and the wrong article there is a small piece of evidence that
    /// nobody looked at the message.</summary>
    static string Article(FileKind kind)
    {
        string name = Typed.NameOf(kind);
        return "aeiou".Contains(name[0]) ? $"an {name}" : $"a {name}";
    }

    public static Result<Saved> Bytes(ContainedPath path, byte[] content, bool overwrite,
        bool allowCredential = false, string? contentText = null)
    {
        string full = path.Full;

        if (Directory.Exists(full))
            return Result<Saved>.Fail(Outcome.Refused,
                "a directory is already using that name", path.Display);

        bool exists = File.Exists(full);
        if (exists && !overwrite)
            return Result<Saved>.Fail(Outcome.TargetExists,
                "the file exists; pass overwrite to replace it", path.Display);

        string? parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(parent))
            return Result<Saved>.Fail(Outcome.NotFound,
                "the directory to write into does not exist; create it first", path.Display);

        // R6.10: the Windows read-only attribute blocks a replace with an
        // error that names nothing. Read it and say so instead.
        Result<Saved> readOnly = RefuseIfReadOnly<Saved>(full, path);
        if (!readOnly.IsOk) return readOnly;

        // The credential net, here because every verb that puts bytes
        // anywhere arrives at this one function - so there is one place
        // to get it right rather than a check per verb and a gap wherever
        // one was forgotten.
        if (!allowCredential)
        {
            Result<Saved> screened = Screen(path, content, contentText, exists);
            if (!screened.IsOk) return screened;
        }

        FileMetadata carried = Metadata.Capture(full);
        string temp = Path.Combine(parent, $".fettle-{Guid.NewGuid():N}.tmp");

        try
        {
            // WriteThrough plus an explicit flush-to-disk: without it the
            // rename can reach the disk before the bytes do, and a crash
            // in that window leaves the destination naming content that
            // was never written.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, bufferSize: 4096, FileOptions.WriteThrough))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(flushToDisk: true);
            }

            Replace(temp, full, exists);
        }
        catch (UnauthorizedAccessException e)
        {
            Discard(temp);
            return Result<Saved>.Fail(Outcome.Denied, TextIo.Denied(path, e), path.Display);
        }
        catch (IOException e)
        {
            Discard(temp);
            return Result<Saved>.Fail(Outcome.Denied, TextIo.Denied(path, e), path.Display);
        }

        IReadOnlyList<string> lost = carried.IsEmpty
            ? []
            : Metadata.Apply(full, carried);

        return Result<Saved>.Ok(new Saved(
            path, content.Length, "utf-8", LineEnding.Lf, MixedEndings: false,
            FinalNewline: false, Hash: TextIo.HashOf(content),
            Created: !exists, MetadataLost: lost));
    }

    static void Replace(string temp, string destination, bool destinationExists)
    {
        if (!destinationExists)
        {
            File.Move(temp, destination);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                // ReplaceFile keeps the destination's attributes and ACLs,
                // which is strictly more than a move does. It refuses
                // across volumes and on some network shares, and the move
                // below is the answer when it does.
                File.Replace(temp, destination, destinationBackupFileName: null);
                return;
            }
            catch (IOException) { }
            catch (PlatformNotSupportedException) { }
        }

        File.Move(temp, destination, overwrite: true);
    }

    /// <summary>
    /// R6.10, in the one place every write passes through: the Windows
    /// read-only attribute is read and named rather than met as an
    /// obscure error further down.
    /// </summary>
    internal static Result<T> RefuseIfReadOnly<T>(string full, ContainedPath path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(full)) return Result<T>.Ok(default!);

        try
        {
            if (File.GetAttributes(full).HasFlag(FileAttributes.ReadOnly))
                return Result<T>.Fail(Outcome.Denied,
                    "the file carries the Windows read-only attribute; clear it, or pass force where the verb offers it",
                    path.Display);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        return Result<T>.Ok(default!);
    }

    /// <summary>
    /// Refuse a write that would ADD a credential the file does not
    /// already carry.
    ///
    /// <para>The previous content is read through <see cref="TextIo"/>
    /// rather than decoded here, so the baseline is whatever the file
    /// actually says in whatever encoding it is in. A file that will not
    /// read as text has no baseline, and everything in the new content
    /// counts as introduced - which is the safe direction.</para>
    ///
    /// <para>Content that is not text is not screened at all. A scanner
    /// hunting credentials through arbitrary binary would find them in
    /// noise, and the refusal it produced would be unanswerable.</para>
    /// </summary>
    static Result<Saved> Screen(ContainedPath path, byte[] content, string? contentText,
                                bool exists)
    {
        string? updated = contentText ?? AsUtf8(content);
        if (updated is null) return Result<Saved>.Ok(default!);

        string? existing = null;
        if (exists)
        {
            Result<TextFile> was = TextIo.Read(path);
            if (was.IsOk) existing = was.Value.Text;
        }

        IReadOnlyList<SecretFinding> introduced = Secrets.Introduced(existing, updated);
        return introduced.Count == 0
            ? Result<Saved>.Ok(default!)
            : Result<Saved>.Fail(Outcome.Credential, Secrets.Describe(introduced), path.Display);
    }

    static string? AsUtf8(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    static void Discard(string temp)
    {
        try { if (File.Exists(temp)) File.Delete(temp); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
