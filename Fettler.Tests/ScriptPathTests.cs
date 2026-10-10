using System.Text;
using System.Text.Json;
using Fettler.Cli;
using Fettler.Mcp;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R2 of the tools-first enhancement: a path inside a script is a tree
/// path on both front ends, and a script can be named by
/// <c>script_path</c> instead of carried inline.
/// </summary>
public sealed class ScriptPathTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    static async Task<(bool IsError, JsonElement Answer)> Call(Sandbox box, string tool, object arguments)
    {
        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);
        string request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments },
        });

        string? reply = await server.AnswerAsync(request);
        Assert.NotNull(reply);

        JsonElement result = JsonDocument.Parse(reply!).RootElement.GetProperty("result");
        bool isError = result.TryGetProperty("isError", out JsonElement e) && e.GetBoolean();
        string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        return (isError, JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>A file outside every tree, as a model would name one.</summary>
    sealed class Outside : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fettle-outside-{Guid.NewGuid():N}.txt");

        public Outside() => File.WriteAllText(Path, "NOT-FOR-THE-TREE");

        public void Dispose() => File.Delete(Path);
    }

    // ---- R2.3 and R2.4: the defect ----

    [Fact]
    public async Task AnInlineScriptOverMcpCannotSpliceInAFileOutsideEveryTree()
    {
        using var box = new Sandbox();
        using var outside = new Outside();
        box.Write("a.txt", "before MARK after\n");

        (bool isError, JsonElement answer) = await Call(box, "edit", new
        {
            path = "a.txt",
            script = new { edits = new[] { new { replace = "MARK", withFile = outside.Path } } },
        });

        Assert.True(isError);
        Assert.Equal("outside-root", answer.GetProperty("outcome").GetString());
        Assert.Contains("edit 1: withFile", answer.GetProperty("message").GetString());
        Assert.Equal("before MARK after\n", box.ReadText("a.txt"));
    }

    [Fact]
    public async Task AnInlineScriptOverMcpMayNameAFileInATree()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "before MARK after\n");
        box.Write("body.txt", "the body");

        (bool isError, _) = await Call(box, "edit", new
        {
            path = "a.txt",
            script = new { edits = new[] { new { replace = "MARK", withFile = "body.txt" } } },
        });

        Assert.False(isError);
        Assert.Equal("before the body after\n", box.ReadText("a.txt"));
    }

    [Fact]
    public async Task AScriptFileOnTheCommandLineFollowsTheSameRuleAndNamesTheEdit()
    {
        using var box = new Sandbox();
        using var outside = new Outside();
        box.Write("a.txt", "one two three\n");
        box.Write("body.txt", "TWO");

        string bad = box.Write("bad.json", JsonSerializer.Serialize(new
        {
            edits = new object[]
            {
                new { replace = "one", with = "ONE" },
                new { replace = "two", withFile = "body.txt" },
                new { replace = "three", withFile = outside.Path },
            },
        }));

        CliResult refused = await Run(box, "edit", "a.txt", "--script", bad);
        Assert.Equal(ExitCodes.OutsideRoot, refused.ExitCode);
        Assert.Contains("edit 3: withFile", refused.Stderr);
        Assert.Equal("one two three\n", box.ReadText("a.txt"));

        string good = box.Write("good.json", JsonSerializer.Serialize(new
        {
            edits = new object[] { new { replace = "two", withFile = "root:body.txt" } },
        }));

        CliResult done = await Run(box, "edit", "a.txt", "--script", good);
        Assert.Equal(ExitCodes.Ok, done.ExitCode);
        Assert.Equal("one TWO three\n", box.ReadText("a.txt"));
    }

    // ---- R2.1: script_path ----

    static string Lines(int count, string word)
    {
        var text = new StringBuilder();
        for (int i = 1; i <= count; i++) text.Append(word).Append(' ').Append(i.ToString("D2")).Append('\n');
        return text.ToString();
    }

    static object ThirtyEdits(bool lastFails) => new
    {
        files = new[]
        {
            new
            {
                path = "list.txt",
                edits = Enumerable.Range(1, 30).Select(i => new
                {
                    replace = i == 30 && lastFails ? "absent" : $"item {i:D2}",
                    with = $"done {i:D2}",
                }).ToArray(),
            },
        },
    };

    [Fact]
    public async Task ScriptPathAppliesAThirtyEditScriptWholeOrNotAtAll()
    {
        using var box = new Sandbox();
        box.Write("list.txt", Lines(30, "item"));
        box.Write("fails.json", JsonSerializer.Serialize(ThirtyEdits(lastFails: true)));
        box.Write("edits.json", JsonSerializer.Serialize(ThirtyEdits(lastFails: false)));

        (bool failed, _) = await Call(box, "edit", new { script_path = "fails.json" });
        Assert.True(failed);
        Assert.Equal(Lines(30, "item"), box.ReadText("list.txt"));

        (bool isError, _) = await Call(box, "edit", new { script_path = "edits.json" });
        Assert.False(isError);
        Assert.Equal(Lines(30, "done"), box.ReadText("list.txt"));
    }

    [Fact]
    public async Task ScriptAndScriptPathTogetherAreRefused()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "x\n");
        box.Write("edits.json", """{"edits":[{"replace":"x","with":"y"}]}""");

        (bool isError, JsonElement answer) = await Call(box, "edit", new
        {
            path = "a.txt",
            script = new { edits = new[] { new { replace = "x", with = "z" } } },
            script_path = "edits.json",
        });

        Assert.True(isError);
        Assert.Equal("invalid", answer.GetProperty("outcome").GetString());
        Assert.Equal("x\n", box.ReadText("a.txt"));
    }

    [Fact]
    public async Task AScriptPathOutsideEveryTreeIsRefused()
    {
        using var box = new Sandbox();
        using var outside = new Outside();

        (bool isError, JsonElement answer) = await Call(box, "batch", new { script_path = outside.Path });

        Assert.True(isError);
        Assert.Equal("outside-root", answer.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task BatchTakesItsScriptByPath()
    {
        using var box = new Sandbox();
        box.Write("ops.json", """{"operations":[{"op":"mkdir","path":"made"},{"op":"new","path":"made/x.txt"}]}""");

        (bool isError, _) = await Call(box, "batch", new { script_path = "ops.json" });

        Assert.False(isError);
        Assert.True(File.Exists(box.Full("made/x.txt")));
    }

    // ---- R6.3: the catalogue ----

    [Fact]
    public async Task EditAndBatchCarryScriptPathAndNoToolTakesARootConfigOrLevel()
    {
        using var box = new Sandbox();
        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);

        string? listed = await server.AnswerAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        JsonElement tools = JsonDocument.Parse(listed!).RootElement.GetProperty("result").GetProperty("tools");

        foreach (JsonElement tool in tools.EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString()!;
            JsonElement properties = tool.GetProperty("inputSchema").GetProperty("properties");

            foreach (string forbidden in new[] { "root", "config", "level" })
                Assert.False(properties.TryGetProperty(forbidden, out _), $"{name} takes {forbidden}");

            if (name is "edit" or "batch")
            {
                Assert.True(properties.TryGetProperty("script_path", out _), $"{name} has no script_path");
                Assert.Contains("script_path", tool.GetProperty("description").GetString());
            }
        }
    }
}
