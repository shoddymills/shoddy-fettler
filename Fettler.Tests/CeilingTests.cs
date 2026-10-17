using System.Text;
using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;
using Fettler.Mcp;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// The ceiling every answer is held to: 40,000 bytes of UTF-8 as written,
/// 10,000 for a refusal, and an exact way on from every answer that stops
/// short.
///
/// <para><b>Measured on what the caller receives</b>, which is the whole
/// point. The earlier cap counted the raw text of a line or a hit and
/// nothing else, so escaping, field names and the first line served
/// "whatever it costs" all went past it. The client then saved the answer
/// to a file instead of showing it, and reading that file back made the
/// same answer again.</para>
/// </summary>
public sealed class CeilingTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>One tool call through the MCP front end, as a client
    /// makes it: the text part, whether it was marked an error, and every
    /// content part.</summary>
    sealed record Called(string Text, bool IsError, JsonElement Content)
    {
        public JsonElement Answer => Parse(Text);
    }

    static async Task<Called> Call(Sandbox box, string tool, object arguments)
    {
        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);
        string message = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments },
        });

        string? reply = await server.AnswerAsync(message);
        JsonElement result = Parse(reply!).GetProperty("result");
        JsonElement content = result.GetProperty("content");

        return new Called(
            content[0].GetProperty("text").GetString()!,
            result.TryGetProperty("isError", out JsonElement e) && e.GetBoolean(),
            content);
    }

    /// <summary>A line of JSON-shaped text: a quote, a backslash and a
    /// slash in every few characters, each of which costs two bytes once
    /// escaped. The shape of a saved tool result, which is one line.</summary>
    static string QuoteHeavy(int length)
    {
        var text = new StringBuilder();
        int i = 0;
        while (text.Length < length)
            text.Append("{\"k").Append(i++).Append("\":\"C:\\\\dir\\\\f\",\"v\":[1,2]},");
        return text.ToString(0, length);
    }

    /// <summary>Read the whole of one file by following <c>next</c>, and
    /// check every answer on the way is under the ceiling.</summary>
    static async Task<string> ReadByNext(Sandbox box, string path, int? tail = null)
    {
        var whole = new StringBuilder();
        object arguments = tail is { } t
            ? new { paths = new[] { path }, tail = t }
            : new { paths = new[] { path } };

        for (int calls = 0; calls < 10_000; calls++)
        {
            Called read = await Call(box, "read", arguments);
            Assert.False(read.IsError, read.Text);
            Assert.True(Bytes(read.Text) <= Budget.Ceiling, $"{Bytes(read.Text)} bytes");

            JsonElement file = read.Answer.GetProperty("files")[0];
            whole.Append(file.GetProperty("text").GetString());

            if (!file.TryGetProperty("next", out JsonElement next)) return whole.ToString();

            int from = next.GetProperty("from").GetInt32();
            arguments = next.TryGetProperty("column", out JsonElement column)
                ? new { paths = new[] { path }, from, column = column.GetInt32() }
                : new { paths = new[] { path }, from };
        }

        throw new InvalidOperationException("next never ran out");
    }

    // ---- read ----

    /// <summary>
    /// The case that looped. A saved tool result is one line of JSON, and
    /// serving the first line whatever it cost made the answer as large as
    /// the file it came from. The line now comes back in pieces, and the
    /// pieces join up to the file exactly.
    /// </summary>
    [Fact]
    public async Task AOneLineFileFarPastTheCeilingComesBackInPiecesThatJoinUpExactly()
    {
        using var box = new Sandbox();
        string line = QuoteHeavy(200_000);
        box.Write("saved.json", line);

        Called first = await Call(box, "read", new { paths = new[] { "saved.json" } });
        JsonElement file = first.Answer.GetProperty("files")[0];

        Assert.True(Bytes(first.Text) <= Budget.Ceiling);
        Assert.Equal(1, file.GetProperty("from").GetInt32());
        Assert.Equal(1, file.GetProperty("next").GetProperty("from").GetInt32());
        Assert.True(file.GetProperty("next").GetProperty("column").GetInt32() > 1);

        Assert.Equal(line, await ReadByNext(box, "saved.json"));
    }

    /// <summary>A long line after short ones does not cut the answer
    /// mid-line: the short lines come back whole, and the long one starts
    /// the next answer.</summary>
    [Fact]
    public async Task ALongLineAfterShortOnesStopsAtTheLineBoundary()
    {
        using var box = new Sandbox();
        string text = string.Concat(Enumerable.Range(1, 10).Select(i => $"short {i}\n"))
                      + new string('w', 100_000) + "\n";
        box.Write("mixed.txt", text);

        Called read = await Call(box, "read", new { paths = new[] { "mixed.txt" } });
        JsonElement file = read.Answer.GetProperty("files")[0];

        Assert.Equal(10, file.GetProperty("to").GetInt32());
        Assert.Equal(11, file.GetProperty("next").GetProperty("from").GetInt32());
        Assert.False(file.GetProperty("next").TryGetProperty("column", out _));

        Assert.Equal(text, await ReadByNext(box, "mixed.txt"));
    }

    /// <summary>Text of three bytes a character fills the ceiling at a
    /// third of the characters, and is never split inside a character.</summary>
    [Fact]
    public async Task MultiByteTextIsMeasuredInBytesAndJoinsUpExactly()
    {
        using var box = new Sandbox();
        string line = string.Concat(Enumerable.Repeat("\u7e54\u7269\u5de5\u5834\U0001F9F5", 12_000));
        box.Write("cloth.txt", line + "\n" + line + "\n");

        Assert.Equal(line + "\n" + line + "\n", await ReadByNext(box, "cloth.txt"));
    }

    /// <summary>A tail asks for the end, and the end is one line too long
    /// to send. That line is read from its start, and next carries on
    /// through it.</summary>
    [Fact]
    public async Task ATailWhoseLastLineIsTooLongReadsThatLineFromItsStart()
    {
        using var box = new Sandbox();
        string last = new string('t', 100_000);
        box.Write("log.txt", "first\nsecond\n" + last + "\n");

        Called read = await Call(box, "read", new { paths = new[] { "log.txt" }, tail = 1 });
        JsonElement file = read.Answer.GetProperty("files")[0];

        Assert.Equal(3, file.GetProperty("from").GetInt32());
        Assert.Equal(3, file.GetProperty("next").GetProperty("from").GetInt32());
        Assert.True(Bytes(read.Text) <= Budget.Ceiling);

        Assert.Equal(last + "\n", await ReadByNext(box, "log.txt", tail: 1));
    }

    /// <summary>The cut is made once, before either output mode is
    /// written, so the terminal and the machine-readable answer stop at
    /// the same place, and both are under the ceiling. A numbered read
    /// carries the prefix in its text, so it stops no later.</summary>
    [Fact]
    public async Task BothOutputModesStopAtTheSamePlaceUnderTheCeiling()
    {
        using var box = new Sandbox();
        box.Write("lines.json", string.Concat(Enumerable.Range(1, 3000).Select(i => QuoteHeavy(60) + "\n")));

        CliResult json = await Run(box, "read", "lines.json", "--json");
        CliResult numbered = await Run(box, "read", "lines.json", "--numbered", "--json");
        CliResult human = await Run(box, "read", "lines.json");

        Assert.True(Bytes(json.Stdout) <= Budget.Ceiling);
        Assert.True(Bytes(numbered.Stdout) <= Budget.Ceiling);
        Assert.True(Bytes(human.Stdout) <= Budget.Ceiling);

        int to = Parse(json.Stdout).GetProperty("files")[0].GetProperty("to").GetInt32();
        Assert.True(Parse(numbered.Stdout).GetProperty("files")[0].GetProperty("to").GetInt32() <= to);
        Assert.Contains($"--from {to + 1}", human.Stdout);
    }

    /// <summary>Several paths share one ceiling, and a path that gets no
    /// room still says where to start.</summary>
    [Fact]
    public async Task SeveralPathsShareOneCeiling()
    {
        using var box = new Sandbox();
        box.Write("one.json", QuoteHeavy(90_000));
        box.Write("two.json", QuoteHeavy(90_000));

        Called read = await Call(box, "read", new { paths = new[] { "one.json", "two.json" } });
        JsonElement files = read.Answer.GetProperty("files");

        Assert.True(Bytes(read.Text) <= Budget.Ceiling);
        Assert.Equal(2, files.GetArrayLength());
        Assert.Equal(1, files[1].GetProperty("next").GetProperty("from").GetInt32());
    }

    /// <summary>A column outside the first line is a mistake to name, not
    /// a request to guess at.</summary>
    [Fact]
    public async Task AColumnPastTheEndOfTheLineIsRefused()
    {
        using var box = new Sandbox();
        box.Write("short.txt", "abc\n");

        Called read = await Call(box, "read", new { paths = new[] { "short.txt" }, column = 9 });

        Assert.True(read.IsError);
        Assert.Contains("column", read.Text);
    }

    // ---- search ----

    /// <summary>
    /// One minified line or SVG path can be hundreds of kilobytes. The hit
    /// is the match and a window either side of it, and says where in the
    /// line the window starts, so it can still be found and edited.
    /// </summary>
    [Fact]
    public async Task AHitInsideAVeryLongLineIsClippedAroundTheMatch()
    {
        using var box = new Sandbox();
        string line = new string('a', 150_000) + "needle" + new string('b', 150_000);
        box.Write("drawing.svg", line + "\n");

        Called found = await Call(box, "search", new { patterns = new[] { "needle" } });
        JsonElement hit = found.Answer.GetProperty("hits")[0];

        Assert.True(Bytes(found.Text) <= Budget.Ceiling);
        Assert.True(hit.GetProperty("clipped").GetBoolean());
        Assert.Equal(line.Length, hit.GetProperty("line_length").GetInt32());

        string text = hit.GetProperty("text").GetString()!;
        int from = hit.GetProperty("text_from_column").GetInt32();
        Assert.Contains("needle", text);
        Assert.Equal(line.Substring(from - 1, text.Length), text);
        Assert.True(text.Length <= "needle".Length + (2 * Budget.MatchWindow));
    }

    /// <summary>A search cut short gives the exact place to resume, and
    /// following it collects every hit once, which the count confirms.</summary>
    [Fact]
    public async Task ASearchThatIsCutGivesTheExactPlaceToResume()
    {
        using var box = new Sandbox();
        for (int f = 0; f < 5; f++)
            box.Write($"part{f}.json", string.Concat(Enumerable.Range(1, 400)
                .Select(i => "needle " + QuoteHeavy(150) + "\n")));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        object arguments = new { patterns = new[] { "needle" }, limit = 100_000 };

        for (int calls = 0; ; calls++)
        {
            Assert.True(calls < 1000, "next never ran out");

            Called found = await Call(box, "search", arguments);
            Assert.True(Bytes(found.Text) <= Budget.Ceiling, $"{Bytes(found.Text)} bytes");

            foreach (JsonElement hit in found.Answer.GetProperty("hits").EnumerateArray())
                Assert.True(seen.Add($"{hit.GetProperty("path").GetString()}:{hit.GetProperty("line").GetInt32()}"));

            if (!found.Answer.TryGetProperty("next", out JsonElement next)) break;
            Assert.True(found.Answer.GetProperty("truncated").GetBoolean());
            arguments = new { patterns = new[] { "needle" }, limit = 100_000, after = next.GetString() };
        }

        Called counted = await Call(box, "search", new { patterns = new[] { "needle" }, count = true });
        Assert.Equal(2000, counted.Answer.GetProperty("hits_count").GetInt32());
        Assert.Equal(2000, seen.Count);
    }

    /// <summary>A count sends no text, so nothing about the size of the
    /// text may stop it. It counts every hit, past the hit limit too.</summary>
    [Fact]
    public async Task ACountIsExactPastTheHitLimitAndTheCeiling()
    {
        using var box = new Sandbox();
        box.Write("many.txt", string.Concat(Enumerable.Range(1, 1500)
            .Select(i => "needle " + new string('x', 900) + "\n")));

        Called counted = await Call(box, "search", new { patterns = new[] { "needle" }, count = true });

        Assert.Equal(1500, counted.Answer.GetProperty("hits_count").GetInt32());
        Assert.Equal(1, counted.Answer.GetProperty("files").GetInt32());
        Assert.False(counted.Answer.GetProperty("truncated").GetBoolean());
    }

    /// <summary>files_only names each file once, with how many lines in it
    /// matched, the same in both output modes.</summary>
    [Fact]
    public async Task FilesOnlyNamesEachFileOnceWithItsHits()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "needle\nneedle\nneedle\n");
        box.Write("b.txt", "needle\n");
        box.Write("c.txt", "nothing\n");

        Called found = await Call(box, "search", new { patterns = new[] { "needle" }, files_only = true });
        JsonElement files = found.Answer.GetProperty("matched_files");

        Assert.Equal(2, files.GetArrayLength());
        Assert.Equal("a.txt", files[0].GetProperty("path").GetString());
        Assert.Equal(3, files[0].GetProperty("hits").GetInt32());
        Assert.Equal("b.txt", files[1].GetProperty("path").GetString());
        Assert.Equal(1, files[1].GetProperty("hits").GetInt32());

        CliResult human = await Run(box, "search", "needle", "--files-only");
        Assert.Contains("a.txt  3", human.Stdout);
        Assert.Contains("b.txt  1", human.Stdout);
    }

    /// <summary>An after that names no hit position is a mistake to say,
    /// not a reason to start from the top.</summary>
    [Fact]
    public async Task AMalformedAfterIsRefused()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "needle\n");

        Called found = await Call(box, "search", new { patterns = new[] { "needle" }, after = "a.txt" });

        Assert.True(found.IsError);
        Assert.Contains("after", found.Text);
    }

    // ---- find ----

    static void Thousands(Sandbox box, int count)
    {
        for (int i = 0; i < count; i++)
            box.Write($"deep/folder/with/a/long/name/file-{i:D5}-{new string('n', 60)}.txt", "x");
    }

    static async Task<List<string>> FindByNext(Sandbox box, string? sort = null)
    {
        var paths = new List<string>();
        object arguments = sort is null ? new { limit = 100_000 } : new { limit = 100_000, sort };

        for (int calls = 0; ; calls++)
        {
            Assert.True(calls < 1000, "next never ran out");

            Called found = await Call(box, "find", arguments);
            Assert.True(Bytes(found.Text) <= Budget.Ceiling, $"{Bytes(found.Text)} bytes");

            foreach (JsonElement f in found.Answer.GetProperty("files").EnumerateArray())
                paths.Add(f.GetProperty("path").GetString()!);

            if (!found.Answer.TryGetProperty("next", out JsonElement next)) return paths;
            arguments = sort is null
                ? new { limit = 100_000, after = next.GetString() }
                : new { limit = 100_000, sort, after = next.GetString() };
        }
    }

    [Fact]
    public async Task AFindOverThousandsOfFilesComesBackInPagesThatCoverEveryFile()
    {
        using var box = new Sandbox();
        Thousands(box, 3000);

        List<string> paths = await FindByNext(box);

        Assert.Equal(3000, paths.Count);
        Assert.Equal(3000, paths.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task AFindByModifiedTimePagesTheSameWay()
    {
        using var box = new Sandbox();
        Thousands(box, 1500);

        List<string> paths = await FindByNext(box, "mtime");

        Assert.Equal(1500, paths.Count);
        Assert.Equal(1500, paths.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A find sorted by time carries on from a time, a space and
    /// a path. The terminal quotes it, so pasting the sentence into a
    /// shell passes it as one argument, and it carries on.</summary>
    [Fact]
    public async Task TheTerminalQuotesANextThatHoldsASpace()
    {
        using var box = new Sandbox();
        Thousands(box, 1500);

        CliResult first = await Run(box, "find", "--sort", "mtime", "--limit", "100000");
        const string Lead = "pass --after \"";
        int at = first.Stdout.IndexOf(Lead, StringComparison.Ordinal);
        Assert.True(at >= 0, first.Stdout);

        int from = at + Lead.Length;
        string after = first.Stdout[from..first.Stdout.IndexOf('"', from)];
        Assert.Contains(' ', after);

        CliResult next = await Run(box, "find", "--sort", "mtime", "--limit", "100000", "--after", after);
        Assert.Equal(ExitCodes.Ok, next.ExitCode);
    }

    // ---- replace ----

    [Fact]
    public async Task AReplaceDryRunOverThousandsOfFilesKeepsExactTotalsAndPages()
    {
        using var box = new Sandbox();
        for (int i = 0; i < 2500; i++)
            box.Write($"src/a/long/folder/name/file-{i:D5}-{new string('r', 50)}.txt", "old\n");

        var listed = new List<string>();
        object arguments = new { find = "old", with = "new", dry_run = true };

        for (int calls = 0; ; calls++)
        {
            Assert.True(calls < 1000, "next never ran out");

            Called replaced = await Call(box, "replace", arguments);
            Assert.True(Bytes(replaced.Text) <= Budget.Ceiling, $"{Bytes(replaced.Text)} bytes");
            Assert.Equal(2500, replaced.Answer.GetProperty("occurrences").GetInt32());
            Assert.Equal(2500, replaced.Answer.GetProperty("count").GetInt32());

            foreach (JsonElement f in replaced.Answer.GetProperty("files").EnumerateArray())
                listed.Add(f.GetProperty("path").GetString()!);

            if (!replaced.Answer.TryGetProperty("next", out JsonElement next)) break;
            arguments = new { find = "old", with = "new", dry_run = true, after = next.GetString() };
        }

        Assert.Equal(2500, listed.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>after pages the list a dry run prints. A replace that
    /// writes changes every file whatever the list shows, so after there
    /// would read as narrowing what it changes, and it does not.</summary>
    [Fact]
    public async Task AfterIsRefusedOnAReplaceThatWrites()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "old\n");

        Called replaced = await Call(box, "replace", new { find = "old", with = "new", after = "a.txt" });

        Assert.True(replaced.IsError);
        Assert.Equal("old\n", box.ReadText("a.txt"));
    }

    /// <summary>A replace that writes has changed every file by the time
    /// it answers. Its list is cut to fit and counts the rest, as an
    /// edit's report does. It gives no next, because after is refused for
    /// a replace that writes, and a next would point at that refusal.</summary>
    [Fact]
    public async Task AReplaceThatWritesThousandsOfFilesCountsWhatItDoesNotList()
    {
        using var box = new Sandbox();
        string Name(int i) => $"src/a/long/folder/name/file-{i:D5}-{new string('r', 50)}.txt";
        for (int i = 0; i < 2500; i++) box.Write(Name(i), "old\n");

        Called replaced = await Call(box, "replace", new { find = "old", with = "new" });
        JsonElement answer = replaced.Answer;

        Assert.False(replaced.IsError, replaced.Text);
        Assert.True(Bytes(replaced.Text) <= Budget.Ceiling, $"{Bytes(replaced.Text)} bytes");
        Assert.False(answer.TryGetProperty("next", out _));
        Assert.Equal(2500, answer.GetProperty("count").GetInt32());
        Assert.Equal(2500, answer.GetProperty("files").GetArrayLength()
                           + answer.GetProperty("files_omitted").GetInt32());
        Assert.Equal("new\n", box.ReadText(Name(2499)));

        CliResult human = await Run(box, "replace", "new", "old");
        Assert.True(Bytes(human.Stdout) <= Budget.Ceiling);
        Assert.Contains("more files, not listed", human.Stdout);
        Assert.DoesNotContain("--after", human.Stdout);
    }

    // ---- edit ----

    /// <summary>
    /// An edit with thousands of parts has done its work by the time it
    /// answers. Its report is cut to fit like any list, and says how much
    /// it left out. Sending it whole would get it saved to a file; refusing
    /// it would tell the caller an applied edit had failed.
    /// </summary>
    [Fact]
    public async Task AnEditOfThousandsOfPartsReportsInsideTheCeiling()
    {
        using var box = new Sandbox();
        box.Write("many.txt", string.Concat(Enumerable.Range(0, 3000).Select(i => $"old-{i:D5}\n")));

        var edits = Enumerable.Range(0, 3000)
            .Select(i => new { replace = $"old-{i:D5}", with = $"new-{i:D5}" }).ToArray();

        Called edited = await Call(box, "edit", new { path = "many.txt", script = new { edits } });
        JsonElement answer = edited.Answer;

        Assert.False(edited.IsError, edited.Text);
        Assert.True(Bytes(edited.Text) <= Budget.Ceiling, $"{Bytes(edited.Text)} bytes");
        Assert.Equal(3000, answer.GetProperty("plan").GetArrayLength()
                           + answer.GetProperty("plan_omitted").GetInt32());
        Assert.Equal(1, answer.GetProperty("files").GetArrayLength());
        Assert.DoesNotContain("old-", box.ReadText("many.txt"));
    }

    // ---- errors ----

    /// <summary>
    /// A client cuts an error answer over about 11,000 characters to its
    /// head and tail, which leaves JSON that does not parse. A batch that
    /// stops early is an error, so it is held to the error ceiling, and
    /// the operation that failed is always in it.
    /// </summary>
    [Fact]
    public async Task ABatchThatStopsEarlyIsAnErrorUnderTheErrorCeiling()
    {
        using var box = new Sandbox();
        var operations = new List<object>();
        for (int i = 0; i < 600; i++)
            operations.Add(new { op = "mkdir", path = $"made/{i:D4}-{new string('m', 80)}" });
        operations.Add(new { op = "delete", path = "not-there.txt" });

        Called ran = await Call(box, "batch", new { script = new { operations } });

        Assert.True(ran.IsError);
        Assert.True(Bytes(ran.Text) <= Budget.ErrorCeiling, $"{Bytes(ran.Text)} bytes");

        JsonElement answer = ran.Answer;
        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal(600, answer.GetProperty("completed").GetInt32());

        JsonElement shown = answer.GetProperty("operations");
        JsonElement failed = shown[shown.GetArrayLength() - 1];
        Assert.Equal(601, failed.GetProperty("index").GetInt32());
        Assert.Equal("not-found", failed.GetProperty("outcome").GetString());
        Assert.Equal(601, shown.GetArrayLength() + answer.GetProperty("operations_omitted").GetInt32());
    }

    /// <summary>An answer is an error only when the operation did not do
    /// what was asked. A doctor report did what was asked whatever it
    /// found, so it is never an error, though the command line still
    /// exits 1 or 2 for a script.</summary>
    [Fact]
    public async Task DoctorIsNeverAnErrorWhateverItFinds()
    {
        using var box = new Sandbox();

        Called doctor = await Call(box, "doctor", new { });

        Assert.False(doctor.IsError, doctor.Text);
        Assert.True(doctor.Answer.GetProperty("ok").GetBoolean());
        Assert.True(Bytes(doctor.Text) <= Budget.Ceiling);
    }

    [Fact]
    public async Task ARefusalIsAnErrorUnderTheErrorCeiling()
    {
        using var box = new Sandbox();

        Called missing = await Call(box, "read", new { paths = new[] { "not-there.txt" } });

        Assert.True(missing.IsError);
        Assert.False(missing.Answer.GetProperty("ok").GetBoolean());
        Assert.True(Bytes(missing.Text) <= Budget.ErrorCeiling);
    }

    // ---- run ----

    const Permission Runnable = Permissions.Full | Permission.Execute;

    static string[] BothStreams(string file) => OperatingSystem.IsWindows()
        ? ["cmd", "/c", $"type {file} & type {file} 1>&2"]
        : ["sh", "-c", $"cat {file}; cat {file} >&2"];

    /// <summary>
    /// A build log: colour codes, Windows paths and quotes on every line.
    /// Each colour code costs six bytes once escaped and carries nothing a
    /// model can use, so it is removed. The end is kept, inside the
    /// ceiling, and what was left out is counted.
    /// </summary>
    [Fact]
    public async Task RunKeepsTheEndInsideTheCeilingAndDropsColourCodes()
    {
        using var box = new Sandbox(Runnable);
        var log = new StringBuilder();
        for (int i = 0; i < 3000; i++)
            log.Append("\u001b[31merror\u001b[0m C:\\src\\proj\\file.cs(").Append(i).Append("): \"bad\" thing\n");
        box.Write("build.log", log.ToString());
        box.Declare(new TaskDecl("build", BothStreams("build.log"), null));

        Called ran = await Call(box, "run", new { name = "build" });
        JsonElement answer = ran.Answer;

        Assert.True(Bytes(ran.Text) <= Budget.Ceiling, $"{Bytes(ran.Text)} bytes");
        Assert.True(answer.GetProperty("truncated").GetBoolean());

        string stdout = answer.GetProperty("stdout").GetString()!;
        Assert.DoesNotContain('\u001b', stdout);
        Assert.EndsWith("C:\\src\\proj\\file.cs(2999): \"bad\" thing\n", stdout.Replace("\r\n", "\n"));
        Assert.True(answer.GetProperty("stdout_omitted").GetInt64() > 0);

        CliResult human = await Run(box, "run", "build");
        Assert.True(Bytes(human.Stdout) <= Budget.Ceiling);
    }

    // ---- images ----

    static byte[] Png(int padding)
    {
        byte[] head =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x07,
            0x00, 0x00, 0x00, 0x03,
            0x08, 0x06, 0x00, 0x00, 0x00,
            0x1F, 0x15, 0xC4, 0x89,
        ];
        return [.. head, .. new byte[padding]];
    }

    static int Images(Called called) =>
        called.Content.EnumerateArray().Count(part => part.GetProperty("type").GetString() == "image");

    /// <summary>A client does not say how it counts an image against its
    /// limit, so one call carries one image. The next path that is an
    /// image says where to read it.</summary>
    [Fact]
    public async Task OneCallCarriesOneImage()
    {
        using var box = new Sandbox();
        box.WriteRaw("one.png", Png(0));
        box.WriteRaw("two.png", Png(0));

        Called read = await Call(box, "read", new { paths = new[] { "one.png", "two.png" } });

        Assert.Equal(1, Images(read));
        JsonElement second = read.Answer.GetProperty("files")[1];
        Assert.Contains("one image", second.GetProperty("withheld").GetString());
    }

    /// <summary>Five megabytes is the most one image may be when it is
    /// sent to the model. A bigger one is described, not sent.</summary>
    [Fact]
    public async Task AnImageOverFiveMegabytesIsDescribedNotSent()
    {
        using var box = new Sandbox();
        box.WriteRaw("huge.png", Png(6 * 1024 * 1024));

        Called read = await Call(box, "read", new { paths = new[] { "huge.png" } });

        Assert.Equal(0, Images(read));
        Assert.False(read.IsError);
        JsonElement file = read.Answer.GetProperty("files")[0];
        Assert.Contains("5 MB", file.GetProperty("withheld").GetString());
        Assert.True(Bytes(read.Text) <= Budget.Ceiling);

        CliResult json = await Run(box, "read", "huge.png", "--json");
        Assert.True(Bytes(json.Stdout) <= Budget.Ceiling);
    }

    // ---- lists made from configuration ----

    [Fact]
    public async Task ThousandsOfDeclaredTasksAreListedInsideTheCeiling()
    {
        using var box = new Sandbox();
        box.Declare([.. Enumerable.Range(0, 1000).Select(i =>
            new TaskDecl($"task-{i:D4}", ["node", $"scripts/{new string('s', 60)}-{i}.js", "--flag"], null))]);

        Called listed = await Call(box, "tasks", new { });

        Assert.True(Bytes(listed.Text) <= Budget.Ceiling, $"{Bytes(listed.Text)} bytes");
        Assert.Equal(1000, listed.Answer.GetProperty("count").GetInt32());
        Assert.Equal(1000, listed.Answer.GetProperty("tasks").GetArrayLength()
                           + listed.Answer.GetProperty("tasks_omitted").GetInt32());

        CliResult human = await Run(box, "tasks");
        Assert.True(Bytes(human.Stdout) <= Budget.Ceiling);
    }

    [UnixFact]
    public async Task ACopyThatSkipsThousandsOfLinksListsThemInsideTheCeiling()
    {
        using var box = new Sandbox();
        box.Write("source/real.txt", "x");
        for (int i = 0; i < 2000; i++)
            File.CreateSymbolicLink(box.Full($"source/link-{i:D4}-{new string('l', 60)}"), box.Full("source/real.txt"));

        Called copied = await Call(box, "copy", new { from = "source", to = "copied", recursive = true });
        JsonElement answer = copied.Answer;

        Assert.True(Bytes(copied.Text) <= Budget.Ceiling, $"{Bytes(copied.Text)} bytes");
        Assert.Equal(2000, answer.GetProperty("links_skipped").GetArrayLength()
                           + answer.GetProperty("links_skipped_omitted").GetInt32());
    }

    // ---- the client and the last check ----

    /// <summary>The ceiling is declared to the client as well, so a
    /// client that honours the setting uses the character count rather
    /// than its own token estimate for text.</summary>
    [Fact]
    public async Task EveryToolDeclaresTheCeilingToTheClient()
    {
        using var box = new Sandbox();
        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);

        string? reply = await server.AnswerAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        foreach (JsonElement tool in Parse(reply!).GetProperty("result").GetProperty("tools").EnumerateArray())
            Assert.Equal(Budget.Ceiling, tool.GetProperty("_meta")
                .GetProperty("anthropic/maxResultSizeChars").GetInt32());
    }

    /// <summary>
    /// The last line of defence. Every answer passes one check on its way
    /// out, and an answer over the ceiling is never sent: it is replaced
    /// by a refusal that names the size, because a defect that is said is
    /// one somebody can fix.
    /// </summary>
    [Fact]
    public void AnAnswerOverTheCeilingIsNeverSent()
    {
        string big = "{\"ok\":true,\"text\":\"" + new string('x', Budget.Ceiling) + "\"}\n";

        CliResult held = Command.Held(new CliResult(ExitCodes.Ok, big, string.Empty), json: true);

        Assert.NotEqual(ExitCodes.Ok, held.ExitCode);
        Assert.True(Bytes(held.Stdout) <= Budget.ErrorCeiling);
        Assert.Contains("ceiling", Parse(held.Stdout).GetProperty("message").GetString());
    }

    /// <summary>A refusal over the error ceiling keeps its outcome and
    /// path, and its message is shortened, with the cut said.</summary>
    [Fact]
    public void ARefusalOverTheErrorCeilingIsShortenedNotDropped()
    {
        string message = new string('m', Budget.ErrorCeiling * 2);
        string big = JsonSerializer.Serialize(new { ok = false, outcome = "refused", message, path = "a.txt" }) + "\n";

        CliResult held = Command.Held(new CliResult(ExitCodes.Refused, big, string.Empty) { Failed = true }, json: true);
        JsonElement answer = Parse(held.Stdout);

        Assert.True(Bytes(held.Stdout) <= Budget.ErrorCeiling);
        Assert.Equal("refused", answer.GetProperty("outcome").GetString());
        Assert.Equal("a.txt", answer.GetProperty("path").GetString());
        Assert.Contains("left out", answer.GetProperty("message").GetString());
    }
}
