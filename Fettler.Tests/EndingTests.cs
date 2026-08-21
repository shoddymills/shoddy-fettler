using System.Text;
using System.Text.Json;
using Fettler.Cli;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// Line endings across an edit: what an edit brings IN is made to match
/// the file, what is already there is left exactly as it was.
/// </summary>
public sealed class EndingTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    static void Put(Sandbox box, string name, string exact) =>
        box.WriteRaw(name, new UTF8Encoding(false).GetBytes(exact));

    /// <summary>
    /// The fault this was written for. A PowerShell here-string is CRLF
    /// whatever the file is, so replacement text composed on Windows
    /// arrives CRLF and used to be spliced in verbatim - leaving a file
    /// disagreeing with itself by exactly one line, which nothing notices
    /// until a version control system does.
    /// </summary>
    [Fact]
    public async Task ReplacementTextIsRewrittenIntoTheFilesOwnEndings()
    {
        using var box = new Sandbox();
        Put(box, "a.cs", "one\ntwo\nthree\n");

        CliResult said = await Run(box, "edit", "a.cs", "--replace", "two", "--with", "second\r\nline");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Equal("one\nsecond\nline\nthree\n", box.ReadText("a.cs"));
        Assert.DoesNotContain((byte)13, box.ReadRaw("a.cs"));
    }

    [Fact]
    public async Task InsertedTextIsRewrittenIntoTheFilesOwnEndings()
    {
        using var box = new Sandbox();
        Put(box, "a.cs", "one\r\ntwo\r\n");

        CliResult said = await Run(box, "edit", "a.cs", "--insert-after", "1", "--text", "new\nlines");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Equal("one\r\nnew\r\nlines\r\ntwo\r\n", box.ReadText("a.cs"));
    }

    [Fact]
    public async Task ReplaceAllRewritesEveryOccurrenceTheSameWay()
    {
        using var box = new Sandbox();
        Put(box, "a.cs", "x\nx\n");

        CliResult said = await Run(box, "edit", "a.cs", "--replace", "x", "--with", "p\r\nq", "--all");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.DoesNotContain((byte)13, box.ReadRaw("a.cs"));
    }

    /// <summary>
    /// The other half of the rule, and the one that keeps R10.2 true: a
    /// file that already disagrees with itself is not tidied up by being
    /// edited. Only what the edit brings in is made to match.
    /// </summary>
    [Fact]
    public async Task ExistingLinesKeepWhateverEndingsTheyAlreadyHad()
    {
        using var box = new Sandbox();
        Put(box, "a.cs", "one\r\ntwo\nthree\n");

        CliResult said = await Run(box, "edit", "a.cs", "--replace", "three", "--with", "third");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Equal("one\r\ntwo\nthird\n", box.ReadText("a.cs"));
    }

    // ---- reporting the mixture rather than hiding it ----

    /// <summary>
    /// Reporting only the dominant ending hid a single stray CRLF in an
    /// otherwise LF file for two commits. The dominant ending is still
    /// what an edit inserts with; the mixture is now said out loud.
    /// </summary>
    [Fact]
    public async Task ReadSaysWhenAFileDisagreesWithItself()
    {
        using var box = new Sandbox();
        Put(box, "mixed.cs", "one\r\ntwo\nthree\n");

        CliResult json = await Run(box, "read", "mixed.cs", "--json");
        JsonElement slice = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.Equal("lf", slice.GetProperty("line_ending").GetString());
        Assert.True(slice.GetProperty("line_ending_mixed").GetBoolean(),
            "a file with both CRLF and LF in it is mixed");

        CliResult human = await Run(box, "read", "mixed.cs");
        Assert.Contains("MIXED", human.Stdout);
    }

    [Theory]
    [InlineData("one\ntwo\n")]
    [InlineData("one\r\ntwo\r\n")]
    [InlineData("just the one line, no ending at all")]
    public async Task AFileThatAgreesWithItselfIsNotCalledMixed(string content)
    {
        using var box = new Sandbox();
        Put(box, "plain.cs", content);

        CliResult json = await Run(box, "read", "plain.cs", "--json");
        JsonElement slice = JsonDocument.Parse(json.Stdout).RootElement.GetProperty("files")[0];

        Assert.False(slice.GetProperty("line_ending_mixed").GetBoolean());
        Assert.DoesNotContain("MIXED", (await Run(box, "read", "plain.cs")).Stdout);
    }
}
