using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;
using Fettler.Mcp;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// Do no harm: input that is not what a tool takes is refused before
/// anything runs, and the refusal shows what it does take.
///
/// <para><b>Each of these was a silent wrong answer.</b> A batch
/// <c>write</c> with no <c>text</c> wrote an empty file. An edit
/// <c>replace</c> with no <c>with</c> deleted what it matched, so a typo
/// in the key name turned a replacement into a deletion. <c>find</c>
/// given <c>glob</c> for <c>pattern</c> dropped it and listed the whole
/// tree. None of them was refused, so none of them left a trace.</para>
/// </summary>
public sealed class StrictInputTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static async Task<(string Text, bool IsError)> Call(Sandbox box, string tool, object arguments)
    {
        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);
        string message = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments },
        });

        JsonElement result = Parse((await server.AnswerAsync(message))!).GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!,
                result.TryGetProperty("isError", out JsonElement e) && e.GetBoolean());
    }

    static string Message(string text) => Parse(text).GetProperty("message").GetString()!;

    // ---- edit scripts ----

    [Fact]
    public async Task AnEditWithAnUnknownKeyIsRefusedAndTheFileIsUntouched()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "alpha\n");

        var (text, error) = await Call(box, "edit",
            new { path = "a.txt", script = new { edits = new[] { new { replace = "alpha", with_text = "beta" } } } });

        Assert.True(error);
        Assert.Contains("with_text", Message(text));
        Assert.Contains("{\"replace\":\"old\",\"with\":\"new\"}", Message(text));
        Assert.Equal("alpha\n", box.ReadText("a.txt"));
    }

    /// <summary>A replacement must say what it replaces with. Deleting is
    /// written as an empty with, so a missing with is a mistake, never a
    /// deletion.</summary>
    [Fact]
    public async Task AReplaceWithoutWithIsRefusedAndAnEmptyWithDeletes()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "keep drop keep\n");

        var (text, error) = await Call(box, "edit",
            new { path = "a.txt", script = new { edits = new[] { new { replace = " drop" } } } });
        Assert.True(error);
        Assert.Contains("\"with\"", Message(text));
        Assert.Equal("keep drop keep\n", box.ReadText("a.txt"));

        (_, error) = await Call(box, "edit",
            new { path = "a.txt", script = new { edits = new[] { new { replace = " drop", with = "" } } } });
        Assert.False(error);
        Assert.Equal("keep keep\n", box.ReadText("a.txt"));
    }

    [Fact]
    public async Task OnTheCommandLineAReplaceNeedsWithToo()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "keep drop keep\n");

        CliResult refused = await Run(box, "edit", "a.txt", "--replace", " drop");
        Assert.Equal(ExitCodes.Invalid, refused.ExitCode);
        Assert.Contains("--with", refused.Stderr);
        Assert.Equal("keep drop keep\n", box.ReadText("a.txt"));

        CliResult deleted = await Run(box, "edit", "a.txt", "--replace", " drop", "--with=");
        Assert.Equal(ExitCodes.Ok, deleted.ExitCode);
        Assert.Equal("keep keep\n", box.ReadText("a.txt"));
    }

    /// <summary>The logs show <c>"insertAfter":"- [No e..."</c>: text
    /// where a line number goes. It fell through to "needs one of", which
    /// did not say what was wrong.</summary>
    [Fact]
    public async Task ALineNumberGivenAsTextIsNamed()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "one\n");

        var (text, error) = await Call(box, "edit",
            new { path = "a.txt", script = new { edits = new[] { new { insertAfter = "one", text = "two" } } } });

        Assert.True(error);
        Assert.Contains("insertAfter", Message(text));
        Assert.Contains("line number", Message(text));
    }

    [Fact]
    public async Task TwoKindsOfEditInOneEntryAreRefused()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "one\ntwo\n");

        var (text, error) = await Call(box, "edit",
            new { path = "a.txt", script = new { edits = new object[] { new { replace = "one", with = "1", deleteFrom = 2 } } } });

        Assert.True(error);
        Assert.Contains("deleteFrom", Message(text));
        Assert.Equal("one\ntwo\n", box.ReadText("a.txt"));
    }

    [Fact]
    public async Task AScriptWithTheWrongTopLevelShapeShowsTheRightOne()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "one\n");

        var (text, error) = await Call(box, "edit",
            new { path = "a.txt", script = new { edit = new[] { new { replace = "one", with = "1" } } } });

        Assert.True(error);
        Assert.Contains("'edit'", Message(text));
        Assert.Contains("{\"edits\":[", Message(text));
    }

    [Fact]
    public async Task AFilesEntryWithoutAPathShowsTheShape()
    {
        using var box = new Sandbox();

        var (text, error) = await Call(box, "edit",
            new { script = new { files = new[] { new { edits = new[] { new { replace = "a", with = "b" } } } } } });

        Assert.True(error);
        Assert.Contains("{\"files\":[{\"path\":", Message(text));
    }

    // ---- batch ----

    /// <summary>A whole script is checked before its first operation
    /// runs. Checking each as it came would have made the directory and
    /// then refused the write, leaving half a change behind.</summary>
    [Fact]
    public async Task ABatchWriteWithNoTextIsRefusedBeforeAnythingRuns()
    {
        using var box = new Sandbox();

        var (text, error) = await Call(box, "batch", new
        {
            script = new { operations = new object[]
            {
                new { op = "mkdir", path = "made" },
                new { op = "write", path = "made/a.txt", content = "hello" },
            } },
        });

        Assert.True(error);
        Assert.Contains("content", Message(text));
        Assert.Contains("\"text\"", Message(text));
        Assert.False(Directory.Exists(box.Full("made")));
    }

    [Theory]
    [InlineData("""{"op":"move","path":"a.txt"}""", "to")]
    [InlineData("""{"op":"write","path":"a.txt","text":"x","overwrite":"true"}""", "overwrite")]
    [InlineData("""{"op":"exec","path":"a.txt"}""", "executable")]
    [InlineData("""{"op":"mkdir"}""", "path")]
    [InlineData("""{"op":"copy","path":"a.txt","to":"b.txt","recurse":true}""", "recurse")]
    public async Task ABatchOperationThatIsNotWhatItTakesIsRefusedByName(string operation, string named)
    {
        using var box = new Sandbox();
        box.Write("a.txt", "x\n");

        CliResult said = await Run(box, "batch", "--script-inline", $$"""{"operations":[{{operation}}]}""", "--json");

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.Contains(named, Message(said.Stdout));
        Assert.Equal("x\n", box.ReadText("a.txt"));
    }

    [Fact]
    public async Task ABatchScriptWithoutOperationsShowsTheShape()
    {
        using var box = new Sandbox();

        var (text, error) = await Call(box, "batch", new { script = new { steps = Array.Empty<object>() } });

        Assert.True(error);
        Assert.Contains("{\"operations\":[{\"op\":\"mkdir\"", Message(text));
    }

    // ---- MCP arguments ----

    /// <summary>The command line refuses an unknown flag (R3.14). Over MCP
    /// an unknown argument was dropped, so find given glob for pattern
    /// listed the whole tree.</summary>
    [Fact]
    public async Task AnUnknownArgumentIsRefusedAndNamesWhatTheToolTakes()
    {
        using var box = new Sandbox();
        box.Write("a.cs", "x\n");

        var (text, error) = await Call(box, "find", new { glob = "**/*.cs" });

        Assert.True(error);
        Assert.Contains("'glob'", Message(text));
        Assert.Contains("pattern", Message(text));
    }

    [Theory]
    [InlineData("read", """{"paths":"a.cs"}""", "a list")]
    [InlineData("search", """{"patterns":["x"],"limit":"50"}""", "a whole number")]
    [InlineData("search", """{"patterns":["x"],"literal":"true"}""", "true or false")]
    [InlineData("find", """{"sort":"name"}""", "path, mtime")]
    [InlineData("read", """{"paths":["a.cs"],"to":1.5}""", "a whole number")]
    [InlineData("search", """{"patterns":[1]}""", "a list of strings")]
    [InlineData("read", """{}""", "paths")]
    public async Task AnArgumentOfTheWrongShapeIsRefusedWithTheShape(string tool, string arguments, string said)
    {
        using var box = new Sandbox();
        box.Write("a.cs", "x\n");

        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);
        string? reply = await server.AnswerAsync(
            $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}""");
        JsonElement result = Parse(reply!).GetProperty("result");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains(said, Message(result.GetProperty("content")[0].GetProperty("text").GetString()!));
    }

    [Fact]
    public async Task RunStillRefusesAnythingButANameAndATimeout()
    {
        using var box = new Sandbox();

        var (text, error) = await Call(box, "run", new { name = "build", args = "--release" });

        Assert.True(error);
        Assert.Contains("'args'", Message(text));
    }

    /// <summary>A whole-number flag that is not one fell back to its
    /// default without a word, so <c>--limit abc</c> searched with 200.</summary>
    [Theory]
    [InlineData("search", "x", "--limit", "abc")]
    [InlineData("read", "a.cs", "--from", "ten")]
    [InlineData("read", "a.cs", "--column", "1.5")]
    public async Task AWholeNumberFlagThatIsNotOneIsRefused(string verb, string first, string flag, string value)
    {
        using var box = new Sandbox();
        box.Write("a.cs", "x\n");

        CliResult said = await Run(box, verb, first, flag, value);

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.Contains(flag, said.Stderr);
        Assert.Contains("whole number", said.Stderr);
    }

    [Fact]
    public async Task CorrectArgumentsStillWork()
    {
        using var box = new Sandbox();
        box.Write("a.cs", "x\n");

        var (_, error) = await Call(box, "search",
            new { patterns = new[] { "x" }, limit = 5, literal = true, glob = "*.cs", context = 1 });

        Assert.False(error);
    }
}
