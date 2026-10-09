using System.Diagnostics;
using System.Text;

namespace Fettler.Core;

/// <summary>One declared task: a name, the argument list it runs, and the
/// line it was declared as.</summary>
public sealed record TaskDecl(
    string Name, IReadOnlyList<string> Command, string? WorkingDirectory, string? Line = null)
{
    /// <summary>How the task reads in a listing: the line as declared,
    /// quotes and all. Rejoining the split words would lose the quoting
    /// that made an argument one argument, and a listing that cannot be
    /// compared with the file is a listing nobody can check.</summary>
    public string Display => Line ?? string.Join(' ', Command);
}

/// <summary>What running a task produced.</summary>
/// <param name="Command">The argument list that was actually launched,
/// with any placeholders already filled. Reported back because a
/// declaration can carry a value the caller did not choose and cannot
/// see: serve re-reads the configuration before every request, so the
/// value can change between the listing and the run, and echoing what
/// ran is the whole of the answer to which one was used.</param>
public sealed record TaskRun(
    string Name,
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    bool TimedOut,
    bool Cancelled,
    IReadOnlyList<string>? Command = null);

/// <summary>
/// The declared-task facility of R7, so builds, tests and gates can be
/// invoked without composing a shell command.
///
/// <para><b>No shell is involved, ever (R7.3).</b> The program is
/// launched with its arguments as a list, not as a string for an
/// interpreter to re-split. That single clause removes the entire
/// quoting-and-dialect failure class this tool exists to escape, and it
/// reaches back into the declaration: the command line is split ONCE, by
/// <see cref="Split"/>, under a grammar small enough to state in a
/// sentence - and what the child receives is the list that produced,
/// never a string for an interpreter to have an opinion about.</para>
///
/// <para><b>Tasks are declared in <c>.fettler.json</c></b>, beside the
/// trees, because what may run in a tree and what may be done to it are
/// one statement about one tree.</para>
///
/// <para><b>That configuration is trusted input, and R7.6 says so out
/// loud.</b> R8's containment guards paths; it does not and cannot guard
/// a command list, which is read from a file inside the very root Fettler
/// was pointed at. Whoever can write it chooses what a caller - a model
/// included - is able to execute. That is a deliberate position: the
/// alternative is an allowlist maintained outside the repository, which
/// puts the declaration somewhere the repository's own contributors
/// cannot change alongside the build it belongs to.</para>
/// </summary>
public static class Tasks
{
    /// <summary>The declared tasks, as the configuration gave them.</summary>
    public static Result<IReadOnlyList<TaskDecl>> Read(Roots roots) =>
        Result<IReadOnlyList<TaskDecl>>.Ok(roots.Tasks);

    /// <summary>
    /// The words of a declared command line, split once, here.
    ///
    /// <para><b>The whole grammar:</b> whitespace separates arguments; a
    /// double-quoted span is one argument or part of one, and the quotes
    /// are removed; two double quotes inside a quoted span are one literal
    /// quote. Nothing else is special - no single quotes, no variables, no
    /// globbing, no redirection, no operators.</para>
    ///
    /// <para><b>A backslash is never an escape.</b> It is an ordinary
    /// character, so <c>"C:\Program Files\pwsh.exe"</c> means what it looks
    /// like. Making it an escape would require every Windows path in the
    /// file to be doubled, and a path that has to be doubled is a path that
    /// will one day not be.</para>
    ///
    /// <para><b>The split happens once and the result is a list.</b> The
    /// program is launched with that list, never with a command line for
    /// something else to re-split, so the child receives exactly the words
    /// this method produced.</para>
    /// </summary>
    public static Result<IReadOnlyList<string>> Split(string line)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        bool quoted = false, started = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { word.Append('"'); i++; }
                else quoted = !quoted;
                started = true;
                continue;
            }

            if (!quoted && (c == ' ' || c == '\t'))
            {
                if (started) { words.Add(word.ToString()); word.Clear(); started = false; }
                continue;
            }

            word.Append(c);
            started = true;
        }

        if (quoted)
            return Result<IReadOnlyList<string>>.Fail(Outcome.Invalid,
                "the command has a double quote that is never closed");

        if (started) words.Add(word.ToString());
        return Result<IReadOnlyList<string>>.Ok(words);
    }

    /// <summary>
    /// The declared words with their placeholders filled in, one word at
    /// a time.
    ///
    /// <para><b>This runs AFTER <see cref="Split"/>, and that ordering is
    /// the whole of its safety.</b> Substituting into the command LINE
    /// and splitting afterwards would hand the grammar a value: a value
    /// of <c>two words</c> would silently become two arguments, and one
    /// carrying a quote would change the parse or fail as an unterminated
    /// span. That is precisely the quoting failure class the split-once
    /// rule exists to remove, re-entered through a side door. Filled into
    /// a word, a value is one argument whatever is in it.</para>
    ///
    /// <para><b>A placeholder may be part of a word.</b>
    /// <c>--message={note}</c> is one argument, and needs no rule of its
    /// own - it falls out of replacing within the word.</para>
    ///
    /// <para><b>One pass, no recursion.</b> A value containing a brace is
    /// inserted literally and never looked up again, so a replacement
    /// cannot assemble another placeholder and there is no cycle to
    /// check for.</para>
    ///
    /// <para><b>Only an IDENTIFIER between braces is a placeholder.</b>
    /// The grammar had no braces before this, so a command line carrying
    /// one was ordinary and correct - a PowerShell scriptblock, a jq
    /// filter, a format string. Reading every <c>{...}</c> as a name
    /// would turn each of those into an undeclared-name refusal and break
    /// configurations that were right the day before. So
    /// <c>{ $_.Name }</c> is text and <c>{feature-branch}</c> is a name,
    /// and the rule that separates them is stated in <see cref="IsName"/>
    /// rather than left to luck.</para>
    ///
    /// <para><b><c>{{</c> is a literal brace</b>, the same doubling
    /// <see cref="Split"/> already uses for a quote - for the case the
    /// identifier rule cannot cover, a command line that must carry the
    /// text <c>{feature-branch}</c> itself. A backslash is not an escape
    /// here either. A lone <c>}</c> is unremarkable and needs no
    /// doubling.</para>
    ///
    /// <para><b>An unresolved name is refused, never passed through.</b>
    /// Of the three things this could do - fill, refuse, pass through -
    /// passing through is the only one that SUCCEEDS at the wrong thing:
    /// <c>shoddy-branch.ps1 feature {feature-branch}</c> does not fail, it
    /// creates a branch called <c>{feature-branch}</c>.</para>
    /// </summary>
    public static Result<IReadOnlyList<string>> Fill(
        IReadOnlyList<string> words,
        IReadOnlyDictionary<string, string> replacements,
        string task,
        string file)
    {
        var filled = new List<string>(words.Count);

        foreach (string word in words)
        {
            Result<string> one = FillWord(word, replacements, task, file);
            if (!one.IsOk) return one.Carry<IReadOnlyList<string>>();
            filled.Add(one.Value);
        }

        return Result<IReadOnlyList<string>>.Ok(filled);
    }

    static Result<string> FillWord(
        string word, IReadOnlyDictionary<string, string> replacements, string task, string file)
    {
        // The overwhelmingly common word has no brace in it at all, and
        // rebuilding it character by character to discover that would be
        // work for nothing.
        if (!word.Contains('{')) return Result<string>.Ok(word);

        var built = new System.Text.StringBuilder();

        for (int i = 0; i < word.Length; i++)
        {
            char c = word[i];

            if (c != '{') { built.Append(c); continue; }

            if (i + 1 < word.Length && word[i + 1] == '{') { built.Append('{'); i++; continue; }

            int close = word.IndexOf('}', i + 1);

            // Not a name, or never closed: ordinary text, left exactly as
            // it was written. This is the clause that keeps every command
            // line that already contained a brace working unchanged.
            if (close < 0 || !IsName(word, i + 1, close))
            {
                built.Append(c);
                continue;
            }

            string name = word[(i + 1)..close];

            if (!replacements.TryGetValue(name, out string? value))
                return Result<string>.Fail(Outcome.Invalid,
                    replacements.Count == 0
                        ? $"task '{task}' uses {{{name}}}, and no \"replacements\" are declared. "
                          + $"Add a \"replacements\" object to {RootsFile.FileName}, or to "
                          + $"{RootsFile.LocalFileName} beside it for a value of your own"
                        : $"task '{task}' uses {{{name}}}, which is not declared; \"replacements\" has: "
                          + string.Join(", ", replacements.Keys.OrderBy(k => k, StringComparer.Ordinal)),
                    file);

            built.Append(value);
            i = close;
        }

        return Result<string>.Ok(built.ToString());
    }

    /// <summary>
    /// Whether <c>word[from..to]</c> is a name a replacement could be
    /// declared under: a letter, then letters, digits, dots, underscores
    /// or hyphens.
    ///
    /// <para>Deliberately narrow. Everything it excludes - a space, a
    /// dollar, a pipe, a colon, a quote - is something that appears in
    /// the braces people already write on command lines, and every one of
    /// those has to keep meaning what it meant.</para>
    ///
    /// <para><b>A letter has to come first, and that rule earns its
    /// keep:</b> it is what leaves <c>{0}</c> and <c>{1}</c> alone. A
    /// .NET format string is exactly the shape a digits-only name would
    /// claim, and one in a declared command would otherwise become an
    /// undeclared-name refusal.</para>
    ///
    /// <para>The same charset is required of a declared replacement's
    /// name, so a name that can be declared can always be referred
    /// to.</para>
    /// </summary>
    public static bool IsName(string word, int from, int to)
    {
        if (to <= from) return false;
        if (!char.IsAsciiLetter(word[from])) return false;

        for (int i = from + 1; i < to; i++)
        {
            char c = word[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '_' && c != '-') return false;
        }

        return true;
    }

    /// <summary>Whether a whole string is a usable replacement name.</summary>
    public static bool IsName(string name) => IsName(name, 0, name.Length);

    public static Result<TaskDecl> Find(Roots roots, string name)
    {
        Result<IReadOnlyList<TaskDecl>> all = Read(roots);
        if (!all.IsOk) return all.Carry<TaskDecl>();

        foreach (TaskDecl task in all.Value)
            if (task.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return Result<TaskDecl>.Ok(task);

        return Result<TaskDecl>.Fail(Outcome.NotFound,
            all.Value.Count == 0
                ? $"no task is declared; add a \"tasks\" object to {RootsFile.FileName}"
                : $"no task called '{name}'; {RootsFile.FileName} declares: {string.Join(", ", all.Value.Select(t => t.Name))}");
    }

    /// <summary>
    /// Run a declared task, capturing both streams and the exit code
    /// (R7.4) and killable by timeout or by cancellation (R7.5, R3.10).
    ///
    /// <para>Nothing here ever writes to the process's own stdout, which
    /// is what keeps R3.2's "stdout carries the protocol and nothing
    /// else" true while a child is running.</para>
    /// </summary>
    /// <summary>
    /// How much of a child's output is kept before the rest is dropped -
    /// and SAID to be dropped.
    ///
    /// <para><b>Without a ceiling the child decides how much memory this
    /// process commits</b>, which is the one thing a boundary may not let
    /// the other side choose. A script stuck in a loop that prints a line
    /// which grows each time produces output that grows QUADRATICALLY,
    /// and <c>ReadToEndAsync</c> will faithfully materialise all of it
    /// into one string, copy it into a writer, and copy it again on the
    /// way out. The timeout fires long before that finishes, so the run
    /// appears to hang for as long as the allocator takes - which is how
    /// a two-minute bound produced a thirty-minute wait.</para>
    ///
    /// <para>The same posture as <see cref="Archives.MaxTotalBytes"/> and
    /// <see cref="Ooxml.MaxLines"/>: refused and reported, never endured.
    /// The stream is still DRAINED past the cap - a child blocked on a
    /// full pipe is a different hang, and swapping one for the other
    /// would not be a fix.</para>
    /// </summary>
    public const int MaxCapturedChars = 8 * 1024 * 1024;

    /// <summary>
    /// How long a stream may still be draining once the process is gone.
    ///
    /// <para><b>A pipe reaches end-of-file when the last handle to its
    /// write end closes, and the child is not always the last holder.</b>
    /// A grandchild that outlived its parent - or was re-parented, and so
    /// is not in the tree <see cref="Kill"/> kills - keeps the inherited
    /// handle open, and a read waiting on that end never returns. Waiting
    /// on it forever means a bounded task with an unbounded runner, which
    /// is the bound not binding.</para>
    ///
    /// <para>Generous on purpose: once the process has exited there is at
    /// most a pipe buffer left to read, so nothing honest comes close to
    /// this. What it bounds is the case where nothing is coming.</para>
    /// </summary>
    static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>How many lines of a killed task's output the refusal
    /// carries, and how wide each may be. Both are needed: a runaway can
    /// produce twelve lines totalling a gigabyte, so bounding the count
    /// without the width bounds nothing.</summary>
    const int TailLines = 12;

    const int TailWidth = 200;

    public static async Task<Result<TaskRun>> RunAsync(
        Roots roots, string name, TimeSpan timeout, CancellationToken cancel,
        int capture = MaxCapturedChars)
    {
        Result<TaskDecl> found = Find(roots, name);
        if (!found.IsOk) return found.Carry<TaskRun>();
        TaskDecl task = found.Value;

        // B.8: the working directory decides. `execute` is never granted
        // by default in any tree, including the one the configuration
        // sits in, because that tree is writable by definition and the
        // configuration lives inside it - write-by-default plus
        // execute-by-default is arbitrary code execution, and it defeats
        // every other permission at once, since a scope protected from
        // `delete` means nothing to a task that can run `del`.
        //
        // R8.2 explicitly includes the working directory of a task.
        Result<ContainedPath> cwd = roots.Resolve(
            task.WorkingDirectory ?? $"{roots.Names[0]}:", Permission.Execute);

        if (!cwd.IsOk)
            return cwd.Failure!.Outcome == Outcome.Refused
                ? Result<TaskRun>.Fail(Outcome.Refused,
                    $"'{task.Name}' would run in a tree that does not allow execute. "
                    + "Add \"execute\" to that tree's or scope's \"can\" in "
                    + $"{RootsFile.FileName}. execute is never a default.",
                    cwd.Failure.Path)
                : cwd.Carry<TaskRun>();

        string workingDirectory = cwd.Value.Full;

        var info = new ProcessStartInfo
        {
            FileName = task.Command[0],
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            // The clause that removes the quoting failure class: an
            // argument list, never a command string for something else
            // to re-split.
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        for (int i = 1; i < task.Command.Count; i++) info.ArgumentList.Add(task.Command[i]);

        using var process = new Process { StartInfo = info };
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Result<TaskRun>.Fail(Outcome.NotFound,
                $"could not start '{task.Command[0]}': {e.Message}");
        }

        // A child that inherits an open stdin can block forever waiting
        // on input nobody is going to send.
        process.StandardInput.Close();

        // Bounded, not ReadToEndAsync: see MaxCapturedChars. Both streams
        // are read concurrently and neither is ever left unread, because a
        // child whose pipe fills blocks forever writing to it.
        Task<string> readOut = Drain(process.StandardOutput, capture);
        Task<string> readErr = Drain(process.StandardError, capture);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        if (timeout > TimeSpan.Zero) deadline.CancelAfter(timeout);

        bool timedOut = false, cancelled = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancel.IsCancellationRequested;
            cancelled = cancel.IsCancellationRequested;
            Kill(process);
        }

        stdout.Write(await Settled(readOut, DrainGrace).ConfigureAwait(false));
        stderr.Write(await Settled(readErr, DrainGrace).ConfigureAwait(false));
        stopwatch.Stop();

        int exit = timedOut || cancelled ? -1 : SafeExitCode(process);

        var run = new TaskRun(task.Name, exit, stdout.ToString(), stderr.ToString(),
            stopwatch.Elapsed, timedOut, cancelled, task.Command);

        // WHAT IT HAD SAID GOES WITH THE REFUSAL. A timeout used to answer
        // with the message alone and drop `run` on the floor, so the one
        // thing that says WHERE a task hung - its own last output - was
        // captured, held, and then thrown away at the door. Diagnosing a
        // hang then meant reproducing it, which for a hang is the whole
        // cost again.
        if (timedOut)
            return Result<TaskRun>.Fail(new Failure(Outcome.TimedOut,
                $"'{task.Name}' did not finish within {timeout.TotalSeconds:0}s and was killed."
                + Tail(run)));

        return Result<TaskRun>.Ok(run);
    }

    /// <summary>The end of what a killed task had said, folded into the
    /// refusal - clipped in both directions, because the output of a task
    /// that had to be killed is exactly the output not to trust the shape
    /// of.</summary>
    static string Tail(TaskRun run)
    {
        string said = run.Stderr.Length > 0 ? run.Stderr : run.Stdout;
        if (said.Length == 0) return " It printed nothing.";

        string[] lines = said.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        int from = Math.Max(0, lines.Length - TailLines);

        var shown = new StringBuilder(" Its last output:");
        for (int i = from; i < lines.Length; i++)
        {
            string line = lines[i];
            shown.Append('\n')
                 .Append(line.Length <= TailWidth ? line : line[..TailWidth] + " [...]");
        }

        return shown.ToString();
    }

    /// <summary>
    /// Read a stream to its end, keeping at most <paramref name="cap"/>
    /// characters of it.
    ///
    /// <para><b>Reading continues past the cap and the surplus is
    /// counted.</b> Stopping the read would fill the pipe and block the
    /// child on its next write, trading a memory problem for a deadlock.
    /// The count is reported rather than the truncation being silent: a
    /// capped answer that looked complete would be a worse failure than
    /// the one being fixed.</para>
    /// </summary>
    static async Task<string> Drain(StreamReader reader, int cap)
    {
        var kept = new StringBuilder();
        char[] buffer = new char[16 * 1024];
        long dropped = 0;

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException
                                          or ObjectDisposedException)
            {
                break;
            }

            if (read == 0) break;

            int room = Math.Max(0, cap - kept.Length);
            int take = Math.Min(read, room);
            if (take > 0) kept.Append(buffer, 0, take);
            dropped += read - take;
        }

        if (dropped > 0)
            kept.Append($"\n[{dropped:N0} more characters were not captured; ")
                .Append($"the limit is {cap:N0}]\n");

        return kept.ToString();
    }

    /// <summary>A task killed mid-flight leaves its whole process tree
    /// behind unless the tree is what gets killed.</summary>
    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>What a stream had, given a grace period to finish - see
    /// <see cref="DrainGrace"/> for why waiting on it indefinitely is not
    /// an option.</summary>
    static async Task<string> Settled(Task<string> reading, TimeSpan grace)
    {
        try
        {
            if (await Task.WhenAny(reading, Task.Delay(grace)).ConfigureAwait(false) != reading)
                return string.Empty;

            return await reading.ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { return string.Empty; }
    }

    static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }
}
