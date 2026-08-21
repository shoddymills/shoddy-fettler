using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// The reader table, and the hole it was built to close.
///
/// <para><b>A PDF used to be readable and unfindable.</b> <c>read</c>
/// rendered one to text; <c>search</c> called <c>TextIo.Read</c>, failed
/// to decode it, and moved on without counting it. So a tree full of
/// documents answered every search with a confident nothing - which is
/// the exact failure Fettler exists to prevent, sitting inside
/// Fettler.</para>
/// </summary>
public sealed class DocumentTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    /// <summary>
    /// A PDF with one page per body given.
    ///
    /// <para>The bodies are single words on purpose. Extraction returns
    /// the glyphs it finds without inventing the spaces between them, so
    /// a sentence comes back run together and a test written against one
    /// would be asserting the extractor's spacing rather than the
    /// search.</para>
    /// </summary>
    static byte[] APdf(params string[] pages)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        UglyToad.PdfPig.Writer.PdfDocumentBuilder.AddedFont font =
            builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);

        foreach (string body in pages)
        {
            UglyToad.PdfPig.Writer.PdfPageBuilder page = builder.AddPage(595, 842);
            page.AddText(body, 12, new UglyToad.PdfPig.Core.PdfPoint(40, 800), font);
        }

        return builder.Build();
    }

    static JsonElement SearchJson(CliResult result) => JsonDocument.Parse(result.Stdout).RootElement;

    // ---- the defect ----

    [Fact]
    public async Task APdfIsSearchedAndNotSilentlySkipped()
    {
        using var box = new Sandbox();
        Documents.Forget();
        box.WriteRaw("notes.pdf", APdf("Kirkburton"));

        CliResult search = await Run(box, "search", "Kirkburton", "--json");

        Assert.Equal(ExitCodes.Ok, search.ExitCode);
        Assert.Equal(1, SearchJson(search).GetProperty("hits_count").GetInt32());
    }

    [Fact]
    public async Task AHitInsideAPdfCitesThePageItIsOn()
    {
        using var box = new Sandbox();
        Documents.Forget();
        box.WriteRaw("notes.pdf", APdf("Kirkburton", "Ossett", "Batley"));

        CliResult search = await Run(box, "search", "Ossett", "--json");
        JsonElement hit = SearchJson(search).GetProperty("hits")[0];

        Assert.Equal("page 2 of 3", hit.GetProperty("cite").GetString());
    }

    [Fact]
    public async Task ATextHitCarriesNoCitationBecauseItsLineNumberAlreadyIsOne()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", "Ossett\n");

        CliResult search = await Run(box, "search", "Ossett", "--json");
        JsonElement hit = SearchJson(search).GetProperty("hits")[0];

        Assert.False(hit.TryGetProperty("cite", out _));
    }

    // ---- what was not looked inside ----

    [Fact]
    public async Task NoDocumentsLeavesThePdfShutAndSaysSoRatherThanReportingACleanZero()
    {
        using var box = new Sandbox();
        Documents.Forget();
        box.WriteRaw("notes.pdf", APdf("Kirkburton"));

        CliResult search = await Run(box, "search", "Kirkburton", "--no-documents", "--json");
        JsonElement answer = SearchJson(search);

        Assert.Equal(ExitCodes.Ok, search.ExitCode);
        Assert.Equal(0, answer.GetProperty("hits_count").GetInt32());

        // The whole point: the zero is qualified. A search that never
        // opened the file has not established that the file is empty.
        Assert.Equal(1, answer.GetProperty("documents_skipped").GetInt32());
    }

    [Fact]
    public async Task TheSkippedCountIsSaidOutLoudInTheTextAnswerToo()
    {
        using var box = new Sandbox();
        Documents.Forget();
        box.WriteRaw("notes.pdf", APdf("Kirkburton"));
        box.Write("other.txt", "Kirkburton\n");

        CliResult search = await Run(box, "search", "Kirkburton", "--no-documents");

        Assert.Contains("not looked inside", search.Stdout);
    }

    [Fact]
    public async Task AnUnreadableDocumentIsCountedRatherThanFailingTheWholeSweep()
    {
        using var box = new Sandbox();
        Documents.Forget();
        box.Write("lying.pdf", "not a pdf, not even slightly");
        box.Write("real.txt", "Ossett\n");

        CliResult search = await Run(box, "search", "Ossett", "--json");
        JsonElement answer = SearchJson(search);

        // The good file is still searched and still found.
        Assert.Equal(ExitCodes.Ok, search.ExitCode);
        Assert.Equal(1, answer.GetProperty("hits_count").GetInt32());
        Assert.Equal(1, answer.GetProperty("documents_skipped").GetInt32());
    }

    [Fact]
    public async Task NothingIsReportedAsSkippedWhenNothingWas()
    {
        using var box = new Sandbox();
        box.Write("plain.txt", "Ossett\n");

        CliResult search = await Run(box, "search", "Ossett", "--json");

        Assert.False(SearchJson(search).TryGetProperty("documents_skipped", out _));
    }

    // ---- the cache ----

    [Fact]
    public async Task TheCacheNeverServesARenderingOfAFileThatHasChanged()
    {
        using var box = new Sandbox();
        Documents.Forget();

        box.WriteRaw("notes.pdf", APdf("Kirkburton"));
        CliResult first = await Run(box, "read", "notes.pdf");
        Assert.Contains("Kirkburton", first.Stdout);

        // Rewritten in place, under the same name, with the rendering of
        // the old one sitting in the cache. A stale hit here would be
        // Fettler answering confidently from a file that no longer says
        // that - the failure the hash on every edit exists to stop.
        box.WriteRaw("notes.pdf", APdf("Heckmondwike"));
        CliResult second = await Run(box, "read", "notes.pdf");

        Assert.Contains("Heckmondwike", second.Stdout);
        Assert.DoesNotContain("Kirkburton", second.Stdout);
    }

    [Fact]
    public async Task TheHashStaysTheFilesEvenThoughTheLinesAreARendering()
    {
        using var box = new Sandbox();
        Documents.Forget();

        byte[] bytes = APdf("Kirkburton");
        box.WriteRaw("notes.pdf", bytes);

        CliResult read = await Run(box, "read", "notes.pdf", "--json");
        JsonElement file = JsonDocument.Parse(read.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal(TextIo.HashOf(bytes), file.GetProperty("hash").GetString());
        Assert.Equal(bytes.Length, file.GetProperty("bytes").GetInt64());
    }

    // ---- the table itself ----

    [Fact]
    public void TheTableClaimsPdfAndTheKindFollowsFromIt()
    {
        Assert.NotNull(Documents.For("whatever.pdf"));
        Assert.NotNull(Documents.For("WHATEVER.PDF"));
        Assert.Null(Documents.For("whatever.txt"));
        Assert.Null(Documents.For("whatever"));

        Assert.Equal(FileKind.Pdf, Typed.KindOf("whatever.pdf"));
        Assert.Contains(".pdf", Documents.Extensions());
    }

    [Theory]
    [InlineData(0, null)]                 // before the first mark
    [InlineData(1, "page 1")]
    [InlineData(4, "page 1")]
    [InlineData(5, "page 2")]
    [InlineData(500, "page 2")]           // past the last mark, still in it
    public void ACitationNamesTheSectionALineFallsIn(int line, string? expected)
    {
        var rendering = new Rendering("irrelevant",
            [new Landmark(1, "page 1"), new Landmark(5, "page 2")]);

        Assert.Equal(expected, rendering.Cite(line));
    }

    [Fact]
    public void ARenderingWithNoLandmarksCitesNothingRatherThanGuessing()
    {
        var rendering = new Rendering("irrelevant", []);

        Assert.Null(rendering.Cite(1));
    }
}
