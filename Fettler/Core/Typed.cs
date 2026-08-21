using System.Text;
using System.Text.Json;

namespace Fettler.Core;

/// <summary>What kind of file this is, and therefore how it is read.</summary>
public enum FileKind
{
    /// <summary>Anything that decodes as text, which is most of a source
    /// tree - and includes CSV, which is why a spreadsheet exported as
    /// CSV needs nothing here.</summary>
    Text,

    /// <summary>A Jupyter notebook: JSON, read as cells rather than as
    /// the JSON it happens to be stored in.</summary>
    Notebook,

    /// <summary>A raster image. Fettler never decodes the pixels; it
    /// reports the facts and hands the bytes on.</summary>
    Image,

    /// <summary>A PDF, read as text page by page.</summary>
    Pdf,

    /// <summary>A workbook, read sheet by sheet as rows of cells - with
    /// the formulas, not only what they last worked out to.</summary>
    Spreadsheet,

    /// <summary>A word-processor document, read as its paragraphs and
    /// tables, with its headings kept as headings so a hit can be cited
    /// under one.</summary>
    Document,

    /// <summary>A zip or a tar, read as its manifest - the members, their
    /// sizes and whether each carries the executable bit. Never unpacked
    /// by reading it; <c>extract</c> is the verb that writes.</summary>
    Archive,

    /// <summary>A lone gzip: one file wearing a coat, read as whatever it
    /// is underneath.</summary>
    Gzip,

    /// <summary>Recognised as none of the above and not text.</summary>
    Binary,
}

/// <summary>An image as Fettler answers it: what it is, how big, and the
/// bytes, which nothing here has looked inside.</summary>
public sealed record ImageFacts(string MimeType, int Width, int Height, long Bytes, byte[] Content);

/// <summary>
/// Reading the file types a developer actually has.
///
/// <para><b>This exists because <c>Read</c> is denied outright.</b> The
/// assistant's own reader is turned off with no path-scoped exception,
/// which makes Fettler the only reader there is - and every type the
/// developer legitimately needs, Fettler must handle, or it is simply
/// unreadable. The deny and these readers are one shipment: shipping the
/// deny first would strand somebody holding a PDF nobody can open.</para>
///
/// <para><b>R4.5 becomes typed.</b> The blanket refusal of anything
/// binary is gone: a RECOGNISED type is handled and an unrecognised one
/// is refused, which is a much better answer than "this is binary" to
/// somebody holding a perfectly ordinary PNG.</para>
///
/// <para><b>Documents are rendered by <see cref="Documents"/>, not
/// here.</b> A PDF, and the office formats that sit beside it, are turned
/// into text by a reader from that table. This class keeps the types
/// whose handling is not a rendering at all: text, notebooks, images and
/// archives.</para>
///
/// <para><b>The old refusal of Word and Excel is withdrawn.</b> It argued
/// that a developer exports Word as PDF and Excel as CSV, and that an
/// office-format parser would be a permanent maintenance liability. The
/// first turned out to describe a chore rather than a workflow: it makes
/// a person convert a file before the assistant may read it, which is the
/// hindrance and not the remedy. The second is answered by what the
/// formats actually are - a zip full of XML, reachable with the archive
/// support already here and the XML reader already in the framework, and
/// so with no new dependency at all.</para>
///
/// <para><b>Plugins are still refused, on exactly the terms they were
/// refused on before.</b> Running a DECLARED, NAMED command needs an
/// explicit <c>execute</c> grant, so a loader running unnamed code in
/// process at full trust would be a hole straight through the boundary. A
/// reader is added by adding a class, in this tree, with tests. For a
/// format Fettler does not ship, the honest form is the one that already
/// exists: a declared converter task, gated by <c>execute</c>.</para>
/// </summary>
public static class Typed
{
    /// <summary>
    /// What a path claims to be, by extension.
    ///
    /// <para>By extension and not by content, deliberately: the extension
    /// is what the caller and their tools already agree the file is, and
    /// a sniffer that disagreed with the name would be right about the
    /// bytes and wrong about the situation. Content still decides
    /// whether a file called <c>.txt</c> is really text - that check has
    /// not moved.</para>
    /// </summary>
    public static FileKind KindOf(string path)
    {
        // The reader table is asked FIRST, and that ordering is
        // load-bearing rather than tidy: an office document is a zip, so
        // asking Archives first would answer "archive" for a file the
        // caller means to read as a document.
        if (Documents.For(path) is { } reader) return reader.Kind;

        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".ipynb" => FileKind.Notebook,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => FileKind.Image,
            _ => Archives.KindOf(path) is not null ? FileKind.Archive
               : Archives.IsLoneGzip(path) ? FileKind.Gzip
               : FileKind.Text,
        };
    }

    public static string NameOf(FileKind kind) => kind switch
    {
        FileKind.Notebook => "notebook",
        FileKind.Image => "image",
        FileKind.Pdf => "pdf",
        FileKind.Spreadsheet => "spreadsheet",
        FileKind.Document => "document",
        FileKind.Archive => "archive",
        FileKind.Gzip => "gzip",
        FileKind.Binary => "binary",
        _ => "text",
    };

    // ---- images ----

    /// <summary>
    /// An image's facts, read from its header. <b>The pixels are never
    /// decoded</b>, which is what keeps this free of an image library and
    /// free of the whole class of defect that comes with parsing
    /// attacker-shaped image data.
    /// </summary>
    public static Result<ImageFacts> ReadImage(ContainedPath path)
    {
        Result<byte[]> raw = TextIo.ReadBytes(path);
        if (!raw.IsOk) return raw.Carry<ImageFacts>();

        byte[] b = raw.Value;
        (string mime, int width, int height) = Measure(b, Path.GetExtension(path.Full).ToLowerInvariant());

        if (mime.Length == 0)
            return Result<ImageFacts>.Fail(Outcome.Refused,
                "the extension says this is an image and the bytes do not agree", path.Display);

        return Result<ImageFacts>.Ok(new ImageFacts(mime, width, height, b.Length, b));
    }

    /// <summary>
    /// Mime type and dimensions from the first few bytes.
    ///
    /// <para>Dimensions are best effort and report zero when the header
    /// shape is one this does not walk - a zero says "not read" rather
    /// than a guess, and nothing depends on them.</para>
    /// </summary>
    static (string Mime, int Width, int Height) Measure(byte[] b, string extension)
    {
        // PNG: signature, then IHDR whose first two big-endian ints are
        // width and height.
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return ("image/png", BigEndian(b, 16), BigEndian(b, 20));

        // GIF: "GIF8", then width and height as little-endian shorts.
        if (b.Length >= 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
            return ("image/gif", b[6] | (b[7] << 8), b[8] | (b[9] << 8));

        // WebP: RIFF....WEBP. Only the simple lossy form's dimensions are
        // walked; the others report zero rather than a wrong number.
        if (b.Length >= 30 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
            && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        {
            if (b[15] == ' ' && b.Length >= 30)
                return ("image/webp", (b[26] | (b[27] << 8)) & 0x3FFF, (b[28] | (b[29] << 8)) & 0x3FFF);
            return ("image/webp", 0, 0);
        }

        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return Jpeg(b);

        // The bytes say nothing; if the extension was one we claim, that
        // disagreement is itself the answer.
        return (string.Empty, 0, 0);
    }

    /// <summary>JPEG carries its size in a start-of-frame marker some way
    /// in, so the segment chain is walked until one turns up.</summary>
    static (string Mime, int Width, int Height) Jpeg(byte[] b)
    {
        for (int i = 2; i + 9 < b.Length;)
        {
            if (b[i] != 0xFF) { i++; continue; }

            byte marker = b[i + 1];
            int length = (b[i + 2] << 8) | b[i + 3];
            if (length < 2) break;

            // Every SOFn but the four that are not frames at all.
            if (marker >= 0xC0 && marker <= 0xCF
                && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                return ("image/jpeg", (b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);

            i += 2 + length;
        }

        return ("image/jpeg", 0, 0);
    }

    static int BigEndian(byte[] b, int at) =>
        (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    // ---- notebooks ----

    /// <summary>
    /// How much of one output cell is shown before it is summarised
    /// instead.
    ///
    /// <para><b>9b.9, and it is not a nicety.</b> A single
    /// <c>.ipynb</c> output cell routinely holds a megabyte of base64
    /// PNG. Reading one straight into a conversation costs more context
    /// than the whole notebook's code, for a picture nobody can see. So
    /// an output that is not text is named and measured rather than
    /// pasted, and text output is capped.</para>
    /// </summary>
    public const int OutputLineCap = 20;

    /// <summary>
    /// A notebook as text: the cells, their sources, and what running
    /// them produced.
    ///
    /// <para>Rendered rather than handed over raw, because the raw form
    /// is JSON with every line of every cell as a separate string, which
    /// is unreadable and several times the size of what it says.</para>
    /// </summary>
    public static Result<string> ReadNotebook(ContainedPath path)
    {
        Result<byte[]> raw = TextIo.ReadBytes(path);
        if (!raw.IsOk) return raw.Carry<string>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(TextIo.WithoutMark(Encoding.UTF8.GetString(raw.Value)));
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException)
        {
            return Result<string>.Fail(Outcome.Refused,
                "this is named as a notebook and is not readable as one: " + e.Message, path.Display);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("cells", out JsonElement cells)
                || cells.ValueKind != JsonValueKind.Array)
                return Result<string>.Fail(Outcome.Refused,
                    "this is named as a notebook and has no cells array", path.Display);

            var text = new StringBuilder();
            int number = 0;

            foreach (JsonElement cell in cells.EnumerateArray())
            {
                number++;
                string kind = cell.TryGetProperty("cell_type", out JsonElement t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()! : "unknown";

                text.Append("=== cell ").Append(number).Append(' ').Append(kind).AppendLine(" ===");
                text.Append(Sources(cell));
                if (text.Length > 0 && text[^1] != '\n') text.AppendLine();

                Outputs(cell, text);
            }

            return Result<string>.Ok(text.ToString());
        }
    }

    /// <summary>A cell's source, which the format allows to be a string
    /// or an array of lines, and which is both in the wild.</summary>
    static string Sources(JsonElement cell)
    {
        if (!cell.TryGetProperty("source", out JsonElement source)) return string.Empty;
        return Flatten(source);
    }

    static string Flatten(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;

        if (value.ValueKind != JsonValueKind.Array) return string.Empty;

        var text = new StringBuilder();
        foreach (JsonElement line in value.EnumerateArray())
            if (line.ValueKind == JsonValueKind.String) text.Append(line.GetString());
        return text.ToString();
    }

    static void Outputs(JsonElement cell, StringBuilder text)
    {
        if (!cell.TryGetProperty("outputs", out JsonElement outputs)
            || outputs.ValueKind != JsonValueKind.Array) return;

        foreach (JsonElement output in outputs.EnumerateArray())
        {
            if (output.ValueKind != JsonValueKind.Object) continue;

            string kind = output.TryGetProperty("output_type", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()! : "output";

            if (kind == "stream")
            {
                if (output.TryGetProperty("text", out JsonElement stream))
                    Capped(text, "--- stream ---", Flatten(stream));
                continue;
            }

            if (kind == "error")
            {
                string message = output.TryGetProperty("evalue", out JsonElement e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString()! : "";
                string type = output.TryGetProperty("ename", out JsonElement n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()! : "error";
                text.Append("--- error --- ").Append(type).Append(": ").AppendLine(message);
                continue;
            }

            if (!output.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
                continue;

            foreach (JsonProperty part in data.EnumerateObject())
            {
                // Anything but plain text is measured, not pasted. This
                // is the clause that keeps a notebook from costing a
                // megabyte of base64 for a picture nobody can see.
                if (part.Name != "text/plain")
                {
                    text.Append("--- ").Append(part.Name).Append(" --- ")
                        .Append(Flatten(part.Value).Length).AppendLine(" characters, not shown");
                    continue;
                }

                Capped(text, "--- out ---", Flatten(part.Value));
            }
        }
    }

    static void Capped(StringBuilder text, string label, string body)
    {
        string[] lines = body.Replace("\r\n", "\n").Split('\n');
        text.AppendLine(label);

        for (int i = 0; i < Math.Min(lines.Length, OutputLineCap); i++)
            text.AppendLine(lines[i]);

        if (lines.Length > OutputLineCap)
            text.Append("... ").Append(lines.Length - OutputLineCap).AppendLine(" more lines of output not shown");
    }
}
