using System.Text;
using System.Text.Json;
using Fettler.Cli;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R3 and R4 of the tools-first enhancement: a glob form this grammar
/// does not have is refused, a glob that matched nothing says so, and
/// <c>read</c> can number its lines.
/// </summary>
public sealed class GlobAndNumberTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    static JsonElement Answer(CliResult result) => JsonDocument.Parse(result.Stdout).RootElement.Clone();

    // ---- R3.1 and R3.5 ----

    public static TheoryData<string, string, string> Unsupported()
    {
        var data = new TheoryData<string, string, string>();
        foreach (string verb in new[] { "find", "search", "replace" })
        {
            data.Add(verb, "{a,b}/**", "'{'");
            data.Add(verb, "a}/*.cs", "'}'");
            data.Add(verb, "[ab].txt", "'['");
            data.Add(verb, "a]/*.cs", "']'");
            data.Add(verb, "!*.cs", "'!'");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Unsupported))]
    public async Task AGlobFormThisGrammarDoesNotHaveIsRefusedAndNamed(string verb, string glob, string named)
    {
        using var box = new Sandbox();
        box.Write("a.cs", "x\n");

        CliResult said = verb switch
        {
            "find" => await Run(box, "find", glob),
            "search" => await Run(box, "search", "x", "--glob", glob),
            _ => await Run(box, "replace", "x", "y", "--glob", glob, "--dry-run"),
        };

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.Contains(named, said.Stderr);
        Assert.Contains("** across segments", said.Stderr);
        Assert.Equal("x\n", box.ReadText("a.cs"));
    }

    [Theory]
    [InlineData("find")]
    [InlineData("search")]
    public async Task AnExcludeWithAnUnsupportedFormIsRefusedRatherThanDropped(string verb)
    {
        using var box = new Sandbox();
        box.Write("keep/a.cs", "x\n");
        box.Write("skip/b.cs", "x\n");

        CliResult said = verb == "find"
            ? await Run(box, "find", "**/*.cs", "--exclude", "[s]kip/**")
            : await Run(box, "search", "x", "--exclude", "[s]kip/**");

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.Contains("'['", said.Stderr);
    }

    /// <summary>R3.2: a file with one of those characters in its name is
    /// still reachable, by path, and by find with ? in its place.</summary>
    [Fact]
    public async Task AFileWithABraceInItsNameIsStillReachable()
    {
        using var box = new Sandbox();
        box.Write("x{1}.txt", "braced\n");

        CliResult found = await Run(box, "find", "x?1?.txt");
        Assert.Equal(ExitCodes.Ok, found.ExitCode);
        Assert.Contains("x{1}.txt", found.Stdout);

        CliResult read = await Run(box, "read", "x{1}.txt");
        Assert.Equal(ExitCodes.Ok, read.ExitCode);
        Assert.Contains("braced", read.Stdout);
    }

    // ---- R3.3 and R3.4 ----

    [Fact]
    public async Task ASearchWhoseGlobMatchedNothingSaysNothingWasSearched()
    {
        using var box = new Sandbox();
        box.Write("a.cs", "x\n");

        JsonElement answer = Answer(await Run(box, "search", "x", "--glob", "nothing-here/**", "--json"));
        Assert.Equal(0, answer.GetProperty("files_matched").GetInt32());
        Assert.Equal(0, answer.GetProperty("files_searched").GetInt32());

        CliResult human = await Run(box, "search", "x", "--glob", "nothing-here/**");
        Assert.EndsWith("0 files matched the glob; nothing was searched", human.Stdout.TrimEnd());
    }

    [Fact]
    public async Task ASearchWhoseGlobMatchedOnlyBinariesSaysNoneCouldBeSearched()
    {
        using var box = new Sandbox();
        box.WriteRaw("blobs/a.dat", [0x78, 0x00, 0x01, 0x02]);

        JsonElement answer = Answer(await Run(box, "search", "x", "--glob", "blobs/*.dat", "--json"));
        Assert.Equal(1, answer.GetProperty("files_matched").GetInt32());
        Assert.Equal(0, answer.GetProperty("files_searched").GetInt32());

        CliResult human = await Run(box, "search", "x", "--glob", "blobs/*.dat");
        Assert.EndsWith("1 file matched the glob; none could be searched as text", human.Stdout.TrimEnd());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReplaceWhoseGlobMatchedNothingSaysSo(bool dryRun)
    {
        using var box = new Sandbox();
        box.Write("a.cs", "x\n");

        CliResult human = dryRun
            ? await Run(box, "replace", "x", "y", "--glob", "nothing-here/**", "--dry-run")
            : await Run(box, "replace", "x", "y", "--glob", "nothing-here/**");

        Assert.Equal(ExitCodes.Ok, human.ExitCode);
        Assert.Contains("0 files matched the glob", human.Stdout);
        Assert.Equal("x\n", box.ReadText("a.cs"));
    }

    // ---- R4 ----

    [Fact]
    public async Task ANumberedReadIsTheTerminalFormWithTheFilesOwnHash()
    {
        using var box = new Sandbox();
        box.Write("five.txt", "one\ntwo\nthree\nfour\nfive\n");

        JsonElement plain = Answer(await Run(box, "read", "five.txt", "--json")).GetProperty("files")[0];
        JsonElement numbered = Answer(await Run(box, "read", "five.txt", "--numbered", "--json")).GetProperty("files")[0];

        Assert.Equal("    1 | one\n    2 | two\n    3 | three\n    4 | four\n    5 | five\n",
            numbered.GetProperty("text").GetString());

        string hash = numbered.GetProperty("hash").GetString()!;
        Assert.Equal(plain.GetProperty("hash").GetString(), hash);

        // One spelling of a numbered line: the terminal answer is the same
        // with the flag and without it.
        Assert.Equal((await Run(box, "read", "five.txt")).Stdout,
                     (await Run(box, "read", "five.txt", "--numbered")).Stdout);

        CliResult edited = await Run(box, "edit", "five.txt", "--replace", "three", "--with", "THREE", "--expect", hash);
        Assert.Equal(ExitCodes.Ok, edited.ExitCode);
    }

    /// <summary>R4.3: the prefix is part of what is served, so a numbered
    /// read of a file over the cap stops earlier, and says which cap.</summary>
    [Fact]
    public async Task TheNumberedTextCountsTowardTheCharacterCap()
    {
        using var box = new Sandbox();
        var text = new StringBuilder();
        for (int i = 0; i < 3000; i++) text.Append(new string('x', 19)).Append('\n');
        box.Write("big.txt", text.ToString());

        JsonElement plain = Answer(await Run(box, "read", "big.txt", "--to", "3000", "--json")).GetProperty("files")[0];
        JsonElement numbered = Answer(await Run(box, "read", "big.txt", "--to", "3000", "--numbered", "--json")).GetProperty("files")[0];

        Assert.True(numbered.GetProperty("budgeted").GetBoolean());
        Assert.True(numbered.GetProperty("to").GetInt32() < plain.GetProperty("to").GetInt32());
        Assert.True(numbered.GetProperty("text").GetString()!.Length <= 40_000);

        CliResult human = await Run(box, "read", "big.txt", "--to", "3000", "--numbered");
        Assert.Contains("-character limit for one call", human.Stdout);
    }
}
