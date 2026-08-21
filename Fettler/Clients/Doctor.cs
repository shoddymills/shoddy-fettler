using System.Text.Json;
using Fettler.Core;

namespace Fettler.Clients;

/// <summary>How one client at one level stands.</summary>
public enum Verdict
{
    /// <summary>The client is not installed, or has no configuration at
    /// this level. Never a failure - Claude Desktop has no project scope
    /// at all, and reporting that as broken would train a reader to
    /// ignore the report.</summary>
    NotApplicable,

    /// <summary>The client is here; Fettler is not registered with it.</summary>
    Absent,

    /// <summary>Registered, and wrong: the binary is missing, the
    /// configuration will not parse, a tree does not exist.</summary>
    Broken,

    /// <summary>Registered, launchable, and the trees open.</summary>
    Healthy,
}

public sealed record ClientReport(string Client, Level Level, Verdict Verdict, string Detail, string? Where);

/// <summary>One thing letting the assistant go round the boundary.</summary>
public sealed record Finding(string Check, bool Serious, string Message, string? Where);

/// <summary>Which binary answered, and whether anything else would.</summary>
public sealed record Installation(string Binary, string Version, bool OnPath, string? PathBinary);

public sealed record Diagnosis(
    Installation Install,
    IReadOnlyList<ClientReport> Clients,
    IReadOnlyList<Finding> Findings)
{
    public bool AnyBroken => Clients.Any(c => c.Verdict == Verdict.Broken);

    public bool AnyWarning => Clients.Any(c => c.Verdict == Verdict.Absent) || Findings.Count > 0;
}

/// <summary>
/// Two jobs in one verb: is Fettler wired into each client, and is
/// anything letting the model go round it.
///
/// <para><b>Both levels, every client, every run.</b> Narrowing the
/// REPORT is offered; narrowing the SCAN is not, because "it looked fine"
/// must never be able to mean "it only looked at one".</para>
///
/// <para><b>The doctor never writes.</b> Diagnosis and repair are
/// separate verbs, so a check run in a session hook can never change a
/// machine. Repair is <see cref="Scaffold"/>.</para>
///
/// <para><b>It never prints a secret.</b> <c>~/.claude/.credentials.json</c>
/// sits in the same directory as files this reads. Doctor names files and
/// keys and never values, and the test for that uses a fixture with a
/// credentials file in it.</para>
/// </summary>
public static class Doctor
{
    public static Diagnosis Examine(Machine machine, Roots? roots, string version)
    {
        var clients = new List<ClientReport>();
        var findings = new List<Finding>();

        Installation install = WhichBinary(version);

        foreach (string client in Places.Clients)
            foreach (Level level in new[] { Level.Global, Level.Repo })
                clients.Add(Look(machine, client, level, install));

        Overrides(machine, roots, findings);
        Declarations(roots, findings);

        return new Diagnosis(install, clients, findings);
    }

    // ---- 5.2: what answered, and whether anything else would ----

    static Installation WhichBinary(string version)
    {
        string running = Environment.ProcessPath ?? "(unknown)";
        string? onPath = Resolve("fettle");

        return new Installation(running, version, onPath is not null, onPath);
    }

    /// <summary>
    /// Find a command the way a client's launcher would: by walking PATH
    /// and, on Windows, PATHEXT.
    ///
    /// <para>This is check 2.12, and it was live here: every registration
    /// in the documentation launched a bare <c>fettle</c>, and nothing on
    /// this machine resolved it. A server that cannot start reports as
    /// registered from every direction but one.</para>
    /// </summary>
    public static string? Resolve(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains('/'))
            return File.Exists(command) ? Path.GetFullPath(command) : null;

        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (string extension in extensions)
            {
                string candidate;
                try { candidate = Path.Combine(directory, command + extension); }
                catch (ArgumentException) { continue; }

                if (File.Exists(candidate)) return candidate;
            }

        return null;
    }

    // ---- the first job: is Fettler wired in ----

    static ClientReport Look(Machine machine, string client, Level level, Installation install)
    {
        IReadOnlyList<Place> places = [.. Places.All(machine).Where(p => p.Client == client && p.Level == level)];

        if (places.Count == 0)
            return new ClientReport(client, level, Verdict.NotApplicable,
                client == Places.ClaudeDesktop && level == Level.Repo
                    ? "this client has no project scope, so there is nothing to configure here"
                    : "this client has nothing at this level",
                null);

        Place? servers = places.FirstOrDefault(p => p.Purpose == Purpose.Servers);

        if (servers is null)
            return new ClientReport(client, level, Verdict.NotApplicable,
                "this client takes its servers from another level", null);

        if (!File.Exists(servers.Path))
            return new ClientReport(client, level, Verdict.NotApplicable,
                "not configured on this machine at this level", servers.Path);

        Result<JsonDocument> read = ReadJson(machine, servers.Path);
        if (!read.IsOk)
            return new ClientReport(client, level, Verdict.Broken, read.Failure!.Message, servers.Path);

        using JsonDocument doc = read.Value;

        if (!Registration(doc.RootElement, out JsonElement entry))
            return new ClientReport(client, level, Verdict.Absent,
                $"no server called '{Places.ServerName}' is registered here", servers.Path);

        return Judge(client, level, entry, servers.Path, install);
    }

    /// <summary>
    /// Find Fettler's registration wherever this client keeps it.
    ///
    /// <para>Three shapes, because the clients genuinely differ:
    /// <c>mcpServers</c> is Claude's, <c>servers</c> is VS Code's, and
    /// Claude Code also keeps a per-project copy under <c>projects</c>.
    /// Looking in all of them costs nothing and means a registration is
    /// never reported absent because it was written in the other
    /// place.</para>
    /// </summary>
    static bool Registration(JsonElement root, out JsonElement entry)
    {
        entry = default;
        if (root.ValueKind != JsonValueKind.Object) return false;

        foreach (string holder in new[] { "mcpServers", "servers" })
            if (root.TryGetProperty(holder, out JsonElement servers)
                && servers.ValueKind == JsonValueKind.Object
                && servers.TryGetProperty(Places.ServerName, out entry))
                return true;

        if (root.TryGetProperty("projects", out JsonElement projects)
            && projects.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty project in projects.EnumerateObject())
                if (project.Value.ValueKind == JsonValueKind.Object
                    && project.Value.TryGetProperty("mcpServers", out JsonElement inProject)
                    && inProject.ValueKind == JsonValueKind.Object
                    && inProject.TryGetProperty(Places.ServerName, out entry))
                    return true;

        return false;
    }

    static ClientReport Judge(string client, Level level, JsonElement entry, string where, Installation install)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("command", out JsonElement command)
            || command.ValueKind != JsonValueKind.String)
            return new ClientReport(client, level, Verdict.Broken,
                "the registration has no command to launch", where);

        string named = command.GetString()!;
        string? resolved = Resolve(named);

        if (resolved is null)
            return new ClientReport(client, level, Verdict.Broken,
                $"the registration launches '{named}', which resolves to nothing on this machine"
                + (install.OnPath ? "" : " - and fettle is not on PATH at all"),
                where);

        return new ClientReport(client, level, Verdict.Healthy,
            $"registered and launchable as {resolved}", where);
    }

    // ---- the second job: what lets the model go round it ----

    /// <summary>
    /// A client can be perfectly registered and never used, which is
    /// exactly what happened in the session that produced this design.
    /// Registering Fettler makes it AVAILABLE; only a deny makes it the
    /// ROUTE.
    /// </summary>
    static void Overrides(Machine machine, Roots? roots, List<Finding> findings)
    {
        foreach (Place place in Places.All(machine))
        {
            if (!File.Exists(place.Path)) continue;

            if (place.Purpose == Purpose.Policy) Policy(machine, place, roots, findings);
            if (place.Purpose == Purpose.Instructions) Instructions(place, findings);
            if (place.Purpose == Purpose.Servers) Servers(machine, place, findings);
        }

        // 2.13: a personal settings file never appears in review, so a
        // permission granted there is invisible to everybody else.
        foreach (Place place in Places.All(machine))
            if (place.Path.EndsWith("settings.local.json", StringComparison.Ordinal) && File.Exists(place.Path))
                findings.Add(new Finding("2.13", false,
                    "a personal settings file is present; anything granted there is uncommitted "
                    + "and invisible to everyone else on the project", place.Path));

        // 2.15: the assistant's own scratch directory, which nothing here
        // granted - the harness gives it - so work staged there is invisible
        // to this boundary.
        //
        // It matters most at the moment the deny list lands. With the
        // built-in Write denied and no tree covering the scratch, an
        // assistant has nowhere to stage anything at all, and learns that at
        // its first write rather than while running setup. Reporting it
        // BEFORE the deny bites is the whole value of the check.
        // Satisfied by a tree INSIDE the scratch root as well as by one
        // containing it, because the advice is to declare the per-project
        // folder rather than the lot. A check that cannot be cleared by
        // doing what it says is a check people learn to scroll past.
        if (roots is not null && ScratchRoot() is { } scratch
            && !InSomeTree(roots, scratch) && !HasTreeInside(roots, scratch))
            findings.Add(new Finding("2.15", false,
                "an assistant scratch directory sits outside every declared tree, so work staged "
                + "there is invisible to this boundary - and once the built-in Write is denied, "
                + "nothing can write there at all. Declare the per-project folder inside it as a "
                + $"tree in {RootsFile.LocalFileName}, which is gitignored: "
                + "{\"trees\":{\"scratch\":{\"path\":\"<that folder>\",\"can\":"
                + "[\"list\",\"read\",\"create\",\"update\",\"rename\",\"delete\"]}}}. "
                + "Declare the project folder rather than this one, which holds every project's; "
                + "and note that this tool does not write that file and setup cannot either - "
                + "granting a tree is a person's act",
                scratch));
    }

    /// <summary>
    /// Where an assistant stages files, if it is there at all.
    ///
    /// <para>A well-known directory under the system temp, read the same way
    /// the client-configuration paths are: a compiled-in location, probed
    /// for existence and nothing else. Absent means absent - no finding, and
    /// no guess about what a harness might have done instead.</para>
    /// </summary>
    static string? ScratchRoot()
    {
        try
        {
            string at = Path.Combine(Path.GetTempPath(), "claude");
            return Directory.Exists(at) ? Path.GetFullPath(at) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Shell commands that write files. Not a blocklist - a
    /// blocklist for this cannot be finished, because <c>echo x &gt; f</c>
    /// defeats every list that could be written. These are the ones worth
    /// REPORTING, because each is a pre-approved route round the boundary
    /// that runs without a prompt.</summary>
    static readonly string[] Mutating =
    [
        "sed", "cp", "mv", "rm", "tee", "dd", "install", "ln", "truncate", "patch", "touch",
        "git checkout", "git restore", "git clean", "git apply",
        "Set-Content", "Add-Content", "Out-File", "Copy-Item", "Move-Item", "Remove-Item",
        "New-Item", "Rename-Item", "xcopy", "robocopy",
        ">", ">>",
    ];

    /// <summary>The built-in tools Fettler replaces. Short, closed, and
    /// identical on every platform - which is what makes this half of the
    /// exclusion list winnable where the shell half is not.</summary>
    public static readonly string[] Replaced =
        ["Read", "Write", "Edit", "NotebookEdit", "Grep", "Glob"];

    /// <summary>The shell tools. Fettler replaces no shell and never
    /// will, which is why these are not on <see cref="Replaced"/> - but a
    /// shell is a complete route round the boundary, and <c>node x.js</c>
    /// reaches it while being none of <see cref="Mutating"/>.
    ///
    /// <para><b>A shell under another name is still a shell.</b>
    /// <c>Monitor</c> is here because its own description says it runs
    /// the <c>command</c> it is given "in the same shell environment as
    /// Bash". Denying Bash and leaving that open denies the word and not
    /// the thing, and it was found wide open on a machine where every
    /// other route was shut - which is the argument for 2.18 as much as
    /// for this line.</para>
    ///
    /// <para>Denying these NAMES is as short, closed and
    /// platform-identical as denying the six. What cannot be finished is
    /// enumerating the commands behind them, and that is the ALLOW list's
    /// job, not this one's: deny the shell, then allow back the few
    /// commands a project actually needs. Leaving the deny out because
    /// the allow cannot be generated left every project reporting healthy
    /// with the shell wide open.</para></summary>
    public static readonly string[] Shell = ["Bash", "PowerShell", "Monitor"];

    /// <summary>
    /// Tools that hand back bytes produced somewhere else: the output of
    /// a command already run, a task already finished.
    ///
    /// <para>They start nothing and write nothing, which is exactly why
    /// they were missed - neither an editor nor a shell. But content
    /// arriving this way reached the model without passing a single check
    /// this tool makes: not the tree boundary, not the secret scan, not
    /// the disclosure screen. <c>TaskOutput</c> settles it by naming its
    /// own alternative: it recommends <c>Read</c> on the task's output
    /// file, so it is a stand-in for a tool that is already denied.</para>
    /// </summary>
    public static readonly string[] Indirect = ["BashOutput", "TaskOutput"];

    /// <summary>Everything <c>setup</c> denies and 2.8 expects to find
    /// denied: the tools Fettler replaces, the shells it does not, and
    /// the ones that carry another tool's output back.</summary>
    public static readonly string[] Denied = [.. Replaced, .. Shell, .. Indirect];

    /// <summary>
    /// Tools that start work of their own, which then runs tools of its
    /// own.
    ///
    /// <para>Whether this boundary reaches that work is the client's to
    /// decide and cannot be observed from here - the same limit B.17
    /// runs into - so these are CLASSIFIED and not denied. Denying them
    /// would remove a capability people legitimately use, on a guess
    /// about another program's rule resolution.</para>
    ///
    /// <para>They are on this list so 2.18 knows them. A tool this file
    /// has never heard of is the finding; a tool it has heard of and
    /// reached a decision about is not.</para>
    /// </summary>
    public static readonly string[] Delegating =
        ["Task", "Agent", "Workflow", "SendMessage", "RemoteTrigger", "Skill", "SlashCommand"];

    /// <summary>
    /// The rest of the inventory: tools that reach no file and carry no
    /// file's content back, so nothing here asks for them to be denied.
    ///
    /// <para><c>Artifact</c> is the awkward one and is named here on
    /// purpose. It publishes a local file outward rather than fetching
    /// anything in, so it is egress and not a route round the READ
    /// boundary. Egress is a boundary this tool has never claimed to
    /// govern; saying so in the list is better than leaving it off and
    /// having 2.18 report it as a surprise every run.</para>
    /// </summary>
    public static readonly string[] Outside =
    [
        "Artifact", "AskUserQuestion", "TodoWrite", "ToolSearch", "WebFetch", "WebSearch",
        "EnterPlanMode", "ExitPlanMode", "EnterWorktree", "ExitWorktree",
        "CronCreate", "CronDelete", "CronList", "ScheduleWakeup", "PushNotification",
        "KillShell", "TaskStop", "DesignSync", "ReportFindings",
        "ListMcpResourcesTool", "ReadMcpResourceTool", "ReadMcpResourceDirTool",
    ];

    /// <summary>Every tool name this fettle has an opinion about, whether
    /// that opinion is deny, classify or leave alone. What is NOT on it
    /// is what 2.18 reports.</summary>
    public static readonly string[] Known = [.. Denied, .. Delegating, .. Outside];

    /// <summary>
    /// The day the inventory above was last drawn against a real client.
    ///
    /// <para><b>A closed list against a client that keeps growing goes
    /// stale, and the staleness is the dangerous part, because a list
    /// that has fallen behind reports exactly as clean as one that has
    /// not.</b> So the date is printed with every report: a reader can
    /// see how old the list is instead of assuming it is current, and
    /// 2.18 turns "the client has a tool this does not know about" from
    /// silence into a finding.</para>
    /// </summary>
    public const string InventoryOf = "2026-08-20";

    static void Policy(Machine machine, Place place, Roots? roots, List<Finding> findings)
    {
        Result<JsonDocument> read = ReadJson(machine, place.Path);
        if (!read.IsOk)
        {
            findings.Add(new Finding("2.x", true, read.Failure!.Message, place.Path));
            return;
        }

        using JsonDocument doc = read.Value;
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

        JsonElement permissions = doc.RootElement.TryGetProperty("permissions", out JsonElement p)
            ? p : default;

        var allowed = new List<string>();
        var denied = new List<string>();

        if (permissions.ValueKind == JsonValueKind.Object)
        {
            Collect(permissions, "allow", allowed);
            Collect(permissions, "deny", denied);

            // 2.9: two boundaries that disagree means the narrower one is
            // decorative.
            if (permissions.TryGetProperty("additionalDirectories", out JsonElement extra)
                && extra.ValueKind == JsonValueKind.Array && roots is not null)
                foreach (JsonElement directory in extra.EnumerateArray())
                    if (directory.ValueKind == JsonValueKind.String
                        && !InSomeTree(roots, directory.GetString()!))
                        findings.Add(new Finding("2.9", false,
                            $"additionalDirectories names '{directory.GetString()}', which is outside "
                            + "every declared tree; the wider boundary is the one that applies",
                            place.Path));
        }

        // 2.6: shell commands that mutate files, pre-approved.
        var shell = new List<string>();
        foreach (string entry in allowed)
            if (entry.StartsWith("Bash(", StringComparison.Ordinal)
                || entry.StartsWith("PowerShell(", StringComparison.Ordinal))
                foreach (string command in Mutating)
                    if (entry.Contains(command, StringComparison.OrdinalIgnoreCase)) { shell.Add(entry); break; }

        if (shell.Count > 0)
            findings.Add(new Finding("2.6", true,
                $"{shell.Count} allow entr{(shell.Count == 1 ? "y" : "ies")} pre-approve a shell command that "
                + $"writes files, so each runs without a prompt: {string.Join(", ", shell.Take(5))}"
                + (shell.Count > 5 ? $", and {shell.Count - 5} more" : ""),
                place.Path));

        // 2.7: an allow on the built-in editors or reader removes the
        // last friction from the habit.
        var builtIn = allowed.Where(a => Replaced.Any(r =>
            a.Equals(r, StringComparison.Ordinal) || a.StartsWith(r + "(", StringComparison.Ordinal))).ToList();

        if (builtIn.Count > 0)
            findings.Add(new Finding("2.7", true,
                $"the built-in tools Fettler replaces are explicitly allowed: {string.Join(", ", builtIn)}. "
                + "Read in particular defeats containment, the read permission and hidden scopes at once",
                place.Path));

        // 2.8: registering Fettler makes it available; only a deny makes
        // it the route. Both lists, because a shell left open is a route
        // round the boundary exactly as an undenied Write is.
        var missing = Denied.Where(r => !denied.Any(d =>
            d.Equals(r, StringComparison.Ordinal) || d.StartsWith(r + "(", StringComparison.Ordinal))).ToList();

        if (missing.Count > 0 && place.Level == Level.Repo)
            findings.Add(new Finding("2.8", false,
                $"nothing denies the built-in {string.Join(", ", missing)}, so Fettler is an option "
                + "rather than the route; `fettle setup claude-code --local` writes the denies",
                place.Path));

        // 2.18: 2.8 can only check the tools this fettle has heard of,
        // and the client keeps growing new ones. Where the configuration
        // names a tool the inventory does not know, say so - because the
        // alternative is 2.8 reporting clean about a list it never looked
        // at. Rules on another server's tools are that server's business
        // and 2.17's, not this list's.
        var named = new List<string>();
        if (permissions.ValueKind == JsonValueKind.Object) Collect(permissions, "ask", named);

        var unknown = allowed.Concat(denied).Concat(named)
            .Select(Tool)
            .Where(t => t.Length > 0 && !t.StartsWith("mcp__", StringComparison.Ordinal))
            .Where(t => !Known.Contains(t, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unknown.Count > 0)
            findings.Add(new Finding("2.18", false,
                $"this names {string.Join(", ", unknown)}, which is not in the tool inventory this "
                + $"fettle was built with (drawn {InventoryOf}). Check 2.8 therefore said nothing "
                + "about it either way - not that it is safe. A newer fettle may know it; until "
                + "then, decide by hand whether it reads files, writes them, or runs a shell",
                place.Path));

        // B.17. The question that decides whether any of this works is
        // whether deny beats allow, and it was tempting to design around
        // an assumption.
        //
        // What can honestly be determined from here is the CONFLICT, not
        // the precedence: Fettler is a separate process and cannot
        // observe another program's rule resolution without running it.
        // So the conflict is reported and the fix named is the one that
        // is right under EITHER precedence - remove the allow - rather
        // than a deny that may or may not win. An answer that does not
        // depend on the assumption is better than a guess at it.
        // A NARROWED allow on a shell whose bare name is denied is the
        // intended shape rather than a conflict - `deny: Bash` closes the
        // shell and `allow: Bash(git status:*)` reopens the two or three
        // commands the project needs. Reporting that as contested would
        // name "remove the allow" as the fix, which is the one change
        // that breaks the configuration this tool now writes. No such
        // exemption for the replaced tools: a narrow allow on Read is
        // still a hole, because reading is precisely what Fettler does.
        var contested = allowed
            .Where(a => denied.Any(d => Same(a, d)))
            .Where(a => !(Shell.Contains(Tool(a), StringComparer.Ordinal)
                          && a.Length > Tool(a).Length))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (contested.Count > 0)
            findings.Add(new Finding("B.17", true,
                $"{string.Join(", ", contested)} appear in BOTH allow and deny. Which wins is the "
                + "client's to decide and cannot be determined from here, so do not rely on the "
                + "deny: remove the allow, which is correct either way",
                place.Path));

        // 2.14: auto-approval reinstates every route the boundary removed.
        if (doc.RootElement.TryGetProperty("chat.tools.autoApprove", out JsonElement auto)
            && auto.ValueKind == JsonValueKind.True)
            findings.Add(new Finding("2.14", true,
                "chat.tools.autoApprove is on, which approves arbitrary tools without asking and "
                + "reinstates every route the boundary removes", place.Path));
    }

    /// <summary>Whether two rules name the same tool, comparing the tool
    /// and not the argument: <c>Read</c> and <c>Read(//c/x/**)</c> are
    /// the same tool granted at different widths.</summary>
    static bool Same(string one, string other) =>
        Tool(one).Equals(Tool(other), StringComparison.Ordinal);

    static string Tool(string rule)
    {
        int bracket = rule.IndexOf('(');
        return bracket < 0 ? rule : rule[..bracket];
    }

    static void Collect(JsonElement permissions, string key, List<string> into)
    {
        if (!permissions.TryGetProperty(key, out JsonElement list) || list.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement entry in list.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.String) into.Add(entry.GetString()!);
    }

    static bool InSomeTree(Roots roots, string directory)
    {
        foreach (string name in roots.Names)
            if (Roots.IsWithin(roots.PathOf(name), directory)) return true;
        return false;
    }

    /// <summary>Whether any declared tree lies inside a directory - the
    /// question <see cref="InSomeTree"/> asks, the other way round.</summary>
    static bool HasTreeInside(Roots roots, string directory)
    {
        foreach (string name in roots.Names)
            if (Roots.IsWithin(directory, roots.PathOf(name))) return true;
        return false;
    }

    /// <summary>The shells whose command line can carry a script for them
    /// to parse, by the name the program is launched under.</summary>
    static readonly string[] Shells = ["pwsh", "powershell", "bash", "sh", "zsh", "cmd"];

    /// <summary>The flags that hand one of them a string to interpret,
    /// rather than a file to run or arguments to pass along.</summary>
    static readonly string[] Interprets = ["-command", "-c", "-e", "-encodedcommand", "/c", "/k"];

    /// <summary>
    /// 2.16: a declared value filled into a string that a shell will parse.
    ///
    /// <para>Filling a replacement into a WORD makes it one argument
    /// whatever is in it - and that guarantee reaches the process
    /// boundary and stops there. A task that hands a shell a STRING is on
    /// the far side of it: the value lands safely in one argument, and
    /// that argument is a script the shell then parses, so a quote or a
    /// semicolon in the value changes what runs.</para>
    ///
    /// <para><b>Named rather than refused.</b> Refusing would need an
    /// opinion about which flags of which programs mean "interpret this"
    /// - an allowlist of shells this tool has deliberately avoided
    /// knowing about, which would be both incomplete and wrong about some
    /// legitimate command. The configuration is trusted input by explicit
    /// decision; whoever writes it gets told, and decides.</para>
    /// </summary>
    static void Declarations(Roots? roots, List<Finding> findings)
    {
        if (roots is null) return;

        foreach (TaskDecl task in roots.Tasks)
        {
            if (task.Command.Count == 0) continue;
            if (task.Line is not { } line || !FillsAValue(line)) continue;

            string program = Path.GetFileNameWithoutExtension(task.Command[0]);
            if (!Shells.Contains(program, StringComparer.OrdinalIgnoreCase)) continue;

            foreach (string word in task.Command)
            {
                if (!Interprets.Contains(word, StringComparer.OrdinalIgnoreCase)) continue;

                findings.Add(new Finding("2.16", false,
                    $"task '{task.Name}' fills a declared value into a string it hands to "
                    + $"{program} to interpret ('{word}'). The value arrives as one argument, but "
                    + "that argument is a script the shell parses, so a quote or a semicolon in "
                    + "the value changes what runs. Declare the steps as separate tasks, or call "
                    + "a script with the value as a parameter, so it stays an argument all the "
                    + "way down",
                    RootsFile.FileName));
                break;
            }
        }
    }

    /// <summary>Whether a declared command line refers to a replacement -
    /// the same reading <see cref="Tasks.Fill"/> does, so this warns about
    /// exactly what that fills and nothing else.</summary>
    static bool FillsAValue(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] != '{') continue;
            if (i + 1 < line.Length && line[i + 1] == '{') { i++; continue; }

            int close = line.IndexOf('}', i + 1);
            if (close > 0 && Tasks.IsName(line, i + 1, close)) return true;
        }

        return false;
    }

    /// <summary>2.10: guidance alone is a weak lever, but its ABSENCE
    /// guarantees the habit wins.</summary>
    static void Instructions(Place place, List<Finding> findings)
    {
        string text;
        try { text = File.ReadAllText(place.Path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        if (!text.Contains("fettle", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("Fettler", StringComparison.OrdinalIgnoreCase))
            findings.Add(new Finding("2.10", false,
                "this says nothing about Fettler, so nothing tells the assistant to prefer it "
                + "over the tools it already reaches for by habit", place.Path));
    }

    /// <summary>
    /// 2.17: every OTHER server registered beside this one.
    ///
    /// <para><see cref="Denied"/> is a closed list of names, and a closed
    /// list can only close the names on it. Another MCP server arrives
    /// with tools this file has never heard of, granted whatever its own
    /// configuration grants and reaching whatever its own process can
    /// reach - and nothing the scaffolder writes narrows any of it,
    /// because those tools are not called Read.</para>
    ///
    /// <para><b>Named, never judged.</b> Most of them are wanted, and a
    /// check that amounted to "you have other servers, remove them"
    /// would be scrolled past by the second week. What is reported is
    /// the fact - these sit outside this boundary - so that somebody
    /// working out where their files can be reached from gets the whole
    /// list rather than the half this tool implements. Denying
    /// <c>mcp__name__*</c> is the lever, for any that turn out not to be
    /// wanted.</para>
    ///
    /// <para>Unlike 2.8 this never goes stale: it reads the servers that
    /// are actually registered on the machine in front of it, so a
    /// server installed tomorrow is reported tomorrow.</para>
    /// </summary>
    static void Siblings(JsonElement root, Place place, List<Finding> findings)
    {
        var others = new List<string>();

        void Take(JsonElement holder)
        {
            if (holder.ValueKind != JsonValueKind.Object) return;
            foreach (JsonProperty one in holder.EnumerateObject())
                if (!one.Name.Equals(Places.ServerName, StringComparison.Ordinal)
                    && !others.Contains(one.Name, StringComparer.Ordinal))
                    others.Add(one.Name);
        }

        foreach (string holder in new[] { "mcpServers", "servers" })
            if (root.TryGetProperty(holder, out JsonElement servers)) Take(servers);

        // The per-project copy, read the same way Registration reads it,
        // so a server declared only there is not reported absent.
        if (root.TryGetProperty("projects", out JsonElement projects)
            && projects.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty project in projects.EnumerateObject())
                if (project.Value.ValueKind == JsonValueKind.Object
                    && project.Value.TryGetProperty("mcpServers", out JsonElement inProject))
                    Take(inProject);

        if (others.Count == 0) return;

        bool one = others.Count == 1;
        findings.Add(new Finding("2.17", false,
            $"{others.Count} other MCP server{(one ? " is" : "s are")} registered here and the deny "
            + $"list cannot narrow {(one ? "it" : "them")}: {(one ? "its" : "their")} tools are not "
            + "called Read, so nothing setup writes says anything about what they may reach. "
            + $"{(one ? "It is" : "They are")}: {string.Join(", ", others.Take(8))}"
            + (others.Count > 8 ? $", and {others.Count - 8} more" : "")
            + ". Deny mcp__<name>__* for any that should not be a route",
            place.Path));
    }

    /// <summary>2.11: approvals attach to one spelling of a project key
    /// and silently do not apply to the other. Live on this machine, with
    /// two entries differing only in a drive letter's case.</summary>
    static void Servers(Machine machine, Place place, List<Finding> findings)
    {
        Result<JsonDocument> read = ReadJson(machine, place.Path);
        if (!read.IsOk) return;

        using JsonDocument doc = read.Value;
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

        Siblings(doc.RootElement, place, findings);

        if (!doc.RootElement.TryGetProperty("projects", out JsonElement projects)
            || projects.ValueKind != JsonValueKind.Object) return;

        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty project in projects.EnumerateObject())
        {
            string key = project.Name.Replace('\\', '/').TrimEnd('/');
            if (seen.TryGetValue(key, out string? first) && !string.Equals(first, key, StringComparison.Ordinal))
            {
                findings.Add(new Finding("2.11", true,
                    $"two project entries name one directory in different spellings ('{first}' and "
                    + $"'{project.Name}'); permissions and approvals attach to whichever was resolved "
                    + "that day and silently do not apply to the other", place.Path));
                continue;
            }
            seen[key] = key;
        }
    }

    // ---- reading, only from the closed list ----

    /// <summary>
    /// The one reader. It refuses any path not on the compiled-in list,
    /// so the exception to R8 is a check rather than a promise.
    /// </summary>
    static Result<JsonDocument> ReadJson(Machine machine, string path)
    {
        if (!Places.Allows(machine, path))
            return Result<JsonDocument>.Fail(Outcome.Refused,
                "that is not one of the configuration files this may read", path);

        string text;
        try { text = Core.TextIo.WithoutMark(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<JsonDocument>.Fail(Outcome.Denied,
                "the configuration could not be read: " + e.Message, path);
        }

        try
        {
            return Result<JsonDocument>.Ok(JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }));
        }
        catch (JsonException e)
        {
            return Result<JsonDocument>.Fail(Outcome.Invalid,
                "the configuration will not parse: " + e.Message, path);
        }
    }
}
