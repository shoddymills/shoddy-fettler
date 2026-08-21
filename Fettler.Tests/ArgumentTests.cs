using System.Text.RegularExpressions;
using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R3.14 and R4.20: what the front end does with an argument it does not
/// recognise, and how a pattern gets past a shell intact.
/// </summary>
public sealed class ArgumentTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    static Task<CliResult> RunWithInput(Sandbox box, string stdin, string[] argv) =>
        Command.RunAsync(argv, new StringReader(stdin), box.Bench);

    [Fact]
    public async Task AnUnknownFlagIsRefusedRatherThanIgnored()
    {
        using var box = new Sandbox();
        box.Write("a.cs", "hello\n");

        CliResult said = await Run(box, "search", "hello", "--totally-made-up");

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.Contains("--totally-made-up", said.Stdout + said.Stderr);
    }

    /// <summary>
    /// The reason R3.14 exists. A misspelt flag that takes a value eats
    /// the argument after it, so the glob vanishes and the search runs
    /// against the whole tree - and answers, confidently, about something
    /// nobody asked. That is a wrong answer, not a failure, which is why
    /// ignoring an unknown flag is not a small discourtesy.
    /// </summary>
    [Fact]
    public async Task AMisspeltFlagCannotSwallowTheArgumentAfterIt()
    {
        using var box = new Sandbox();
        box.Write("wanted/a.cs", "needle\n");
        box.Write("elsewhere/b.txt", "needle\n");

        CliResult said = await Run(box, "search", "needle", "--gob", "wanted/*.cs");

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.DoesNotContain("elsewhere", said.Stdout);
    }

    /// <summary>
    /// The same rule, one character short of being caught by it. A
    /// one-dash argument fell straight through to the positional list, so
    /// <c>search PATTERN -i</c> - the spelling every grep in the world
    /// uses for case-insensitive - was taken as a SECOND PATTERN. The
    /// search then answered the union of two questions: exit 0, a
    /// confident list, and files in it whose only match was the literal
    /// "-i". Found by typing it, which is the only way this one gets
    /// found.
    /// </summary>
    [Fact]
    public async Task AnUnknownShortFlagIsRefusedRatherThanReadAsAPattern()
    {
        using var box = new Sandbox();
        box.Write("wanted/a.cs", "needle\n");
        box.Write("elsewhere/b.txt", "cancel-in-progress\n");

        CliResult said = await Run(box, "search", "needle", "-i");

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.Contains("no such flag: -i", said.Stdout + said.Stderr);
        Assert.DoesNotContain("elsewhere", said.Stdout);
    }

    /// <summary>The one short flag there is still works, so the rule above
    /// is a rule and not a ban.</summary>
    [Fact]
    public async Task TheOneShortFlagThereIsStillCarriesItsPattern()
    {
        using var box = new Sandbox();
        box.Write("a.cs", "alpha\n");
        box.Write("b.cs", "beta\n");

        CliResult said = await Run(box, "search", "alpha", "-e", "beta", "--files-only");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Contains("a.cs", said.Stdout);
        Assert.Contains("b.cs", said.Stdout);
    }

    /// <summary><c>-e</c> with nothing after it is a missing value, not an
    /// unknown flag. The name was right; saying "no such flag" would send
    /// the caller looking for a typo that is not there.</summary>
    [Fact]
    public async Task TheShortFlagWithNothingAfterItSaysItsValueIsMissing()
    {
        using var box = new Sandbox();
        box.Write("a.cs", "alpha\n");

        CliResult said = await Run(box, "search", "alpha", "-e");

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.Contains("-e needs a value", said.Stdout + said.Stderr);
        Assert.DoesNotContain("no such flag", said.Stdout + said.Stderr);
    }

    // ---- read --tail: the end of a file, without its length ----

    /// <summary>
    /// The last n lines, counted from the end, so a caller reaching for
    /// the end of a log needs neither the line count nor any arithmetic.
    /// Without this the end of a file costs two calls, and the tool that
    /// cannot answer "what did it just say" sends its caller to a shell -
    /// which is the thing this one exists to make unnecessary.
    /// </summary>
    [Fact]
    public async Task TailIsTheLastLinesCountedFromTheEnd()
    {
        using var box = new Sandbox();
        box.Write("build.log", string.Concat(Enumerable.Range(1, 500).Select(i => $"line {i}\n")));

        CliResult said = await Run(box, "read", "build.log", "--tail", "3");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Contains("line 498", said.Stdout);
        Assert.Contains("line 500", said.Stdout);
        Assert.DoesNotContain("line 497", said.Stdout);
        Assert.DoesNotContain("line 1\n", said.Stdout);
    }

    /// <summary>A tail longer than the file is the whole file, not a
    /// refusal and not a range starting below line one.</summary>
    [Fact]
    public async Task ATailLongerThanTheFileIsTheWholeFile()
    {
        using var box = new Sandbox();
        box.Write("short.log", "one\ntwo\n");

        CliResult said = await Run(box, "read", "short.log", "--tail", "99");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Contains("one", said.Stdout);
        Assert.Contains("two", said.Stdout);
    }

    /// <summary>
    /// The end of the file and a named range are two different questions.
    /// Answering one of them by precedence would be R3.14's failure in
    /// another costume: not an error, a confident wrong answer.
    /// </summary>
    [Fact]
    public async Task TailAndARangeTogetherAreRefusedRatherThanRanked()
    {
        using var box = new Sandbox();
        box.Write("build.log", "one\ntwo\nthree\n");

        CliResult said = await Run(box, "read", "build.log", "--tail", "2", "--from", "1");

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.Contains("one or the other", said.Stdout + said.Stderr);
    }

    [Fact]
    public async Task ATailOfZeroIsRefused()
    {
        using var box = new Sandbox();
        box.Write("build.log", "one\ntwo\n");

        CliResult said = await Run(box, "read", "build.log", "--tail", "0");

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.Contains("at least 1", said.Stdout + said.Stderr);
    }

    /// <summary>
    /// The way out that the refusal depends on. A pattern that genuinely
    /// begins with a hyphen goes through after a bare <c>--</c>, so the
    /// new rule closes a wrong answer without closing a real question.
    /// </summary>
    [Fact]
    public async Task APatternThatReallyBeginsWithAHyphenGoesThroughADoubleDash()
    {
        using var box = new Sandbox();
        box.Write("flags.txt", "cancel-in-progress\n");

        CliResult said = await Run(box, "search", "--files-only", "--", "-in-pro");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Contains("flags.txt", said.Stdout);
    }

    [Fact]
    public async Task EveryUnknownFlagIsNamed_NotJustTheFirst()
    {
        using var box = new Sandbox();

        CliResult said = await Run(box, "find", "*", "--one-typo", "x", "--another-typo", "y");

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.Contains("--one-typo", said.Stdout + said.Stderr);
        Assert.Contains("--another-typo", said.Stdout + said.Stderr);
    }

    /// <summary>
    /// The stated limit of the above, found by writing the test the
    /// optimistic way first. Two unknown flags written back to back
    /// surface ONE at a time, because an unknown flag is not a switch and
    /// so takes the next argument as its value - even when that argument
    /// is itself a flag. The refusal is still a refusal and nothing runs;
    /// it just takes two goes to learn both names.
    /// </summary>
    [Fact]
    public async Task TwoUnknownFlagsWrittenBackToBackSurfaceOneAtATime()
    {
        using var box = new Sandbox();

        CliResult said = await Run(box, "find", "*", "--one-typo", "--another-typo");

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.Contains("--one-typo", said.Stdout + said.Stderr);
    }

    /// <summary>The help is the only place a caller learns a flag's name,
    /// so a name printed there and refused by the parser is a trap. This
    /// holds the two together.</summary>
    [Fact]
    public async Task EveryFlagTheHelpNamesIsOneTheParserAccepts()
    {
        using var box = new Sandbox();
        string help = (await Run(box, "help")).Stdout;

        var strangers = new List<string>();
        var seen = new HashSet<string>();
        foreach (Match m in Regex.Matches(help, "--([a-z][a-z0-9-]*)"))
        {
            string flag = m.Groups[1].Value;
            if (seen.Add(flag) && !Arguments.IsKnown(flag)) strangers.Add(flag);
        }

        Assert.NotEmpty(seen);
        Assert.True(strangers.Count == 0,
            "the help names flags the parser would refuse: " + string.Join(", ", strangers));
    }

    [Fact]
    public async Task HelpAndVersionStillWorkAndAreNotStrangers()
    {
        using var box = new Sandbox();

        Assert.Equal(ExitCodes.Ok, (await Run(box, "--help")).ExitCode);
        Assert.Equal(ExitCodes.Ok, (await Run(box, "--version")).ExitCode);
    }

    // ---- R4.20: a pattern that never touches a command line ----

    /// <summary>
    /// A pattern is the argument a shell is likeliest to spoil, and the
    /// damage is silent: Windows argument marshalling eats an unbalanced
    /// quote, so the pattern arrives as something else and matches
    /// nothing, or worse, matches.
    /// </summary>
    [Fact]
    public async Task SearchTakesItsPatternFromAFileWithNoEscapingAnywhere()
    {
        using var box = new Sandbox();
        box.Write("a.cs", "he said \"hello\" loudly\n");
        box.Write("b.cs", "he said nothing\n");

        string pattern = box.Write("p.txt", "said \\\"hello\\\"\n");

        CliResult said = await Run(box, "search", "--pattern-file", pattern, "--files-only");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Contains("a.cs", said.Stdout);
        Assert.DoesNotContain("b.cs", said.Stdout);
    }

    [Fact]
    public async Task SearchTakesPatternsFromStdinOnePerLine()
    {
        using var box = new Sandbox();
        box.Write("a.cs", "alpha\n");
        box.Write("b.cs", "beta\n");
        box.Write("c.cs", "gamma\n");

        CliResult said = await RunWithInput(box, "alpha\n\nbeta\n",
            ["search", "--pattern-stdin", "--files-only"]);

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Contains("a.cs", said.Stdout);
        Assert.Contains("b.cs", said.Stdout);
        Assert.DoesNotContain("c.cs", said.Stdout);
    }

    [Fact]
    public async Task APatternFileThatIsNotThereIsRefusedAndSaysSo()
    {
        using var box = new Sandbox();

        CliResult said = await Run(box, "search", "--pattern-file", box.Full("absent.txt"));

        Assert.Equal(ExitCodes.NotFound, said.ExitCode);
        Assert.Contains("--pattern-file", said.Stdout + said.Stderr);
    }

    [Fact]
    public async Task SearchWithNoPatternNamesEveryWayOfGivingOne()
    {
        using var box = new Sandbox();

        CliResult said = await Run(box, "search");

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        string told = said.Stdout + said.Stderr;
        Assert.Contains("-e", told);
        Assert.Contains("--pattern-file", told);
        Assert.Contains("--pattern-stdin", told);
    }
    /// <summary>
    /// A byte-order mark on a stream is the producer's habit, not the
    /// caller's content. Windows PowerShell emits one whatever it is
    /// told, and the two ways it went wrong before this existed were
    /// both silent: a pattern that matched nothing, and an invisible
    /// character spliced into the middle of a source file.
    /// </summary>
    [Fact]
    public async Task ALeadingByteOrderMarkOnStdinIsNotContent()
    {
        using var box = new Sandbox();
        box.Write("a.cs", "Flag\n");

        CliResult found = await RunWithInput(box, "\uFEFFFlag\n",
            ["search", "--pattern-stdin", "--files-only"]);

        Assert.Equal(ExitCodes.Ok, found.ExitCode);
        Assert.Contains("a.cs", found.Stdout);

        CliResult wrote = await RunWithInput(box, "\uFEFFhello\n",
            ["write", "b.txt", "--stdin"]);

        Assert.Equal(ExitCodes.Ok, wrote.ExitCode);
        Assert.Equal("hello\n", box.ReadText("b.txt"));

        CliResult edited = await RunWithInput(box, "\uFEFFbetter",
            ["edit", "a.cs", "--replace", "Flag", "--with-stdin"]);

        Assert.Equal(ExitCodes.Ok, edited.ExitCode);
        byte[] raw = box.ReadRaw("a.cs");
        Assert.True(raw.Length < 3 || raw[0] != 0xEF || raw[1] != 0xBB || raw[2] != 0xBF,
            "an edit put a byte-order mark on the file: "
            + string.Join(" ", Array.ConvertAll(raw, b => b.ToString("X2"))));
        Assert.Equal("better\n", box.ReadText("a.cs"));
    }

    /// <summary>A mark further in is a zero-width no-break space that
    /// somebody may have meant, and is left alone.</summary>
    [Fact]
    public async Task AMarkThatIsNotAtTheFrontIsLeftAlone()
    {
        using var box = new Sandbox();

        CliResult wrote = await RunWithInput(box, "a\uFEFFb",
            ["write", "c.txt", "--stdin"]);

        Assert.Equal(ExitCodes.Ok, wrote.ExitCode);
        Assert.Equal("a\uFEFFb", box.ReadText("c.txt"));
    }
}

