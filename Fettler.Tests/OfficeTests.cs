using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// The two office readers.
///
/// <para><b>The fixtures are written out as XML rather than produced by
/// a library.</b> There is no library here to produce them with, which is
/// the point of the readers - but it is also the better test. A file made
/// by the same understanding of the format that reads it proves only that
/// the understanding is self-consistent. These say, in full, what Excel
/// and Word actually put in a package, including the parts that are easy
/// to get wrong: sheets held in a different order from the one they are
/// listed in, a formula written once and shared by the cells it was
/// filled into, a date stored as a day count, and text a tracked change
/// has taken out.</para>
/// </summary>
public sealed class OfficeTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    static JsonElement Answer(CliResult result) => JsonDocument.Parse(result.Stdout).RootElement;

    static int Hits(CliResult result) => Answer(result).GetProperty("hits_count").GetInt32();

    static JsonElement Hit(CliResult result) => Answer(result).GetProperty("hits")[0];

    /// <summary>An OOXML package: a zip of named XML parts, which is all
    /// either of these formats is.</summary>
    static byte[] Package(params (string Part, string Xml)[] parts)
    {
        using var bytes = new MemoryStream();

        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            foreach ((string part, string xml) in parts)
            {
                ZipArchiveEntry entry = zip.CreateEntry(part);
                using Stream stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(xml);
            }

        return bytes.ToArray();
    }

    // ---- a workbook ----

    const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="xml" ContentType="application/xml"/>
        </Types>
        """;

    // Summary is listed FIRST and is held in sheet2.xml; Q1 is listed
    // second and is held in sheet1.xml. A reader that took the worksheet
    // files in name order would silently reorder the workbook.
    const string Workbook = """
        <?xml version="1.0" encoding="UTF-8"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                  xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets>
            <sheet name="Summary" sheetId="7" r:id="rId2"/>
            <sheet name="Q1" sheetId="3" r:id="rId1"/>
            <sheet name="Old" sheetId="9" state="hidden" r:id="rId3"/>
          </sheets>
        </workbook>
        """;

    const string WorkbookRels = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Target="worksheets/sheet1.xml"
            Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"/>
          <Relationship Id="rId2" Target="worksheets/sheet2.xml"
            Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"/>
          <Relationship Id="rId3" Target="worksheets/sheet3.xml"
            Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"/>
        </Relationships>
        """;

    // Index 4 is one string broken into two runs, which is what Excel
    // does the moment one word in a cell is formatted differently.
    // Index 5 carries a phonetic run: the READING of the word, beside
    // the word, and rendering both puts the cell in twice.
    const string SharedStrings = """
        <?xml version="1.0" encoding="UTF-8"?>
        <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="7" uniqueCount="7">
          <si><t>Region</t></si>
          <si><t>Sales</t></si>
          <si><t>Heckmondwike</t></si>
          <si><t>Gomersal</t></si>
          <si><r><t>Cleck</t></r><r><t>heaton</t></r></si>
          <si><t>Ossett</t><rPh sb="0" eb="6"><t>OHSETTO</t></rPh></si>
          <si><t>Birstall</t></si>
        </sst>
        """;

    // Style 1 is built-in format 14, a date. Style 2 is a custom code
    // whose quoted literals are full of the letters a naive reading
    // would take for a date and which is one anyway.
    const string Styles = """
        <?xml version="1.0" encoding="UTF-8"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="1">
            <numFmt numFmtId="164" formatCode="dd&quot;/&quot;mm&quot;/&quot;yyyy"/>
          </numFmts>
          <cellXfs count="3">
            <xf numFmtId="0"/>
            <xf numFmtId="14"/>
            <xf numFmtId="164"/>
          </cellXfs>
        </styleSheet>
        """;

    const string SheetQ1 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <dimension ref="A1:C4"/>
          <sheetData>
            <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
            <row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2"><v>1200</v></c><c r="C2" s="1"><v>46096</v></c></row>
            <row r="3"><c r="A3" t="s"><v>3</v></c><c r="C3"><v>7719</v></c></row>
            <row r="4"><c r="B4"><f>SUM(B2:B3)</f><v>1200</v></c></row>
          </sheetData>
        </worksheet>
        """;

    // B3 owns a shared formula and B4 was filled from it, carrying only
    // the index back. No dimension here, deliberately: a workbook saved
    // by something other than Excel need not have one.
    const string SheetSummary = """
        <?xml version="1.0" encoding="UTF-8"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <sheetData>
            <row r="1"><c r="A1" t="inlineStr"><is><t>Dewsbury total</t></is></c><c r="B1"><f>Q1!B4*2</f><v>2400</v></c></row>
            <row r="2"><c r="A2" t="s"><v>4</v></c><c r="B2" t="s"><v>5</v></c></row>
            <row r="3"><c r="B3"><f t="shared" ref="B3:B4" si="0">B1*3</f><v>7200</v></c></row>
            <row r="4"><c r="B4"><f t="shared" si="0"/><v>21600</v></c></row>
            <row r="5"><c r="A5" t="b"><v>1</v></c><c r="B5" s="2"><v>46096</v></c></row>
          </sheetData>
        </worksheet>
        """;

    const string SheetOld = """
        <?xml version="1.0" encoding="UTF-8"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <sheetData>
            <row r="1"><c r="A1" t="s"><v>6</v></c></row>
          </sheetData>
        </worksheet>
        """;

    static byte[] AWorkbook() => Package(
        ("[Content_Types].xml", ContentTypes),
        ("xl/workbook.xml", Workbook),
        ("xl/_rels/workbook.xml.rels", WorkbookRels),
        ("xl/sharedStrings.xml", SharedStrings),
        ("xl/styles.xml", Styles),
        ("xl/worksheets/sheet1.xml", SheetQ1),
        ("xl/worksheets/sheet2.xml", SheetSummary),
        ("xl/worksheets/sheet3.xml", SheetOld));

    static Sandbox WithWorkbook()
    {
        var box = new Sandbox();
        Documents.Forget();
        box.WriteRaw("book.xlsx", AWorkbook());
        return box;
    }

    [Fact]
    public async Task AWorkbookIsSearchedAndTheHitCitesTheSheetItIsOn()
    {
        using Sandbox box = WithWorkbook();

        CliResult search = await Run(box, "search", "Heckmondwike", "--json");

        Assert.Equal(ExitCodes.Ok, search.ExitCode);
        Assert.Equal(1, Hits(search));
        Assert.Equal("Q1", Hit(search).GetProperty("cite").GetString());
    }

    [Fact]
    public async Task SheetsComeOutInTheOrderTheWorkbookListsRatherThanTheOrderTheFilesAreIn()
    {
        using Sandbox box = WithWorkbook();

        CliResult read = await Run(box, "read", "book.xlsx");

        int summary = read.Stdout.IndexOf("=== Summary ===", StringComparison.Ordinal);
        int quarter = read.Stdout.IndexOf("=== Q1 ===", StringComparison.Ordinal);

        Assert.True(summary >= 0 && quarter >= 0, "both sheets should be rendered");
        Assert.True(summary < quarter,
            "Summary is listed first and held in sheet2.xml; taking the files in name order reverses them");
    }

    [Fact]
    public async Task AFormulaIsRenderedSoASearchForOneFindsTheSheetThatUsesIt()
    {
        using Sandbox box = WithWorkbook();

        // Not a bare SUM: the sheet is called Summary, and a search is
        // case-insensitive by default.
        CliResult search = await Run(box, "search", "SUM\\(B2", "--json");

        Assert.Equal(1, Hits(search));

        // The formula AND what it last worked out to. The two answer
        // different questions and only one of them is stored twice.
        Assert.Contains("=SUM(B2:B3) {1200}", Hit(search).GetProperty("text").GetString());
    }

    [Fact]
    public async Task ASharedFormulaFollowerNamesItsOwnerRatherThanCopyingTheOwnersText()
    {
        using Sandbox box = WithWorkbook();

        // The owner is rendered in full, so anybody searching for the
        // function finds it.
        Assert.Equal(1, Hits(await Run(box, "search", "B1\\*3", "--json")));

        CliResult follower = await Run(box, "search", "shared formula 0", "--json");
        Assert.Equal(1, Hits(follower));
        Assert.Contains("=[shared formula 0] {21600}", Hit(follower).GetProperty("text").GetString());
    }

    [Fact]
    public async Task ADateIsRenderedAsADateAndNotAsTheDayCountItIsStoredAs()
    {
        using Sandbox box = WithWorkbook();

        // Both cells hold 46096: one under a built-in date format, one
        // under a custom code whose quoted separators are not letters.
        Assert.Equal(2, Hits(await Run(box, "search", "2026-03-15", "--json")));

        // And the number itself is gone, which is the whole point: a
        // workbook full of dates nobody can search for is the same
        // readable-but-unfindable fault as a PDF nobody can search.
        Assert.Equal(0, Hits(await Run(box, "search", "46096", "--json")));
    }

    [Fact]
    public async Task SharedStringsAreResolvedAndTheRunsWithinOneAreJoined()
    {
        using Sandbox box = WithWorkbook();

        Assert.Equal(1, Hits(await Run(box, "search", "Cleckheaton", "--json")));
    }

    [Fact]
    public async Task APhoneticRunIsLeftOutSoTheCellIsNotInTheTextTwice()
    {
        using Sandbox box = WithWorkbook();

        Assert.Equal(1, Hits(await Run(box, "search", "Ossett", "--json")));
        Assert.Equal(0, Hits(await Run(box, "search", "OHSETTO", "--json")));
    }

    [Fact]
    public async Task AGapInARowKeepsItsColumnRatherThanClosingUp()
    {
        using Sandbox box = WithWorkbook();

        CliResult search = await Run(box, "search", "7719", "--json");

        // B3 is empty, so 7719 is still in C. Closing the gap would put
        // it under Sales and make the rendering say something false.
        Assert.Equal("3\tGomersal\t\t7719", Hit(search).GetProperty("text").GetString());
    }

    [Fact]
    public async Task AHiddenSheetIsStillReadAndIsSaidToBeHidden()
    {
        using Sandbox box = WithWorkbook();

        CliResult read = await Run(box, "read", "book.xlsx");
        Assert.Contains("=== Old ===  (hidden)", read.Stdout);

        CliResult search = await Run(box, "search", "Birstall", "--json");
        Assert.Equal("Old", Hit(search).GetProperty("cite").GetString());
    }

    [Fact]
    public async Task ABooleanCellReadsAsTrueRatherThanAsTheOneItIsStoredAs()
    {
        using Sandbox box = WithWorkbook();

        Assert.Equal(1, Hits(await Run(box, "search", "TRUE", "--json")));
    }

    [Fact]
    public async Task AWorkbookThatIsNotAZipIsRefusedRatherThanTakingTheProcessWithIt()
    {
        using var box = new Sandbox();
        Documents.Forget();
        box.Write("lying.xlsx", "this is not a workbook and never was");

        CliResult read = await Run(box, "read", "lying.xlsx");

        Assert.NotEqual(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("could not be read", read.Stderr);
    }

    [Fact]
    public async Task AZipWithNoWorkbookPartIsRefusedAndSaysWhatIsMissing()
    {
        using var box = new Sandbox();
        Documents.Forget();
        box.WriteRaw("empty.xlsx", Package(("[Content_Types].xml", ContentTypes)));

        CliResult read = await Run(box, "read", "empty.xlsx");

        Assert.NotEqual(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("xl/workbook.xml", read.Stderr);
    }

    // ---- a document ----

    const string WordDocument = """
        <?xml version="1.0" encoding="UTF-8"?>
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:body>
            <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Payment Terms</w:t></w:r></w:p>
            <w:p>
              <w:r><w:t xml:space="preserve">Invoices fall due in </w:t></w:r>
              <w:r><w:t>Heckmondwike</w:t></w:r>
              <w:r><w:t xml:space="preserve"> within thirty days.</w:t></w:r>
            </w:p>
            <w:p><w:r><w:t>Mirfield</w:t></w:r><w:r><w:t>Moor</w:t></w:r></w:p>
            <w:p>
              <w:r><w:t>Before</w:t></w:r>
              <w:del w:id="4"><w:r><w:delText>Liversedge</w:delText></w:r></w:del>
              <w:r><w:t>After</w:t></w:r>
            </w:p>
            <w:tbl>
              <w:tr>
                <w:tc><w:p><w:r><w:t>Region</w:t></w:r></w:p></w:tc>
                <w:tc><w:p><w:r><w:t>Sales</w:t></w:r></w:p></w:tc>
              </w:tr>
              <w:tr>
                <w:tc><w:p><w:r><w:t>Ossett</w:t></w:r></w:p></w:tc>
                <w:tc><w:p><w:r><w:t>7719</w:t></w:r></w:p></w:tc>
              </w:tr>
            </w:tbl>
            <w:p><w:pPr><w:pStyle w:val="Heading2"/></w:pPr><w:r><w:t>Late Payment</w:t></w:r></w:p>
            <w:p><w:r><w:t>Interest accrues at Batley rates.</w:t></w:r></w:p>
          </w:body>
        </w:document>
        """;

    const string Footnotes = """
        <?xml version="1.0" encoding="UTF-8"?>
        <w:footnotes xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:footnote w:id="0" w:type="separator"><w:p><w:r><w:t>Skipton</w:t></w:r></w:p></w:footnote>
          <w:footnote w:id="1"><w:p><w:r><w:t>Rates are set in Dewsbury each quarter.</w:t></w:r></w:p></w:footnote>
        </w:footnotes>
        """;

    static Sandbox WithDocument()
    {
        var box = new Sandbox();
        Documents.Forget();
        box.WriteRaw("terms.docx", Package(
            ("[Content_Types].xml", ContentTypes),
            ("word/document.xml", WordDocument),
            ("word/footnotes.xml", Footnotes)));
        return box;
    }

    [Fact]
    public async Task ADocumentIsSearchedAndTheHitCitesTheHeadingItSitsUnder()
    {
        using Sandbox box = WithDocument();

        CliResult search = await Run(box, "search", "Batley", "--json");

        Assert.Equal(ExitCodes.Ok, search.ExitCode);
        Assert.Equal(1, Hits(search));

        // A heading, not a page number. Word does not store where the
        // pages break - that is worked out at layout time and moves with
        // the font - and a heading is what a person would say anyway.
        Assert.Equal("Late Payment", Hit(search).GetProperty("cite").GetString());
    }

    [Fact]
    public async Task AHitEarlierInTheDocumentCitesTheEarlierHeading()
    {
        using Sandbox box = WithDocument();

        CliResult search = await Run(box, "search", "Heckmondwike", "--json");

        Assert.Equal("Payment Terms", Hit(search).GetProperty("cite").GetString());
    }

    [Fact]
    public async Task RunsAreJoinedSoAWordBrokenByFormattingIsStillOneWord()
    {
        using Sandbox box = WithDocument();

        // Word breaks a paragraph wherever anything about the formatting
        // changes, so one bold letter is enough to split a word in two.
        Assert.Equal(1, Hits(await Run(box, "search", "MirfieldMoor", "--json")));
    }

    [Fact]
    public async Task ATableRowRendersAsItsCellsRatherThanAsLooseParagraphs()
    {
        using Sandbox box = WithDocument();

        CliResult search = await Run(box, "search", "7719", "--json");

        Assert.Equal("Ossett\t7719", Hit(search).GetProperty("text").GetString());
    }

    [Fact]
    public async Task TrackedDeletionsAreNotRenderedBecauseNobodyReadingTheDocumentCanSeeThem()
    {
        using Sandbox box = WithDocument();

        Assert.Equal(0, Hits(await Run(box, "search", "Liversedge", "--json")));

        // The text around the deletion is still there and still joined.
        Assert.Equal(1, Hits(await Run(box, "search", "BeforeAfter", "--json")));
    }

    [Fact]
    public async Task AFootnoteIsRenderedBecauseItCarriesRealContent()
    {
        using Sandbox box = WithDocument();

        CliResult search = await Run(box, "search", "Dewsbury", "--json");

        Assert.Equal(1, Hits(search));
        Assert.Equal("footnotes", Hit(search).GetProperty("cite").GetString());
    }

    [Fact]
    public async Task WordsOwnSeparatorFootnoteIsFurnitureAndIsLeftOut()
    {
        using Sandbox box = WithDocument();

        Assert.Equal(0, Hits(await Run(box, "search", "Skipton", "--json")));
    }

    [Fact]
    public async Task AZipWithNoDocumentPartIsRefusedAndSaysWhatIsMissing()
    {
        using var box = new Sandbox();
        Documents.Forget();
        box.WriteRaw("empty.docx", Package(("[Content_Types].xml", ContentTypes)));

        CliResult read = await Run(box, "read", "empty.docx");

        Assert.NotEqual(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("word/document.xml", read.Stderr);
    }

    // ---- the table, and the one direction these never go ----

    [Fact]
    public void TheTableClaimsTheOfficeExtensionsAndTheKindsFollowFromIt()
    {
        Assert.Equal(FileKind.Spreadsheet, Typed.KindOf("accounts.xlsx"));
        Assert.Equal(FileKind.Spreadsheet, Typed.KindOf("MACROS.XLSM"));
        Assert.Equal(FileKind.Document, Typed.KindOf("terms.docx"));
        Assert.Equal(FileKind.Document, Typed.KindOf("terms.docm"));

        // An office document IS a zip, so the reader table has to be
        // asked before the archive check or every one of these answers
        // "archive" and reads as a list of its own parts.
        Assert.NotEqual(FileKind.Archive, Typed.KindOf("accounts.xlsx"));

        // .xlsb wears the same coat and is not this format at all.
        Assert.Null(Documents.For("accounts.xlsb"));
        Assert.Null(Documents.For("letter.doc"));

        foreach (string extension in new[] { ".xlsx", ".xlsm", ".docx", ".docm" })
            Assert.Contains(extension, Documents.Extensions());
    }

    [Fact]
    public async Task WritingTextOverADocumentIsRefusedBecauseTheTextIsNotTheFile()
    {
        using Sandbox box = WithWorkbook();

        Result<Saved> over = await box.Bench.WriteAsync("book.xlsx", "Region\tSales\n", overwrite: true);

        Assert.False(over.IsOk, "a rendering written back would replace the workbook with a fraction of it");
        Assert.Equal(Outcome.Refused, over.Failure!.Outcome);
        Assert.Contains("rendering", over.Failure.Message);

        // And the workbook is still a workbook.
        Documents.Forget();
        Assert.Equal(1, Hits(await Run(box, "search", "Heckmondwike", "--json")));
    }

    [Fact]
    public async Task CreatingADocumentByWritingTextAtOneIsRefusedForTheSameReason()
    {
        using var box = new Sandbox();

        Result<Saved> made = await box.Bench.WriteAsync("new.docx", "Payment Terms\n", overwrite: false);

        Assert.False(made.IsOk, "nothing becomes a Word document by having text written at its name");
        Assert.Equal(Outcome.Refused, made.Failure!.Outcome);
        Assert.False(File.Exists(box.Full("new.docx")));
    }
}
