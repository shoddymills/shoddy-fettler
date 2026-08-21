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

    public sealed record Options(
        string Client,
        Level Level,
        bool DryRun = false,
        bool Force = false,
        bool Hooks = false,
        bool Deny = false,
        string? Command = null);

    public static Result<Scaffolding> Apply(Machine machine, Options options, string version)
    {
        if (!Places.Clients.Contains(options.Client))
            return Result<Scaffolding>.Fail(Outcome.Invalid,
                $"no client called '{options.Client}'; they are: {string.Join(", ", Places.Clients)}");

        var changes = new List<Change>();
        var notes = new List<string>();

        // It cannot correct installation, and must not pretend to.
        string command = options.Command ?? Environment.ProcessPath ?? "fettle";
        if (options.Command is null && Doctor.Resolve("fettle") is { } onPath)
        {
            command = "fettle";
            notes.Add($"fettle is on PATH at {onPath}, so the registration names it plainly");
        }
        else if (options.Command is null)
        {
            notes.Add($"fettle is not on PATH, so the registration names this binary by its "
                + $"absolute path: {command}. Put fettle on PATH and re-run to make it portable");
        }

        IReadOnlyList<Place> places =
            [.. Places.All(machine).Where(p => p.Client == options.Client && p.Level == options.Level)];

        if (places.Count == 0)
            return Result<Scaffolding>.Fail(Outcome.Invalid,
                $"{options.Client} has nothing at the {options.Level.ToString().ToLowerInvariant()} level");

        // 3.4: a tree that has no configuration gets one, naming the tree
        // the command was run in. Creating where none exists is
        // scaffolding; CHANGING one is the escape B.13 closes, and
        // belongs to a person with an editor.
        if (options.Level == Level.Repo)
        {
            Result<Change?> config = TreeConfig(machine, options.DryRun);
            if (!config.IsOk) return config.Carry<Scaffolding>();
            if (config.Value is { } made) changes.Add(made);
        }
        else
        {
            // A global run configures the machine and declares no tree,
            // so it leaves the one file every verb needs still missing.
            // Saying so here is the difference between a first-time setup
            // that works and one that reports success and then refuses
            // every command afterwards.
            notes.Add("this is the machine level and declares no tree; each project still needs its "
                + "own .fettler.json - run `fettle setup " + options.Client + " --local` in the tree, "
                + "which creates one declaring that folder as 'work'");
        }

        foreach (Place place in places)
        {
            Result<Change?> change = place.Purpose switch
            {
                Purpose.Servers => Register(machine, place, command, options),
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
                $"'{Places.ServerName}' is already registered here and says something different; "
                + "pass --force to replace it, or --dry-run to see what it would become",
                place.Path);

        if (replacing && !options.Force)
            return Result<Change?>.Ok(new Change(place.Path,
                $"REPLACE the existing '{Places.ServerName}' registration with "
                + $"command {command} - needs --force, and would not happen without it",
                false));

        servers[Places.ServerName] = wanted;

        return Save(machine, place.Path, root, options.DryRun,
            (replacing ? "replace " : "register ") + Places.ServerName + " under " + holder);
    }

    // ---- the instruction block ----

    static Result<Change?> Instruct(Machine machine, Place place, Options options)
    {
        string body = $"""
            {BlockOpen}
            ## Working on files here

            Use the Fettler tools (`mcp__fettler__*`) for every file operation: find,
            search, read, write, edit, move, copy, delete. They are the route, not an
            option, and the built-in Read, Write, Edit, NotebookEdit, Grep and Glob are
            denied so that habit cannot quietly take over.

            **There is no working directory.** Paths resolve against declared trees, so
            `cd` and `Set-Location` do nothing for Fettler and reaching for one is a sign
            the wrong tool is being used. Ask `roots` first: it says which trees are open,
            what may be done in each, and which one an unqualified path lands in.

            **A tree may be read-only**, and a scope inside it may grant more or less than
            the tree does. Running a declared task needs `execute`, which is never granted
            by default.

            `.fettler.json` and `.fettler.local.json` say what this tool may do - the
            trees it may touch and the tasks it may run - and it does not write them.
            A person edits those.

            Run `fettle doctor` if anything here looks wrong.
            {BlockClose}
            """;

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

    // ---- policy: offered at the global level, written at the local one ----

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
        bool write = options.Level == Level.Repo || options.Deny;

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

        var adding = Doctor.Denied.Where(tool => !held.Contains(tool)).ToList();

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
                notes.Add($"these allow entries in {place.Path} grant the very tools being denied, and "
                    + $"are LEFT ALONE because they are yours to remove: {string.Join(", ", conflicting)}");
        }

        if (adding.Count > 0 && !write)
            notes.Add($"not written: {string.Join(", ", adding)} would be denied in {place.Path}. "
                + "Changing how the assistant works in every project needs --deny said out loud");

        bool denying = write && adding.Count > 0;
        if (denying) foreach (string tool in adding) deny.Add(tool);

        // Denying the shell stops the next build, test run and git
        // command, and the allow list that restores them cannot be
        // generated from here - which project needs which command is not
        // knowable. So say it, rather than leaving somebody to discover
        // it at the first command after a run that reported success.
        if (denying && adding.Any(tool => Doctor.Shell.Contains(tool, StringComparer.Ordinal)))
            notes.Add("the shell is denied too, so every command through it now stops. Allow back "
                + "only what this project needs - \"allow\": [\"Bash(git status:*)\"] and the like "
                + "- or declare them as tasks in .fettler.json, which run inside the boundary. "
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
            _ => "add fettle doctor --hook to SessionStart (appended, nothing replaced)",
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

    // ---- the tree's own configuration ----

    static Result<Change?> TreeConfig(Machine machine, bool dryRun)
    {
        string path = Path.Combine(machine.Tree, RootsFile.FileName);
        if (File.Exists(path)) return Result<Change?>.Ok(null);

        string body = $$"""
            {
              "trees": {
                "work": { "path": "." }
              }
            }

            """;

        if (dryRun) return Result<Change?>.Ok(new Change(path, "create, declaring this folder as 'work'", false));

        try
        {
            File.WriteAllText(path, body.Replace("\r\n", "\n"), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<Change?>.Fail(Outcome.Denied, "could not write: " + e.Message, path);
        }

        return Result<Change?>.Ok(new Change(path, "created, declaring this folder as 'work'", true));
    }

    // ---- reading and writing, only inside the closed list ----

    static Result<JsonObject> Load(Machine machine, string path)
    {
        if (!Places.Allows(machine, path))
            return Result<JsonObject>.Fail(Outcome.Refused,
                "that is not one of the configuration files this may write", path);

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
                "this will not parse, and setup will not overwrite what it cannot read: " + e.Message, path);
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
                "that is not one of the configuration files this may write", path);

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
