using System.Reflection;
using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;
using Fettler.Mcp;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R10.1, R10.9 to R10.21, R10.24, R10.25 and R10.35: the two front
/// ends, the glob and search semantics behind them, and the constraints
/// that are only true if something checks.
/// </summary>
public sealed class FrontEndTests
{
    // Deliberately not overloaded on a leading string: an overload taking
    // (box, stdin, params argv) silently wins for Run(box, "find", ...)
    // and swallows the verb as stdin.
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    static Task<CliResult> RunWithInput(Sandbox box, string stdin, string[] argv) =>
        Command.RunAsync(argv, new StringReader(stdin), box.Bench);

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---- R10.1: independence ----

    /// <summary>
    /// R1.2 and R1.4. Fettler shares this repository, its release and its
    /// version number with what else lives here, and shares nothing else.
    /// A constraint that is not tested is a constraint that lasts until
    /// somebody is in a hurry, so this reads the built assemblies.
    /// </summary>
    /// <summary>
    /// The allowlist R1.2 permits, spelled out here as well as in the
    /// csproj, so adding a package means changing a test on purpose
    /// rather than watching one stop failing.
    ///
    /// <para>PdfPig ships seven assemblies from one package. Naming all
    /// seven is the point: the test knows exactly what was let in, and a
    /// package that quietly grew an eighth would be caught.</para>
    /// </summary>
    static readonly string[] Allowed =
    [
        "UglyToad.PdfPig",
        "UglyToad.PdfPig.Core",
        "UglyToad.PdfPig.DocumentLayoutAnalysis",
        "UglyToad.PdfPig.Fonts",
        "UglyToad.PdfPig.Package",
        "UglyToad.PdfPig.Tokenization",
        "UglyToad.PdfPig.Tokens",
    ];

    [Fact]
    public void TheShippedAssembliesReferenceNothingOutsideTheirOwnLane()
    {
        string[] ours = ["Fettler", "fettle"];

        foreach (string name in ours)
        {
            Assembly assembly = name == "Fettler"
                ? typeof(Bench).Assembly
                : typeof(Fettle.Program).Assembly;

            foreach (AssemblyName referenced in assembly.GetReferencedAssemblies())
            {
                string reference = referenced.Name ?? "";

                // The framework is not a dependency in the sense R1.2
                // means: it is what "targeting .NET" is. What R1.2
                // forbids is another project in this repository or a
                // third-party package riding along.
                bool framework =
                    reference.StartsWith("System", StringComparison.Ordinal)
                    || reference.StartsWith("Microsoft.Win32.", StringComparison.Ordinal)
                    || reference is "netstandard" or "mscorlib" or "Microsoft.CSharp";

                Assert.False(reference.StartsWith("Shoddy.", StringComparison.Ordinal),
                    $"{name} references {reference}: Fettler shares this repository and nothing else (R1.2)");

                Assert.True(ours.Contains(reference) || framework || Allowed.Contains(reference),
                    $"{name} references {reference}, which is neither Fettler's own, "
                    + "nor the framework, nor on R1.2's allowlist");
            }
        }
    }

    /// <summary>
    /// R3.1: the core is callable code with no protocol and no console in
    /// it. Kept by namespace inside one assembly, and asserted here,
    /// because a core that knows about a protocol has already stopped
    /// being one.
    /// </summary>
    [Fact]
    public void TheCoreNamesNoProtocolAndNoConsole()
    {
        foreach (Type type in typeof(Bench).Assembly.GetTypes())
        {
            if (type.Namespace?.StartsWith("Fettler.Core", StringComparison.Ordinal) != true) continue;

            // R3.1 revised to match its own reason (9b.5): the core's
            // SURFACE may not take or return a protocol type. Its
            // internals may parse JSON, because configuration is JSON and
            // always was - RootsFile has read it since the day it was
            // written. A core that SPEAKS a protocol has stopped being
            // one; a core that PARSES a config file has not, and a test
            // that forbade the second was testing more than the reason
            // supports.
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.False(method.ReturnType.FullName?.Contains("System.Text.Json") == true,
                    $"{type.Name}.{method.Name} answers a System.Text.Json type; the core must not (R3.1)");

                foreach (ParameterInfo parameter in method.GetParameters())
                    Assert.False(parameter.ParameterType.FullName?.Contains("System.Text.Json") == true,
                        $"{type.Name}.{method.Name} takes a System.Text.Json type; the core must not (R3.1)");
            }
        }
    }

    // ---- R10.9 and R10.32: glob semantics ----

    [Theory]
    [InlineData("*.cs", "A.cs", true)]
    [InlineData("*.cs", "deep/A.cs", false)]          // * does not cross a separator
    [InlineData("**/*.cs", "A.cs", true)]             // ** matches no segments too
    [InlineData("**/*.cs", "deep/A.cs", true)]
    [InlineData("**/*.cs", "a/b/c/A.cs", true)]
    [InlineData("src/**/*.cs", "src/A.cs", true)]
    [InlineData("src/**/*.cs", "other/A.cs", false)]
    [InlineData("*.CS", "a.cs", true)]                // matching is case-insensitive everywhere
    [InlineData("a?c.txt", "abc.txt", true)]
    [InlineData("a?c.txt", "a/c.txt", false)]         // ? does not cross a separator either
    [InlineData("*.txt", ".hidden.txt", true)]        // a dotfile is an ordinary file (R4.19)
    [InlineData("a.b", "axb", false)]                 // a dot is a dot, not a regex class
    public void GlobSemanticsAreWhatR47Says(string pattern, string path, bool matches)
    {
        Result<Glob> glob = Glob.Compile(pattern);
        Assert.True(glob.IsOk, glob.Failure?.Message);
        Assert.Equal(matches, glob.Value.Matches(path));
    }

    [Fact]
    public async Task FindMatchesEachFileExactlyOnceAndSkipsGeneratedDirectories()
    {
        using var box = new Sandbox();
        box.Write("src/A.cs", "a");
        box.Write("src/deep/B.cs", "b");
        box.Write("obj/Generated.cs", "generated");
        box.Write("bin/Other.cs", "other");

        CliResult found = await Run(box, "find", "**/*.cs", "--json");
        JsonElement answer = Parse(found.Stdout);

        string[] paths = answer.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("path").GetString()!).ToArray();

        Assert.Equal(2, paths.Length);
        Assert.Equal(paths.Length, paths.Distinct().Count());
        Assert.Contains("src/A.cs", paths);
        Assert.Contains("src/deep/B.cs", paths);
        Assert.Equal(2, answer.GetProperty("excluded").GetInt32());

        // And the flag that includes them does.
        CliResult all = await Run(box, "find", "**/*.cs", "--include-generated", "--json");
        Assert.Equal(4, Parse(all.Stdout).GetProperty("count").GetInt32());
    }

    // ---- R10.19: determinism ----

    [Fact]
    public async Task TheSameSearchOverAnUnchangedTreeIsByteIdentical()
    {
        using var box = new Sandbox();
        for (int i = 0; i < 30; i++) box.Write($"src/f{i:00}.cs", $"public sealed class C{i}\n");

        CliResult first = await Run(box, "search", "^public sealed", "--glob", "**/*.cs", "--limit", "10", "--json");
        CliResult second = await Run(box, "search", "^public sealed", "--glob", "**/*.cs", "--limit", "10", "--json");

        Assert.Equal(first.Stdout, second.Stdout);
        Assert.True(Parse(first.Stdout).GetProperty("truncated").GetBoolean(),
            "a limit that bites must say so rather than reading as complete");
    }

    // ---- R10.11: bounded output ----

    [Fact]
    public async Task ASearchThatHitsItsLimitReportsTheTruncation()
    {
        using var box = new Sandbox();
        for (int i = 0; i < 20; i++) box.Write($"f{i:00}.txt", "needle\n");

        CliResult limited = await Run(box, "search", "needle", "--limit", "5", "--json");
        JsonElement answer = Parse(limited.Stdout);

        Assert.Equal(5, answer.GetProperty("hits_count").GetInt32());
        Assert.True(answer.GetProperty("truncated").GetBoolean());
    }

    // ---- R10.21: pattern safety ----

    /// <summary>
    /// R4.13: a pattern arrives from a model or a script and is not known
    /// to be well-behaved. This one backtracks catastrophically on the
    /// input it is given, and must come back rather than consume the tool.
    /// </summary>
    [Fact]
    public async Task ACatastrophicallyBacktrackingPatternDoesNotRunAway()
    {
        using var box = new Sandbox();
        box.Write("a.txt", new string('a', 40) + "!\n");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        CliResult searched = await Run(box, "search", "(a+)+$", "--json");
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20),
            $"the search took {clock.Elapsed.TotalSeconds:0.0}s; a pattern must be bounded (R4.13)");
        Assert.Equal(ExitCodes.Ok, searched.ExitCode);
    }

    // ---- R10.10 and R10.24: result reuse and plural inputs ----

    /// <summary>
    /// R4.9: a path from find is a path read accepts verbatim, and a hit
    /// from search addresses the same line in edit. This is what makes
    /// the tool a set rather than a collection.
    /// </summary>
    [Fact]
    public async Task APathFromFindIsAcceptedVerbatimByReadAndAHitAddressesTheSameLine()
    {
        using var box = new Sandbox();
        box.Write("src/A.cs", "one\npublic sealed class A\nthree\n");

        CliResult found = await Run(box, "find", "**/*.cs", "--json");
        string path = Parse(found.Stdout).GetProperty("files")[0].GetProperty("path").GetString()!;

        CliResult read = await Run(box, "read", path, "--json");
        Assert.Equal(ExitCodes.Ok, read.ExitCode);

        CliResult hit = await Run(box, "search", "public sealed", "--json");
        JsonElement first = Parse(hit.Stdout).GetProperty("hits")[0];
        int line = first.GetProperty("line").GetInt32();
        Assert.Equal(2, line);

        // The same line number addresses the same text in edit.
        CliResult edited = await Run(box, "edit", first.GetProperty("path").GetString()!,
            "--replace", "public sealed", "--with", "internal", "--between", $"{line}-{line}");

        Assert.Equal(ExitCodes.Ok, edited.ExitCode);
        Assert.Equal("one\ninternal class A\nthree\n", box.ReadText("src/A.cs"));
    }

    [Fact]
    public async Task OneReadOfFivePathsAnswersWhatFiveReadsAnswer()
    {
        using var box = new Sandbox();
        for (int i = 1; i <= 5; i++) box.Write($"f{i}.txt", $"content {i}\n");

        string[] paths = [.. Enumerable.Range(1, 5).Select(i => $"f{i}.txt")];

        CliResult together = await Run(box, ["read", .. paths, "--json"]);
        JsonElement all = Parse(together.Stdout);
        Assert.Equal(5, all.GetProperty("count").GetInt32());

        for (int i = 0; i < 5; i++)
        {
            CliResult alone = await Run(box, "read", paths[i], "--json");
            JsonElement one = Parse(alone.Stdout).GetProperty("files")[0];
            JsonElement inBatch = all.GetProperty("files")[i];

            Assert.Equal(one.GetProperty("text").GetString(), inBatch.GetProperty("text").GetString());
            Assert.Equal(one.GetProperty("hash").GetString(), inBatch.GetProperty("hash").GetString());
        }
    }

    /// <summary>
    /// Found by using the tool rather than by reading it: <c>--version</c>
    /// parses as a flag and leaves no verb behind, so an empty-verb test
    /// placed first swallowed it and printed the help. Both spellings are
    /// asserted, because it is the flag form a script reaches for.
    /// </summary>
    [Theory]
    [InlineData("--version")]
    [InlineData("version")]
    public async Task VersionAnswersTheVersionAndNotTheHelp(string spelling)
    {
        using var box = new Sandbox();

        CliResult said = await Run(box, spelling);

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.StartsWith("fettle ", said.Stdout);
        Assert.DoesNotContain("Global flags", said.Stdout);
    }

    [Fact]
    public async Task HelpIsStillTheAnswerWhenNothingWasAsked()
    {
        using var box = new Sandbox();

        Assert.Contains("Global flags", (await Run(box)).Stdout);
        Assert.Contains("Global flags", (await Run(box, "help")).Stdout);
    }

    /// <summary>
    /// R10.37 and R8.8: a caller can ask what it is bounded to, rather
    /// than discovering the boundary by being refused by it.
    /// </summary>
    [Fact]
    public async Task RootsAnswersEveryDeclaredRootAndWhichIsDefault()
    {
        using var box = new Sandbox("scratch");

        CliResult listed = await Run(box, "roots", "--json");
        JsonElement answer = Parse(listed.Stdout);

        Assert.Equal(2, answer.GetProperty("count").GetInt32());

        JsonElement first = answer.GetProperty("roots")[0];
        Assert.Equal("root", first.GetProperty("name").GetString());
        Assert.True(first.GetProperty("default").GetBoolean(),
            "the first declared root is where an unqualified path lands");
        Assert.Equal(box.Root, first.GetProperty("path").GetString());

        JsonElement second = answer.GetProperty("roots")[1];
        Assert.Equal("scratch", second.GetProperty("name").GetString());
        Assert.False(second.GetProperty("default").GetBoolean());
    }

    /// <summary>
    /// R8.4 survives the pointer R8.8 adds: the refusal names the
    /// boundary in constant text, so it still cannot report whether
    /// anything is there.
    /// </summary>
    [Fact]
    public async Task TheOutsideRootRefusalPointsAtTheBoundaryWithoutLeaking()
    {
        using var box = new Sandbox();

        string real = Path.Combine(Path.GetTempPath(), $"fettle-leak-{Guid.NewGuid():N}.txt");
        File.WriteAllText(real, "here");
        string absent = Path.Combine(Path.GetTempPath(), $"fettle-leak-{Guid.NewGuid():N}.txt");

        try
        {
            CliResult present = await Run(box, "read", real, "--json");
            CliResult missing = await Run(box, "read", absent, "--json");

            Assert.Equal(missing.ExitCode, present.ExitCode);
            Assert.Equal(missing.Stdout, present.Stdout);
            Assert.Contains("roots", Parse(present.Stdout).GetProperty("message").GetString());
        }
        finally
        {
            File.Delete(real);
        }
    }

    // ---- R10.14: exit codes ----


    [Fact]
    public async Task EachFailureClassProducesItsOwnExitCode()
    {
        using var box = new Sandbox();
        box.Write("there.txt", "content");

        Assert.Equal(ExitCodes.NotFound, (await Run(box, "read", "absent.txt")).ExitCode);
        Assert.Equal(ExitCodes.OutsideRoot, (await Run(box, "read", "../escape.txt")).ExitCode);
        Assert.Equal(ExitCodes.TargetExists, (await Run(box, "new", "there.txt")).ExitCode);
        Assert.Equal(ExitCodes.Invalid, (await Run(box, "nonsense")).ExitCode);
        Assert.Equal(ExitCodes.Refused, (await Run(box, "delete", "*.txt")).ExitCode);
        Assert.Equal(ExitCodes.Ok, (await Run(box, "read", "there.txt")).ExitCode);
    }

    /// <summary>
    /// R3.7, and the whole reason it is a clause. PowerShell 5.1 turns a
    /// native program's redirected stderr into NativeCommandError records
    /// and sets $? false even on exit code 0, so a failure a script has
    /// to read must arrive on stdout.
    /// </summary>
    [Fact]
    public async Task AFailureInMachineReadableModeArrivesCompleteOnStdout()
    {
        using var box = new Sandbox();

        CliResult failed = await Run(box, "read", "absent.txt", "--json");

        Assert.NotEqual(ExitCodes.Ok, failed.ExitCode);
        Assert.Empty(failed.Stderr);
        Assert.NotEmpty(failed.Stdout);

        JsonElement answer = Parse(failed.Stdout);
        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal("not-found", answer.GetProperty("outcome").GetString());
        Assert.NotEmpty(answer.GetProperty("message").GetString()!);
    }

    // ---- R10.35: batch ----

    [Fact]
    public async Task ABatchRunsInOrderAndStopsAtTheFirstFailureSayingWhatWasDone()
    {
        using var box = new Sandbox();

        string script = Path.Combine(box.Root, "ops.json");
        File.WriteAllText(script, """
            {"operations": [
              {"op": "mkdir", "path": "made"},
              {"op": "new",   "path": "made/a.txt"},
              {"op": "new",   "path": "made/a.txt"},
              {"op": "new",   "path": "made/never.txt"}
            ]}
            """);

        CliResult ran = await Run(box, "batch", "--script", script, "--json");
        JsonElement answer = Parse(ran.Stdout);

        Assert.Equal(2, answer.GetProperty("completed").GetInt32());
        Assert.True(answer.GetProperty("stopped_early").GetBoolean());
        Assert.Equal(3, answer.GetProperty("operations").GetArrayLength());

        // Not transactional, and it says so: the first two are done and
        // are not undone, and the fourth never ran.
        Assert.True(File.Exists(box.Full("made/a.txt")));
        Assert.False(File.Exists(box.Full("made/never.txt")));
    }

    // ---- R10.13: front-end parity ----

    /// <summary>
    /// R3.4: a capability available to a model and not to a script, or
    /// the reverse, is a defect. Both front ends reach the operations
    /// through one dispatcher, so this asserts the tool catalogue and the
    /// verb list have not drifted apart, and that a tool answers what the
    /// verb answers.
    /// </summary>
    [Fact]
    public async Task EveryOperationIsReachableFromBothFrontEndsAndAnswersEquivalently()
    {
        using var box = new Sandbox();
        box.Write("src/A.cs", "public sealed class A\n");

        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);

        string? listed = await server.AnswerAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.NotNull(listed);

        string[] tools = Parse(listed!).GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToArray();

        string[] verbs =
        [
            "find", "search", "read", "write", "edit", "replace", "new", "mkdir",
            "move", "copy", "delete", "exec", "extract", "roots", "tasks", "run", "batch",

            // A model diagnosing its own wiring is the point of this one:
            // otherwise the only way to find out why a tool is missing is
            // to ask a person to open a terminal.
            "doctor",
        ];

        // `setup` is the ONE verb deliberately not offered to a model,
        // and the reason is not squeamishness. Setup writes the client
        // configuration files - including the one naming the command that
        // launches this very server - so a model holding it could point
        // its own boundary at a different binary, or grant back the
        // permissions the deny list removes. Parity is a rule about
        // capability, not about reach: scaffolding a machine is a
        // person's act, performed once, in a terminal.
        string[] byHandOnly = ["setup"];

        foreach (string verb in verbs)
            Assert.Contains(verb, tools);
        foreach (string tool in tools)
            Assert.Contains(tool, verbs);
        foreach (string verb in byHandOnly)
            Assert.DoesNotContain(verb, tools);

        // And one operation answers the same thing through both doors.
        string? called = await server.AnswerAsync("""
            {"jsonrpc":"2.0","id":2,"method":"tools/call",
             "params":{"name":"find","arguments":{"pattern":"**/*.cs"}}}
            """);

        string throughMcp = Parse(called!).GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!;

        CliResult throughCli = await Run(box, "find", "**/*.cs", "--json");

        Assert.Equal(throughCli.Stdout.TrimEnd(), throughMcp);
    }

    // ---- R10.16 and R10.17: the front end does not block, and cancels ----

    /// <summary>
    /// R3.8: a message is read, dispatched, and the reader returns to
    /// reading before the dispatched operation completes. Without it a
    /// four-minute task stalls every other operation, and R3.10's
    /// cancellation becomes not merely unimplemented but impossible.
    /// </summary>
    [Fact]
    public async Task TheReaderKeepsReadingWhileAnOperationIsStillRunning()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "content");

        var input = new BlockingReader();
        var output = new StringWriter();
        using var server = new McpServer(box.Bench, input, output);

        var running = server.RunAsync();

        input.Push("""{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        input.Push("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        input.Finish();

        await running.WaitAsync(TimeSpan.FromSeconds(10));

        string written = output.ToString();
        Assert.Contains("\"id\":1", written);
        Assert.Contains("\"id\":2", written);
    }

    [Fact]
    public async Task StdoutCarriesOneWholeMessagePerLine()
    {
        using var box = new Sandbox();

        var input = new BlockingReader();
        var output = new StringWriter();
        using var server = new McpServer(box.Bench, input, output);

        var running = server.RunAsync();
        for (int i = 1; i <= 25; i++)
            input.Push($$"""{"jsonrpc":"2.0","id":{{i}},"method":"ping"}""");
        input.Finish();

        await running.WaitAsync(TimeSpan.FromSeconds(20));

        string[] lines = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        Assert.Equal(25, lines.Length);
        foreach (string line in lines)
        {
            // R3.9: two messages interleaved on one stream is a corrupted
            // protocol, not a slow one. Every line must parse whole.
            JsonElement message = Parse(line);
            Assert.Equal("2.0", message.GetProperty("jsonrpc").GetString());
        }
    }

    sealed class BlockingReader : TextReader
    {
        readonly System.Collections.Concurrent.BlockingCollection<string> lines = [];

        public void Push(string line) => lines.Add(line);

        public void Finish() => lines.CompleteAdding();

        public override string? ReadLine()
        {
            try { return lines.Take(); }
            catch (InvalidOperationException) { return null; }
        }

        public override Task<string?> ReadLineAsync() => Task.Run(ReadLine);

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancel) =>
            new(Task.Run(ReadLine, cancel));
    }
}
