using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fettler.Core;

namespace Fettler.Clients;

/// <summary>One change setup made, or would make.</summary>
public sealed record Change(string Path, string What, bool Applied, string? Backup = null);

public sealed record Scaffolding(IReadOnlyList<Change> Changes, IReadOnlyList<string> Notes, bool DryRun);

/// <summary>
/// The scaffolder: a command rather than a pair of scripts, so there is
/// one implementation instead of a <c>.ps1</c> and a <c>.sh</c> that have
/// to be kept in step by hand - the mistake this project already made
/// once and undid.
///
/// <para><b>Setup is the ONE thing in this tool that writes outside a
/// tree</b>, and it may write only to the compiled-in list of client
/// configuration paths that <see cref="Doctor"/> reads. That is the whole
/// exception, stated as a requirement rather than left as an
/// implementation detail.</para>
///
/// <para><b>Merging into JSON, never clobbering, is the whole risk of the
/// feature.</b> These files hold other people's servers, other people's
/// permission grants, and a hook array that is already doing something.
/// A merge bug here does visible damage to a machine outside this
/// repository - so: back up before the first write, add or update
/// Fettler's own key and nothing else, and dry-run freely.</para>
///
/// <para><b>What it will and will not correct.</b> It corrects the
/// WIRING - a missing or wrong registration, a missing configuration, a
/// missing instruction block, a missing hook. It does not silently
/// correct POLICY: an existing allow entry is the user's own
/// configuration, and a tool that quietly removes it has done exactly
/// what this design objects to. It reports those and offers. And it
/// cannot correct INSTALLATION: a binary that is not on PATH is not a
/// config edit, and setup says so rather than writing a registration it
/// knows will fail to launch.</para>
/// </summary>
public static class Scaffold
{
    /// <summary>The marker that makes an instruction block idempotent.
    /// Re-running updates in place rather than appending a second
    /// copy, which is what an unmarked block always ends up doing.</summary>
    public const string BlockOpen = "<!-- fettler:begin -->";

    public const string BlockClose = "<!-- fettler:end -->";

    /// <summary>What one run of setup is asked to do.</summary>
    /// <param name="Level">The level to set up: <c>tools</c>,
    /// <c>guardrails</c> or <c>screenings</c>.</param>
    /// <param name="Models">The models directory the project names, if
    /// any, for <c>setup screenings</c> to check.</param>
    /// <param name="FirstTree">The default tree's name, for the starter
    /// <c>screenings</c> section to screen.</param>
    public sealed record Options(
        string Client,
        Reach Reach,
        bool DryRun = false,
        bool Force = false,
        bool Hooks = false,
        bool Deny = false,
        string? Command = null,
        string Level = Levels.ToolsName,
        string? Models = null,
        string? FirstTree = null);

    /// <summary>
    /// Set up one level for one client (R7.4).
    ///
    /// <para><b>Each level writes what makes it work.</b> <c>tools</c>
    /// registers the server, denies the built-in file tools and output
    /// readers, and writes the tools paragraph of the instruction block.
    /// Registering alone makes Fettler available; only the deny makes it
    /// the route, and a tools-level project without one keeps using the
    /// built-ins out of habit. <c>guardrails</c> adds the shell denies,
    /// the guardrails paragraph, the hook when asked, and a starter
    /// <c>guardrails</c> section. The shells stay with guardrails because
    /// a declared task is what replaces a shell. <c>screenings</c> checks
    /// burler and the models directory and writes a starter
    /// <c>screenings</c> section.</para>
    /// </summary>
    public static Result<Scaffolding> Apply(Machine machine, Options options, string version)
    {
        if (!Places.Clients.Contains(options.Client))
            return Result<Scaffolding>.Fail(Outcome.Invalid,
                $"no client called '{options.Client}'. The clients are: {string.Join(", ", Places.Clients)}");

        if (!Levels.Names.Contains(options.Level, StringComparer.Ordinal))
            return Result<Scaffolding>.Fail(Outcome.Invalid,
                $"no level called '{options.Level}'. The levels are: {string.Join(", ", Levels.Names)}");

        var changes = new List<Change>();
        var notes = new List<string>();

        if (options.Level == Levels.ScreeningsName)
            return Screenings(machine, options, changes, notes);

        // It cannot correct installation, and must not pretend to.
        // Only the tools level registers, so only it says which name the
        // registration uses.
        string command = options.Command ?? Environment.ProcessPath ?? "fettle";
        bool registering = options.Level == Levels.ToolsName && options.Command is null;
        if (registering && Doctor.Resolve("fettle") is { } onPath)
        {
            command = "fettle";
            notes.Add($"fettle is on PATH at {onPath}; the registration uses the name 'fettle'");
        }
        else if (registering)
        {
            notes.Add($"fettle is not on PATH, so the registration uses the full path {command}. "
                + "Put fettle on PATH and run setup again to register the name instead");
        }

        IReadOnlyList<Place> places =
            [.. Places.All(machine).Where(p => p.Client == options.Client && p.Reach == options.Reach)];

        if (places.Count == 0)
            return Result<Scaffolding>.Fail(Outcome.Invalid,
                $"{options.Client} has no configuration at the {Word(options.Reach)} reach");

        bool guardrails = options.Level == Levels.GuardrailsName;

        if (guardrails && options.Reach == Reach.Repo)
        {
            // 3.4: a project with no guardrails section gets a starter
            // one. Creating where none exists is scaffolding; CHANGING a
            // section that exists is the escape B.13 closes, and belongs
            // to a person with an editor.
            Result<Change?> config = StarterSection(machine, Levels.GuardrailsName,
                new JsonObject { ["trees"] = new JsonObject { [RootsFile.WorkName] = new JsonObject { ["path"] = "." } } },
                "declaring this folder as the tree 'work'", options.DryRun);
            if (!config.IsOk) return config.Carry<Scaffolding>();
            if (config.Value is { } made) changes.Add(made);
        }
        else if (guardrails)
        {
            notes.Add("the global reach declares no tree. Guardrails are a project's choice. In each "
                + "project that wants them, run: fettle setup guardrails " + options.Client + " --local");
        }

        foreach (Place place in places)
        {
            // Only tools registers. Both levels write the instruction
            // block and the policy, each adding its own paragraph and its
            // own denies, so guardrails never registers and tools never
            // writes a guardrails paragraph.
            Result<Change?> change = place.Purpose switch
            {
                Purpose.Servers when !guardrails => Register(machine, place, command, options),
                Purpose.Instructions => Instruct(machine, place, options),
                Purpose.Policy => Policy(machine, place, options, notes),
                _ => Result<Change?>.Ok(null),
            };

            if (!change.IsOk) return change.Carry<Scaffolding>();
            if (change.Value is { } one) changes.Add(one);
        }

        return Result<Scaffolding>.Ok(new Scaffolding(changes, notes, options.DryRun));
    }

    // ---- the MCP registration ----

    static Result<Change?> Register(Machine machine, Place place, string command, Options options)
    {
        Result<JsonObject> read = Load(machine, place.Path);
        if (!read.IsOk) return read.Carry<Change?>();

        JsonObject root = read.Value;

        // VS Code keeps servers under "servers"; Claude under
        // "mcpServers". Writing the one this client reads rather than
        // both, so a file never grows a key its client ignores.
        string holder = place.Client == Places.VsCodeCopilot ? "servers" : "mcpServers";

        if (root[holder] is not JsonObject servers)
        {
            servers = [];
            root[holder] = servers;
        }

        var wanted = new JsonObject
        {
            ["command"] = command,
            ["args"] = new JsonArray("serve"),
        };

        bool already = servers[Places.ServerName] is JsonNode existing
            && existing.ToJsonString() == wanted.ToJsonString();

        if (already) return Result<Change?>.Ok(null);

        // 3.x: --force is needed to change a key that already holds
        // something different, so re-running never silently reshapes
        // somebody's own registration.
        //
        // A DRY RUN never refuses for this, and that is not a nicety: its
        // whole job is to say what would change, so refusing to describe
        // the one interesting case - and, as it first did, advising the
        // very flag the caller had already passed - defeats the feature
        // at exactly the moment it is wanted. Found by running the
        // documented commands as written, which is what that exercise is
        // for.
        bool replacing = servers[Places.ServerName] is not null;

        if (replacing && !options.Force && !options.DryRun)
            return Result<Change?>.Fail(Outcome.TargetExists,
                $"'{Places.ServerName}' is already registered here with a different command. "
                + "Pass --force to replace it, or --dry-run to see the change",
                place.Path);

        if (replacing && !options.Force)
            return Result<Change?>.Ok(new Change(place.Path,
                $"replace the existing '{Places.ServerName}' registration with "
                + $"command {command} (needs --force)",
                false));

        servers[Places.ServerName] = wanted;

        return Save(machine, place.Path, root, options.DryRun,
            (replacing ? "replace " : "register ") + Places.ServerName + " under " + holder);
    }

    // ---- the instruction block ----

    /// <summary>The tools paragraph of the instruction block, written in
    /// every block (R9.6).</summary>
    public const string ToolsParagraph = """
        ## Working on files here

        Use the Fettler tools (`mcp__fettler__*`) for file work: find, search, read,
        write, edit, move, copy and delete. Call `roots` first. It lists the trees,
        what each allows, and which tree an unqualified path lands in.

        **There is no working directory.** Paths resolve against the trees. `cd` and
        `Set-Location` do nothing for Fettler. `read` returns a hash. Pass it as
        `expect` on an edit, and the edit is refused if the file changed.

        If something looks wrong, run `fettle doctor`.
        """;

    /// <summary>The guardrails paragraph, written only by
    /// <c>setup guardrails</c> (R9.6).</summary>
    public const string GuardrailsParagraph = """
        **Guardrails are on.** The built-in Read, Write, Edit, NotebookEdit, Grep and
        Glob are denied. Use the Fettler tools for every file operation.

        **A tree may be read-only.** A scope inside a tree may allow more or less
        than the tree does. Running a declared task needs `execute`, which is never
        a default.

        `.fettler.json` and `.fettler.local.json` say what Fettler may do. Fettler
        does not write them. A person edits them.
        """;

    static Result<Change?> Instruct(Machine machine, Place place, Options options)
    {
        string paragraphs = options.Level == Levels.GuardrailsName
            ? ToolsParagraph + "\n\n" + GuardrailsParagraph
            : ToolsParagraph;

        string body = BlockOpen + "\n" + paragraphs + "\n" + BlockClose;

        string existing = string.Empty;
        if (File.Exists(place.Path))
        {
            try { existing = File.ReadAllText(place.Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Result<Change?>.Fail(Outcome.Denied, "could not read: " + e.Message, place.Path);
            }
        }

        int open = existing.IndexOf(BlockOpen, StringComparison.Ordinal);
        int close = existing.IndexOf(BlockClose, StringComparison.Ordinal);

        string updated;
        if (open >= 0 && close > open)
        {
            // The tools level asks only that its paragraph be there. A
            // block guardrails wrote holds it and more, and re-running
            // setup tools on that project must not strip the guardrails
            // paragraph out of it.
            if (options.Level == Levels.ToolsName
                && existing[open..close].Contains(ToolsParagraph, StringComparison.Ordinal))
                return Result<Change?>.Ok(null);

            string replaced = existing[..open] + body + existing[(close + BlockClose.Length)..];
            if (replaced == existing) return Result<Change?>.Ok(null);
            updated = replaced;
        }
        else
        {
            updated = existing.Length == 0
                ? body + "\n"
                : existing.TrimEnd() + "\n\n" + body + "\n";
        }

        return SaveText(machine, place.Path, updated, options.DryRun,
            open >= 0 ? "update the Fettler instruction block" : "add a Fettler instruction block");
    }

    // ---- policy: offered globally, written in the project ----

    /// <summary>
    /// The denies that make Fettler the route rather than merely an
    /// option.
    ///
    /// <para><b>Half one of the exclusion list, and the only half that
    /// can be finished.</b> Six entries, no arguments, no platform
    /// variants - and it works because the Fettler tools have different
    /// names, so denying the built-ins does not disarm the assistant, it
    /// ROUTES it.</para>
    ///
    /// <para><b>Half two - the shell - is not written as a blocklist,
    /// because a blocklist for it cannot be finished.</b>
    /// <c>echo x &gt; file</c> defeats every list that could be written,
    /// and so does <c>python -c</c>, and so does a redirect on any
    /// command at all. Setup names the existing allow entries instead and
    /// leaves the decision with the person whose configuration it
    /// is.</para>
    ///
    /// <para><b>Split by level, the way the levels are used.</b> The tools
    /// level writes <see cref="Doctor.ToolsDenied"/>: the six and the two
    /// output readers, so a project that only registered Fettler is
    /// routed to it rather than left to habit. Guardrails writes
    /// <see cref="Doctor.Denied"/>, which adds the three shells, because a
    /// declared task is what replaces a shell. Each level only adds, so
    /// running tools after guardrails removes nothing.</para>
    /// </summary>
    static Result<Change?> Policy(Machine machine, Place place, Options options, List<string> notes)
    {
        // A personal settings file is somebody's own and uncommitted;
        // setup never writes one.
        if (place.Path.EndsWith("settings.local.json", StringComparison.Ordinal))
            return Result<Change?>.Ok(null);

        // --local writes the deny list by default: it lands in the
        // project's own settings, is reviewable in a diff, and affects
        // only this tree. Asking for setup here IS asking for Fettler to
        // be the route.
        //
        // --global only offers, and needs the flag: changing how the
        // assistant may work in EVERY project is not a side effect of
        // configuring one.
        bool write = options.Reach == Reach.Repo || options.Deny;

        Result<JsonObject> read = Load(machine, place.Path);
        if (!read.IsOk) return read.Carry<Change?>();

        JsonObject root = read.Value;
        if (root["permissions"] is not JsonObject permissions)
        {
            permissions = [];
            root["permissions"] = permissions;
        }

        if (permissions["deny"] is not JsonArray deny)
        {
            deny = [];
            permissions["deny"] = deny;
        }

        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? entry in deny) if (entry is not null) held.Add(entry.ToString());

        string[] wanted = options.Level == Levels.GuardrailsName ? Doctor.Denied : Doctor.ToolsDenied;
        var adding = wanted.Where(tool => !held.Contains(tool)).ToList();

        // Named, never deleted. They are the user's own configuration,
        // and a tool that quietly removes them has done exactly what this
        // design objects to.
        if (permissions["allow"] is JsonArray allow)
        {
            var conflicting = new List<string>();
            foreach (JsonNode? entry in allow)
            {
                if (entry is null) continue;
                string rule = entry.ToString();
                // Doctor.Replaced, not Doctor.Denied: a narrowed shell
                // allow beside the shell deny written below is the shape
                // this tool is asking for, so naming it as a conflict
                // would be telling the user to undo the fix.
                foreach (string tool in Doctor.Replaced)
                    if (rule == tool || rule.StartsWith(tool + "(", StringComparison.Ordinal))
                        conflicting.Add(rule);
            }

            if (conflicting.Count > 0)
                notes.Add($"allow entries in {place.Path} grant tools that are now denied: "
                    + $"{string.Join(", ", conflicting)}. Setup does not remove them; remove them yourself");
        }

        if (adding.Count > 0 && !write)
            notes.Add($"not written: {string.Join(", ", adding)} would be denied in {place.Path}. "
                + "Pass --deny to write the deny list globally");

        bool denying = write && adding.Count > 0;
        if (denying) foreach (string tool in adding) deny.Add(tool);

        // Denying the shell stops the next build, test run and git
        // command, and the allow list that restores them cannot be
        // generated from here - which project needs which command is not
        // knowable. So say it, rather than leaving somebody to discover
        // it at the first command after a run that reported success.
        if (denying && adding.Any(tool => Doctor.Shell.Contains(tool, StringComparer.Ordinal)))
            notes.Add("the shell is denied too, so build and git commands through it now stop. "
                + "Allow back only the commands this project needs, for example "
                + "\"allow\": [\"Bash(git status:*)\"], or declare them as tasks under \"guardrails\" in .fettler.json. "
                + "Never allow a command that writes files");

        // The hook is asked for by --hooks and by nothing else, so it must
        // not depend on whether the deny list happened to need changing.
        // It used to sit after an early return taken whenever the denies
        // were ALREADY complete, which made --hooks a silent no-op on
        // every re-run - exactly what the documented order asks for - and
        // unreachable at --global without --deny. It reported success and
        // installed nothing, which is the worst of the three outcomes.
        bool hooking = options.Hooks
            && options.Client == Places.ClaudeCode
            && AddHook(root);

        if (!denying && !hooking) return Result<Change?>.Ok(null);

        string what = (denying, hooking) switch
        {
            (true, true) => $"deny the built-in {string.Join(", ", adding)}, and add fettle doctor --hook to SessionStart",
            (true, false) => $"deny the built-in {string.Join(", ", adding)}",
            _ => "add fettle doctor --hook to SessionStart",
        };

        return Save(machine, place.Path, root, options.DryRun, what);
    }

    // ---- the session hook ----

    /// <summary>
    /// Install the doctor into <c>SessionStart</c> by MERGING into the
    /// array that is already there.
    ///
    /// <para>There is a working hook on the machine this was written for,
    /// running an unrelated script. Replacing that array would silently
    /// remove somebody's working setup, which is 3.1's risk in a second
    /// file. It is appended to, and only if Fettler's own entry is not
    /// there already.</para>
    ///
    /// <para><b>Bounded, and always exit 0.</b> A diagnostic that can stop
    /// a session from starting will be removed within a week.</para>
    /// </summary>
    static bool AddHook(JsonObject root)
    {
        string command = Environment.ProcessPath ?? "fettle";

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = [];
            root["hooks"] = hooks;
        }

        if (hooks["SessionStart"] is not JsonArray sessions)
        {
            sessions = [];
            hooks["SessionStart"] = sessions;
        }

        foreach (JsonNode? entry in sessions)
            if (entry?.ToJsonString().Contains("doctor", StringComparison.Ordinal) == true)
                return false;

        sessions.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"\"{command}\" doctor --hook",
                ["timeout"] = 10,
            }),
        });

        return true;
    }

    // ---- the screenings level ----

    /// <summary>
    /// <c>setup screenings</c>: check that burler is beside fettle, check
    /// the models directory, and write a starter <c>screenings</c>
    /// section into the project's file when it has none.
    ///
    /// <para>The models are never downloaded here. They are a person's to
    /// fetch and put in a directory, and setup says where to name it.</para>
    /// </summary>
    static Result<Scaffolding> Screenings(Machine machine, Options options, List<Change> changes, List<string> notes)
    {
        string burler = Sidecar.Executable();
        notes.Add(File.Exists(burler)
            ? $"burler is beside fettle at {burler}"
            : $"burler is not beside fettle at {burler}. Only identifiers can be screened until it is. "
              + "Install fettle from a release archive, which carries burler");

        if (options.Models is { } models)
            notes.Add(Directory.Exists(models)
                ? $"the models directory {models} exists"
                : $"the models directory {models} does not exist. Create it and put the models in it");
        else
            notes.Add("no models directory is named, so only identifiers are screened. Name one as "
                + "\"models\" under \"screenings\"");

        if (options.Reach == Reach.Repo)
        {
            string tree = options.FirstTree ?? RootsFile.WorkName;
            Result<Change?> section = StarterSection(machine, Levels.ScreeningsName,
                new JsonObject { ["screen"] = new JsonObject { [tree] = true } },
                $"screening the tree '{tree}' for every category", options.DryRun);
            if (!section.IsOk) return section.Carry<Scaffolding>();
            if (section.Value is { } made) changes.Add(made);
        }
        else
        {
            notes.Add("the global reach writes no project file. Screenings are a project's choice. In each "
                + "project that wants them, run: fettle setup screenings " + options.Client + " --local");
        }

        return Result<Scaffolding>.Ok(new Scaffolding(changes, notes, options.DryRun));
    }

    // ---- the project's own configuration ----

    /// <summary>
    /// Write a starter section into the project's <c>.fettler.json</c>,
    /// creating the file when there is none.
    ///
    /// <para><b>Never into a file that already has the section</b> (R7.5).
    /// Creating where none exists is scaffolding. Changing a section that
    /// exists is the escape the governed-file refusal closes, and belongs
    /// to a person with an editor. A file in the old flat shape is left
    /// alone: the next fettle command names the rewrite.</para>
    /// </summary>
    static Result<Change?> StarterSection(Machine machine, string section, JsonObject starter, string what, bool dryRun)
    {
        string path = Path.Combine(machine.Tree, RootsFile.FileName);
        JsonObject root;
        bool exists = File.Exists(path);

        if (exists)
        {
            string text;
            try { text = TextIo.WithoutMark(File.ReadAllText(path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Result<Change?>.Fail(Outcome.Denied, "could not read: " + e.Message, path);
            }

            try
            {
                if (JsonNode.Parse(text, null, new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    }) is not JsonObject parsed)
                    return Result<Change?>.Ok(null);
                root = parsed;
            }
            catch (JsonException)
            {
                return Result<Change?>.Ok(null);
            }

            if (root.ContainsKey(section) || root.ContainsKey("trees") || root.ContainsKey("roots"))
                return Result<Change?>.Ok(null);
        }
        else
        {
            root = [];
        }

        root[section] = starter;

        string verb = exists ? $"add a \"{section}\" section" : $"create, with a \"{section}\" section";
        if (dryRun) return Result<Change?>.Ok(new Change(path, $"{verb} {what}", false));

        string body = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        string? backup = null;
        try
        {
            if (exists)
            {
                backup = path + Places.BackupSuffix;
                if (!File.Exists(backup)) File.Copy(path, backup);
            }

            File.WriteAllText(path, body.Replace("\r\n", "\n"), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<Change?>.Fail(Outcome.Denied, "could not write: " + e.Message, path);
        }

        return Result<Change?>.Ok(new Change(path, (exists ? $"added a \"{section}\" section " : $"created, with a \"{section}\" section ") + what, true, backup));
    }

    static string Word(Reach reach) => reach == Reach.Global ? "global" : "repo";

    // ---- reading and writing, only inside the closed list ----

    static Result<JsonObject> Load(Machine machine, string path)
    {
        if (!Places.Allows(machine, path))
            return Result<JsonObject>.Fail(Outcome.Refused,
                "setup does not write this file", path);

        if (!File.Exists(path)) return Result<JsonObject>.Ok([]);

        string text;
        try { text = TextIo.WithoutMark(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<JsonObject>.Fail(Outcome.Denied, "could not read: " + e.Message, path);
        }

        if (text.Trim().Length == 0) return Result<JsonObject>.Ok([]);

        try
        {
            JsonNode? node = JsonNode.Parse(text, null, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            return node is JsonObject root
                ? Result<JsonObject>.Ok(root)
                : Result<JsonObject>.Fail(Outcome.Invalid, "this is not a JSON object", path);
        }
        catch (JsonException e)
        {
            // 6.6: never a silent skip. A file that will not parse is a
            // file setup must not overwrite, because overwriting it
            // throws away whatever it holds.
            return Result<JsonObject>.Fail(Outcome.Invalid,
                "this file is not valid JSON, so setup will not overwrite it: " + e.Message, path);
        }
    }

    static Result<Change?> Save(Machine machine, string path, JsonObject root, bool dryRun, string what)
    {
        // Indented, because a reformatted ~/.claude.json is an
        // unreviewable diff and two spaces is what these files already
        // use. A minimal textual edit would preserve more, and would need
        // a JSON editor this deliberately does not have.
        string text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        return SaveText(machine, path, text, dryRun, what);
    }

    static Result<Change?> SaveText(Machine machine, string path, string text, bool dryRun, string what)
    {
        if (!Places.Allows(machine, path))
            return Result<Change?>.Fail(Outcome.Refused,
                "setup does not write this file", path);

        if (dryRun) return Result<Change?>.Ok(new Change(path, what, false));

        string? backup = null;
        try
        {
            // 3.3: back up before the first write, beside the original,
            // and say where it went.
            if (File.Exists(path))
            {
                backup = path + Places.BackupSuffix;
                if (!File.Exists(backup)) File.Copy(path, backup);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            }

            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<Change?>.Fail(Outcome.Denied, "could not write: " + e.Message, path);
        }

        return Result<Change?>.Ok(new Change(path, what, true, backup));
    }
}
