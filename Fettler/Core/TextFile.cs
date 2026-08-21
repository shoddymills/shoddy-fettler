using System.Security.Cryptography;
using System.Text;

namespace Fettler.Core;

/// <summary>The dominant line ending of a file, used for text an edit
/// inserts. Existing lines keep whatever ending they already had.</summary>
public enum LineEnding
{
    /// <summary>A file with no line ending in it at all - one line, no
    /// terminator. New text uses the platform's, but nothing is rewritten.</summary>
    None,
    Lf,
    CrLf,
    Cr,
}

/// <summary>
/// One line and the ending that followed it. The ending is carried per
/// line rather than per file, which is what makes R10.2's byte-identical
/// round trip hold for a file whose endings are not uniform: a mixed
/// file read and written back unchanged is unchanged, rather than
/// silently regularised into whatever the majority was.
/// </summary>
public readonly record struct Line(string Text, string Ending)
{
    public override string ToString() => Text + Ending;
}

/// <summary>
/// A text file as read, carrying the three facts a later write needs in
/// order to put it back the way it found it (R4.1), and the hash that
/// lets a caller prove it has not moved without sending it back (R5.9).
/// </summary>
public sealed record TextFile(
    ContainedPath Path,
    string Text,
    IReadOnlyList<Line> Lines,
    string EncodingName,
    LineEnding LineEnding,
    bool MixedEndings,
    bool FinalNewline,
    string Hash,
    long Bytes)
{
    public int LineCount => Lines.Count;
}

/// <summary>
/// Reading and writing text, under the stated rule of R4.14 rather than
/// a heuristic.
///
/// <para><b>The rule:</b> a byte-order mark decides and is preserved;
/// absent one, content that decodes as UTF-8 is UTF-8; content that does
/// not decode as UTF-8 is binary and is refused under R4.5. No other
/// encoding is inferred and no statistical guess is made, because a
/// wrong guess corrupts a file on the way out - the one outcome R4.2
/// exists to prevent.</para>
///
/// <para><b>One addition to that rule, made deliberately:</b> content
/// carrying a NUL byte is treated as binary even where it happens to
/// decode. R4.14's rule is about which encoding to believe; R4.5's
/// requirement is to refuse what is not text, and a file with a NUL in
/// it is not text anybody edits. Refusing it is the conservative
/// direction: the worst case is a caller told to use the file-level
/// operations of R6, which do not inspect content at all.</para>
/// </summary>
public static class TextIo
{
    /// <summary>How much of a file is examined for a NUL before it is
    /// believed to be text. Reading the whole of a large binary just to
    /// reject it is work spent to reach the same answer.</summary>
    const int SniffBytes = 8192;

    // The encodings a BOM can name. Order matters: UTF-32 LE begins with
    // the same two bytes as UTF-16 LE, so the longer mark is tested first
    // or every UTF-32 file is read as UTF-16 and quietly mangled.
    static readonly (byte[] Bom, string Name, Encoding Encoding)[] Boms =
    [
        ([0xFF, 0xFE, 0x00, 0x00], "utf-32le-bom", new UTF32Encoding(bigEndian: false, byteOrderMark: true)),
        ([0x00, 0x00, 0xFE, 0xFF], "utf-32be-bom", new UTF32Encoding(bigEndian: true, byteOrderMark: true)),
        ([0xEF, 0xBB, 0xBF], "utf-8-bom", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)),
        ([0xFF, 0xFE], "utf-16le-bom", new UnicodeEncoding(bigEndian: false, byteOrderMark: true)),
        ([0xFE, 0xFF], "utf-16be-bom", new UnicodeEncoding(bigEndian: true, byteOrderMark: true)),
    ];

    static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Read a file as text, or say why it is not text.</summary>
    public static Result<TextFile> Read(ContainedPath path)
    {
        Result<byte[]> raw = ReadBytes(path);
        if (!raw.IsOk) return raw.Carry<TextFile>();
        return Decode(path, raw.Value);
    }

    public static Result<byte[]> ReadBytes(ContainedPath path)
    {
        try
        {
            return Result<byte[]>.Ok(File.ReadAllBytes(path.Full));
        }
        catch (FileNotFoundException)
        {
            return Result<byte[]>.Fail(Outcome.NotFound, "no such file", path.Display);
        }
        catch (DirectoryNotFoundException)
        {
            return Result<byte[]>.Fail(Outcome.NotFound, "no such file", path.Display);
        }
        catch (UnauthorizedAccessException e)
        {
            return Result<byte[]>.Fail(Outcome.Denied, Denied(path, e), path.Display);
        }
        catch (IOException e)
        {
            return Result<byte[]>.Fail(Outcome.Denied, Denied(path, e), path.Display);
        }
    }

    /// <summary>The message for an operation the platform refused, kept
    /// in one place so R3.13's distinction reads the same everywhere.</summary>
    internal static string Denied(ContainedPath path, Exception e) =>
        $"the operating system refused this: {e.Message.TrimEnd('.')}";

    public static Result<TextFile> Decode(ContainedPath path, byte[] raw)
    {
        (string name, Encoding encoding, int bomLength) = DetectEncoding(raw);

        int sniff = Math.Min(raw.Length, SniffBytes);
        for (int i = bomLength; i < sniff; i++)
            if (raw[i] == 0x00 && !name.StartsWith("utf-16") && !name.StartsWith("utf-32"))
                return Result<TextFile>.Fail(Outcome.Refused,
                    "this is a binary file: it carries a NUL byte, and read, write and edit are text operations (R4.5)",
                    path.Display);

        string text;
        try
        {
            text = encoding.GetString(raw, bomLength, raw.Length - bomLength);
        }
        catch (DecoderFallbackException)
        {
            return Result<TextFile>.Fail(Outcome.Refused,
                "this is a binary file: it does not decode as UTF-8 and carries no byte-order mark (R4.14)",
                path.Display);
        }

        IReadOnlyList<Line> lines = SplitLines(text);
        return Result<TextFile>.Ok(new TextFile(
            path,
            text,
            lines,
            name,
            DominantEnding(lines),
            Mixed(lines),
            FinalNewline: lines.Count > 0 && lines[^1].Ending.Length > 0,
            Hash: HashOf(raw),
            Bytes: raw.Length));
    }

    static (string Name, Encoding Encoding, int BomLength) DetectEncoding(byte[] raw)
    {
        foreach ((byte[] bom, string name, Encoding encoding) in Boms)
            if (raw.Length >= bom.Length && raw.AsSpan(0, bom.Length).SequenceEqual(bom))
                return (name, encoding, bom.Length);

        return ("utf-8", StrictUtf8, 0);
    }

    /// <summary>
    /// The bytes a file with these properties holds. Round-tripping this
    /// against <see cref="Decode"/> with no edit in between is what
    /// R10.2 asserts, for every combination of encoding, line ending and
    /// trailing newline.
    /// </summary>
    public static byte[] Encode(string text, string encodingName)
    {
        (_, Encoding encoding, int bomLength) = EncodingByName(encodingName);
        byte[] body = encoding.GetBytes(text);
        if (bomLength == 0) return body;

        byte[] bom = encoding.GetPreamble();
        byte[] all = new byte[bom.Length + body.Length];
        bom.CopyTo(all, 0);
        body.CopyTo(all, bom.Length);
        return all;
    }

    static (string Name, Encoding Encoding, int BomLength) EncodingByName(string name)
    {
        foreach ((byte[] bom, string n, Encoding encoding) in Boms)
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase))
                return (n, encoding, bom.Length);

        return ("utf-8", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);
    }

    /// <summary>
    /// Split preserving each line's own ending, so nothing is regularised
    /// on the way past. A file that ends without a newline yields a last
    /// line whose ending is empty, which is how the trailing-newline
    /// state survives a rewrite.
    /// </summary>
    public static IReadOnlyList<Line> SplitLines(string text)
    {
        var lines = new List<Line>();
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                bool crlf = i + 1 < text.Length && text[i + 1] == '\n';
                lines.Add(new Line(text[start..i], crlf ? "\r\n" : "\r"));
                i += crlf ? 1 : 0;
                start = i + 1;
            }
            else if (text[i] == '\n')
            {
                lines.Add(new Line(text[start..i], "\n"));
                start = i + 1;
            }
        }

        if (start < text.Length) lines.Add(new Line(text[start..], string.Empty));
        return lines;
    }

    public static string Join(IEnumerable<Line> lines)
    {
        var sb = new StringBuilder();
        foreach (Line line in lines) sb.Append(line.Text).Append(line.Ending);
        return sb.ToString();
    }

    static LineEnding DominantEnding(IReadOnlyList<Line> lines)
    {
        int lf = 0, crlf = 0, cr = 0;
        foreach (Line line in lines)
            switch (line.Ending)
            {
                case "\n": lf++; break;
                case "\r\n": crlf++; break;
                case "\r": cr++; break;
            }

        if (lf == 0 && crlf == 0 && cr == 0) return LineEnding.None;
        if (crlf >= lf && crlf >= cr) return LineEnding.CrLf;
        return lf >= cr ? LineEnding.Lf : LineEnding.Cr;
    }

    /// <summary>
    /// Whether the file disagrees with itself about line endings.
    ///
    /// <para>Reported, never corrected: R10.2's byte-identical round trip
    /// means a file read and written back unchanged is unchanged, mixture
    /// and all. But reporting only the DOMINANT ending hides the mixture
    /// entirely, and a single stray CRLF in an otherwise LF file is
    /// exactly what nobody notices - it stayed invisible here until git
    /// complained about it, two commits after it was introduced.</para>
    /// </summary>
    public static bool Mixed(IReadOnlyList<Line> lines)
    {
        bool lf = false, crlf = false, cr = false;
        foreach (Line line in lines)
            switch (line.Ending)
            {
                case "\n": lf = true; break;
                case "\r\n": crlf = true; break;
                case "\r": cr = true; break;
            }

        return (lf ? 1 : 0) + (crlf ? 1 : 0) + (cr ? 1 : 0) > 1;
    }

    public static string EndingText(LineEnding ending) => ending switch
    {
        LineEnding.CrLf => "\r\n",
        LineEnding.Cr => "\r",
        _ => "\n",
    };

    public static string EndingName(LineEnding ending) => ending switch
    {
        LineEnding.CrLf => "crlf",
        LineEnding.Cr => "cr",
        LineEnding.Lf => "lf",
        _ => "none",
    };

    /// <summary>
    /// The content hash of R5.9, over the raw bytes rather than the
    /// decoded text: it has to prove the file on disk has not moved, and
    /// a BOM or a line ending changing underneath a caller is exactly the
    /// kind of move it must catch.
    /// </summary>
    /// <summary>
    /// Text with a leading byte-order mark taken off.
    ///
    /// <para>A BOM is a fact about a FILE's encoding, not a character in
    /// its content, and it belongs at the head of a file or nowhere.
    /// Text arriving on a stream carries one whenever the producer felt
    /// like emitting one - Windows PowerShell does, from
    /// <c>[Console]::OutputEncoding</c>, even when told not to - and
    /// keeping it is silently wrong every time: a search pattern
    /// prefixed with U+FEFF matches nothing, and replacement text
    /// prefixed with one splices an invisible character into the MIDDLE
    /// of a file, where no BOM has any meaning at all. Both were
    /// observed before this existed.</para>
    ///
    /// <para>Only the first one goes, and only at the very front. A BOM
    /// later in the text is a zero-width no-break space that somebody
    /// may have meant.</para>
    /// </summary>
    public static string WithoutMark(string text) =>
        text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;

    public static string HashOf(byte[] raw) =>
        Convert.ToHexStringLower(SHA256.HashData(raw));
}
