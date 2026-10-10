using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R2, R6 and R11.1 to R11.5b: the tools level with no file, and the
/// client's folders fettle finds itself.
///
/// <para>Every machine here is a fixture: a fake home, a fake temp folder
/// and a fake <c>/tmp</c>, laid out the way the client lays them out.
/// No test reads the real machine.</para>
/// </summary>
public sealed class HarnessTests
{
    /// <summary>A launch folder and a fake machine around it.</summary>
    sealed class Machine : IDisposable
    {
        readonly string root = Real(Directory.CreateTempSubdirectory("fettle-harness-").FullName);

        public Machine()
        {
            Launch = Dir("projects/app");
            Home = Dir("home");
            Temp = Dir("temp");
            Tmp = Dir("tmp");
            Slug = Harness.Slugs(Launch)[0];
        }

        public string Launch { get; }
        public string Home { get; }
        public string Temp { get; }
        public string Tmp { get; }
        public string Slug { get; }

        public string Dir(string relative)
        {
            string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(full);
            return full;
        }

        /// <summary>A session's scratch folder under a scratch root, made
        /// at a stated time so the newest is known.</summary>
        public string Session(string scratchRoot, Guid id, DateTime created)
        {
            string session = Path.Combine(scratchRoot, Slug, id.ToString("D"));
            Directory.CreateDirectory(Path.Combine(session, "scratchpad"));
            Directory.SetCreationTimeUtc(session, created);
            return Path.Combine(session, "scratchpad");
        }

        /// <summary>The client's own folder for the project.</summary>
        public string Client => Path.Combine(Home, ".claude", "projects", Slug);

        public Host Host(IReadOnlyDictionary<string, string>? environment = null, string? uid = null) =>
            new(Home, Temp, uid, null, environment ?? new Dictionary<string, string>(), Tmp);

        public Roots Open(Host host)
        {
            Result<Boundary> opened = Arguments.Parse(["roots"]).DeclaredBoundary(host, Launch);
            Assert.True(opened.IsOk, opened.Failure?.Message);
            return opened.Value.Roots;
        }

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static string Real(string path) =>
        Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;

    static Task<CliResult> Run(Bench bench, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), bench);

    // ---- R2 and R11.1: the tools level needs no file ----

    /// <summary>R11.1: in a folder with no file every tools-level verb
    /// works, and run refuses naming the missing file.</summary>
    [Fact]
    public async Task AFolderWithNoFileHasEveryToolsVerbAndRunRefuses()
    {
        using var m = new Machine();
        using var bench = new Bench(m.Open(Host.None));

        Assert.True((await bench.WriteAsync("a.txt", "alpha\n", overwrite: false)).IsOk);
        Assert.True(bench.Read(new ReadRequest(["a.txt"])).IsOk);
        Assert.True((await bench.EditAsync([new FileEdits("a.txt", null, [new Edit.Replace("alpha", "beta")])], dryRun: false)).IsOk);
        Assert.True((await bench.CopyAsync("a.txt", "b.txt", recursive: false, overwrite: false, preserveTimes: false)).IsOk);
        Assert.True((await bench.MoveAsync("b.txt", "c.txt", overwrite: false)).IsOk);
        Assert.True((await bench.MakeDirectoryAsync("sub")).IsOk);
        Assert.True((await bench.DeleteAsync("c.txt", recursive: false, force: false)).IsOk);

        CliResult found = await Run(bench, "find", "**/*");
        Assert.Equal(0, found.ExitCode);
        Assert.Contains("a.txt", found.Stdout);

        CliResult run = await Run(bench, "run", "build");
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(RootsFile.FileName, run.Stderr + run.Stdout);
        Assert.Contains("guardrails", run.Stderr + run.Stdout);
    }

    /// <summary>Acceptance criterion 1: roots names work, says guardrails
    /// and screenings are off, and lists the client's folders with how
    /// each was found.</summary>
    [Fact]
    public async Task RootsAtTheToolsLevelNamesTheLevelsAndTheClientsFolders()
    {
        using var m = new Machine();
        Guid id = Guid.NewGuid();
        m.Session(Path.Combine(m.Temp, "claude"), id, DateTime.UtcNow);
        Directory.CreateDirectory(Path.Combine(m.Client, id.ToString("D"), "tool-results"));
        Directory.CreateDirectory(Path.Combine(m.Client, "memory"));

        using var bench = new Bench(m.Open(m.Host()));
        CliResult roots = await Run(bench, "roots");

        Assert.Equal(0, roots.ExitCode);
        Assert.Contains("guardrails", roots.Stdout);
        Assert.Matches(@"guardrails\s+off", roots.Stdout);
        Assert.Matches(@"screenings\s+off", roots.Stdout);
        Assert.Contains("work", roots.Stdout);
        Assert.Contains("found by fettle", roots.Stdout);
        foreach (string name in Harness.Names)
            Assert.Contains(name, roots.Stdout);
        Assert.Contains("newest session folder", roots.Stdout);
    }

    // ---- R6: the scratch folder, found and not declared ----

    /// <summary>R11.4, first route: a value in the environment that is a
    /// session folder's exact name picks that folder, and roots says so.</summary>
    [Fact]
    public void TheEnvironmentNamesThisSessionsFolder()
    {
        using var m = new Machine();
        string scratchRoot = Path.Combine(m.Temp, "claude");
        Guid older = Guid.NewGuid(), mine = Guid.NewGuid(), newer = Guid.NewGuid();
        m.Session(scratchRoot, older, DateTime.UtcNow.AddHours(-2));
        string pad = m.Session(scratchRoot, mine, DateTime.UtcNow.AddHours(-1));
        m.Session(scratchRoot, newer, DateTime.UtcNow);

        Roots roots = m.Open(m.Host(new Dictionary<string, string> { ["FAKE_SESSION"] = mine.ToString("D") }));

        Assert.Equal(Real(pad), roots.PathOf(Harness.ScratchName));
        Assert.Contains("FAKE_SESSION", roots.HowFound(Harness.ScratchName));
    }

    /// <summary>R11.4, second route: with nothing in the environment, the
    /// newest session folder is chosen; the others are not trees, and a
    /// path into one is refused in the same words as any path outside.</summary>
    [Fact]
    public void WithNothingInTheEnvironmentTheNewestSessionIsChosenAndTheRestAreOutside()
    {
        using var m = new Machine();
        string scratchRoot = Path.Combine(m.Temp, "claude");
        string first = m.Session(scratchRoot, Guid.NewGuid(), DateTime.UtcNow.AddHours(-2));
        string second = m.Session(scratchRoot, Guid.NewGuid(), DateTime.UtcNow.AddHours(-1));
        string newest = m.Session(scratchRoot, Guid.NewGuid(), DateTime.UtcNow);
        File.WriteAllText(Path.Combine(first, "old.txt"), "an older session\n");

        Roots roots = m.Open(m.Host());

        Assert.Equal(Real(newest), roots.PathOf(Harness.ScratchName));
        Assert.Contains("newest session folder", roots.HowFound(Harness.ScratchName));
        Assert.Equal(Permissions.Full, roots.GrantOf(Harness.ScratchName).Can);

        Result<ContainedPath> older = roots.Resolve(Path.Combine(first, "old.txt"), Permission.Read);
        Result<ContainedPath> outside = roots.Resolve(Path.Combine(m.Temp, "nowhere.txt"), Permission.Read);
        Assert.False(older.IsOk);
        Assert.Equal(Outcome.OutsideRoot, older.Failure!.Outcome);
        Assert.Equal(outside.Failure!.Outcome, older.Failure.Outcome);
        Assert.DoesNotContain(roots.Names, n => roots.PathOf(n).Equals(Real(second), Roots.PathComparison));
    }

    /// <summary>R11.5b: the scratch root is found under /tmp/claude-uid and
    /// under the temp folder, and with neither there is no scratch tree
    /// and no failure.</summary>
    [Fact]
    public void TheScratchRootIsFoundUnderTmpOrTempAndAbsentMeansNoTree()
    {
        using var m = new Machine();
        string pad = m.Session(Path.Combine(m.Tmp, "claude-501"), Guid.NewGuid(), DateTime.UtcNow);

        Roots fromTmp = m.Open(m.Host(uid: "501"));
        Assert.Equal(Real(pad), fromTmp.PathOf(Harness.ScratchName));

        using var other = new Machine();
        string inTemp = other.Session(Path.Combine(other.Temp, "claude"), Guid.NewGuid(), DateTime.UtcNow);
        Assert.Equal(Real(inTemp), other.Open(other.Host()).PathOf(Harness.ScratchName));

        using var bare = new Machine();
        Roots none = bare.Open(bare.Host(uid: "501"));
        Assert.DoesNotContain(Harness.ScratchName, none.Names);
    }

    /// <summary>R6.4: tools.scratch in the project file overrides the
    /// choice by hand, and roots names the file that said so.</summary>
    [Fact]
    public void ToolsScratchInTheProjectFileOverridesTheChoice()
    {
        using var m = new Machine();
        string chosen = m.Dir("projects/app/my-scratch");
        m.Session(Path.Combine(m.Temp, "claude"), Guid.NewGuid(), DateTime.UtcNow);
        File.WriteAllText(Path.Combine(m.Launch, RootsFile.FileName), """{ "tools": { "scratch": "my-scratch" } }""");

        Roots roots = m.Open(m.Host());

        Assert.Equal(Real(chosen), roots.PathOf(Harness.ScratchName));
        Assert.Contains(RootsFile.FileName, roots.HowFound(Harness.ScratchName));
    }

    /// <summary>R6.6: the scratch tree never grants execute.</summary>
    [Fact]
    public void TheScratchTreeNeverGrantsExecute()
    {
        using var m = new Machine();
        m.Session(Path.Combine(m.Temp, "claude"), Guid.NewGuid(), DateTime.UtcNow);

        Roots roots = m.Open(m.Host());

        Assert.False(roots.GrantOf(Harness.ScratchName).Can.HasFlag(Permission.Execute));
    }

    // ---- R6.7 to R6.9 and R11.4a: the client's own folder ----

    /// <summary>R11.4a: tool-results reads and refuses writes, memory
    /// reads and writes, and a transcript beside them is outside.</summary>
    [Fact]
    public async Task ToolResultsIsReadOnlyMemoryIsWritableAndTranscriptsAreOutside()
    {
        using var m = new Machine();
        Guid id = Guid.NewGuid();
        m.Session(Path.Combine(m.Temp, "claude"), id, DateTime.UtcNow);

        string results = Path.Combine(m.Client, id.ToString("D"), "tool-results");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "big.txt"), "a large answer\n");
        Directory.CreateDirectory(Path.Combine(m.Client, "memory"));
        string transcript = Path.Combine(m.Client, id.ToString("D") + ".jsonl");
        File.WriteAllText(transcript, "{}\n");

        using var bench = new Bench(m.Open(m.Host()));

        Assert.True(bench.Read(new ReadRequest(["tool-results:big.txt"])).IsOk);

        Result<Saved> spill = await bench.WriteAsync("tool-results:mine.txt", "no\n", overwrite: false);
        Assert.False(spill.IsOk);
        Assert.Equal(Outcome.Refused, spill.Failure!.Outcome);
        Assert.Contains("create", spill.Failure.Message);

        Assert.True((await bench.WriteAsync("memory:note.md", "remember\n", overwrite: false)).IsOk);
        Assert.True(bench.Read(new ReadRequest(["memory:note.md"])).IsOk);

        Result<IReadOnlyList<ReadSlice>> read = bench.Read(new ReadRequest([transcript]));
        Assert.False(read.IsOk);
        Assert.Equal(Outcome.OutsideRoot, read.Failure!.Outcome);
    }

    /// <summary>A tool-results folder directly under the client's project
    /// folder is used when there is none in this session's folder.</summary>
    [Fact]
    public void AToolResultsFolderDirectlyUnderTheProjectIsFoundToo()
    {
        using var m = new Machine();
        string direct = Path.Combine(m.Client, "tool-results");
        Directory.CreateDirectory(direct);

        Roots roots = m.Open(m.Host());

        Assert.Equal(Real(direct), roots.PathOf(Harness.ToolResultsName));
        Assert.Equal(Permissions.ReadOnly, roots.GrantOf(Harness.ToolResultsName).Can);
    }

    // ---- serve at the tools level ----

    /// <summary>With no file at launch, the boundary is opened again on
    /// every call, so a guardrails section written later binds on the
    /// next call without a restart.</summary>
    [Fact]
    public void AGuardrailsFileWrittenAfterLaunchBindsOnTheNextOpen()
    {
        using var m = new Machine();
        Result<Boundary> launched = Arguments.Parse(["serve"]).DeclaredBoundary(Host.None, m.Launch);
        Assert.True(launched.IsOk, launched.Failure?.Message);
        Assert.False(launched.Value.Roots.Levels.GuardrailsOn);

        File.WriteAllText(Path.Combine(m.Launch, RootsFile.FileName),
            """{ "guardrails": { "trees": { "work": { "path": ".", "can": ["list"] } } } }""");

        Result<Roots> next = launched.Value.Reopen();
        Assert.True(next.IsOk, next.Failure?.Message);
        Assert.True(next.Value.Levels.GuardrailsOn);
        Assert.Equal(Permission.List, next.Value.GrantOf("work").Can);

        // Once found, that file is the file re-read. Deleting it refuses
        // rather than falling back to the tools level.
        File.Delete(Path.Combine(m.Launch, RootsFile.FileName));
        Assert.False(launched.Value.Reopen().IsOk);
    }

    // ---- R7: the verbs say the level ----

    /// <summary>R10.4: setup's first word is the level, and the old
    /// spelling is refused with the new ones shown.</summary>
    [Fact]
    public async Task TheOldSetupSpellingIsRefusedNamingTheNewOnes()
    {
        using var m = new Machine();
        using var bench = new Bench(m.Open(Host.None));

        CliResult old = await Run(bench, "setup", "claude-code", "--dry-run");

        Assert.Equal(ExitCodes.Of(Outcome.Invalid), old.ExitCode);
        Assert.Contains("fettle setup tools claude-code", old.Stderr + old.Stdout);
        Assert.Contains("fettle setup guardrails claude-code", old.Stderr + old.Stdout);
    }

    /// <summary>R7.8: the MCP instructions open with the tools and say in
    /// one sentence each whether guardrails and screenings are on.</summary>
    [Fact]
    public void TheServerInstructionsSayWhichLevelsAreOn()
    {
        string off = Fettler.Mcp.McpServer.InstructionsFor(Levels.ToolsOnly);
        string on = Fettler.Mcp.McpServer.InstructionsFor(new Levels([], ["a"], ["b"]));

        Assert.StartsWith("File, search and edit operations", off);
        Assert.Contains("Guardrails are off", off);
        Assert.Contains("Screenings are off", off);
        Assert.Contains("Guardrails are on", on);
        Assert.Contains("Screenings are on", on);
    }

    // ---- the slug rule ----

    [Fact]
    public void TheSlugReplacesSeparatorsAndTheDriveColonWithHyphens()
    {
        if (OperatingSystem.IsWindows())
            Assert.Equal("c--github-shoddy-fettler", Harness.Slugs(@"c:\github\shoddy-fettler")[0]);
        else
            Assert.Equal("-home-me-code-app", Harness.Slugs("/home/me/code/app")[0]);
    }
}
