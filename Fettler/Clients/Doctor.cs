using System.Text.Json;
using Fettler.Core;

namespace Fettler.Clients;

/// <summary>How one client at one reach stands.</summary>
public enum Verdict
{
    /// <summary>The client is not installed, or has no configuration at
    /// this reach. Never a failure - Claude Desktop has no project scope
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

public sealed record ClientReport(string Client, Reach Reach, Verdict Verdict, string Detail, string? Where);

/// <summary>One thing letting the assistant go round the boundary, and
/// the level it belongs to. A finding is reported only when its level is
/// on.</summary>
public sealed record Finding(string Check, bool Serious, string Message, string? Where,
    string Level = Levels.GuardrailsName);

/// <summary>One line of plain fact in a level's section: where a folder
/// is, what a level holds. Never a warning.</summary>
public sealed record Fact(string Level, string Text);

/// <summary>Which binary answered, and whether anything else would.</summary>
public sealed record Installation(string Binary, string Version, bool OnPath, string? PathBinary);

public sealed record Diagnosis(
    Installation Install,
    IReadOnlyList<ClientReport> Clients,
    IReadOnlyList<Finding> Findings)
{
    /// <summary>Which levels are on for the project examined.</summary>
    public Levels Levels { get; init; } = Levels.ToolsOnly;

    /// <summary>The plain facts, by level, in the order they are printed.</summary>
    public IReadOnlyList<Fact> Facts { get; init; } = [];

    /// <summary>A broken registration, or a companion whose root sits
    /// where the guardrails cannot reach it (R12.3). Either is exit 2.</summary>
    public bool AnyBroken =>
        Clients.Any(c => c.Verdict == Verdict.Broken)
        || Findings.Any(f => f.Check == Doctor.CompanionCheck);

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
    /// <summary>
    /// Examine the machine for the project <paramref name="roots"/>
    /// describes.
    ///
    /// <para><b>Every check runs; a finding is kept only for a level that
    /// is on (R5.5).</b> The tools level is always on and writes a deny
    /// list of its own, so a project missing those denies is told so
    /// whatever else is on. A project missing only the shell denies is
    /// told so only with guardrails on, because a declared task is what
    /// replaces a shell. The registration rows and the tools facts are
    /// always reported.</para>
    /// </summary>
    public static Diagnosis Examine(Machine machine, Roots? roots, string version)
    {
        var clients = new List<ClientReport>();
        var findings = new List<Finding>();
        var facts = new List<Fact>();

        Installation install = WhichBinary(version);
        Levels levels = roots?.Levels ?? Levels.ToolsOnly;

        foreach (string client in Places.Clients)
            foreach (Reach reach in new[] { Reach.Global, Reach.Repo })
                clients.Add(Look(machine, client, reach, install));

        Overrides(machine, roots, findings);
        Declarations(roots, findings);
        ToolsFacts(roots, facts);
        CompanionRoots(machine, roots, levels, facts, findings);
        ScreeningsFacts(roots, levels, facts);

        return new Diagnosis(install, clients, [.. findings.Where(f => levels.IsOn(f.Level))])
        {
            Levels = levels,
            Facts = facts,
        };
    }

    /// <summary>The number of the companion-root finding of R12.3.</summary>
    public const string CompanionCheck = "2.19";

    /// <summary>The number of the guardrails finding for an open shell.
    /// 2.8 used to cover the shells too. It is the tools level's finding
    /// now, and the shells have their own.</summary>
    public const string ShellCheck = "2.20";

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

    static ClientReport Look(Machine machine, string client, Reach reach, Installation install)
    {
        IReadOnlyList<Place> places = [.. Places.All(machine).Where(p => p.Client == client && p.Reach == reach)];

        if (places.Count == 0)
            return new ClientReport(client, reach, Verdict.NotApplicable,
                client == Places.ClaudeDesktop && reach == Reach.Repo
                    ? "this client has no project registration"
                    : "nothing to configure here",
                null);

        Place? servers = places.FirstOrDefault(p => p.Purpose == Purpose.Servers);

        if (servers is null)
            return new ClientReport(client, reach, Verdict.NotApplicable,
                $"registered at the {(reach == Reach.Global ? "repo" : "global")} reach only", null);

        if (!File.Exists(servers.Path))
            return new ClientReport(client, reach, Verdict.NotApplicable,
                "no configuration file here", servers.Path);

        Result<JsonDocument> read = ReadJson(machine, servers.Path);
        if (!read.IsOk)
            return new ClientReport(client, reach, Verdict.Broken, read.Failure!.Message, servers.Path);

        using JsonDocument doc = read.Value;

        if (!Registration(doc.RootElement, out JsonElement entry))
            return new ClientReport(client, reach, Verdict.Absent,
                $"'{Places.ServerName}' is not registered in this file", servers.Path);

        return Judge(client, reach, entry, servers.Path, install);
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

    static ClientReport Judge(string client, Reach reach, JsonElement entry, string where, Installation install)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("command", out JsonElement command)
            || command.ValueKind != JsonValueKind.String)
            return new ClientReport(client, reach, Verdict.Broken,
                "the registration has no command", where);

        string named = command.GetString()!;
        string? resolved = Resolve(named);

        if (resolved is null)
            return new ClientReport(client, reach, Verdict.Broken,
                $"the registration runs '{named}', and nothing on this machine has that name"
                + (install.OnPath ? "" : ". fettle is not on PATH"),
                where);

        return new ClientReport(client, reach, Verdict.Healthy,
            $"registered; runs {resolved}", where);
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
        // 2.8 and 2.20 are judged per client across every policy file of
        // that client, global and repo, so the denies are collected first.
        var denies = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (Place place in Places.All(machine))
        {
            if (!File.Exists(place.Path)) continue;

            // VS Code is here when any of its files is, and its denies come
            // from the agent file alone. The entry is made first, so a VS
            // Code with no agent file is judged, and 2.8 says so.
            if (place.Client == Places.VsCodeCopilot && !denies.ContainsKey(place.Client))
                denies[place.Client] = [];

            if (place.Purpose == Purpose.Policy) Policy(machine, place, roots, findings, denies);
            if (place.Purpose == Purpose.Instructions) Instructions(place, findings);
            if (place.Purpose == Purpose.Servers) Servers(machine, place, findings);
            if (place.Purpose == Purpose.Agent) Agent(machine, place, findings, denies);
        }

        MissingDenies(machine, denies, findings);

        // 2.13: a personal settings file never appears in review, so a
        // permission granted there is invisible to everybody else.
        foreach (Place place in Places.All(machine))
            if (place.Path.EndsWith("settings.local.json", StringComparison.Ordinal) && File.Exists(place.Path))
                findings.Add(new Finding("2.13", false,
                    "settings.local.json is present; its permissions are not committed", place.Path));

        // 2.15 is retired and its number is not reused. fettle now opens
        // this session's scratch folder itself, at every level, so the
        // condition it reported no longer arises.
    }

    /// <summary>
    /// 2.8 and 2.20: registering Fettler makes it available; only a deny
    /// makes it the route.
    ///
    /// <para><b>Split the way setup writes them.</b> 2.8 is the tools
    /// level's finding: the six built-in file tools and the two output
    /// readers, which <c>setup tools</c> denies, so a project at any level
    /// is told when the tools it registered will go unused. 2.20 is the
    /// guardrails finding: the three shells, which <c>setup guardrails</c>
    /// denies, because a declared task is what replaces a shell and a
    /// project with no tasks has nothing to replace it with.</para>
    ///
    /// <para><b>Judged the way the client merges its files (R5.6).</b> A
    /// deny in the global settings binds every project, and a deny in
    /// <c>.claude/settings.json</c> binds whatever
    /// <c>.claude/settings.local.json</c> says, so a tool is missing only
    /// when no file of that client denies it. The finding names the
    /// project file setup writes at <c>--local</c>, so the path in the
    /// report and the remedy point at the same place, and running the
    /// remedy clears the finding (R5.7).</para>
    ///
    /// <para><b>VS Code Copilot is judged by its agent file.</b> VS Code has
    /// no deny list, so for that client the denied names are the built-in
    /// tool sets its Fettler agent file leaves out, <see cref="Agent"/>
    /// collects them, and the finding names the project agent file that
    /// <c>setup tools vscode-copilot --local</c> writes.</para>
    /// </summary>
    static void MissingDenies(Machine machine, Dictionary<string, List<string>> denies, List<Finding> findings)
    {
        foreach ((string client, List<string> denied) in denies)
        {
            bool copilot = client == Places.VsCodeCopilot;

            Place? writes = Places.All(machine)
                .FirstOrDefault(p => p.Client == client && p.Reach == Reach.Repo
                    && p.Purpose == (copilot ? Purpose.Agent : Purpose.Policy));
            if (writes is null) continue;

            List<string> Missing(IEnumerable<string> wanted) => [.. wanted.Where(r => !denied.Any(d =>
                d.Equals(r, StringComparison.Ordinal) || d.StartsWith(r + "(", StringComparison.Ordinal)))];

            List<string> tools = Missing(copilot ? CopilotReplaced : ToolsDenied);
            if (tools.Count > 0)
                findings.Add(new Finding("2.8", false,
                    (copilot
                        ? $"Copilot keeps its built-in {string.Join(", ", tools)}; no Fettler agent file excludes them. "
                        : $"deny does not list the built-in {string.Join(", ", tools)}. ")
                    + $"Run: fettle setup tools {client} --local",
                    writes.Path, Levels.ToolsName));

            List<string> shells = Missing(copilot ? CopilotShell : GuardrailsDenied);
            if (shells.Count > 0)
                findings.Add(new Finding(ShellCheck, false,
                    (copilot
                        ? $"Copilot keeps its terminal; no Fettler agent file excludes {string.Join(", ", shells)}. "
                        : $"deny does not list the shell {string.Join(", ", shells)}. ")
                    + $"Run: fettle setup guardrails {client} --local",
                    writes.Path));
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

    /// <summary>
    /// VS Code Copilot's built-in tool sets, by the names its agent files
    /// use: <c>read</c> is the file reader, <c>edit</c> the editors and
    /// <c>search</c> the searches, the three Fettler replaces. VS Code has
    /// no deny list, so these are left OUT of the Fettler agent file's
    /// <c>tools:</c> list, and 2.8 reports an agent file that keeps one.
    /// Copilot has no separate output readers; its terminal set carries
    /// its own output.
    /// </summary>
    public static readonly string[] CopilotReplaced = ["read", "edit", "search"];

    /// <summary>The terminal, which is Copilot's shell. Left out of the
    /// agent file by <c>setup guardrails</c>, and 2.20 reports an agent
    /// file that keeps it.</summary>
    public static readonly string[] CopilotShell = ["execute"];

    /// <summary>
    /// What <c>setup tools</c> denies, and 2.8 expects to find denied: the
    /// tools Fettler replaces and the ones that carry another tool's
    /// output back.
    ///
    /// <para>Registering Fettler makes it available; only a deny makes it
    /// the route. A tools-level project that leaves these open keeps
    /// using the built-ins out of habit, and the tools it registered go
    /// unused. That is why the deny belongs to the tools level and not to
    /// guardrails.</para>
    /// </summary>
    public static readonly string[] ToolsDenied = [.. Replaced, .. Indirect];

    /// <summary>What <c>setup guardrails</c> adds, and 2.20 expects to find
    /// denied: the shells. They stay with guardrails because a declared
    /// task is what replaces a shell, and a project with no tasks has
    /// nothing to replace it with.</summary>
    public static readonly string[] GuardrailsDenied = [.. Shell];

    /// <summary>Everything <c>setup</c> denies across both levels, in the
    /// order the two levels write them.</summary>
    public static readonly string[] Denied = [.. ToolsDenied, .. GuardrailsDenied];

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

    static void Policy(Machine machine, Place place, Roots? roots, List<Finding> findings,
                       Dictionary<string, List<string>> denies)
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
                            + "every declared tree. Remove the entry, or declare that directory as a tree",
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
                $"{shell.Count} allow entr{(shell.Count == 1 ? "y pre-approves" : "ies pre-approve")} a shell command that "
                + $"writes files: {string.Join(", ", shell.Take(5))}"
                + (shell.Count > 5 ? $", and {shell.Count - 5} more" : "")
                + (shell.Count == 1 ? ". Remove it" : ". Remove them"),
                place.Path));

        // 2.7: an allow on the built-in editors or reader removes the
        // last friction from the habit.
        var builtIn = allowed.Where(a => Replaced.Any(r =>
            a.Equals(r, StringComparison.Ordinal) || a.StartsWith(r + "(", StringComparison.Ordinal))).ToList();

        if (builtIn.Count > 0)
            findings.Add(new Finding("2.7", true,
                $"allow lists built-in tools that Fettler replaces: {string.Join(", ", builtIn)}. "
                + "Remove those entries",
                place.Path));

        // 2.8 and 2.20 are judged after every file is read, global and
        // repo together, because that is how the client merges them: see
        // MissingDenies. VS Code has no deny list, so a permissions block
        // in its settings is a key it ignores and counts for nothing here;
        // Agent reads that client's file.
        if (!denies.TryGetValue(place.Client, out List<string>? all))
            denies[place.Client] = all = [];
        if (place.Client != Places.VsCodeCopilot) all.AddRange(denied);

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
                $"{string.Join(", ", unknown)} {(unknown.Count == 1 ? "is" : "are")} not in this fettle's tool inventory "
                + $"({InventoryOf}). Deny any that reads or writes files or runs a shell",
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
                $"{string.Join(", ", contested)} appear in both allow and deny. Remove the allow entry",
                place.Path));

        // 2.14: auto-approval reinstates every route the boundary removed.
        if (doc.RootElement.TryGetProperty("chat.tools.autoApprove", out JsonElement auto)
            && auto.ValueKind == JsonValueKind.True)
            findings.Add(new Finding("2.14", true,
                "chat.tools.autoApprove is on, so every tool runs without asking. Turn it off",
                place.Path));
    }

    /// <summary>
    /// The Fettler agent file, read as VS Code Copilot's deny list.
    ///
    /// <para>VS Code names the built-ins as tool sets in the file's
    /// <c>tools:</c> list, and a set the list leaves out is one the agent
    /// cannot use. So every set of <see cref="CopilotReplaced"/> and
    /// <see cref="CopilotShell"/> that is NOT listed counts as denied. The
    /// file binds when a person picks that agent in the chat, which this
    /// cannot observe; the file is what it can see. A file with no
    /// <c>tools:</c> line gives the agent every tool and denies nothing.</para>
    /// </summary>
    static void Agent(Machine machine, Place place, List<Finding> findings, Dictionary<string, List<string>> denies)
    {
        Result<string> read = ReadText(machine, place.Path);
        if (!read.IsOk)
        {
            findings.Add(new Finding("2.x", true, read.Failure!.Message, place.Path));
            return;
        }

        if (!denies.TryGetValue(place.Client, out List<string>? all))
            denies[place.Client] = all = [];

        if (AgentFile.Tools(read.Value) is not { } listed) return;

        foreach (string set in CopilotReplaced.Concat(CopilotShell))
            if (!listed.Contains(set, StringComparer.OrdinalIgnoreCase)) all.Add(set);
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
                    $"task '{task.Name}' puts a declared value inside a string that "
                    + $"{program} parses ('{word}'). Declare the steps as separate tasks, or pass the value to a "
                    + "script as a parameter",
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
                "this file does not mention Fettler. Add a line telling the assistant to use it",
                place.Path));
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
            $"{others.Count} other MCP server{(one ? "" : "s")} registered: "
            + $"{string.Join(", ", others.Take(8))}"
            + (others.Count > 8 ? $", and {others.Count - 8} more" : "")
            + ". Deny mcp__<name>__* for any that should not reach files",
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
                    $"two project entries name one directory with different spellings: '{first}' "
                    + $"and '{project.Name}'. Permissions apply to only one of them. Merge them by hand",
                    place.Path, Levels.ToolsName));
                continue;
            }
            seen[key] = key;
        }
    }

    // ---- the facts each level reports ----

    /// <summary>
    /// The tools section's facts: the client's folders fettle opened, with
    /// how each was found, and the machine file.
    ///
    /// <para>A folder that is not there is a fact, not a finding. The
    /// client makes these folders when it needs them, and the tree appears
    /// on the next call.</para>
    /// </summary>
    static void ToolsFacts(Roots? roots, List<Fact> facts)
    {
        if (roots is null) return;

        foreach (string name in Harness.Names)
        {
            string? found = roots.Names.FirstOrDefault(n =>
                n.Equals(name, Roots.PathComparison) && roots.HowFound(n) is not null);

            facts.Add(new Fact(Levels.ToolsName, found is not null
                ? $"{name}: {roots.PathOf(found)}"
                : $"{name}: not found"));
        }

        foreach (string file in roots.Levels.Tools.Concat(roots.Levels.Screenings).Distinct(StringComparer.Ordinal))
            if (Path.GetFileName(file).Equals(Host.MachineFileName, StringComparison.Ordinal))
            {
                facts.Add(new Fact(Levels.ToolsName, $"machine file: {file}"));
                break;
            }
    }

    /// <summary>
    /// R12.3: where each companion server's root sits, measured against
    /// the declared trees.
    ///
    /// <para><b>With guardrails off</b>, each root is one line of fact in
    /// the tools section and nothing is a finding. No tree states a rule
    /// the companion could ignore.</para>
    ///
    /// <para><b>With guardrails on</b>, a root inside a declared tree,
    /// inside a screened tree or scope, or in a folder a governed file
    /// switches, is a serious finding. The remedy it names is to move the
    /// root. Denying the companion would remove what it is for.</para>
    /// </summary>
    static void CompanionRoots(Machine machine, Roots? roots, Levels levels, List<Fact> facts, List<Finding> findings)
    {
        IReadOnlyList<Companion> companions = Companions.Find(machine);

        foreach (Companion c in companions)
        {
            if (c.Root is not { } root) continue;

            if (!levels.GuardrailsOn || roots is null)
            {
                facts.Add(new Fact(Levels.ToolsName,
                    $"{c.Name}: --root {root}, no fettle rules apply"));
                continue;
            }

            var problems = new List<string>();
            string? pairing = null;
            Screened screened = Screened.None;

            foreach (string name in roots.Names)
            {
                if (roots.HowFound(name) is not null) continue;

                string tree = roots.PathOf(name);
                if (!Roots.IsWithin(tree, root)) continue;

                Grant grant = roots.GrantOf(name);
                screened |= grant.At(tree, root).Screen;
                foreach (Scope scope in grant.Scopes)
                    if (Roots.IsWithin(root, scope.Path) && scope.Screen is { } s) screened |= s;

                if (Roots.IsWithin(root, tree))
                {
                    pairing = name;
                    continue;
                }

                problems.Add($"It is inside the tree '{name}', which grants {Permissions.Write(grant.Can)}"
                    + (grant.Scopes.Count > 0 ? " and declares scopes" : "")
                    + $". {c.Name} follows none of that");
            }

            if (screened != Screened.None)
                problems.Add($"It holds files screened for {Screens.Write(screened)}, and {c.Name} reads them unscreened");

            if (GovernedAt(root) is { } governed)
                problems.Add($"{governed}, so {c.Name} can rewrite the file that turns guardrails on and off");

            if (problems.Count > 0)
                findings.Add(new Finding(CompanionCheck, true,
                    $"{c.Name} reads and writes every file under {root} with none of fettle's rules. "
                    + string.Join(". ", problems)
                    + $". Move the --root of {c.Name} to a folder outside every tree",
                    c.Registration));
            else
                facts.Add(new Fact(Levels.GuardrailsName, pairing is not null
                    ? $"{c.Name}: its root {root} is the tree '{pairing}', so the two share files"
                    : $"{c.Name}: its root {root} is outside every declared tree"));
        }

        var rootless = companions.Where(c => c.Root is null).Select(c => c.Name).ToList();
        if (rootless.Count > 0)
            facts.Add(new Fact(Levels.ToolsName,
                $"servers without --root: {string.Join(", ", rootless)}. fettle opens no tree for them"));
    }

    /// <summary>
    /// Whether a governed file switches the folder, or sits in it: a
    /// <c>.fettler.json</c> or a <c>.picker.json</c> at or above it, or a
    /// governed file directly inside it.
    /// </summary>
    static string? GovernedAt(string root)
    {
        try
        {
            for (DirectoryInfo? dir = new(root); dir is not null; dir = dir.Parent)
                foreach (string name in new[] { RootsFile.FileName, RootsFile.LocalFileName, ".picker.json", ".picker.local.json" })
                    if (File.Exists(Path.Combine(dir.FullName, name)))
                        return $"A {name} at {dir.FullName} governs the folder";

            foreach (string tail in RuleFiles.Tails)
                if (File.Exists(Path.Combine(root, tail.Replace('/', Path.DirectorySeparatorChar))))
                    return $"The folder holds {tail}";

            foreach (string file in Directory.EnumerateFiles(root))
                if (RuleFiles.Governs(Path.GetFileName(file)))
                    return $"The folder holds {Path.GetFileName(file)}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or System.Security.SecurityException)
        {
            return null;
        }

        return null;
    }

    /// <summary>The screenings section's facts: burler, the models
    /// directory, and the model installed for each category. Reported
    /// only when screenings are on.</summary>
    static void ScreeningsFacts(Roots? roots, Levels levels, List<Fact> facts)
    {
        if (roots is null || !levels.ScreeningsOn) return;

        string burler = Sidecar.Executable();
        facts.Add(new Fact(Levels.ScreeningsName, File.Exists(burler)
            ? $"burler: {burler}"
            : $"burler: not beside fettle at {burler}, so only identifiers are screened"));

        if (roots.Models is not { } models)
        {
            facts.Add(new Fact(Levels.ScreeningsName, "models: none named, so only identifiers are screened"));
            return;
        }

        facts.Add(new Fact(Levels.ScreeningsName, Directory.Exists(models)
            ? $"models: {models}"
            : $"models: {models} does not exist; screens needing a model refuse every read"));

        foreach (Screened one in Screens.All)
        {
            if (one == Screened.Identifiers) continue;

            IReadOnlyList<Installed.Model> installed = Installed.For(models, Screens.NameOf(one));
            facts.Add(new Fact(Levels.ScreeningsName,
                $"{Screens.NameOf(one)}: {(installed.Count == 0 ? "no model installed" : string.Join(", ", installed))}"));
        }
    }

    // ---- reading, only from the closed list ----

    /// <summary>The text of a listed file, for the one place that is not
    /// JSON. The same refusal as <see cref="ReadJson"/>.</summary>
    static Result<string> ReadText(Machine machine, string path)
    {
        if (!Places.Allows(machine, path))
            return Result<string>.Fail(Outcome.Refused, "doctor does not read this file", path);

        try { return Result<string>.Ok(Core.TextIo.WithoutMark(File.ReadAllText(path))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<string>.Fail(Outcome.Denied, "could not read this file: " + e.Message, path);
        }
    }

    /// <summary>
    /// The one reader. It refuses any path not on the compiled-in list,
    /// so the exception to R8 is a check rather than a promise.
    /// </summary>
    static Result<JsonDocument> ReadJson(Machine machine, string path)
    {
        if (!Places.Allows(machine, path))
            return Result<JsonDocument>.Fail(Outcome.Refused,
                "doctor does not read this file", path);

        string text;
        try { text = Core.TextIo.WithoutMark(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<JsonDocument>.Fail(Outcome.Denied,
                "could not read this file: " + e.Message, path);
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
                "this file is not valid JSON: " + e.Message, path);
        }
    }
}
