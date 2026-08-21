using System.Text;
using System.Text.Json;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R10.12 and R10.16 to R10.18: running a declared task, the argument
/// fidelity R7.3 exists for, and what concurrency does and does not do.
/// </summary>
public sealed class TaskTests
{
    /// <summary>The shipped executable, sitting beside the test assembly
    /// because Fettler.Tests references it. Driving the real binary is
    /// also what R2.5 asks CI to do on every push.</summary>
    static string? Fettle()
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "fettle.exe" : "fettle");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// A tree that may actually run something.
    ///
    /// <para>B.8: <c>execute</c> is never a default, in any tree,
    /// including the one the configuration sits in - so every test that
    /// runs a task has to say so, and the three that do are the three
    /// where it matters. The alternative, a sandbox that quietly granted
    /// it, would have made the whole rule untested by making it
    /// invisible.</para>
    /// </summary>
    const Permission Runnable = Permissions.Full | Permission.Execute;

    /// <summary>
    /// R10.12 and R7.3: an argument containing a space, a double quote
    /// and a backslash arrives at the child exactly as given.
    ///
    /// <para>The child is <c>fettle</c> itself, searching for that string
    /// literally and echoing the pattern back in its machine-readable
    /// answer. Nothing about the test can pass by accident: if a shell
    /// were involved anywhere, or if the declaration file had a quoting
    /// syntax to get wrong, the string would come back mangled.</para>
    /// </summary>
    [Fact]
    public async Task AnArgumentWithSpacesQuotesAndBackslashesArrivesVerbatim()
    {
        string? fettle = Fettle();
        if (fettle is null) return;

        using var box = new Sandbox(Runnable);

        const string awkward = """a "quoted" thing and \a\back\slash and  two spaces""";
        box.Write("subject.txt", $"before\n{awkward}\nafter\n");

        // The child gets --root because there is no implicit current
        // directory any more (B.3): a bare `fettle search` outside a
        // configured tree refuses, which is the point. A read-only tree
        // is all a search needs, and all --root can grant.
        //
        // Declared as a list here rather than as a line, because this test
        // is about what reaches the CHILD: the awkward string is stated
        // once and must arrive as one argument, whatever produced it.
        box.Declare(new TaskDecl("echo",
            [fettle, "search", "--root", box.Root, "--literal", "--json",
             "--glob", "subject.txt", awkward],
            null));

        Result<TaskRun> ran = await box.Bench.RunAsync("echo", TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.True(ran.IsOk, ran.Failure?.Message);
        Assert.Equal(0, ran.Value.ExitCode);

        JsonElement answer = JsonDocument.Parse(ran.Value.Stdout).RootElement;
        Assert.True(answer.GetProperty("ok").GetBoolean(), ran.Value.Stdout);

        string received = answer.GetProperty("patterns")[0].GetString()!;
        Assert.Equal(awkward, received);
        Assert.Equal(1, answer.GetProperty("hits_count").GetInt32());
    }

    [Fact]
    public void TasksAreListedFromTheConfiguration()
    {
        using var box = new Sandbox();
        box.Declare(
            new TaskDecl("build", ["pwsh", "-File", "build.ps1"], null),
            new TaskDecl("gate", ["node", "scripts/gate/driver.mjs", "gate", "--resume"], null));

        Result<IReadOnlyList<TaskDecl>> tasks = box.Bench.TaskList();

        Assert.True(tasks.IsOk, tasks.Failure?.Message);
        Assert.Equal(2, tasks.Value.Count);
        Assert.Equal("build", tasks.Value[0].Name);
        Assert.Equal(["pwsh", "-File", "build.ps1"], tasks.Value[0].Command);
        Assert.Equal(4, tasks.Value[1].Command.Count);
    }

    [Fact]
    public async Task AnUndeclaredTaskIsNotFoundAndNamesWhatIsDeclared()
    {
        using var box = new Sandbox();
        box.Declare(new TaskDecl("build", ["pwsh"], null));

        Result<TaskRun> ran = await box.Bench.RunAsync("gate", TimeSpan.Zero, CancellationToken.None);

        Assert.False(ran.IsOk);
        Assert.Equal(Outcome.NotFound, ran.Failure!.Outcome);
        Assert.Contains("build", ran.Failure.Message);
    }

    /// <summary>R7.5: a task that does not terminate is killable, with a
    /// timeout the caller sets.</summary>
    /// <summary>
    /// A child that runs a long time whatever its stdin does.
    ///
    /// <para>Note what will NOT serve here: <c>fettle serve</c> exits at
    /// once, because <see cref="Tasks"/> closes the child's standard
    /// input on purpose - a child that inherits an open stdin can block
    /// forever waiting on input nobody is going to send, and that is the
    /// hang R7.5 exists to prevent rather than to demonstrate.</para>
    /// </summary>
    static string[] LongRunning() => OperatingSystem.IsWindows()
        ? ["ping", "-n", "30", "127.0.0.1"]
        : ["sleep", "30"];

    [Fact]
    public async Task ATaskThatOutrunsItsTimeoutIsKilledAndSaidToHaveBeen()
    {
        using var box = new Sandbox(Runnable);
        box.Declare(new TaskDecl("forever", LongRunning(), null));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Result<TaskRun> ran = await box.Bench.RunAsync("forever", TimeSpan.FromSeconds(2), CancellationToken.None);
        clock.Stop();

        Assert.False(ran.IsOk);
        Assert.Equal(Outcome.TimedOut, ran.Failure!.Outcome);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "the timeout must actually kill it");
    }

    /// <summary>
    /// A child that says something and then does not finish, so a timeout
    /// has partial output to carry. Driven through the platform shell
    /// because the marker has to be exact: <c>ping</c>'s own banner is
    /// localised, and a test that reads it passes only in English.
    /// </summary>
    static string[] NoisyLongRunning() => OperatingSystem.IsWindows()
        ? ["cmd", "/c", "echo the-first-thing-it-said && ping -n 30 127.0.0.1 >NUL"]
        : ["sh", "-c", "echo the-first-thing-it-said; sleep 30"];

    /// <summary>
    /// A refusal that drops the evidence costs the whole diagnosis: the
    /// only record of WHERE a task hung is what it had said, and a hang is
    /// the most expensive thing there is to reproduce.
    /// </summary>
    [Fact]
    public async Task ATimedOutTaskCarriesWhatItHadSaidBeforeItWasKilled()
    {
        using var box = new Sandbox(Runnable);
        box.Declare(new TaskDecl("noisy", NoisyLongRunning(), null));

        Result<TaskRun> ran = await box.Bench.RunAsync("noisy", TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.False(ran.IsOk);
        Assert.Equal(Outcome.TimedOut, ran.Failure!.Outcome);
        Assert.Contains("the-first-thing-it-said", ran.Failure.Message);
    }

    /// <summary>
    /// The ceiling of <see cref="Tasks.MaxCapturedChars"/>, proven with a
    /// small one. Without it the CHILD decides how much memory this
    /// process commits, and a loop printing a line that grows each time
    /// produces output growing quadratically - which is what turned a
    /// bounded task into an unbounded wait.
    /// </summary>
    [Fact]
    public async Task AChildThatFloodsIsCappedAndTheDroppedAmountIsSaidOutLoud()
    {
        string? fettle = Fettle();
        if (fettle is null) return;

        using var box = new Sandbox(Runnable);

        var flood = new StringBuilder();
        for (int i = 0; i < 400; i++) flood.Append("needle on line ").Append(i).Append('\n');
        box.Write("flood.txt", flood.ToString());

        box.Declare(new TaskDecl("flood",
            [fettle, "search", "--root", box.Root, "--literal", "--json",
             "--glob", "flood.txt", "needle"],
            null));

        Result<TaskRun> ran = await box.Bench.RunAsync(
            "flood", TimeSpan.FromSeconds(60), CancellationToken.None, capture: 500);

        Assert.True(ran.IsOk, ran.Failure?.Message);

        // The cap, plus the sentence that says the cap was reached.
        Assert.True(ran.Value.Stdout.Length < 700,
            $"a capped capture must not carry the flood ({ran.Value.Stdout.Length} chars)");
        Assert.Contains("not captured", ran.Value.Stdout);

        // And the child still finished: the surplus is drained and
        // discarded, never left to fill the pipe and block it.
        Assert.False(ran.Value.TimedOut, "the child must not be blocked by the ceiling");
        Assert.Equal(0, ran.Value.ExitCode);
    }

    /// <summary>
    /// R3.10 and R3.11: a task can be cancelled mid-flight, and because
    /// <c>run</c> holds no lock, cancelling it is possible at all - on a
    /// blocking design the cancellation could not be delivered until the
    /// thing it cancels had already finished.
    /// </summary>
    [Fact]
    public async Task ATaskCanBeCancelledMidFlight()
    {
        using var box = new Sandbox(Runnable);
        box.Declare(new TaskDecl("forever", LongRunning(), null));

        using var cancelling = new CancellationTokenSource();
        Task<Result<TaskRun>> running = box.Bench.RunAsync("forever", TimeSpan.Zero, cancelling.Token);

        await Task.Delay(300);
        await cancelling.CancelAsync();

        Result<TaskRun> ran = await running.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(ran.IsOk, ran.Failure?.Message);
        Assert.True(ran.Value.Cancelled, "a cancelled task must report that it was cancelled");
    }

    // ---- R10.18: concurrent mutations do not interleave ----

    /// <summary>
    /// R3.11 and R5.8. Two edits to one file, issued together, produce
    /// one success and one staleness refusal - never two successes - and
    /// the file is left holding one whole result rather than a blend of
    /// both.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentEditsToOneFileYieldOneSuccessAndOneRefusal()
    {
        using var box = new Sandbox();
        box.Write("contended.txt", "original\n");

        string hash = box.Bench.Read(new ReadRequest(["contended.txt"])).Value[0].Hash;

        // Both carry the same starting hash, which is exactly the race:
        // both believe the file is what they last read.
        Task<Result<EditAnswer>> first = box.Bench.EditAsync(
            [new FileEdits("contended.txt", hash, [new Edit.Replace("original", "first")])], dryRun: false);

        Task<Result<EditAnswer>> second = box.Bench.EditAsync(
            [new FileEdits("contended.txt", hash, [new Edit.Replace("original", "second")])], dryRun: false);

        Result<EditAnswer>[] both = await Task.WhenAll(first, second);

        int succeeded = both.Count(r => r.IsOk);
        Assert.Equal(1, succeeded);

        Result<EditAnswer> refused = both.First(r => !r.IsOk);
        Assert.Equal(Outcome.Stale, refused.Failure!.Outcome);

        string left = box.ReadText("contended.txt");
        Assert.True(left is "first\n" or "second\n", $"the file must hold one whole result, not '{left}'");
    }

    /// <summary>R3.11: read-only operations are not serialized against
    /// each other, so a tree can be read from several places at once.</summary>
    [Fact]
    public async Task ManyReadsRunTogetherAndAllAnswer()
    {
        using var box = new Sandbox();
        for (int i = 0; i < 20; i++) box.Write($"f{i:00}.txt", $"content {i}\n");

        Task<Result<IReadOnlyList<ReadSlice>>>[] reads = [.. Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => box.Bench.Read(new ReadRequest([$"f{i:00}.txt"]))))];

        Result<IReadOnlyList<ReadSlice>>[] all = await Task.WhenAll(reads);

        Assert.All(all, r => Assert.True(r.IsOk, r.Failure?.Message));
        for (int i = 0; i < 20; i++)
            Assert.Equal($"content {i}\n", all[i].Value[0].Text);
    }
}
