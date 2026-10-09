using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;
using Fettler.Mcp;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// 9b: the file types a developer actually has, and the line cap that
/// makes routing every read through this tool affordable.
///
/// <para>These exist because <c>Read</c> is denied to the assistant
/// outright, which makes Fettler the only reader. Every one of these
/// tests is a type somebody would otherwise simply be unable to open.</para>
/// </summary>
public sealed class TypedReadTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    // ---- 9b.1: notebooks ----

    const string Notebook = """
        {
          "cells": [
            { "cell_type": "markdown", "source": ["# Title\n", "Some prose.\n"] },
            {
              "cell_type": "code",
              "source": "print('hello')\n",
              "outputs": [
                { "output_type": "stream", "name": "stdout", "text": ["hello\n"] },
                { "output_type": "display_data",
                  "data": { "image/png": "AAAABBBBCCCCDDDD", "text/plain": ["<Figure>"] } }
              ]
            },
            {
              "cell_type": "code",
              "source": "boom()",
              "outputs": [ { "output_type": "error", "ename": "NameError", "evalue": "boom is not defined" } ]
            }
          ],
          "metadata": {},
          "nbformat": 4
        }
        """;

    [Fact]
    public async Task ANotebookIsReadAsCellsRatherThanAsTheJsonItIsStoredIn()
    {
        using var box = new Sandbox();
        box.Write("analysis.ipynb", Notebook);

        CliResult read = await Run(box, "read", "analysis.ipynb");

        Assert.Equal(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("cell 1 markdown", read.Stdout);
        Assert.Contains("# Title", read.Stdout);
        Assert.Contains("print('hello')", read.Stdout);
        Assert.Contains("NameError: boom is not defined", read.Stdout);

        // The JSON scaffolding it is stored in is not what a reader wants.
        Assert.DoesNotContain("nbformat", read.Stdout);
        Assert.DoesNotContain("cell_type", read.Stdout);
    }

    /// <summary>
    /// 9b.9. A single output cell routinely holds a megabyte of base64
    /// PNG, and pasting one into a conversation costs more context than
    /// the whole notebook's code for a picture nobody can see.
    /// </summary>
    [Fact]
    public async Task ANotebooksNonTextOutputIsMeasuredRatherThanPasted()
    {
        using var box = new Sandbox();
        box.Write("analysis.ipynb", Notebook);

        CliResult read = await Run(box, "read", "analysis.ipynb");

        Assert.DoesNotContain("AAAABBBBCCCCDDDD", read.Stdout);
        Assert.Contains("image/png", read.Stdout);
        Assert.Contains("not shown", read.Stdout);
    }

    [Fact]
    public async Task ALongStreamOutputIsCappedAndSaysHowMuchWasLeft()
    {
        using var box = new Sandbox();
        string lines = string.Join("", Enumerable.Range(0, 200).Select(i => $"\"row {i}\\n\","));
        box.Write("loud.ipynb", $$"""
            { "cells": [ { "cell_type": "code", "source": "x",
              "outputs": [ { "output_type": "stream", "text": [{{lines.TrimEnd(',')}}] } ] } ] }
            """);

        CliResult read = await Run(box, "read", "loud.ipynb");

        Assert.Contains("row 0", read.Stdout);
        Assert.DoesNotContain("row 199", read.Stdout);
        Assert.Contains("more lines of output not shown", read.Stdout);
    }

    [Fact]
    public async Task AFileNamedAsANotebookThatIsNotOneIsRefusedSayingSo()
    {
        using var box = new Sandbox();
        box.Write("lying.ipynb", "this is not a notebook at all");

        CliResult read = await Run(box, "read", "lying.ipynb");

        Assert.NotEqual(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("notebook", read.Stderr);
    }

    // ---- 9b.2: images ----

    /// <summary>A one-pixel PNG, written byte by byte so the test states
    /// exactly what it means rather than depending on a library.</summary>
    static byte[] OnePixelPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x07,                             // width 7
        0x00, 0x00, 0x00, 0x03,                             // height 3
        0x08, 0x06, 0x00, 0x00, 0x00,
        0x1F, 0x15, 0xC4, 0x89,
    ];

    [Fact]
    public async Task AnImageAnswersItsFactsAndTheTerminalIsNotSentBase64()
    {
        using var box = new Sandbox();
        box.WriteRaw("shot.png", OnePixelPng());

        CliResult human = await Run(box, "read", "shot.png");

        Assert.Equal(ExitCodes.Ok, human.ExitCode);
        Assert.Contains("image/png", human.Stdout);
        Assert.Contains("7x3", human.Stdout);
        Assert.DoesNotContain("iVBOR", human.Stdout);
    }

    [Fact]
    public async Task TheMachineReadableAnswerCarriesTheBytes()
    {
        using var box = new Sandbox();
        box.WriteRaw("shot.png", OnePixelPng());

        CliResult json = await Run(box, "read", "shot.png", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal("image", file.GetProperty("kind").GetString());
        Assert.Equal("image/png", file.GetProperty("mime_type").GetString());
        Assert.Equal(Convert.ToBase64String(OnePixelPng()), file.GetProperty("base64").GetString());
    }

    /// <summary>
    /// The MCP content model carries an image natively, so the bytes go
    /// there rather than sitting in a text field as unreadable base64 -
    /// and they are not in both places, which would cost the caller the
    /// same context twice.
    /// </summary>
    [Fact]
    public async Task OverMcpAnImageBecomesAnImagePartAndLeavesTheText()
    {
        using var box = new Sandbox();
        box.WriteRaw("shot.png", OnePixelPng());

        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);
        string? reply = await server.AnswerAsync("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call",
             "params":{"name":"read","arguments":{"paths":["shot.png"]}}}
            """);

        JsonElement content = JsonDocument.Parse(reply!).RootElement
            .GetProperty("result").GetProperty("content");

        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.DoesNotContain("base64", content[0].GetProperty("text").GetString());
        Assert.Contains("image/png", content[0].GetProperty("text").GetString());

        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("image/png", content[1].GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(OnePixelPng()), content[1].GetProperty("data").GetString());
    }

    [Fact]
    public async Task AnImageWhoseBytesDisagreeWithItsNameIsRefused()
    {
        using var box = new Sandbox();
        box.Write("lying.png", "plainly text");

        CliResult read = await Run(box, "read", "lying.png");

        Assert.NotEqual(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("not an image format", read.Stderr);
    }

    /// <summary>
    /// R4.5 already covered `edit` and `replace`: both read the file as
    /// text before they change anything, and a PNG never gets past that.
    /// `write --overwrite` reads only to LEARN the encoding and carries
    /// on when that read fails, so it was the one route by which a line
    /// of text could take an image's place.
    /// </summary>
    [Fact]
    public async Task WritingTextOverAnImageIsRefusedAndTheImageIsStillThere()
    {
        using var box = new Sandbox();
        box.WriteRaw("shot.png", OnePixelPng());

        Result<Saved> over = await box.Bench.WriteAsync("shot.png", "not a picture\n", overwrite: true);

        Assert.False(over.IsOk, "a PNG replaced by a line of text is a PNG destroyed");
        Assert.Equal(Outcome.Refused, over.Failure!.Outcome);
        Assert.Contains("an image", over.Failure.Message);
        Assert.True(OnePixelPng().SequenceEqual(box.ReadRaw("shot.png")),
            "the refusal has to leave the file exactly as it found it");
    }

    [Fact]
    public async Task NothingBecomesAnImageArchiveOrGzipByHavingTextWrittenAtItsName()
    {
        using var box = new Sandbox();

        foreach (string name in new[] { "new.png", "new.webp", "new.zip", "new.tar.gz", "new.json.gz" })
        {
            Result<Saved> made = await box.Bench.WriteAsync(name, "text\n", overwrite: false);

            Assert.False(made.IsOk, $"{name} is not a text file and does not become one by being written at");
            Assert.Equal(Outcome.Refused, made.Failure!.Outcome);
            Assert.False(File.Exists(box.Full(name)), $"{name} was created by a refused write");
        }
    }

    /// <summary>A notebook is the one recognised kind this refusal leaves
    /// alone, and deliberately: a <c>.ipynb</c> IS JSON, so text written
    /// at one is the file - however much <c>read</c> renders it as
    /// cells.</summary>
    [Fact]
    public async Task ANotebookIsStillWritableBecauseANotebookIsText()
    {
        using var box = new Sandbox();

        Result<Saved> made = await box.Bench.WriteAsync("fresh.ipynb", Notebook, overwrite: false);

        Assert.True(made.IsOk, made.Failure?.Message);
        Assert.True(File.Exists(box.Full("fresh.ipynb")));
    }

    [Fact]
    public async Task EditWasNeverExposedToThisBecauseItReadsAsTextFirst()
    {
        using var box = new Sandbox();
        box.WriteRaw("shot.png", OnePixelPng());

        Result<EditAnswer> done = await box.Bench.EditAsync(
            [new FileEdits("shot.png", null, [new Edit.Replace("PNG", "GIF")])], dryRun: false);

        Assert.False(done.IsOk, "an edit reads the file as text and R4.5 refuses it there");
        Assert.True(OnePixelPng().SequenceEqual(box.ReadRaw("shot.png")));
    }

    // ---- 9b.3: PDF ----

    static byte[] APdf(string body)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        UglyToad.PdfPig.Writer.PdfDocumentBuilder.AddedFont font =
            builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);

        UglyToad.PdfPig.Writer.PdfPageBuilder page = builder.AddPage(595, 842);
        page.AddText(body, 12, new UglyToad.PdfPig.Core.PdfPoint(40, 800), font);

        return builder.Build();
    }

    [Fact]
    public async Task APdfIsReadAsTextPageByPage()
    {
        using var box = new Sandbox();
        box.WriteRaw("requirements.pdf", APdf("The requirement is written here"));

        CliResult read = await Run(box, "read", "requirements.pdf");

        Assert.Equal(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("page 1 of 1", read.Stdout);
        Assert.Contains("Therequirementiswrittenhere".Substring(0, 3), read.Stdout);
    }

    [Fact]
    public async Task ThePdfHashIsOfTheFileAndNotOfTheRendering()
    {
        using var box = new Sandbox();
        byte[] bytes = APdf("anything");
        box.WriteRaw("doc.pdf", bytes);

        CliResult json = await Run(box, "read", "doc.pdf", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal("pdf", file.GetProperty("kind").GetString());
        Assert.Equal(TextIo.HashOf(bytes), file.GetProperty("hash").GetString());
        Assert.Equal(bytes.Length, file.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public async Task AFileNamedAsAPdfThatIsNotOneIsRefusedRatherThanCrashing()
    {
        using var box = new Sandbox();
        box.Write("lying.pdf", "not a pdf, not even slightly");

        CliResult read = await Run(box, "read", "lying.pdf");

        Assert.NotEqual(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("could not be read", read.Stderr);
    }

    // ---- 9b.8: the line cap ----

    /// <summary>
    /// find stops at a thousand and search at two hundred; read had no
    /// limit at all. Denying the assistant's own reader - which truncates
    /// at two thousand - and routing everything through an uncapped read
    /// would have made the context budget WORSE.
    /// </summary>
    [Fact]
    public async Task ReadStopsAtTheCapAndSaysHowToSeeTheRest()
    {
        using var box = new Sandbox();
        box.Write("long.txt", string.Join("", Enumerable.Range(1, 2500).Select(i => $"line {i}\n")));

        CliResult json = await Run(box, "read", "long.txt", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal(Bench.DefaultLineCap, file.GetProperty("to").GetInt32());
        Assert.Equal(2500, file.GetProperty("lines").GetInt32());
        Assert.True(file.GetProperty("truncated").GetBoolean());
        Assert.Equal(500, file.GetProperty("lines_remaining").GetInt32());

        CliResult human = await Run(box, "read", "long.txt");
        Assert.Contains("500 more line(s)", human.Stdout);
        Assert.Contains("--from 2001", human.Stdout);
    }

    [Fact]
    public async Task AnExplicitRangeLiftsTheCapBecauseTheCallerKnowsWhatTheyAsked()
    {
        using var box = new Sandbox();
        box.Write("long.txt", string.Join("", Enumerable.Range(1, 2500).Select(i => $"line {i}\n")));

        CliResult json = await Run(box, "read", "long.txt", "--from", "1", "--to", "2500", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal(2500, file.GetProperty("to").GetInt32());
        Assert.False(file.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task AFileShorterThanTheCapIsNotReportedAsTruncated()
    {
        using var box = new Sandbox();
        box.Write("short.txt", "one\ntwo\n");

        CliResult json = await Run(box, "read", "short.txt", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.False(file.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, file.GetProperty("to").GetInt32());
    }

    // ---- 9b.4: R4.5 becomes typed ----

    [Fact]
    public async Task AnUnrecognisedBinaryIsStillRefusedAsBinary()
    {
        using var box = new Sandbox();
        box.WriteRaw("mystery.bin", [0x00, 0x01, 0x02, 0x00, 0xFF]);

        CliResult read = await Run(box, "read", "mystery.bin");

        Assert.NotEqual(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("binary", read.Stderr);
    }

    [Fact]
    public async Task CsvIsTextAndNeedsNothingSpecialAtAll()
    {
        using var box = new Sandbox();
        box.Write("figures.csv", "name,amount\nwool,42\n");

        CliResult read = await Run(box, "read", "figures.csv");

        Assert.Equal(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("wool,42", read.Stdout);
    }

    // ---- the answer budget, and the escaping that made it necessary ----

    /// <summary>
    /// Markup on the wire, unescaped. System.Text.Json's default encoder
    /// turns every angle bracket, ampersand, apostrophe and quote into a
    /// six-character <c>\uXXXX</c> sequence, so an HTML page arrived HALF
    /// AGAIN as large as it is on disk - enough to be rejected for size,
    /// which made a one-call read cost three.
    /// </summary>
    [Fact]
    public async Task MarkupIsNotInflatedIntoUnicodeEscapesOnTheWire()
    {
        using var box = new Sandbox();
        box.Write("page.html", "<p class=\"a\">Bread & butter, 'tis</p>\n");

        CliResult json = await Run(box, "read", "page.html", "--json");

        // The characters themselves, not their escapes.
        Assert.Contains("<p class=\\\"a\\\">", json.Stdout);
        Assert.DoesNotContain("\\u003C", json.Stdout);
        Assert.DoesNotContain("\\u003E", json.Stdout);
        Assert.DoesNotContain("\\u0026", json.Stdout);
        Assert.DoesNotContain("\\u0027", json.Stdout);
        Assert.DoesNotContain("\\u0022", json.Stdout);

        // And it is still JSON, which is the half a size test can lose.
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];
        Assert.Equal("<p class=\"a\">Bread & butter, 'tis</p>\n",
                     file.GetProperty("text").GetString());
    }

    /// <summary>200 lines of 200 characters: the whole budget exactly, a
    /// tenth of the line cap, and the file is twice that.</summary>
    static string Wide(int lines) =>
        string.Join("", Enumerable.Range(1, lines).Select(_ => new string('x', 199) + "\n"));

    /// <summary>
    /// The line cap was never the binding one. A file well inside 2000
    /// lines is still large enough for a caller to reject the answer
    /// whole - which costs a turn and returns nothing - so the size of
    /// what is about to be sent is what gets measured.
    /// </summary>
    [Fact]
    public async Task ReadStopsAtTheCharacterBudgetLongBeforeTheLineCap()
    {
        using var box = new Sandbox();
        box.Write("wide.txt", Wide(400));

        CliResult json = await Run(box, "read", "wide.txt", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal(400, file.GetProperty("lines").GetInt32());
        Assert.Equal(200, file.GetProperty("to").GetInt32());
        Assert.Equal(200, file.GetProperty("lines_remaining").GetInt32());
        Assert.True(file.GetProperty("truncated").GetBoolean());
        Assert.True(file.GetProperty("budgeted").GetBoolean());
        Assert.Equal(Bench.DefaultCharCap, file.GetProperty("text").GetString()!.Length);

        // And the human form says which cap stopped it, because "pass
        // --to for more" is bad advice when the range was never the
        // constraint.
        CliResult human = await Run(box, "read", "wide.txt");
        Assert.Contains("limit for one call", human.Stdout);
    }

    /// <summary>
    /// The line cap yields to an explicit range; this one does not.
    /// <c>to</c> says which range the caller wants, and the budget says
    /// how much of it survives the trip - so lifting it on request would
    /// restore the exact failure it exists to remove: ask for a large
    /// range, get nothing at all.
    /// </summary>
    [Fact]
    public async Task AnExplicitRangeDoesNotLiftTheCharacterBudget()
    {
        using var box = new Sandbox();
        box.Write("wide.txt", Wide(400));

        CliResult json = await Run(box, "read", "wide.txt", "--from", "1", "--to", "400", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal(200, file.GetProperty("to").GetInt32());
        Assert.True(file.GetProperty("budgeted").GetBoolean());
    }

    /// <summary>
    /// One budget for the call, not one per path. Reading several paths
    /// at once exists to save the caller turns, and per-file budgets
    /// would multiply and overrun the very ceiling this stays under.
    /// </summary>
    [Fact]
    public async Task TheBudgetIsSpentAcrossTheCallAndNotPerFile()
    {
        using var box = new Sandbox();
        box.Write("first.txt", Wide(400));
        box.Write("second.txt", Wide(400));

        CliResult json = await Run(box, "read", "first.txt", "second.txt", "--json");
        JsonElement files = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files");

        Assert.Equal(200, files[0].GetProperty("to").GetInt32());

        // The first path spent it all, so the second gets the one line
        // that is always served, and says the rest is still there.
        Assert.Equal(1, files[1].GetProperty("to").GetInt32());
        Assert.Equal(399, files[1].GetProperty("lines_remaining").GetInt32());
        Assert.True(files[1].GetProperty("budgeted").GetBoolean());
    }

    /// <summary>
    /// A minified file is one enormous line. Serving nothing back would
    /// be a new dead end rather than a fix for the old one, so the first
    /// line goes whatever it costs.
    /// </summary>
    [Fact]
    public async Task OneLineLongerThanTheWholeBudgetIsStillServed()
    {
        using var box = new Sandbox();
        box.Write("minified.js", new string('z', Bench.DefaultCharCap * 2) + "\n");

        CliResult json = await Run(box, "read", "minified.js", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal(1, file.GetProperty("to").GetInt32());
        Assert.False(file.GetProperty("truncated").GetBoolean());
        Assert.Equal((Bench.DefaultCharCap * 2) + 1, file.GetProperty("text").GetString()!.Length);
    }

    /// <summary>
    /// The budget trims from the end the caller cares least about. A tail
    /// asked for the END of the file, so a trimmed tail keeps its end and
    /// gives up its beginning; trimming the other way would answer a
    /// different question from the one asked.
    /// </summary>
    [Fact]
    public async Task ABudgetedTailKeepsTheEndOfTheFile()
    {
        using var box = new Sandbox();
        box.Write("log.txt", string.Join("", Enumerable.Range(1, 400)
            .Select(i => new string('x', 193) + $"{i,6}" + "\n")));

        CliResult json = await Run(box, "read", "log.txt", "--tail", "400", "--json");
        JsonElement file = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal(400, file.GetProperty("to").GetInt32());
        Assert.Equal(201, file.GetProperty("from").GetInt32());
        Assert.True(file.GetProperty("budgeted").GetBoolean());
        Assert.Contains("   400", file.GetProperty("text").GetString());
    }

    /// <summary>
    /// <c>limit</c> bounds the COUNT of hits, and one hit can be a
    /// thousand-character line of markup - so a search well inside its
    /// hit limit could still be rejected whole for size, which returns
    /// the caller nothing.
    /// </summary>
    [Fact]
    public async Task SearchStopsOnSizeBeforeItReachesTheHitLimit()
    {
        using var box = new Sandbox();
        box.Write("wide.txt", string.Join("", Enumerable.Range(1, 150)
            .Select(_ => "needle " + new string('x', 993) + "\n")));

        CliResult json = await Run(box, "search", "needle", "--json");
        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;

        // Forty thousand characters of hit text, at a thousand apiece -
        // and nowhere near the two hundred hits `limit` would have
        // allowed.
        Assert.Equal(40, root.GetProperty("hits_count").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
    }
}
