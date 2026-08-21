using System.Diagnostics;
using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;
using Fettler.Mcp;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// Phase 1: the defects found by USING the tool, each one a thing that
/// went wrong in the session that wrote this list rather than a thing
/// somebody imagined might.
/// </summary>
public sealed class DefectTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    // ---- 1.1: the anchor and the file disagree about line endings ----

    /// <summary>
    /// An anchor spanning two lines, composed on Windows, against a file
    /// written with LF. It looks right, it reads right, and byte-exact
    /// matching never finds it.
    /// </summary>
    [Fact]
    public async Task ACrlfAnchorFindsItsPlaceInAnLfFile()
    {
        using var box = new Sandbox();
        box.Write("source.cs", "one\ntwo\nthree\n");

        Result<EditAnswer> edited = await box.Bench.EditAsync(
            [new FileEdits("source.cs", null, [new Edit.Replace("one\r\ntwo", "ONE\r\nTWO")])],
            dryRun: false);

        Assert.True(edited.IsOk, edited.Failure?.Message);
        Assert.Equal("ONE\nTWO\nthree\n", box.ReadText("source.cs"));
    }

    [Fact]
    public async Task AnLfAnchorFindsItsPlaceInACrlfFile()
    {
        using var box = new Sandbox();
        box.Write("source.cs", "one\r\ntwo\r\nthree\r\n");

        Result<EditAnswer> edited = await box.Bench.EditAsync(
            [new FileEdits("source.cs", null, [new Edit.Replace("one\ntwo", "ONE\nTWO")])],
            dryRun: false);

        Assert.True(edited.IsOk, edited.Failure?.Message);
        Assert.Equal("ONE\r\nTWO\r\nthree\r\n", box.ReadText("source.cs"));
    }

    /// <summary>
    /// The exact match is tried FIRST, so a caller who genuinely means
    /// CRLF still wins on a file that genuinely has it. The fallback
    /// never changes what an already-matching anchor does.
    /// </summary>
    [Fact]
    public async Task AnExactMatchStillWinsWhereThereIsOne()
    {
        using var box = new Sandbox();
        box.Write("mixed.txt", "keep\r\nhere\nkeep\r\nhere\n");

        // "keep\r\nhere" matches exactly, twice; the fallback would have
        // matched four times and the count says which happened.
        Result<EditAnswer> edited = await box.Bench.EditAsync(
            [new FileEdits("mixed.txt", null, [new Edit.Replace("keep\r\nhere", "X", All: true)])],
            dryRun: true);

        Assert.True(edited.IsOk, edited.Failure?.Message);
        Assert.Contains("2 x", edited.Value.Plan[0].Description);
    }

    // ---- 1.2: write says when it left a file mixed ----

    /// <summary>
    /// The two verbs treat endings differently on purpose, and the
    /// reports now say which happened.
    ///
    /// <para><c>write</c> replaces a whole file, so it regularises: a
    /// caller sending CRLF text to an LF file gets an LF file, and the
    /// answer can never be mixed. <c>edit</c> splices into a file that
    /// already exists and leaves the lines it did not touch alone - so an
    /// edit CAN leave a file disagreeing with itself, and that is exactly
    /// the stray CRLF that once sat in this repository for two commits
    /// until version control complained.</para>
    /// </summary>
    [Fact]
    public async Task WriteRegularisesSoItsAnswerIsNeverMixed()
    {
        using var box = new Sandbox();

        CliResult wrote = await Run(box, "write", "mixed.txt", "--text", "a\r\nb\nc\n", "--eol", "lf", "--json");
        JsonElement answer = JsonDocument.Parse(wrote.Stdout).RootElement;

        Assert.True(answer.GetProperty("ok").GetBoolean());
        Assert.False(answer.GetProperty("line_ending_mixed").GetBoolean());
        Assert.Equal("a\nb\nc\n", box.ReadText("mixed.txt"));
    }

    [Fact]
    public async Task EditSaysWhenItHasLeftAFileDisagreeingWithItself()
    {
        using var box = new Sandbox();

        // A file that is already mixed, written from outside Fettler the
        // way one arrives in real life.
        box.Write("legacy.txt", "a\r\nb\nc\n");

        CliResult edited = await Run(box, "edit", "legacy.txt", "--replace", "c", "--with", "C", "--json");
        JsonElement file = JsonDocument.Parse(edited.Stdout).RootElement.GetProperty("files")[0];

        Assert.True(file.GetProperty("line_ending_mixed").GetBoolean(),
            "an edit that leaves a file disagreeing with itself must say so");

        CliResult human = await Run(box, "edit", "legacy.txt", "--replace", "b", "--with", "B");
        Assert.Contains("(MIXED line endings)", human.Stdout);
    }

    [Fact]
    public async Task AnOrdinaryEditIsNotReportedAsMixed()
    {
        using var box = new Sandbox();
        box.Write("plain.txt", "a\nb\nc\n");

        CliResult edited = await Run(box, "edit", "plain.txt", "--replace", "b", "--with", "B", "--json");
        JsonElement file = JsonDocument.Parse(edited.Stdout).RootElement.GetProperty("files")[0];

        Assert.False(file.GetProperty("line_ending_mixed").GetBoolean());
    }

    // ---- 1.3: a glob prunes the walk rather than filtering it ----

    /// <summary>
    /// A pattern with a fixed prefix names the only directory that can
    /// possibly match, and walking anywhere else is work spent to reach
    /// the same answer. Rooted at a home directory this made a one-file
    /// search exceed two minutes.
    /// </summary>
    [Fact]
    public void AFixedPrefixIsWholeSegmentsAndStopsAtTheFirstWildcard()
    {
        Assert.Equal("src/main", Glob.Compile("src/main/**/*.cs").Value.FixedPrefix);
        Assert.Equal("src", Glob.Compile("src/ma*/x.cs").Value.FixedPrefix);
        Assert.Equal("", Glob.Compile("**/*.cs").Value.FixedPrefix);
        Assert.Equal("", Glob.Compile("*.cs").Value.FixedPrefix);
        Assert.Equal("a/b/c", Glob.Compile("a/b/c/d.cs").Value.FixedPrefix);
        // A backslash is a separator too, so a Windows-shaped pattern
        // prunes exactly as far as the forward-slash one does.
        Assert.Equal("src/deep", Glob.Compile(@"src\deep\*.cs").Value.FixedPrefix);
    }

    [Fact]
    public async Task PruningDoesNotChangeWhichFilesAreFound()
    {
        using var box = new Sandbox();
        box.Write("src/main/one.cs", "x");
        box.Write("src/main/deep/two.cs", "x");
        box.Write("src/other/three.cs", "x");
        box.Write("elsewhere/four.cs", "x");

        CliResult found = await Run(box, "find", "src/main/**/*.cs", "--json");
        JsonElement files = JsonDocument.Parse(found.Stdout).RootElement.GetProperty("files");

        Assert.Equal(2, files.GetArrayLength());
        Assert.Contains("src/main/one.cs", found.Stdout);
        Assert.Contains("src/main/deep/two.cs", found.Stdout);
    }

    [Fact]
    public async Task APrefixNamingADirectoryThatIsNotThereFindsNothingQuickly()
    {
        using var box = new Sandbox();
        for (int i = 0; i < 200; i++) box.Write($"noise/{i}/file.cs", "x");

        var clock = Stopwatch.StartNew();
        CliResult found = await Run(box, "find", "absent/**/*.cs", "--json");
        clock.Stop();

        Assert.Contains("\"count\":0", found.Stdout);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "a prefix that cannot match must not walk the tree");
    }

    // ---- 1.4: accepted and documented, not fixed ----

    /// <summary>
    /// Two unknown flags written back to back surface one at a time: the
    /// first is not a switch, so it eats the second as its value.
    ///
    /// <para><b>Accepted rather than fixed.</b> Nothing runs either way -
    /// the command is refused and the caller is told which flag was
    /// wrong - and telling a value apart from a misspelt switch needs a
    /// table per verb, which is a second list that can drift from the
    /// dispatcher. This test exists so the limitation is a decision on
    /// the record rather than a surprise.</para>
    /// </summary>
    [Fact]
    public async Task TwoUnknownFlagsTogetherSurfaceOneAtATime()
    {
        using var box = new Sandbox();

        CliResult refused = await Run(box, "find", "--one-typo", "--another-typo");

        Assert.Equal(ExitCodes.Invalid, refused.ExitCode);
        Assert.Contains("--one-typo", refused.Stderr);
        Assert.DoesNotContain("--another-typo", refused.Stderr);
    }

    // ---- B.16: say it where it is read ----

    /// <summary>
    /// The highest-leverage grounding there is: read at the moment a tool
    /// is chosen, shipped with the tool, needing nobody's cooperation.
    /// Instruction alone is the weak lever - in the session that produced
    /// this design the assistant had extensive grounding and reached for
    /// a working directory anyway - so this asserts the words are there
    /// and the design removes the need as well.
    /// </summary>
    [Fact]
    public async Task TheServerTellsEveryClientThereIsNoWorkingDirectory()
    {
        using var box = new Sandbox();
        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);

        string? reply = await server.AnswerAsync("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}
            """);

        string instructions = JsonDocument.Parse(reply!).RootElement
            .GetProperty("result").GetProperty("instructions").GetString()!;

        Assert.Contains("no current directory", instructions);
        Assert.Contains("changing directory does nothing", instructions);
        Assert.Contains("Call roots FIRST", instructions);
        Assert.Contains("execute, which is never granted by default", instructions);
        Assert.Contains(RootsFile.FileName, instructions);
    }

    [Fact]
    public async Task ThePerVerbDescriptionsSayItToo()
    {
        using var box = new Sandbox();
        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);

        string? reply = await server.AnswerAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        JsonElement tools = JsonDocument.Parse(reply!).RootElement.GetProperty("result").GetProperty("tools");

        string Description(string name)
        {
            foreach (JsonElement tool in tools.EnumerateArray())
                if (tool.GetProperty("name").GetString() == name)
                    return tool.GetProperty("description").GetString()!;
            throw new Xunit.Sdk.XunitException($"no tool called {name}");
        }

        Assert.Contains("not to any working directory", Description("find"));
        Assert.Contains("only inside declared trees", Description("search"));
        Assert.Contains("WHAT MAY BE DONE", Description("roots"));
        Assert.Contains("never granted by default", Description("run"));
    }

    /// <summary>
    /// The other half of B.16, and the half that actually works: no tool
    /// schema carries a root or a config flag, so a model cannot name a
    /// tree it was not given. <c>in</c> selects among trees already open
    /// and grants nothing.
    /// </summary>
    [Fact]
    public async Task NoToolLETSAModelNameATreeItWasNotGiven()
    {
        using var box = new Sandbox();
        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);

        string? reply = await server.AnswerAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        JsonElement tools = JsonDocument.Parse(reply!).RootElement.GetProperty("result").GetProperty("tools");

        foreach (JsonElement tool in tools.EnumerateArray())
        {
            if (!tool.GetProperty("inputSchema").TryGetProperty("properties", out JsonElement properties)) continue;

            foreach (JsonProperty property in properties.EnumerateObject())
                Assert.True(property.Name is not ("root" or "config" or "no_config" or "no-config"),
                    $"the {tool.GetProperty("name").GetString()} tool offers '{property.Name}', "
                    + "which would let a model move its own boundary");
        }
    }

    // ---- the MCP front end reads a stream too ----

    /// <summary>
    /// Found by driving the published binary from Windows PowerShell 5.1,
    /// which prefixes a byte-order mark to whatever it pipes into a native
    /// program: initialize came back -32700 and the session never opened
    /// at all. Every CLI stdin path had taken the mark off since early on
    /// and this one had not - and the CI step that drives the MCP front
    /// end runs under pwsh 7, which emits no mark, so nothing ever saw it.
    /// </summary>
    [Fact]
    public async Task ALeadingByteOrderMarkOnTheMcpStreamIsNotContent()
    {
        using var box = new Sandbox();
        var written = new StringWriter();
        const string initialize =
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""";

        using var server = new McpServer(
            box.Bench, new StringReader("\uFEFF" + initialize + "\n"), written);
        await server.RunAsync();

        JsonElement reply = JsonDocument.Parse(written.ToString()).RootElement;
        Assert.False(reply.TryGetProperty("error", out _), written.ToString());
        Assert.Equal("fettler", reply.GetProperty("result")
            .GetProperty("serverInfo").GetProperty("name").GetString());
    }

    /// <summary>
    /// Text a caller sends arrives as UTF-8, and the bytes that reach
    /// disk are the ones it meant.
    ///
    /// <para><b>The defect.</b> <c>fettle serve</c> handed
    /// <c>Console.In</c> to the server, and on Windows that decodes stdin
    /// with the CONSOLE'S INPUT CODE PAGE - 437 or 850 on a default
    /// install. The three UTF-8 bytes of an em-dash were read as three
    /// separate characters and then written back out as UTF-8 in their
    /// own right: six bytes where there should be three. Silently - the
    /// round trip is self-consistent, so the hash matched and nothing
    /// downstream could tell. <c>Console.OutputEncoding</c> had been set
    /// since early on and the input side never was; the asymmetry was the
    /// whole of it.</para>
    ///
    /// <para>It reached further than writing. A search for a non-ASCII
    /// pattern matched nothing and said so calmly, which is the worse of
    /// the two failures.</para>
    ///
    /// <para><b>What this covers and what it does not.</b> The server is
    /// driven from real UTF-8 BYTES, through the reader the fix builds,
    /// so the whole chain below the front end is held to it. The single
    /// line it cannot reach is the wiring in <c>Program.Main</c> - and
    /// that is exactly why nothing caught this, since every other MCP
    /// test uses <c>TextReader.Null</c> or a <c>StringReader</c>, and a
    /// StringReader cannot reproduce the fault because by then the
    /// decoding has already happened.</para>
    /// </summary>
    [Fact]
    public async Task TextFromTheCallerArrivesAsUtf8WhateverTheConsoleCodePageIs()
    {
        using var box = new Sandbox();

        // An em-dash, curly quotes and an arrow: characters whose UTF-8
        // bytes a code page reads as several apiece.
        // Built from code points rather than written as literals. Not
        // squeamishness: this file has to state the expected bytes
        // exactly, and a literal here would be at the mercy of whatever
        // wrote the file - which is the defect above, met while writing
        // the test for it.
        string awkward = "em-dash " + (char)0x2014 + " curly " + (char)0x2019
            + "quote" + (char)0x2019 + " arrow " + (char)0x2192;

        // The default encoder escapes non-ASCII to \uXXXX, which would
        // put pure ASCII on the wire and prove nothing at all. This is
        // the raw payload a real client sends.
        string call = JsonSerializer.Serialize(
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = "write",
                    arguments = new { path = "note.md", text = awkward },
                },
            },
            new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

        var encoding = new System.Text.UTF8Encoding(false);
        var pipe = new MemoryStream(encoding.GetBytes(call + "\n"));
        using var reader = new StreamReader(pipe, encoding);

        var written = new StringWriter();
        using var server = new McpServer(box.Bench, reader, written);
        await server.RunAsync();

        Assert.DoesNotContain("\"isError\":true", written.ToString());
        Assert.Equal(awkward, box.ReadText("note.md"));

        // Stated in bytes too: a character comparison would still pass if
        // both sides were wrong in the same direction.
        Assert.Equal(encoding.GetBytes(awkward), box.ReadRaw("note.md"));
    }

    // ---- a refused tool call is still a machine-readable answer ----

    /// <summary>
    /// R3.7 says the complete answer, failures included, is machine
    /// readable. A refusal was being described a second time on the way
    /// out, which put "refused: " in front of the JSON document and left
    /// the caller something that no longer parsed - and the word was the
    /// same one every time, where the payload's own outcome is the true
    /// one.
    /// </summary>
    [Fact]
    public async Task ARefusedToolCallCarriesTheJsonAnswerAndNothingInFrontOfIt()
    {
        using var box = new Sandbox();
        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);

        string? reply = await server.AnswerAsync("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call",
             "params":{"name":"read","arguments":{"paths":["absent.txt"]}}}
            """);

        JsonElement result = JsonDocument.Parse(reply!).RootElement.GetProperty("result");

        Assert.True(result.GetProperty("isError").GetBoolean());

        string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        JsonElement payload = JsonDocument.Parse(text).RootElement;

        Assert.False(payload.GetProperty("ok").GetBoolean());
        Assert.Equal("not-found", payload.GetProperty("outcome").GetString());
    }

    // ---- a known flag, and no value after it ----

    /// <summary>
    /// R3.14 refuses a flag nobody knows. This is the same wrong answer
    /// with the name spelt right: the value was missing, the switch value
    /// stood in for it, and `--insert-after 1 --text` wrote the word
    /// "true" into a source file and answered 0. Windows PowerShell 5.1
    /// gets here by dropping an empty argument rather than passing it, so
    /// `--text ''` is the spelling that found it.
    /// </summary>
    [Fact]
    public async Task AKnownFlagWithNoValueIsRefusedRatherThanGuessedAt()
    {
        using var box = new Sandbox();
        box.Write("f.txt", "one\ntwo\n");

        CliResult refused = await Run(box, "edit", "f.txt", "--insert-after", "1", "--text");

        Assert.Equal(ExitCodes.Invalid, refused.ExitCode);
        Assert.Contains("--text", refused.Stdout + refused.Stderr);
        Assert.Equal("one\ntwo\n", box.ReadText("f.txt"));

        // And the way to say a deliberately empty one still works, so the
        // refusal closes a hole rather than a capability.
        CliResult inserted = await Run(box, "edit", "f.txt", "--insert-after", "1", "--text=");

        Assert.Equal(ExitCodes.Ok, inserted.ExitCode);
        Assert.Equal("one\n\ntwo\n", box.ReadText("f.txt"));
    }

    // ---- a task takes nothing from its caller ----

    /// <summary>
    /// A task is declared whole, so nothing is composed at the call. An
    /// extra word is refused rather than dropped: dropping it runs
    /// something other than what the caller asked for and answers 0, which
    /// is R3.14's wrong answer one argument further in.
    /// </summary>
    [Fact]
    public async Task ATaskTakesNoArgumentsFromItsCaller()
    {
        using var box = new Sandbox(Permissions.Full | Permission.Execute);
        box.Declare(new TaskDecl("hello", ["cmd"], null));

        CliResult refused = await Run(box, "run", "hello", "EXTRA-ARGUMENT");

        Assert.Equal(ExitCodes.Invalid, refused.ExitCode);
        Assert.Contains("EXTRA-ARGUMENT", refused.Stdout + refused.Stderr);
    }

    // ---- a name as wide as the column it sits in ----

    /// <summary>
    /// A fixed-width name column is fixed until something reaches it. A
    /// name of exactly the column's width leaves no gap, and the name runs
    /// into the value it introduces as one word — which is unreadable
    /// rather than merely untidy, since neither half can be picked out.
    /// </summary>
    [Fact]
    public async Task AWideNameStillLeavesAGapBeforeItsValue()
    {
        using var box = new Sandbox("requirements");   // twelve characters
        box.Declare(new TaskDecl("regenerate-all", ["cmd"], null));

        CliResult trees = await Run(box, "roots");
        CliResult tasks = await Run(box, "tasks");

        Assert.Equal(ExitCodes.Ok, trees.ExitCode);
        Assert.DoesNotContain("requirements" + box.Extra["requirements"], trees.Stdout);
        Assert.Contains("requirements  ", trees.Stdout);

        Assert.Equal(ExitCodes.Ok, tasks.ExitCode);
        Assert.DoesNotContain("regenerate-allcmd", tasks.Stdout);
        Assert.Contains("regenerate-all  ", tasks.Stdout);
    }
}
