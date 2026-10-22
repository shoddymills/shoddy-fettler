using System.IO.Compression;
using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// The range a read is given, checked against the file before it is
/// clamped to it. A range the file cannot answer is refused, never
/// answered empty: an empty answer that says ok is the shape of a
/// complete read, and a caller acts on it as one.
/// </summary>
public sealed class RangeTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    static JsonElement Files(CliResult json) =>
        JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files");

    static JsonElement FirstFile(CliResult json) => Files(json)[0];

    static string Lines(int n) => string.Concat(Enumerable.Range(1, n).Select(i => $"line {i}\n"));

    const string Three = "one\ntwo\nthree\n";

    // ---- from ----

    [Fact]
    public async Task AFromOnTheLastLineIsServedAndOnePastItIsRefused()
    {
        using var box = new Sandbox();
        box.Write("three.txt", Three);

        CliResult last = await Run(box, "read", "three.txt", "--from", "3", "--json");
        Assert.Equal(ExitCodes.Ok, last.ExitCode);
        JsonElement file = FirstFile(last);
        Assert.Equal("three\n", file.GetProperty("text").GetString());
        Assert.Equal(3, file.GetProperty("from").GetInt32());
        Assert.Equal(3, file.GetProperty("to").GetInt32());
        Assert.False(file.GetProperty("truncated").GetBoolean());

        CliResult past = await Run(box, "read", "three.txt", "--from", "4");
        Assert.Equal(ExitCodes.Invalid, past.ExitCode);
        Assert.Contains("from 4 is past the end", past.Stdout + past.Stderr);
        Assert.Contains("3 line", past.Stdout + past.Stderr);
    }

    [Fact]
    public async Task AFromOfZeroIsRefusedAsColumnAndTailAre()
    {
        using var box = new Sandbox();
        box.Write("three.txt", Three);

        CliResult zero = await Run(box, "read", "three.txt", "--from", "0");

        Assert.Equal(ExitCodes.Invalid, zero.ExitCode);
        Assert.Contains("from counts from 1", zero.Stdout + zero.Stderr);
    }

    /// <summary>Line 1 of an empty file is a place: its start. The
    /// refusal is for a line the file does not have, and an empty file
    /// read from its start is served as what it is, no lines.</summary>
    [Fact]
    public async Task AnEmptyFileReadsAsEmptyAndIsNotRefused()
    {
        using var box = new Sandbox();
        box.Write("empty.txt", "");

        foreach (string[] argv in new[]
                 {
                     new[] { "read", "empty.txt", "--json" },
                     new[] { "read", "empty.txt", "--from", "1", "--json" },
                 })
        {
            CliResult read = await Run(box, argv);
            Assert.Equal(ExitCodes.Ok, read.ExitCode);
            JsonElement file = FirstFile(read);
            Assert.Equal("", file.GetProperty("text").GetString());
            Assert.Equal(0, file.GetProperty("lines").GetInt32());
            Assert.False(file.GetProperty("truncated").GetBoolean());
        }

        CliResult past = await Run(box, "read", "empty.txt", "--from", "2");
        Assert.Equal(ExitCodes.Invalid, past.ExitCode);
    }

    // ---- to ----

    /// <summary>The same fault in its second shape. A to below from was
    /// pulled up to one below from, the range served nothing, and the
    /// answer said truncated with a next pointing back at from, so a
    /// caller following next with the same to never moved.</summary>
    [Fact]
    public async Task AToBeforeFromIsRefusedRatherThanAnsweredEmpty()
    {
        using var box = new Sandbox();
        box.Write("ten.txt", Lines(10));

        CliResult backwards = await Run(box, "read", "ten.txt", "--from", "5", "--to", "4");
        Assert.Equal(ExitCodes.Invalid, backwards.ExitCode);
        Assert.Contains("to 4 is before from 5", backwards.Stdout + backwards.Stderr);

        CliResult zero = await Run(box, "read", "ten.txt", "--to", "0");
        Assert.Equal(ExitCodes.Invalid, zero.ExitCode);
        Assert.Contains("to 0 is before from 1", zero.Stdout + zero.Stderr);
    }

    [Fact]
    public async Task AToPastTheEndReadsToTheEndAndSaysWhereItStopped()
    {
        using var box = new Sandbox();
        box.Write("three.txt", Three);

        CliResult read = await Run(box, "read", "three.txt", "--from", "2", "--to", "999", "--json");

        Assert.Equal(ExitCodes.Ok, read.ExitCode);
        JsonElement file = FirstFile(read);
        Assert.Equal("two\nthree\n", file.GetProperty("text").GetString());
        Assert.Equal(3, file.GetProperty("to").GetInt32());
        Assert.False(file.GetProperty("truncated").GetBoolean());
    }

    // ---- every kind, one rule ----

    /// <summary>An archive is read as its manifest, one member a line,
    /// and the rule is the same for it as for any text.</summary>
    [Fact]
    public async Task AnArchiveManifestIsHeldToTheSameRule()
    {
        using var box = new Sandbox();
        box.WriteRaw("dist.zip", Zip(("a.txt", "a"), ("b.txt", "b")));

        CliResult whole = await Run(box, "read", "dist.zip", "--json");
        Assert.Equal(ExitCodes.Ok, whole.ExitCode);
        int lines = FirstFile(whole).GetProperty("lines").GetInt32();
        Assert.True(lines > 0);

        CliResult past = await Run(box, "read", "dist.zip", "--from", (lines + 1).ToString());
        Assert.Equal(ExitCodes.Invalid, past.ExitCode);
        Assert.Contains("past the end", past.Stdout + past.Stderr);
    }

    static byte[] Zip(params (string Name, string Body)[] members)
    {
        using var into = new MemoryStream();
        using (var zip = new ZipArchive(into, ZipArchiveMode.Create, leaveOpen: true))
            foreach ((string name, string body) in members)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);
                using StreamWriter writer = new(entry.Open());
                writer.Write(body);
            }

        return into.ToArray();
    }

    // ---- a path with its own range ----

    /// <summary>
    /// PATH:FROM or PATH:FROM-TO is the form a search hit and a search
    /// next already have, so a hit goes straight to read, and two files
    /// in one call can start at two different lines, which one from for
    /// the call could not say.
    /// </summary>
    [Fact]
    public async Task APathMayCarryItsOwnRange()
    {
        using var box = new Sandbox();
        box.Write("ten.txt", Lines(10));
        box.Write("three.txt", Three);

        CliResult read = await Run(box, "read", "ten.txt:4-5", "three.txt", "--json");

        Assert.Equal(ExitCodes.Ok, read.ExitCode);
        JsonElement files = Files(read);
        Assert.Equal("ten.txt", files[0].GetProperty("path").GetString());
        Assert.Equal("line 4\nline 5\n", files[0].GetProperty("text").GetString());
        Assert.Equal(4, files[0].GetProperty("from").GetInt32());
        Assert.Equal(5, files[0].GetProperty("to").GetInt32());
        Assert.Equal("three.txt", files[1].GetProperty("path").GetString());
        Assert.Equal(Three, files[1].GetProperty("text").GetString());

        CliResult open = await Run(box, "read", "ten.txt:9", "--json");
        Assert.Equal("line 9\nline 10\n", FirstFile(open).GetProperty("text").GetString());
    }

    [Fact]
    public async Task ASearchHitGoesStraightToRead()
    {
        using var box = new Sandbox();
        box.Write("ten.txt", Lines(10));

        CliResult found = await Run(box, "search", "line 7", "--json");
        JsonElement hit = JsonDocument.Parse(found.Stdout).RootElement.GetProperty("hits")[0];
        string position = hit.GetProperty("path").GetString() + ":" + hit.GetProperty("line").GetInt32();

        CliResult read = await Run(box, "read", position, "--json");

        Assert.Equal(ExitCodes.Ok, read.ExitCode);
        Assert.Equal(7, FirstFile(read).GetProperty("from").GetInt32());
        Assert.StartsWith("line 7\n", FirstFile(read).GetProperty("text").GetString());
    }

    /// <summary>Two answers to one question. A range on the path and a
    /// range for the call are refused together, as tail with from is.</summary>
    [Fact]
    public async Task APathRangeAndACallRangeTogetherAreRefusedRatherThanRanked()
    {
        using var box = new Sandbox();
        box.Write("ten.txt", Lines(10));

        foreach (string[] extra in new[]
                 {
                     new[] { "--from", "2" },
                     new[] { "--to", "8" },
                     new[] { "--tail", "2" },
                     new[] { "--column", "2" },
                 })
        {
            CliResult refused = await Run(box, ["read", "ten.txt:4-5", .. extra]);
            Assert.Equal(ExitCodes.Invalid, refused.ExitCode);
            Assert.Contains("its own range", refused.Stdout + refused.Stderr);
        }
    }

    [Fact]
    public async Task APathRangeIsCheckedLikeAnyOther()
    {
        using var box = new Sandbox();
        box.Write("three.txt", Three);

        Assert.Equal(ExitCodes.Invalid, (await Run(box, "read", "three.txt:0")).ExitCode);
        Assert.Equal(ExitCodes.Invalid, (await Run(box, "read", "three.txt:5-3")).ExitCode);
        Assert.Equal(ExitCodes.Invalid, (await Run(box, "read", "three.txt:4")).ExitCode);
    }

    /// <summary>A trailing colon and digits is always a range. A file
    /// whose name ends that way is reached by giving it a range of its
    /// own, so nothing is unreadable.</summary>
    [UnixFact]
    public async Task AFileNamedWithAColonAndDigitsIsReadByGivingItARange()
    {
        using var box = new Sandbox();
        box.Write("notes:5", Three);

        CliResult asRange = await Run(box, "read", "notes:5");
        Assert.Equal(ExitCodes.NotFound, asRange.ExitCode);

        CliResult asPath = await Run(box, "read", "notes:5:1", "--json");
        Assert.Equal(ExitCodes.Ok, asPath.ExitCode);
        Assert.Equal(Three, FirstFile(asPath).GetProperty("text").GetString());
    }

    [Fact]
    public void TheRangeOnAPathIsReadOnlyAtItsEnd()
    {
        Expect("work:dir/a.cs:634-646", "work:dir/a.cs", 634, 646);
        Expect("a.cs:7", "a.cs", 7, null);
        Expect("a.cs:7-", "a.cs:7-", null, null);
        Expect("a.cs:x", "a.cs:x", null, null);
        Expect("a.cs", "a.cs", null, null);
        Expect(":5", ":5", null, null);
        Expect(@"C:\dir\a.cs", @"C:\dir\a.cs", null, null);
    }

    static void Expect(string given, string path, int? from, int? to)
    {
        Result<RangedPath> split = ReadRange.Split(given);
        Assert.True(split.IsOk, split.Failure?.Message);
        Assert.Equal(path, split.Value.Path);
        Assert.Equal(from, split.Value.From);
        Assert.Equal(to, split.Value.To);
    }

    [Fact]
    public void ADigitRunTooLongForALineNumberIsRefusedNotTakenAsAPath()
    {
        Result<RangedPath> split = ReadRange.Split("a.cs:99999999999");
        Assert.False(split.IsOk);
        Assert.Equal(Outcome.Invalid, split.Failure!.Outcome);
    }
}
