using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Fettler.Core;

/// <summary>
/// What the two office readers share: getting at a part of the package,
/// and reading XML on terms that cannot be turned against the reader.
///
/// <para><b>An office document is a zip full of XML</b>, which is why
/// these readers add no dependency at all. The zip support was already
/// here for <c>read</c>ing an archive's manifest and the XML reader is in
/// the framework. That is the whole of it, and it is the reason the old
/// refusal of Word and Excel - that a parser for them would be a
/// permanent maintenance liability - did not survive being looked
/// at.</para>
///
/// <para><b>Both readers are hostile-input readers.</b> A document
/// arrives from wherever documents arrive from, and this one runs inside
/// a server whose whole job is to be a boundary. So: no DTD, no
/// resolver, and no part is read past a cap however large its own header
/// claims it is not.</para>
/// </summary>
static class Ooxml
{
    /// <summary>How much any one part may unpack to.
    ///
    /// <para>Checked while reading rather than read off the zip's
    /// central directory, because the directory is written by whoever
    /// made the file and a zip bomb's entire method is to say a
    /// comfortable number there.</para>
    /// </summary>
    public const long MaxPartBytes = 128L * 1024 * 1024;

    /// <summary>How many rendered lines a document may come to before
    /// the rest is left unread and SAID to be unread. A spreadsheet can
    /// hold a million rows, and turning all of them into text to answer
    /// one search is not a service to anybody.</summary>
    public const int MaxLines = 200_000;

    /// <summary>
    /// XML settings for a file this process did not write.
    ///
    /// <para><c>DtdProcessing.Prohibit</c> and a null resolver together
    /// close both classic XML attacks in one: an external entity that
    /// reads a file off this machine and hands it back through the
    /// document, and an internal one that expands to gigabytes from a
    /// few lines. Neither is exotic and both are one attribute
    /// away.</para>
    ///
    /// <para>Whitespace is NOT ignored: a run of spaces inside
    /// <c>xml:space="preserve"</c> is content in both formats, and
    /// dropping it silently joins words that were apart.</para>
    /// </summary>
    public static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
    };

    /// <summary>The named part, or null when the package has no such
    /// part. Matched exactly first and case-insensitively after: the
    /// specification fixes these names, and a producer that varies the
    /// case has made a file every other tool still opens.</summary>
    public static ZipArchiveEntry? Part(ZipArchive zip, string name)
    {
        ZipArchiveEntry? exact = zip.GetEntry(name);
        if (exact is not null) return exact;

        foreach (ZipArchiveEntry entry in zip.Entries)
            if (string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase))
                return entry;

        return null;
    }

    /// <summary>A whole part as an element tree, or null when it is not
    /// there. Absent parts are ordinary: a workbook with no formulas has
    /// no shared strings, and a document with no footnotes has no
    /// footnotes part.</summary>
    public static XElement? Tree(ZipArchive zip, string name)
    {
        ZipArchiveEntry? part = Part(zip, name);
        if (part is null) return null;

        using Stream raw = part.Open();
        using var capped = new Capped(raw, MaxPartBytes);
        using XmlReader reader = XmlReader.Create(capped, Settings());
        return XDocument.Load(reader).Root;
    }

    /// <summary>Every descendant's text, in document order, skipping the
    /// subtrees named. Used for a cell's string and for a paragraph's
    /// runs, both of which are text broken into pieces by formatting
    /// that means nothing once it is text.</summary>
    public static string TextOf(XElement element, params string[] skipped)
    {
        var text = new StringBuilder();

        foreach (XElement node in element.DescendantsAndSelf())
        {
            if (node.Name.LocalName != "t") continue;
            if (skipped.Length > 0 && node.Ancestors().Any(a => skipped.Contains(a.Name.LocalName)))
                continue;

            text.Append(node.Value);
        }

        return text.ToString();
    }
}

/// <summary>
/// A read-only stream that stops rather than being led on.
///
/// <para>The uncompressed size in a zip's directory is a claim by
/// whoever wrote the file. This counts what actually arrives, so a part
/// that keeps producing bytes ends as a refusal naming the cap instead
/// of as this process running out of memory.</para>
/// </summary>
sealed class Capped(Stream inner, long cap) : Stream
{
    long read;

    public override int Read(byte[] buffer, int offset, int count)
    {
        int got = inner.Read(buffer, offset, count);
        read += got;

        if (read > cap)
            throw new InvalidDataException(
                $"a part of this file unpacks past {cap} bytes, which is where this stops reading");

        return got;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A workbook as text: one line per row, one landmark per sheet.
///
/// <para><b>A row to a line, tab separated, is how a spreadsheet is
/// actually read.</b> A cell to a line would make every hit a single
/// value with nothing around it, and the thing a person wants from a
/// search of a workbook is the record the value sits in.</para>
///
/// <para><b>Formulas are rendered as well as values</b>, because the two
/// answer different questions and only one of them is in the file
/// twice. A search for <c>VLOOKUP</c> is a search for how a sheet works
/// and would find nothing at all in the cached results; a search for a
/// customer name is a search of the data and would find nothing in the
/// formulas. Both are in, and a cell holding a formula shows the formula
/// with its last computed value beside it in braces.</para>
///
/// <para><b>Dates are rendered as dates.</b> Excel stores one as a day
/// count and a number format, so the cell a person sees as
/// <c>2026-03-15</c> is the number <c>46096</c> in the file. Rendering
/// the number would leave a workbook full of dates nobody can search
/// for, which is the same readable-but-unfindable fault that had
/// <c>search</c> skipping PDFs.</para>
/// </summary>
sealed class XlsxReader : IDocumentReader
{
    // .xlsm is the same format carrying macros; it is read the same way
    // and nothing here executes anything. .xlsb is NOT here: it is a
    // binary format wearing the same coat, and a reader that claimed it
    // would fail on every file.
    public IReadOnlyList<string> Extensions => [".xlsx", ".xlsm"];

    public FileKind Kind => FileKind.Spreadsheet;

    sealed record Sheet(string Name, string Part, bool Hidden);

    sealed record Formats(IReadOnlyList<int> OfStyle, IReadOnlyDictionary<int, string> Custom);

    public Result<Rendering> Render(ContainedPath path, byte[] content)
    {
        var lines = new List<string>();
        var marks = new List<Landmark>();

        try
        {
            using var bytes = new MemoryStream(content, writable: false);
            using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);

            XElement? workbook = Ooxml.Tree(zip, "xl/workbook.xml");
            if (workbook is null)
                return Result<Rendering>.Fail(Outcome.Refused,
                    "this has the name of a workbook but no xl/workbook.xml inside it, so it is not one",
                    path.Display);

            IReadOnlyList<Sheet> sheets = SheetsOf(zip, workbook);
            if (sheets.Count == 0)
                return Result<Rendering>.Fail(Outcome.Refused,
                    "this workbook names no worksheets", path.Display);

            IReadOnlyList<string> shared = SharedStrings(zip);
            Formats formats = FormatsOf(zip);

            // The 1904 system is Excel for Mac's, and a workbook saved
            // under it has every date four years and a day out if it is
            // read on the other one. It is one attribute and it is the
            // difference between a date and a wrong date.
            bool from1904 = workbook.Descendants().Any(e =>
                e.Name.LocalName == "workbookPr"
                && e.Attributes().Any(a => a.Name.LocalName == "date1904"
                                           && (a.Value == "1" || a.Value == "true")));

            foreach (Sheet sheet in sheets)
            {
                marks.Add(new Landmark(lines.Count + 1, sheet.Name));
                lines.Add($"=== {sheet.Name} ==={(sheet.Hidden ? "  (hidden)" : string.Empty)}");

                if (lines.Count >= Ooxml.MaxLines) break;

                RenderSheet(zip, sheet, shared, formats, from1904, lines);
            }

            if (lines.Count >= Ooxml.MaxLines)
                lines.Add($"[stopped at {Ooxml.MaxLines} lines; the rest of this workbook was not read]");
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            // A zip that is not one, XML that does not parse, a part that
            // runs past the cap. None of them may take the server down,
            // and all of them are the same answer to the caller: this
            // file could not be read, and here is what was wrong with it.
            return Result<Rendering>.Fail(Outcome.Refused,
                "this workbook could not be read: " + e.Message, path.Display);
        }

        var text = new StringBuilder();
        foreach (string line in lines) text.Append(line).Append('\n');

        return Result<Rendering>.Ok(new Rendering(text.ToString(), marks));
    }

    /// <summary>
    /// The sheets, in the order the workbook lists them.
    ///
    /// <para>The order matters and is not the order of the files: a
    /// workbook whose tabs read Summary, Q1, Q2 may hold them as
    /// sheet3, sheet1, sheet2, and a reader that trusted the filenames
    /// would quietly reorder somebody's workbook.</para>
    ///
    /// <para>If the relationships part is missing or names nothing this
    /// can resolve, the worksheet files are taken in name order instead.
    /// A degraded reading of a damaged workbook is worth more than a
    /// refusal, as long as it is not presented as the ordered one.</para>
    /// </summary>
    static IReadOnlyList<Sheet> SheetsOf(ZipArchive zip, XElement workbook)
    {
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        XElement? rels = Ooxml.Tree(zip, "xl/_rels/workbook.xml.rels");

        if (rels is not null)
            foreach (XElement rel in rels.Elements().Where(e => e.Name.LocalName == "Relationship"))
            {
                string? id = rel.Attribute("Id")?.Value;
                string? target = rel.Attribute("Target")?.Value;
                if (id is not null && target is not null) targets[id] = Resolve(target);
            }

        var sheets = new List<Sheet>();

        foreach (XElement sheet in workbook.Descendants().Where(e => e.Name.LocalName == "sheet"))
        {
            string name = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "name")?.Value
                          ?? $"Sheet{sheets.Count + 1}";
            string? id = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
            string state = sheet.Attributes().FirstOrDefault(a => a.Name.LocalName == "state")?.Value
                           ?? "visible";

            if (id is null || !targets.TryGetValue(id, out string? part)) continue;
            if (Ooxml.Part(zip, part) is null) continue;

            sheets.Add(new Sheet(name, part, !string.Equals(state, "visible", StringComparison.Ordinal)));
        }

        if (sheets.Count > 0) return sheets;

        foreach (ZipArchiveEntry entry in zip.Entries
                     .Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                                 && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            sheets.Add(new Sheet(Path.GetFileNameWithoutExtension(entry.FullName), entry.FullName, false));
        }

        return sheets;
    }

    /// <summary>A relationship target as a package path. Targets are
    /// relative to the part that declared them, which for the workbook
    /// is <c>xl/</c>, and an absolute one is written from the package
    /// root.</summary>
    static string Resolve(string target)
    {
        string cleaned = target.Replace('\\', '/');
        if (cleaned.StartsWith('/')) return cleaned.TrimStart('/');

        var segments = new List<string>(["xl"]);

        foreach (string segment in cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }
            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// The shared string table, indexed as the cells index it.
    ///
    /// <para>Excel stores every distinct string once and has the cells
    /// point at it, so a sheet read without this is a grid of integers.
    /// Phonetic runs are left out: they are the reading of a Japanese
    /// word rather than the word, and including them puts every such
    /// cell into the text twice.</para>
    /// </summary>
    static IReadOnlyList<string> SharedStrings(ZipArchive zip)
    {
        XElement? table = Ooxml.Tree(zip, "xl/sharedStrings.xml");
        if (table is null) return [];

        var strings = new List<string>();

        foreach (XElement item in table.Elements().Where(e => e.Name.LocalName == "si"))
            strings.Add(Ooxml.TextOf(item, "rPh"));

        return strings;
    }

    /// <summary>The number format each cell style carries, which is how
    /// a date is told from the day count it is stored as.</summary>
    static Formats FormatsOf(ZipArchive zip)
    {
        XElement? styles = Ooxml.Tree(zip, "xl/styles.xml");
        if (styles is null) return new Formats([], new Dictionary<int, string>());

        var custom = new Dictionary<int, string>();

        foreach (XElement format in styles.Descendants().Where(e => e.Name.LocalName == "numFmt"))
        {
            string? id = format.Attribute("numFmtId")?.Value;
            string? code = format.Attribute("formatCode")?.Value;

            if (id is not null && code is not null
                && int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                custom[number] = code;
        }

        var ofStyle = new List<int>();
        XElement? cellFormats = styles.Elements().FirstOrDefault(e => e.Name.LocalName == "cellXfs");

        if (cellFormats is not null)
            foreach (XElement format in cellFormats.Elements().Where(e => e.Name.LocalName == "xf"))
                ofStyle.Add(int.TryParse(format.Attribute("numFmtId")?.Value, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int number) ? number : 0);

        return new Formats(ofStyle, custom);
    }

    /// <summary>
    /// One sheet's rows, appended to the rendering.
    ///
    /// <para>Streamed rather than loaded: a worksheet part is the one
    /// piece of a workbook that is routinely enormous, and the rows are
    /// wanted one at a time. Each cell is small, so each is turned into
    /// an element and read from there - the arithmetic of walking a
    /// reader by hand through <c>f</c>, <c>v</c> and <c>is</c> is
    /// exactly the kind that is wrong in the case nobody tested.</para>
    /// </summary>
    static void RenderSheet(ZipArchive zip, Sheet sheet, IReadOnlyList<string> shared,
        Formats formats, bool from1904, List<string> lines)
    {
        ZipArchiveEntry? part = Ooxml.Part(zip, sheet.Part);
        if (part is null) return;

        using Stream raw = part.Open();
        using var capped = new Capped(raw, Ooxml.MaxPartBytes);
        using XmlReader reader = XmlReader.Create(capped, Ooxml.Settings());

        int emitted = 0;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            // Excel writes the used range, so the column letters can be
            // named up front rather than guessed from the first row -
            // which would be wrong for every sheet whose first row is
            // shorter than its widest.
            if (reader.LocalName == "dimension" && emitted == 0)
            {
                string letters = Header(reader.GetAttribute("ref"));
                if (letters.Length > 0) lines.Add(letters);
                continue;
            }

            if (reader.LocalName != "row") continue;

            if (lines.Count >= Ooxml.MaxLines) return;

            using XmlReader subtree = reader.ReadSubtree();
            XElement row = XElement.Load(subtree);

            string? rendered = RenderRow(row, shared, formats, from1904);
            if (rendered is null) continue;

            lines.Add(rendered);
            emitted++;
        }
    }

    /// <summary>A row, or null when every cell in it was empty. An empty
    /// row is a line of tabs saying nothing, and a sheet with a thousand
    /// of them between two tables would otherwise render as mostly
    /// nothing.</summary>
    static string? RenderRow(XElement row, IReadOnlyList<string> shared, Formats formats, bool from1904)
    {
        string number = row.Attribute("r")?.Value ?? string.Empty;
        var cells = new SortedDictionary<int, string>();
        int widest = 0;

        foreach (XElement cell in row.Elements().Where(e => e.Name.LocalName == "c"))
        {
            string reference = cell.Attribute("r")?.Value ?? string.Empty;
            int column = ColumnOf(reference);
            if (column == 0) column = widest + 1;

            string rendered = RenderCell(cell, shared, formats, from1904);
            widest = Math.Max(widest, column);

            if (rendered.Length > 0) cells[column] = rendered;
        }

        if (cells.Count == 0) return null;

        var line = new StringBuilder(number);

        for (int column = 1; column <= cells.Keys.Max(); column++)
        {
            line.Append('\t');
            if (cells.TryGetValue(column, out string? cell)) line.Append(cell);
        }

        return line.ToString();
    }

    /// <summary>
    /// One cell: what it holds, and what works it out when something
    /// does.
    ///
    /// <para>A shared formula is written once, on the cell that owns it,
    /// and every cell it was filled into carries only the index back to
    /// that one. The followers are rendered as a reference to the owner
    /// rather than as a copy of its text, because the copy would be a
    /// lie: the whole point of a shared formula is that its references
    /// shift per cell. Whatever function a person is searching for is on
    /// the owner, which is rendered in full.</para>
    /// </summary>
    static string RenderCell(XElement cell, IReadOnlyList<string> shared, Formats formats, bool from1904)
    {
        string type = cell.Attributes().FirstOrDefault(a => a.Name.LocalName == "t")?.Value ?? string.Empty;

        XElement? formula = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "f");
        XElement? stored = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v");
        XElement? inline = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "is");

        string value = type switch
        {
            "s" => Lookup(shared, stored?.Value),
            "inlineStr" => inline is null ? string.Empty : Ooxml.TextOf(inline, "rPh"),
            "str" => stored?.Value ?? string.Empty,
            "b" => stored?.Value == "1" ? "TRUE" : stored is null ? string.Empty : "FALSE",
            "e" => stored?.Value ?? string.Empty,
            _ => Number(cell, stored?.Value, formats, from1904),
        };

        if (formula is null) return value;

        string written = formula.Value.Length > 0
            ? "=" + formula.Value
            : string.Equals(formula.Attribute("t")?.Value, "shared", StringComparison.Ordinal)
                ? $"=[shared formula {formula.Attribute("si")?.Value ?? "?"}]"
                : "=";

        return value.Length > 0 ? $"{written} {{{value}}}" : written;
    }

    static string Lookup(IReadOnlyList<string> shared, string? index) =>
        index is not null
        && int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out int at)
        && at >= 0 && at < shared.Count
            ? shared[at]
            : string.Empty;

    /// <summary>A numeric cell, rendered as a date when its style says it
    /// is one and as the number it is otherwise.</summary>
    static string Number(XElement cell, string? stored, Formats formats, bool from1904)
    {
        if (stored is null || stored.Length == 0) return string.Empty;

        if (!int.TryParse(cell.Attributes().FirstOrDefault(a => a.Name.LocalName == "s")?.Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int style))
            return stored;

        if (style < 0 || style >= formats.OfStyle.Count) return stored;

        int numberFormat = formats.OfStyle[style];
        string? code = Code(numberFormat, formats.Custom);
        if (code is null) return stored;

        if (!double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out double serial))
            return stored;

        return AsMoment(serial, from1904, code) ?? stored;
    }

    /// <summary>
    /// The format code for a numeric format id, or null when it is not a
    /// date or time at all.
    ///
    /// <para>The built-in ids are fixed by the specification and carry
    /// no code in the file. Anything else is looked up, and judged by a
    /// deliberately narrow reading: quoted literals, escaped characters
    /// and bracketed sections come out first, because <c>"day"</c> and
    /// <c>[Red]</c> are full of the letters this is looking for and
    /// none of them mean a date.</para>
    /// </summary>
    static string? Code(int id, IReadOnlyDictionary<int, string> custom)
    {
        // 14-17 dates, 18-21 times, 22 date and time, 45-47 elapsed time.
        if (id is >= 14 and <= 17) return "yyyy-mm-dd";
        if (id is >= 18 and <= 21 or >= 45 and <= 47) return "hh:mm:ss";
        if (id == 22) return "yyyy-mm-dd hh:mm:ss";

        if (!custom.TryGetValue(id, out string? code)) return null;

        var bare = new StringBuilder();
        bool quoted = false, bracketed = false;

        for (int at = 0; at < code.Length; at++)
        {
            char c = code[at];

            if (c == '\\') { at++; continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c == '[') { bracketed = true; continue; }
            if (c == ']') { bracketed = false; continue; }
            if (bracketed) continue;

            bare.Append(char.ToLowerInvariant(c));
        }

        string plain = bare.ToString();
        bool date = plain.Contains('y') || plain.Contains('d');
        bool time = plain.Contains('h') || plain.Contains('s');

        return date || time ? plain : null;
    }

    /// <summary>
    /// A day count as the moment it stands for, or null when it is not
    /// one.
    ///
    /// <para>Two calendars and a bug. The default counts from the last
    /// day of 1899, except that Lotus 1-2-3 treated 1900 as a leap year
    /// and Excel kept the mistake for compatibility, so every serial
    /// past the imaginary 29th of February is one further along than the
    /// arithmetic says. Serial 60 IS that imaginary day and is left as
    /// the number it is, there being no date to render it as. The 1904
    /// calendar, from Excel for Mac, has neither the offset nor the
    /// bug.</para>
    /// </summary>
    static string? AsMoment(double serial, bool from1904, string code)
    {
        if (double.IsNaN(serial) || double.IsInfinity(serial)) return null;
        if (serial < 0 || serial > 2_958_465) return null;

        DateTime moment;

        if (from1904) moment = new DateTime(1904, 1, 1).AddDays(serial);
        else if (serial >= 61) moment = new DateTime(1899, 12, 30).AddDays(serial);
        else if (serial >= 60) return null;
        else moment = new DateTime(1899, 12, 31).AddDays(serial);

        bool date = code.Contains('y') || code.Contains('d');
        bool time = code.Contains('h') || code.Contains('s');

        return (date, time) switch
        {
            (true, true) => moment.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            (false, true) => moment.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            _ => moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The column letters across the top, from the sheet's own
    /// used range.</summary>
    static string Header(string? dimension)
    {
        if (dimension is null || dimension.Length == 0) return string.Empty;

        string last = dimension.Split(':')[^1];
        int columns = ColumnOf(last);
        if (columns is <= 0 or > 1024) return string.Empty;

        var header = new StringBuilder("row");
        for (int column = 1; column <= columns; column++) header.Append('\t').Append(LetterOf(column));

        return header.ToString();
    }

    /// <summary>The 1-based column a cell reference names, or 0 when it
    /// names none.</summary>
    static int ColumnOf(string reference)
    {
        int column = 0;

        foreach (char c in reference)
        {
            if (c is >= 'A' and <= 'Z') column = (column * 26) + (c - 'A' + 1);
            else if (c is >= 'a' and <= 'z') column = (column * 26) + (c - 'a' + 1);
            else break;

            // A1 notation stops at XFD. Anything longer is not a
            // reference, and continuing to multiply would overflow.
            if (column > 16_384) return 0;
        }

        return column;
    }

    static string LetterOf(int column)
    {
        var letters = new StringBuilder();

        while (column > 0)
        {
            int remainder = (column - 1) % 26;
            letters.Insert(0, (char)('A' + remainder));
            column = (column - 1) / 26;
        }

        return letters.ToString();
    }
}

/// <summary>
/// A word-processor document as text: its paragraphs, its tables, and
/// its headings kept as headings.
///
/// <para><b>The headings are the landmarks</b>, and they are worth more
/// here than a page number would be. Word does not store where the pages
/// break - that is worked out at layout time by whatever is displaying
/// the document, and it moves with the font and the paper. A heading is
/// in the file, is stable, and is what a person would say if you asked
/// them where in the document something was.</para>
///
/// <para><b>Tables are rendered as rows</b>, tab separated, for the same
/// reason a spreadsheet's rows are: a cell on its own is a value with
/// nothing around it.</para>
/// </summary>
sealed class DocxReader : IDocumentReader
{
    // .docm is the same format carrying macros, read the same way and
    // running nothing. .doc is NOT here - the old binary format shares
    // nothing but three letters.
    public IReadOnlyList<string> Extensions => [".docx", ".docm"];

    public FileKind Kind => FileKind.Document;

    sealed class Sink
    {
        public List<string> Lines { get; } = [];
        public List<Landmark> Marks { get; } = [];
        public bool Full => Lines.Count >= Ooxml.MaxLines;

        public void Add(string line) { if (!Full) Lines.Add(line); }

        public void Mark(string label)
        {
            if (Full) return;
            Marks.Add(new Landmark(Lines.Count + 1, label));
        }
    }

    public Result<Rendering> Render(ContainedPath path, byte[] content)
    {
        var sink = new Sink();

        try
        {
            using var bytes = new MemoryStream(content, writable: false);
            using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);

            XElement? document = Ooxml.Tree(zip, "word/document.xml");
            if (document is null)
                return Result<Rendering>.Fail(Outcome.Refused,
                    "this has the name of a document but no word/document.xml inside it, so it is not one",
                    path.Display);

            XElement body = document.Elements().FirstOrDefault(e => e.Name.LocalName == "body") ?? document;

            sink.Mark("body");
            Walk(body, sink);

            // Notes carry real content - a definition, a citation, the
            // exception to the clause above them - and a search that
            // could not see them would miss it. Word's own separator
            // notes are furniture and are left out.
            Notes(zip, "word/footnotes.xml", "footnote", sink);
            Notes(zip, "word/endnotes.xml", "endnote", sink);

            if (sink.Full)
                sink.Lines.Add($"[stopped at {Ooxml.MaxLines} lines; the rest of this document was not read]");
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            return Result<Rendering>.Fail(Outcome.Refused,
                "this document could not be read: " + e.Message, path.Display);
        }

        if (sink.Lines.Count == 0)
            return Result<Rendering>.Fail(Outcome.Refused,
                "this document holds no text at all, so there is nothing here to read; if it is "
                + "pictures of text, nothing here does character recognition", path.Display);

        var text = new StringBuilder();
        foreach (string line in sink.Lines) text.Append(line).Append('\n');

        return Result<Rendering>.Ok(new Rendering(text.ToString(), sink.Marks));
    }

    /// <summary>
    /// Everything under an element that carries text, in order.
    ///
    /// <para>Recursive rather than a walk of every descendant, because
    /// the containers matter: a paragraph inside a table cell belongs to
    /// its row and not to the body, and a table can hold a table. A flat
    /// sweep of every <c>w:p</c> would render a table twice - once as
    /// its rows and once as a run of loose paragraphs.</para>
    /// </summary>
    static void Walk(XElement element, Sink sink)
    {
        foreach (XElement child in element.Elements())
        {
            if (sink.Full) return;

            switch (child.Name.LocalName)
            {
                case "p":
                    Paragraph(child, sink);
                    break;

                case "tbl":
                    Table(child, sink);
                    break;

                // A content control. Its content is a level down and is
                // ordinary body content; the wrapper says how it is
                // edited, which is nothing to do with reading it.
                case "sdt":
                case "sdtContent":
                case "customXml":
                    Walk(child, sink);
                    break;

                // An accepted insertion, or text moved to here. It is
                // what the document says now, so it is walked into like
                // any other wrapper.
                case "ins":
                case "moveTo":
                    Walk(child, sink);
                    break;

                // A tracked deletion, and the far end of a move. This is
                // what the document USED to say. Rendering it would put
                // text into every search that nobody reading the document
                // can see - and the answer would look like a find rather
                // than like a ghost.
                case "del":
                case "moveFrom":
                    break;
            }
        }
    }

    static void Paragraph(XElement paragraph, Sink sink)
    {
        string text = TextOf(paragraph);
        int heading = HeadingLevel(paragraph);

        if (heading > 0 && text.Length > 0)
        {
            sink.Mark(text);
            sink.Add(new string('#', Math.Min(heading, 6)) + " " + text);
            return;
        }

        // A blank paragraph is a blank line, which is how the shape of a
        // document survives being turned into text. A run of them is
        // not: that is spacing done by pressing return, and it would
        // push everything else apart in the rendering.
        if (text.Length == 0)
        {
            if (sink.Lines.Count > 0 && sink.Lines[^1].Length > 0) sink.Add(string.Empty);
            return;
        }

        sink.Add(text);
    }

    static void Table(XElement table, Sink sink)
    {
        foreach (XElement row in table.Elements().Where(e => e.Name.LocalName == "tr"))
        {
            if (sink.Full) return;

            var cells = new List<string>();

            foreach (XElement cell in row.Elements().Where(e => e.Name.LocalName == "tc"))
            {
                // A cell holding a table of its own is rendered where it
                // stands rather than flattened into the cell, which
                // would run two grids together into one unreadable line.
                if (cell.Elements().Any(e => e.Name.LocalName == "tbl"))
                {
                    if (cells.Any(c => c.Length > 0)) sink.Add(string.Join('\t', cells));
                    cells.Clear();
                    Walk(cell, sink);
                    continue;
                }

                cells.Add(string.Join(' ', cell.Elements()
                    .Where(e => e.Name.LocalName == "p")
                    .Select(TextOf)
                    .Where(t => t.Length > 0)));
            }

            if (cells.Any(c => c.Length > 0)) sink.Add(string.Join('\t', cells));
        }
    }

    static void Notes(ZipArchive zip, string part, string what, Sink sink)
    {
        XElement? notes = Ooxml.Tree(zip, part);
        if (notes is null || sink.Full) return;

        bool announced = false;

        foreach (XElement note in notes.Elements().Where(e => e.Name.LocalName == what))
        {
            string type = note.Attributes().FirstOrDefault(a => a.Name.LocalName == "type")?.Value
                          ?? string.Empty;

            if (type is "separator" or "continuationSeparator") continue;
            if (TextOf(note).Length == 0) continue;

            if (!announced)
            {
                sink.Mark($"{what}s");
                sink.Add(string.Empty);
                sink.Add($"=== {what}s ===");
                announced = true;
            }

            string id = note.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? "?";
            sink.Add($"[{what} {id}]");
            Walk(note, sink);
        }
    }

    /// <summary>
    /// A paragraph's text, with the breaks inside it kept as spacing.
    ///
    /// <para>Word breaks a paragraph into runs wherever anything about
    /// the formatting changes, so a single sentence with one bold word
    /// in it arrives as three pieces. Deleted text is left out: a
    /// tracked deletion is what the document used to say, and a search
    /// that found it would be reporting text nobody can see.</para>
    /// </summary>
    static string TextOf(XElement paragraph)
    {
        var text = new StringBuilder();

        foreach (XElement node in paragraph.Descendants())
        {
            if (node.Ancestors().Any(a => a.Name.LocalName is "del" or "moveFrom")) continue;

            switch (node.Name.LocalName)
            {
                case "t":
                    text.Append(node.Value);
                    break;

                case "tab":
                    text.Append('\t');
                    break;

                case "br":
                case "cr":
                    text.Append(' ');
                    break;

                // A symbol run carries its character as a hex attribute
                // rather than as text. Most are bullets and arrows from a
                // symbol font, and the codes are font-specific, so what
                // goes in is a space: the word after it stays a separate
                // word, which is what matters for finding it.
                case "sym":
                    text.Append(' ');
                    break;
            }
        }

        return text.ToString().Trim();
    }

    /// <summary>The heading level a paragraph's style names, or 0 when it
    /// names none. Word writes these style ids whatever language its
    /// interface is in, so this does not need translating.</summary>
    static int HeadingLevel(XElement paragraph)
    {
        XElement? properties = paragraph.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr");
        XElement? style = properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "pStyle");

        string? named = style?.Attributes().FirstOrDefault(a => a.Name.LocalName == "val")?.Value;
        if (named is null) return 0;

        if (string.Equals(named, "Title", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(named, "Subtitle", StringComparison.OrdinalIgnoreCase)) return 2;

        if (!named.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)) return 0;

        return int.TryParse(named.AsSpan("Heading".Length), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int level) && level > 0 ? level : 1;
    }
}
