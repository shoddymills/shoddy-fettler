using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// The permission model: B.13's escape, the matrix of 6.13, hidden
/// leaking nothing (6.14), the scope edges of 6.15, and 6.16's
/// guarantee that a shell cannot widen the boundary.
///
/// <para>This is the largest single body of tests in the project and the
/// one that must not be sampled. Everything else trusts it.</para>
/// </summary>
public sealed class BoundaryTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    // ---- B.13 and B.15: Fettler never writes its own rules ----

    /// <summary>
    /// The escape, reproduced from the design document and refused at the
    /// write.
    ///
    /// <para>Before this existed, the two commands below succeeded in
    /// order: Fettler wrote its own task declaration and then executed
    /// it. That defeats every other permission at once - a scope
    /// protected from delete means nothing to a task that can run
    /// <c>del</c> - and the same route rewrites
    /// <c>.fettler.json</c>, which is where the permissions come from in
    /// the first place.</para>
    /// </summary>
    [Fact]
    public async Task TheEscapeIsRefusedAtTheWrite()
    {
        using var box = new Sandbox(Permissions.Full | Permission.Execute);

        Result<Saved> wrote = await box.Bench.WriteAsync(
            RootsFile.FileName,
            """{"trees":{"root":{"path":"."}},"tasks":{"probe":{"run":"echo I-WROTE-THIS-MYSELF"}}}""",
            overwrite: true);

        Assert.False(wrote.IsOk, "Fettler must not be able to write its own declaration");
        Assert.Equal(Outcome.Governed, wrote.Failure!.Outcome);
        Assert.False(File.Exists(box.Full(RootsFile.FileName)), "and nothing may be left behind");
    }

    /// <summary>
    /// Every verb that can put bytes somewhere, against every rule file.
    /// One missed path is the whole hole, so this enumerates rather than
    /// samples.
    /// </summary>
    [Theory]
    [InlineData(".fettler.json")]
    [InlineData(".fettler.local.json")]
    public async Task NoWritingVerbTouchesAFileThatGovernsThisTool(string ruled)
    {
        using var box = new Sandbox();
        box.Write("ordinary.txt", "content\n");
        box.Write("tree/keep.txt", "keep\n");

        // Put a real one there so the verbs that need an existing file
        // have one, and so a refusal cannot pass by the file being absent.
        box.Write(ruled, "# written from outside Fettler, as a person would\n");

        var refusals = new List<(string What, Outcome Outcome)>();

        async Task Try(string what, Task<Result<bool>> attempt) =>
            refusals.Add((what, (await attempt).IsOk ? Outcome.Ok : (await attempt).Failure!.Outcome));

        static async Task<Result<bool>> As<T>(Task<Result<T>> operation)
        {
            Result<T> r = await operation;
            return r.IsOk ? Result<bool>.Ok(true) : r.Carry<bool>();
        }

        await Try("write", As(box.Bench.WriteAsync(ruled, "hijacked", overwrite: true)));
        await Try("new", As(box.Bench.NewFileAsync(ruled)));
        await Try("edit", As(box.Bench.EditAsync(
            [new FileEdits(ruled, null, [new Edit.Replace("written", "hijacked")])], dryRun: false)));
        await Try("edit dry-run", As(box.Bench.EditAsync(
            [new FileEdits(ruled, null, [new Edit.Replace("written", "hijacked")])], dryRun: true)));
        await Try("delete", As(box.Bench.DeleteAsync(ruled, recursive: false, force: true)));
        await Try("move onto", As(box.Bench.MoveAsync("ordinary.txt", ruled, overwrite: true)));
        await Try("move away", As(box.Bench.MoveAsync(ruled, "stolen.txt", overwrite: true)));
        await Try("copy onto", As(box.Bench.CopyAsync("ordinary.txt", ruled, recursive: false,
            overwrite: true, preserveTimes: false)));
        await Try("mkdir", As(box.Bench.MakeDirectoryAsync(ruled)));
        await Try("exec", As(box.Bench.SetExecutableAsync(ruled, executable: true)));

        foreach ((string what, Outcome outcome) in refusals)
            Assert.True(outcome is Outcome.Governed,
                $"{what} on {ruled} answered {outcome}; it must be governed");

        Assert.StartsWith("# written from outside", File.ReadAllText(box.Full(ruled)));
    }

    /// <summary>
    /// The rule is by NAME, not by prefix. Refusing a file merely
    /// resembling one of the two would be superstition rather than a
    /// rule, and would make the tool useless for writing about itself.
    /// </summary>
    [Theory]
    [InlineData(".fettler.json.md")]
    [InlineData(".fettler.json.bak")]
    [InlineData("notes/fettler.json")]
    [InlineData("fettler.local.json")]
    public async Task AFileThatMerelyRESEMBLESARuleFileIsOrdinary(string ordinary)
    {
        using var box = new Sandbox();
        Directory.CreateDirectory(box.Full("notes"));

        Result<Saved> wrote = await box.Bench.WriteAsync(ordinary, "ordinary\n", overwrite: true);

        Assert.True(wrote.IsOk, wrote.Failure?.Message);
    }

    /// <summary>
    /// A rule file deeper in the tree governs its own subtree when a
    /// command is run from inside it, so protecting only the one at the
    /// top would leave every other one writable.
    /// </summary>
    [Fact]
    public async Task ARuleFileInASubdirectoryIsProtectedToo()
    {
        using var box = new Sandbox();
        Directory.CreateDirectory(box.Full("nested"));

        Result<Saved> wrote = await box.Bench.WriteAsync(
            "nested/.fettler.json", """{"trees":{"mine":{"path":".","can":["execute"]}}}""", overwrite: true);

        Assert.False(wrote.IsOk);
        Assert.Equal(Outcome.Governed, wrote.Failure!.Outcome);
    }

    /// <summary>Reading them is fine and has to stay fine: <c>tasks</c>
    /// reads the declaration, and a caller asking what the rules are is
    /// the opposite of the problem.</summary>
    [Fact]
    public void ARuleFileMayStillBeREAD()
    {
        using var box = new Sandbox();
        box.Write(RootsFile.FileName, """{"trees":{"work":{"path":"."}}}""");

        Result<IReadOnlyList<ReadSlice>> read = box.Bench.Read(new ReadRequest([RootsFile.FileName]));

        Assert.True(read.IsOk, read.Failure?.Message);
        Assert.Contains("trees", read.Value[0].Text);
    }

    // ---- 6.13: the permission matrix ----

    /// <summary>
    /// Every verb against the permission it needs, granted and withheld.
    ///
    /// <para>Each row states the ONE permission the verb needs beyond
    /// list; the test runs it in a tree granting exactly
    /// <c>list read</c> plus that permission (which must succeed) and in
    /// one granting everything except it (which must be refused). That
    /// second half is what catches a verb checking the wrong permission,
    /// which a granted-only test never would.</para>
    /// </summary>
    [Theory]
    [InlineData("new", Permission.Create)]
    [InlineData("mkdir", Permission.Create)]
    [InlineData("write-new", Permission.Create)]
    [InlineData("write-over", Permission.Update)]
    [InlineData("edit", Permission.Update)]
    [InlineData("exec", Permission.Update)]
    [InlineData("delete", Permission.Delete)]
    [InlineData("read", Permission.Read)]
    public async Task EachVerbNeedsItsOwnPermissionAndIsRefusedWithoutIt(string verb, Permission needed)
    {
        await Attempt(verb, Permissions.ReadOnly | needed, shouldWork: true);
        await Attempt(verb, Permissions.Full & ~needed, shouldWork: false);
    }

    static async Task Attempt(string verb, Permission can, bool shouldWork)
    {
        using var box = new Sandbox(can);
        box.Write("existing.txt", "before\n");

        Result<bool> outcome = verb switch
        {
            "new" => Flatten(await box.Bench.NewFileAsync("fresh.txt")),
            "mkdir" => Flatten(await box.Bench.MakeDirectoryAsync("fresh")),
            "write-new" => Flatten(await box.Bench.WriteAsync("fresh.txt", "x", overwrite: false)),
            "write-over" => Flatten(await box.Bench.WriteAsync("existing.txt", "x", overwrite: true)),
            "edit" => Flatten(await box.Bench.EditAsync(
                [new FileEdits("existing.txt", null, [new Edit.Replace("before", "after")])], dryRun: false)),
            "exec" => Flatten(await box.Bench.SetExecutableAsync("existing.txt", executable: true)),
            "delete" => Flatten(await box.Bench.DeleteAsync("existing.txt", recursive: false, force: false)),
            "read" => Flatten(box.Bench.Read(new ReadRequest(["existing.txt"]))),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "no such row"),
        };

        Assert.True(outcome.IsOk == shouldWork,
            $"{verb} with '{Permissions.Write(can)}' should {(shouldWork ? "work" : "be refused")}"
            + (outcome.IsOk ? "" : $"; it said: {outcome.Failure!.Message}"));

        if (!shouldWork) Assert.Equal(Outcome.Refused, outcome.Failure!.Outcome);
    }

    static Result<bool> Flatten<T>(Result<T> r) => r.IsOk ? Result<bool>.Ok(true) : r.Carry<bool>();

    /// <summary>
    /// A scope REPLACES what its tree grants rather than adding to it,
    /// which is what lets it take a permission away. Nesting resolves
    /// most-specific-first, at any depth.
    /// </summary>
    [Fact]
    public void TheMostSpecificScopeDecidesAndItReplacesRatherThanAdds()
    {
        string tree = Path.Combine("C:", "t");
        if (!OperatingSystem.IsWindows()) tree = "/t";

        var grant = new Grant(Permissions.Full,
        [
            new Scope(Path.Combine(tree, "a"), Permissions.ReadOnly),
            new Scope(Path.Combine(tree, "a", "b"), Permissions.Full | Permission.Execute),
            new Scope(Path.Combine(tree, "a", "b", "c"), Permission.None),
        ]);

        Assert.Equal(Permissions.Full, grant.At(tree, Path.Combine(tree, "x")).Can);
        Assert.Equal(Permissions.ReadOnly, grant.At(tree, Path.Combine(tree, "a", "x")).Can);
        Assert.Equal(Permissions.Full | Permission.Execute, grant.At(tree, Path.Combine(tree, "a", "b", "x")).Can);
        Assert.Equal(Permission.None, grant.At(tree, Path.Combine(tree, "a", "b", "c", "x")).Can);

        // A path exactly ON a boundary belongs to the scope it names.
        Assert.Equal(Permissions.ReadOnly, grant.At(tree, Path.Combine(tree, "a")).Can);

        // And a name that merely starts the same is not inside it.
        Assert.Equal(Permissions.Full, grant.At(tree, Path.Combine(tree, "abc")).Can);
    }

    // ---- 6.14: hidden leaks nothing ----

    /// <summary>
    /// A scope with no <c>list</c> is absent from find and from search,
    /// absent from their counts and totals, and a direct path to it is
    /// refused in the SAME WORDS as a path outside every tree - so
    /// hidden and absent cannot be told apart by asking.
    /// </summary>
    [Fact]
    public async Task AScopeWithoutListIsIndistinguishableFromNotBeingThere()
    {
        using var box = new Sandbox(Permissions.Full,
            [new ScopeDecl("cancelled", Permission.None)]);

        Directory.CreateDirectory(box.Full("cancelled"));
        box.Write("open/kept.txt", "NEEDLE here\n");
        box.Write("cancelled/dropped.txt", "NEEDLE there\n");
        box.Write("cancelled/deeper/also.txt", "NEEDLE deeper\n");

        CliResult found = await Run(box, "find", "**/*.txt", "--json");
        Assert.DoesNotContain("cancelled", found.Stdout);
        Assert.Contains("kept.txt", found.Stdout);
        Assert.Contains("\"count\":1", found.Stdout);
        Assert.Contains("\"excluded\":0", found.Stdout);

        CliResult searched = await Run(box, "search", "NEEDLE", "--json");
        Assert.DoesNotContain("cancelled", searched.Stdout);
        Assert.Contains("\"hits_count\":1", searched.Stdout);
        Assert.Contains("\"files\":1", searched.Stdout);
        Assert.Contains("\"files_searched\":1", searched.Stdout);

        // The same words, exactly, as a path that is not in any tree.
        Result<ContainedPath> hidden = box.Roots.Resolve("cancelled/dropped.txt", Permission.Read);
        Result<ContainedPath> outside = box.Roots.Resolve(
            Path.Combine(Path.GetTempPath(), "fettle-nowhere-at-all", "x.txt"), Permission.Read);

        Assert.False(hidden.IsOk);
        Assert.False(outside.IsOk);
        Assert.Equal(outside.Failure!.Outcome, hidden.Failure!.Outcome);
        Assert.Equal(outside.Failure.Message, hidden.Failure.Message);

        // Including one that is not there at all inside the hidden scope,
        // so existence cannot be probed either.
        Result<ContainedPath> absent = box.Roots.Resolve("cancelled/never-existed.txt", Permission.Read);
        Assert.Equal(hidden.Failure.Message, absent.Failure!.Message);
    }

    /// <summary>
    /// The verb that does not name what it touches. Without this a scope
    /// is protected only against being named, and a recursive delete of
    /// its parent takes it anyway.
    /// </summary>
    [Fact]
    public async Task ARecursiveDeleteWillNotReachThroughAProtectedScope()
    {
        using var box = new Sandbox(Permissions.Full,
            [new ScopeDecl("requirements/cancelled", Permission.None)]);

        box.Write("requirements/live.md", "live\n");
        box.Write("requirements/cancelled/dead.md", "dead\n");

        Result<DeleteReport> deleted = await box.Bench.DeleteAsync("requirements", recursive: true, force: false);

        Assert.False(deleted.IsOk, "a recursive delete must not reach through a scope that forbids it");
        Assert.Equal(Outcome.Refused, deleted.Failure!.Outcome);
        Assert.True(File.Exists(box.Full("requirements/cancelled/dead.md")));

        // And the refusal names no path inside the hidden scope.
        Assert.DoesNotContain("cancelled", deleted.Failure.Message);
    }

    [Fact]
    public async Task ARecursiveCopyWillNotCarryAHiddenScopeOutIntoTheOpen()
    {
        using var box = new Sandbox(Permissions.Full,
            [new ScopeDecl("requirements/cancelled", Permission.None)]);

        box.Write("requirements/live.md", "live\n");
        box.Write("requirements/cancelled/dead.md", "dead\n");

        Result<CopyReport> copied = await box.Bench.CopyAsync(
            "requirements", "exposed", recursive: true, overwrite: false, preserveTimes: false);

        Assert.False(copied.IsOk);
        Assert.False(Directory.Exists(box.Full("exposed")));
    }

    // ---- 6.15: move across a scope edge ----

    /// <summary>
    /// The case the whole model was asked for: requirements may be
    /// created, read, updated and REORGANISED, and may never be
    /// destroyed. Moving deeper is allowed; moving out is not, because a
    /// file that leaves is gone from here either way.
    /// </summary>
    [Fact]
    public async Task MovingWithinAScopeIsAllowedAndMovingOutOfItIsNot()
    {
        // The tree itself is fully writable, so nothing below is refused
        // for the wrong reason: the ONLY thing withheld is delete inside
        // requirements, which is what the move out has to trip on.
        using var box = new Sandbox(Permissions.Full,
        [
            new ScopeDecl("requirements",
                Permission.List | Permission.Read | Permission.Create
                | Permission.Update | Permission.Rename),
        ]);

        box.Write("requirements/one.md", "one\n");
        box.Write("requirements/two.md", "two\n");
        Directory.CreateDirectory(box.Full("requirements/done"));
        Directory.CreateDirectory(box.Full("elsewhere"));

        // Into a descendant: rename on the source, create at the
        // destination, and both are granted.
        Result<MoveReport> inward = await box.Bench.MoveAsync(
            "requirements/one.md", "requirements/done/one.md", overwrite: false);
        Assert.True(inward.IsOk, inward.Failure?.Message);

        // Out of the scope: that needs delete as well, and it is withheld.
        Result<MoveReport> outward = await box.Bench.MoveAsync(
            "requirements/two.md", "elsewhere/two.md", overwrite: false);
        Assert.False(outward.IsOk, "leaving a scope needs delete");
        Assert.Equal(Outcome.Refused, outward.Failure!.Outcome);
        Assert.Contains("delete", outward.Failure.Message);
        Assert.True(File.Exists(box.Full("requirements/two.md")));

        // And a plain delete is refused for the same reason.
        Result<DeleteReport> deleted = await box.Bench.DeleteAsync(
            "requirements/two.md", recursive: false, force: false);
        Assert.False(deleted.IsOk);
    }

    [Fact]
    public async Task MovingOutOfAScopeSUCCEEDSWhenDeleteIsGranted()
    {
        using var box = new Sandbox(Permissions.Full,
            [new ScopeDecl("requirements", Permissions.Full)]);

        box.Write("requirements/two.md", "two\n");
        Directory.CreateDirectory(box.Full("elsewhere"));

        Result<MoveReport> outward = await box.Bench.MoveAsync(
            "requirements/two.md", "elsewhere/two.md", overwrite: false);

        Assert.True(outward.IsOk, outward.Failure?.Message);
    }

    /// <summary>A move that replaces something destroys it, whatever the
    /// verb was called, so the destination needs delete too.</summary>
    [Fact]
    public async Task MovingONTOSomethingNeedsDeleteAtTheDestination()
    {
        using var box = new Sandbox(Permissions.Full & ~Permission.Delete);
        box.Write("from.txt", "from\n");
        box.Write("onto.txt", "onto\n");

        Result<MoveReport> moved = await box.Bench.MoveAsync("from.txt", "onto.txt", overwrite: true);

        Assert.False(moved.IsOk);
        Assert.Equal("onto\n", box.ReadText("onto.txt"));
    }

    // ---- B.8: execute ----

    [Fact]
    public async Task RunNeedsExecuteAndSaysSoWhenItIsNotGranted()
    {
        using var box = new Sandbox(Permissions.Full);
        box.Declare(new TaskDecl("hello", ["cmd"], null));

        Result<TaskRun> ran = await box.Bench.RunAsync("hello", TimeSpan.Zero, CancellationToken.None);

        Assert.False(ran.IsOk, "execute is never granted by default, including in the tree the config sits in");
        Assert.Equal(Outcome.Refused, ran.Failure!.Outcome);
        Assert.Contains("execute", ran.Failure.Message);
        Assert.Contains(RootsFile.FileName, ran.Failure.Message);
    }

    [Fact]
    public void TasksMayStillBeLISTEDWithoutExecute()
    {
        using var box = new Sandbox(Permissions.Full);
        box.Declare(new TaskDecl("hello", ["cmd"], null));

        Result<IReadOnlyList<TaskDecl>> tasks = box.Bench.TaskList();

        Assert.True(tasks.IsOk, tasks.Failure?.Message);
        Assert.Single(tasks.Value);
    }

    // ---- B.9: the boundary can be read rather than discovered ----

    [Fact]
    public async Task RootsReportsWhatMayBeDoneAndNotOnlyWhereItIs()
    {
        using var box = new Sandbox(Permissions.ReadOnly,
            [new ScopeDecl("requirements", Permissions.Full)]);
        Directory.CreateDirectory(box.Full("requirements"));

        CliResult human = await Run(box, "roots");
        Assert.Contains("can: list read", human.Stdout);
        Assert.Contains("requirements", human.Stdout);

        CliResult json = await Run(box, "roots", "--json");
        Assert.Contains("\"can\":\"list read\"", json.Stdout);
        Assert.Contains("\"scopes\"", json.Stdout);
    }

    // ---- 6.16: a shell cannot widen the boundary ----

    /// <summary>
    /// Every shape of <c>--root</c>, and no combination promotes it. The
    /// hole this closes is not the flag - it is that a command line is
    /// not a deliberate act inside the tree it governs, and used to grant
    /// everything.
    /// </summary>
    [Fact]
    public async Task NoArrangementOfFlagsPromotesACommandLineRootAboveReadOnly()
    {
        string tree = Directory.CreateTempSubdirectory("fettle-cmdline-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tree, "subject.txt"), "before\n");

            foreach (string[] argv in new[]
            {
                new[] { "write", "subject.txt", "--text", "after", "--overwrite", "--root", tree },
                ["write", "subject.txt", "--text", "after", "--overwrite", "--root", $"a={tree}", "--root", $"b={tree}"],
                ["write", "subject.txt", "--text", "after", "--overwrite", "--root", tree, "--no-config"],
                ["delete", "subject.txt", "--force", "--root", tree],
                ["new", "invented.txt", "--root", tree],
            })
            {
                CliResult result = await Command.RunAsync(argv, new StringReader(string.Empty));
                Assert.NotEqual(ExitCodes.Ok, result.ExitCode);
            }

            Assert.Equal("before\n", File.ReadAllText(Path.Combine(tree, "subject.txt")));
            Assert.False(File.Exists(Path.Combine(tree, "invented.txt")));

            // Reading, on the other hand, is exactly what it is for.
            CliResult read = await Command.RunAsync(
                ["read", "subject.txt", "--root", tree, "--json"], new StringReader(string.Empty));
            Assert.Equal(ExitCodes.Ok, read.ExitCode);
        }
        finally
        {
            Directory.Delete(tree, recursive: true);
        }
    }
}
