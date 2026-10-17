using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;
using Fettler.Mcp;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// Hints: a short second text part the MCP server adds when the calls so
/// far show a missed batch or a missed hand-off.
///
/// <para><b>Measured before it was built.</b> Across 17 surveyed sessions,
/// 78% of reads named one path and only 36% of turns sent more than one
/// call. Back-to-back single-target calls came mostly in pairs, so a run
/// is hinted from its second call. 38% of second reads named a path the
/// answer before them had given, and had to wait; those are not hinted,
/// because the server holds that answer and can tell.</para>
///
/// <para><b>"One after another" means a call arrived after the previous
/// answer had gone out</b>, by more than a turn's gap. Calls sent
/// together in one turn arrive within milliseconds of each other and are
/// never hinted. These tests set the gap to zero to stand for a model's
/// turn between calls.</para>
/// </summary>
public sealed class HintTests
{
    sealed class Session(Sandbox box, TimeSpan? gap = null) : IDisposable
    {
        readonly McpServer server = new(box.Bench, TextReader.Null, TextWriter.Null)
        {
            TurnGap = gap ?? TimeSpan.Zero,
        };

        int id;

        public async Task<(string Text, string? Hint, bool IsError)> Call(string tool, object arguments)
        {
            string message = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = ++id, method = "tools/call",
                @params = new { name = tool, arguments },
            });

            JsonElement result = JsonDocument.Parse((await server.AnswerAsync(message))!).RootElement
                .GetProperty("result").Clone();
            JsonElement[] texts = [.. result.GetProperty("content").EnumerateArray()
                .Where(p => p.GetProperty("type").GetString() == "text")];

            return (texts[0].GetProperty("text").GetString()!,
                    texts.Length > 1 ? texts[1].GetProperty("text").GetString() : null,
                    result.TryGetProperty("isError", out JsonElement e) && e.GetBoolean());
        }

        public void Dispose() => server.Dispose();
    }

    static Sandbox Files(params string[] names)
    {
        var box = new Sandbox();
        foreach (string name in names) box.Write(name, $"text of {name}\n");
        return box;
    }

    // ---- 1 to 4: runs of single-target calls ----

    [Fact]
    public async Task SingleReadsOneAfterAnotherAreHintedFromTheSecondWithTheCount()
    {
        using var box = Files("a.txt", "b.txt", "c.txt");
        using var s = new Session(box);

        Assert.Null((await s.Call("read", new { paths = new[] { "a.txt" } })).Hint);
        Assert.Equal("hint: the last 2 read calls each named one path, sent one after another. "
                     + "read takes several paths in one call.",
            (await s.Call("read", new { paths = new[] { "b.txt" } })).Hint);
        Assert.StartsWith("hint: the last 3 read calls",
            (await s.Call("read", new { paths = new[] { "c.txt" } })).Hint);
    }

    [Fact]
    public async Task AReadOfAPathTheAnswerBeforeItNamedIsNotHinted()
    {
        using var box = new Sandbox();
        box.Write("index.txt", "see notes.txt\n");
        box.Write("notes.txt", "x\n");
        using var s = new Session(box);

        await s.Call("read", new { paths = new[] { "index.txt" } });
        Assert.Null((await s.Call("read", new { paths = new[] { "notes.txt" } })).Hint);
    }

    [Fact]
    public async Task ACallThatTakesSeveralEndsTheRun()
    {
        using var box = Files("a.txt", "b.txt", "c.txt", "d.txt", "e.txt");
        using var s = new Session(box);

        await s.Call("read", new { paths = new[] { "a.txt" } });
        Assert.NotNull((await s.Call("read", new { paths = new[] { "b.txt" } })).Hint);
        Assert.Null((await s.Call("read", new { paths = new[] { "c.txt", "d.txt" } })).Hint);
        Assert.Null((await s.Call("read", new { paths = new[] { "e.txt" } })).Hint);
    }

    /// <summary>Calls sent together in one turn arrive within moments of
    /// each other. With the real gap, two calls that close together are
    /// one turn, and nothing is hinted.</summary>
    [Fact]
    public async Task CallsCloseTogetherAreOneTurnAndAreNotHinted()
    {
        using var box = Files("a.txt", "b.txt");
        using var s = new Session(box, TimeSpan.FromSeconds(5));

        await s.Call("read", new { paths = new[] { "a.txt" } });
        Assert.Null((await s.Call("read", new { paths = new[] { "b.txt" } })).Hint);
    }

    [Fact]
    public async Task SinglePatternSearchesOneAfterAnotherAreHinted()
    {
        using var box = Files("a.txt");
        using var s = new Session(box);

        await s.Call("search", new { patterns = new[] { "alpha" } });
        Assert.Equal("hint: the last 2 search calls each had one pattern, sent one after another. "
                     + "search takes several patterns in one call.",
            (await s.Call("search", new { patterns = new[] { "beta" } })).Hint);
    }

    [Fact]
    public async Task SingleFileOperationsOneAfterAnotherAreHinted()
    {
        using var box = new Sandbox();
        using var s = new Session(box);

        await s.Call("mkdir", new { path = "made" });
        Assert.Equal("hint: the last 2 calls were single file operations (mkdir, write), sent one after "
                     + "another. batch runs a list of them in one call.",
            (await s.Call("write", new { path = "made/a.txt", text = "x" })).Hint);
    }

    [Fact]
    public async Task EditsToOneFileOneAfterAnotherAreHinted()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "one\ntwo\n");
        using var s = new Session(box);

        await s.Call("edit", new { path = "a.txt", script = new { edits = new[] { new { replace = "one", with = "1" } } } });
        Assert.Equal("hint: the last 2 edit calls changed the same file, one after another. "
                     + "One edit call takes every change to a file.",
            (await s.Call("edit", new { path = "a.txt", script = new { edits = new[] { new { replace = "two", with = "2" } } } })).Hint);
    }

    // ---- 5 to 7: hand-offs ----

    [Fact]
    public async Task AnEditWithoutExpectOnAFileReadEarlierIsHinted()
    {
        using var box = Files("a.txt");
        using var s = new Session(box);

        await s.Call("read", new { paths = new[] { "a.txt" } });
        string? hint = (await s.Call("edit",
            new { path = "a.txt", script = new { edits = new[] { new { replace = "text", with = "TEXT" } } } })).Hint;

        Assert.Equal("hint: this edit had no expect. a.txt was read earlier in this session. "
                     + "Pass the hash that read returned as expect.", hint);
    }

    [Fact]
    public async Task ReadingTheSameUnchangedLinesAgainIsHinted()
    {
        using var box = Files("a.txt");
        using var s = new Session(box);

        await s.Call("read", new { paths = new[] { "a.txt" } });
        Assert.Equal("hint: this read returned the same lines of a.txt as an earlier read in this session. "
                     + "The file had not changed.",
            (await s.Call("read", new { paths = new[] { "a.txt" } })).Hint);
    }

    [Fact]
    public async Task ANarrowReadAroundTheHitOfTheSearchBeforeItIsHinted()
    {
        using var box = new Sandbox();
        box.Write("big.txt", string.Concat(Enumerable.Range(1, 100).Select(i => i == 50 ? "needle\n" : $"line {i}\n")));
        using var s = new Session(box);

        await s.Call("search", new { patterns = new[] { "needle" } });
        Assert.Equal("hint: this read covered the lines around a hit from the search before it. "
                     + "search takes context N to return them with the hit.",
            (await s.Call("read", new { paths = new[] { "big.txt" }, from = 45, to = 55 })).Hint);
    }

    // ---- 8 and 9: refusals ----

    [Fact]
    public async Task TheSameRefusalAgainIsHinted()
    {
        using var box = new Sandbox();
        using var s = new Session(box);

        var first = await s.Call("read", new { paths = new[] { "missing.txt" } });
        Assert.True(first.IsError);
        Assert.Null(first.Hint);

        Assert.Equal("hint: this call was refused the same way earlier in this session.",
            (await s.Call("read", new { paths = new[] { "gone.txt" } })).Hint);
    }

    [Fact]
    public async Task APathUnderNoTreeIsHinted()
    {
        using var box = new Sandbox();
        using var s = new Session(box);

        Assert.Equal("hint: no tree covers this path. Retrying will not help. A person has to add a tree "
                     + "to .fettler.local.json.",
            (await s.Call("read", new { paths = new[] { "/etc/hosts" } })).Hint);
    }

    // ---- what a hint never does ----

    /// <summary>The answer is the same with or without a hint: the hint is
    /// a part of its own, and the first part stays what the command line
    /// prints.</summary>
    [Fact]
    public async Task AHintLeavesTheAnswerAsTheCommandLineGivesIt()
    {
        using var box = Files("a.txt", "b.txt");
        using var s = new Session(box);

        await s.Call("read", new { paths = new[] { "a.txt" } });
        var hinted = await s.Call("read", new { paths = new[] { "b.txt" } });
        CliResult cli = await Command.RunAsync(["read", "b.txt", "--json"], new StringReader(""), box.Bench);

        Assert.NotNull(hinted.Hint);
        Assert.Equal(cli.Stdout.TrimEnd(), hinted.Text);
    }

    [Fact]
    public async Task TheCommandLineNeverHints()
    {
        using var box = Files("a.txt", "b.txt");

        foreach (string path in new[] { "a.txt", "b.txt", "a.txt" })
        {
            CliResult said = await Command.RunAsync(["read", path], new StringReader(""), box.Bench);
            Assert.DoesNotContain("hint:", said.Stdout);
        }
    }

    [Fact]
    public async Task AHintFitsTheRoomKeptForIt()
    {
        using var box = new Sandbox();
        string deep = string.Join("/", Enumerable.Repeat(new string('d', 60), 8)) + "/a.txt";
        box.Write(deep, "x\n");
        using var s = new Session(box);

        await s.Call("read", new { paths = new[] { deep } });
        string? hint = (await s.Call("read", new { paths = new[] { deep } })).Hint;

        Assert.NotNull(hint);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(hint) <= Budget.HintRoom);
    }
}
