using System.Text;
using System.Text.Json;
using Fettler.Cli;
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

    static string Flood(int lines)
    {
        var flood = new StringBuilder();
        for (int i = 0; i < lines; i++) flood.Append("needle on line ").Append(i).Append('\n');
        return flood.ToString();
    }

    /// <summary>A child that prints one file to stdout and then the same
    /// file to stderr, through the platform shell because nothing else
    /// writes a known amount to both streams.</summary>
    static string[] BothStreams(string file) => OperatingSystem.IsWindows()
        ? ["cmd", "/c", $"type {file} & type {file} 1>&2"]
        : ["sh", "-c", $"cat {file}; cat {file} >&2"];

    /// <summary>
    /// The bound the core keeps, <see cref="Budget.Ceiling"/> characters
    /// across both streams, proven with a small one.
    ///
    /// <para>The END is kept, because that is where a build or a test run
    /// says what went wrong. What was dropped from the front is counted
    /// exactly, because a cut answer that read as a whole one would be a
    /// worse failure than the one the cut prevents.</para>
    /// </summary>
    [Fact]
    public async Task AChildThatFloodsKeepsItsTailAndCountsWhatWasLeftOut()
    {
        string? fettle = Fettle();
        if (fettle is null) return;

        using var box = new Sandbox(Runnable);
        box.Write("flood.txt", Flood(400));

        box.Declare(new TaskDecl("flood",
            [fettle, "search", "--root", box.Root, "--literal", "--json",
             "--glob", "flood.txt", "needle"],
            null));

        Result<TaskRun> whole = await box.Bench.RunAsync(
            "flood", TimeSpan.FromSeconds(60), CancellationToken.None);
        Result<TaskRun> capped = await box.Bench.RunAsync(
            "flood", TimeSpan.FromSeconds(60), CancellationToken.None, capture: 500);

        Assert.True(whole.IsOk, whole.Failure?.Message);
        Assert.True(capped.IsOk, capped.Failure?.Message);

        // Under the limit the output is the output, and says so.
        Assert.False(whole.Value.Truncated);
        Assert.Equal(0L, whole.Value.StdoutOmitted);

        string all = whole.Value.Stdout;
        TaskRun run = capped.Value;

        Assert.True(run.Truncated);
        Assert.Equal(500, run.Stdout.Length);
        Assert.EndsWith(run.Stdout, all);
        Assert.Equal(all.Length - 500L, run.StdoutOmitted);

        // And the child still finished: the surplus is drained and
        // discarded, never left to fill the pipe and block it.
        Assert.False(run.TimedOut, "the child must not be blocked by the ceiling");
        Assert.Equal(0, run.ExitCode);
    }

    /// <summary>One limit for the call, as <c>read</c> has one limit
    /// across all its paths: two streams that both flood get half
    /// each.</summary>
    [Fact]
    public async Task StdoutAndStderrShareOneLimit()
    {
        using var box = new Sandbox(Runnable);
        string flood = Flood(400);
        box.Write("flood.txt", flood);
        box.Declare(new TaskDecl("both", BothStreams("flood.txt"), null));

        Result<TaskRun> ran = await box.Bench.RunAsync(
            "both", TimeSpan.FromSeconds(60), CancellationToken.None, capture: 1000);

        Assert.True(ran.IsOk, ran.Failure?.Message);
        TaskRun run = ran.Value;

        Assert.True(run.Truncated);
        Assert.Equal(500, run.Stdout.Length);
        Assert.Equal(500, run.Stderr.Length);
        Assert.EndsWith(run.Stdout, flood);
        Assert.EndsWith(run.Stderr, flood);
        Assert.Equal(flood.Length - 500L, run.StdoutOmitted);
        Assert.Equal(flood.Length - 500L, run.StderrOmitted);
    }

    /// <summary>Half each is the floor, not the share: a stream that
    /// needs less than half hands the rest to the other.</summary>
    [Fact]
    public void AStreamThatNeedsLessGivesTheRestToTheOther()
    {
        (Captured output, Captured error) = Tasks.Share(
            new Captured("short", 0), new Captured(new string('e', 2000), 0), 1000);

        Assert.Equal("short", output.Text);
        Assert.Equal(0L, output.Omitted);
        Assert.Equal(995, error.Text.Length);
        Assert.Equal(1005L, error.Omitted);
    }

    /// <summary>
    /// The machine-readable answer and the terminal's report the same
    /// facts, because the cut is made once, before either is written.
    /// </summary>
    [Fact]
    public async Task BothOutputModesReportTheSameCut()
    {
        using var box = new Sandbox(Runnable);
        string flood = Flood(3000);
        box.Write("flood.txt", flood);
        box.Declare(new TaskDecl("both", BothStreams("flood.txt"), null));

        Assert.True(flood.Length > Budget.Ceiling, "the flood must be larger than the ceiling");

        CliResult json = await Command.RunAsync(["run", "both", "--json"], new StringReader(string.Empty), box.Bench);
        JsonElement answer = JsonDocument.Parse(json.Stdout).RootElement;

        long omitted = answer.GetProperty("stdout_omitted").GetInt64();
        long errorOmitted = answer.GetProperty("stderr_omitted").GetInt64();
        string kept = answer.GetProperty("stdout").GetString()!;

        Assert.True(answer.GetProperty("truncated").GetBoolean(), json.Stdout);
        Assert.Equal(flood.Length - (long)kept.Length, omitted);
        Assert.Equal(flood.Length - (long)answer.GetProperty("stderr").GetString()!.Length, errorOmitted);
        Assert.EndsWith(kept, flood);
        Assert.True(Encoding.UTF8.GetByteCount(json.Stdout) <= Budget.Ceiling);

        // Each stream is owed half the room. When the room is an odd number
        // of bytes, one of them keeps one character more.
        Assert.InRange(errorOmitted - omitted, -1, 1);

        CliResult text = await Command.RunAsync(["run", "both"], new StringReader(string.Empty), box.Bench);

        Assert.Contains($"{omitted:N0} characters of stdout and {errorOmitted:N0} of stderr", text.Stdout);
        Assert.Contains($"{Budget.Ceiling:N0} bytes", text.Stdout);
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
