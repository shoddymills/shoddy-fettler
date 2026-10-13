using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fettler.Core;

/// <summary>
/// Where the trees come from when the command line does not say them,
/// and the only place a permission beyond <c>list read</c> can be
/// granted.
///
/// <para><b>Three sections, one per level.</b> A configuration file holds
/// any of <c>tools</c>, <c>guardrails</c> and <c>screenings</c>, and
/// nothing else at the top. <c>guardrails</c> holds the trees, the tasks
/// and the replacements. <c>screenings</c> holds the models directory and
/// the screens. <c>tools</c> holds the generated-folder names and where
/// the client's folders are. Writing a section is what turns its level
/// on, so a reader sees which levels a project uses by reading the top
/// of the file.</para>
///
/// <para><b>No file is the tools level.</b> When the walk upward finds
/// no <c>.fettler.json</c>, the launch folder is one tree called
/// <c>work</c>, granted everything but execute. A file found above the
/// launch folder is always honoured: ignoring one would widen the
/// boundary without a word. The root of a disk, the home folder and any
/// folder above it are never made the tree this way: see
/// <see cref="Withheld"/>.</para>
///
/// <para><b>A path in the file is relative to THE FILE, never to the
/// current directory (R8.9).</b> A relative tree read against the
/// caller's cwd names a different tree from every directory. Resolving
/// against the file's own directory makes the boundary a property of the
/// tree. An absolute path in the file stays absolute.</para>
///
/// <para><b>A malformed file is a refusal, never a fallback.</b> Falling
/// back to the tools level from a broken file would widen the boundary
/// at the moment somebody had tried to state one.</para>
///
/// <para><b>Three files, merged in order.</b> The machine file first,
/// then the project's <c>.fettler.json</c>, then the gitignored
/// <c>.fettler.local.json</c> beside it. Each later file wins section by
/// section, and inside a section an entry named in both is the later
/// file's entirely. The machine file may not hold <c>guardrails</c>.</para>
/// </summary>
public static class RootsFile
{
    public const string FileName = ".fettler.json";

    /// <summary>The personal overlay: gitignored, merged over the
    /// checked-in file.</summary>
    public const string LocalFileName = ".fettler.local.json";

    /// <summary>The name of the one tree at the tools level.</summary>
    public const string WorkName = "work";

    /// <summary>A configuration that was read, or the tools level when
    /// none was found.</summary>
    /// <param name="File">The project's <c>.fettler.json</c>, or null when
    /// the walk found none.</param>
    /// <param name="Trees">The trees to open: the declared ones when
    /// guardrails are on, and the launch folder as <c>work</c> when they
    /// are off.</param>
    public sealed record Found(string? File, IReadOnlyList<TreeDecl> Trees)
    {
        /// <summary>Where the boundary came from, in words, naming every
        /// file that was read.</summary>
        public string Origin
        {
            get
            {
                string project = File is null
                    ? $"no {FileName}, so the launch folder is the tree"
                    : Overlay is null ? File : $"{File} (with {Overlay})";
                return Machine is null ? project : $"{project}, and the machine file {Machine}";
            }
        }

        public string? Overlay { get; init; }

        /// <summary>The machine file, when there was one.</summary>
        public string? Machine { get; init; }

        /// <summary>The folder the walk started from.</summary>
        public string Launch { get; init; } = "";

        public Levels Levels { get; init; } = Levels.ToolsOnly;

        /// <summary>The tasks it declared, empty when it declared none.</summary>
        public IReadOnlyList<TaskDecl> Tasks { get; init; } = [];

        /// <summary>The replacements it declared: a name, and the string a
        /// task's command line gets in its place.</summary>
        public IReadOnlyDictionary<string, string> Replacements { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The absolute path of the screening models directory,
        /// or null when no file named one.</summary>
        public string? Models { get; init; }

        /// <summary>The folder names enumeration skips, or null for the
        /// compiled-in list.</summary>
        public IReadOnlyList<string>? Generated { get; init; }

        /// <summary>Where the client's folders are, when a file said.</summary>
        public HarnessSettings Harness { get; init; } = HarnessSettings.None;
    }

    // ---- finding the files ----

    /// <summary>
    /// The configuration for a command launched in a folder: the nearest
    /// <c>.fettler.json</c> at or above it, its overlay, and the machine
    /// file.
    ///
    /// <para>Walking up is what lets a command work from a subdirectory
    /// of the tree. When the walk reaches the volume root and finds
    /// nothing, the answer is the tools level, not a refusal. Any other
    /// failure means a file WAS found and was wrong, and is reported
    /// rather than walked past.</para>
    /// </summary>
    /// <param name="launch">The folder the command was launched in.</param>
    /// <param name="machineFile">The machine file, or null for none.</param>
    /// <param name="home">The user's home folder, which is what decides
    /// whether the launch folder may be the implicit tree. Null, which is
    /// what a test on <see cref="Host.None"/> passes, makes any folder the
    /// tree.</param>
    public static Result<Found> Discover(string launch, string? machineFile = null, string? home = null)
    {
        string full;
        try
        {
            full = Path.GetFullPath(launch);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<Found>.Fail(Outcome.Invalid, $"cannot read the directory to search from: {e.Message}", launch);
        }

        for (string? dir = full; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, FileName);
            if (File.Exists(candidate)) return Assemble(candidate, full, machineFile, home);

            // A lone overlay is read as the project's file. Ignoring it
            // would leave its guardrails off without saying so.
            string lone = Path.Combine(dir, LocalFileName);
            if (File.Exists(lone)) return Assemble(lone, full, machineFile, home);
        }

        return Assemble(null, full, machineFile, home);
    }

    /// <summary>The tools level for a folder, with the machine file but
    /// without walking for a project file. This is <c>--no-config</c>.</summary>
    public static Result<Found> ToolsLevel(string launch, string? machineFile = null, string? home = null) =>
        Assemble(null, Path.GetFullPath(launch), machineFile, home);

    /// <summary>Read a named configuration and the overlay beside it.</summary>
    public static Result<Found> ReadWithOverlay(string file, string? launch = null, string? machineFile = null, string? home = null)
    {
        string full;
        try
        {
            full = Path.GetFullPath(file);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<Found>.Fail(Outcome.Invalid, $"not a usable path: {e.Message}", file);
        }

        if (!File.Exists(full))
            return Result<Found>.Fail(Outcome.NotFound, "the file does not exist", full);

        return Assemble(full, Path.GetFullPath(launch ?? Path.GetDirectoryName(full)!), machineFile, home);
    }

    /// <summary>Read one configuration file on its own: no overlay and no
    /// machine file. For a test, or for a caller that wants exactly what
    /// one file says.</summary>
    public static Result<Found> Read(string file)
    {
        string full = Path.GetFullPath(file);
        Result<Sections> one = Parse(full, Role.Project);
        if (!one.IsOk) return one.Carry<Found>();
        return Build([one.Value], full, null, null, Path.GetDirectoryName(full)!, null);
    }

    /// <summary>
    /// The boundary, re-read from a named configuration and opened.
    ///
    /// <para><b>This is serve's per-request read.</b> What is re-read is
    /// the FILE FOUND AT LAUNCH, never a fresh discovery: a configuration
    /// that has gone missing refuses, because walking up from where it
    /// was could find a broader one above it.</para>
    /// </summary>
    public static Result<Roots> Reopen(string file, string? launch = null, string? machineFile = null)
    {
        Result<Found> found = ReadWithOverlay(file, launch, machineFile);
        return found.IsOk ? Open(found.Value) : found.Carry<Roots>();
    }

    /// <summary>Open what a configuration declared, with the client's
    /// folders and any companion trees appended after the declared
    /// ones.</summary>
    public static Result<Roots> Open(Found found, IReadOnlyList<HarnessTree>? extra = null) =>
        Roots.Open(found.Trees, found.Origin, found.Tasks, found.Models,
            found.Levels, found.Generated, extra, found.File);

    /// <summary>The machine file, read, or null when there is none.</summary>
    static Result<Sections?> ReadMachine(string? machineFile)
    {
        if (machineFile is null || !File.Exists(machineFile)) return Result<Sections?>.Ok(null);

        Result<Sections> read = Parse(Path.GetFullPath(machineFile), Role.Machine);
        return read.IsOk ? Result<Sections?>.Ok(read.Value) : read.Carry<Sections?>();
    }

    static Result<Found> Assemble(string? file, string launch, string? machineFile, string? home)
    {
        Result<Sections?> machine = ReadMachine(machineFile);
        if (!machine.IsOk) return machine.Carry<Found>();

        var parts = new List<Sections>();
        if (machine.Value is { } m) parts.Add(m);

        string? overlay = null;
        if (file is not null)
        {
            Result<Sections> main = Parse(file, Role.Project);
            if (!main.IsOk) return main.Carry<Found>();
            parts.Add(main.Value);

            if (Path.GetFileName(file).Equals(FileName, Roots.PathComparison))
            {
                string beside = Path.Combine(Path.GetDirectoryName(file)!, LocalFileName);
                if (File.Exists(beside))
                {
                    Result<Sections> local = Parse(beside, Role.Project);
                    if (!local.IsOk) return local.Carry<Found>();
                    parts.Add(local.Value);
                    overlay = beside;
                }
            }
        }

        return Build(parts, file, overlay, machine.Value?.File, launch, home);
    }

    // ---- merging the files into one boundary ----

    static Result<Found> Build(IReadOnlyList<Sections> parts, string? file, string? overlay, string? machine, string launch, string? home)
    {
        var trees = new List<TreeDecl>();
        var tasks = new List<TaskDecl>();
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var screens = new List<(string Key, Screened Screen, string File)>();
        var tools = new List<string>();
        var guardrails = new List<string>();
        var screenings = new List<string>();

        IReadOnlyList<string>? generated = null;
        Named? scratch = null, scratchRoot = null, clientRoot = null;
        string? models = null;

        foreach (Sections part in parts)
        {
            if (part.Tools)
            {
                tools.Add(part.File);
                generated = part.Generated ?? generated;
                scratch = part.Scratch ?? scratch;
                scratchRoot = part.ScratchRoot ?? scratchRoot;
                clientRoot = part.ClientRoot ?? clientRoot;
            }

            if (part.Guardrails)
            {
                guardrails.Add(part.File);
                ByName(trees, part.Trees, t => t.Name, Roots.PathComparison);
                ByName(tasks, part.Tasks, t => t.Name, StringComparison.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> one in part.Replacements) replacements[one.Key] = one.Value;
            }

            if (part.Screenings)
            {
                screenings.Add(part.File);
                models = part.Models ?? models;
                foreach ((string key, Screened screen) in part.Screens)
                {
                    screens.RemoveAll(s => s.Key.Equals(key, Roots.PathComparison));
                    screens.Add((key, screen, part.File));
                }
            }
        }

        string at = file ?? launch;

        if (guardrails.Count > 0)
        {
            if (trees.Count == 0)
                return Result<Found>.Fail(Outcome.Invalid,
                    "has a \"guardrails\" section that declares no trees. Add a \"trees\" object to it, "
                    + "or remove the section to use the launch folder as the tree", at);

            foreach (TreeDecl tree in trees)
                foreach (string reserved in Harness.Names)
                    if (tree.Name.Equals(reserved, Roots.PathComparison))
                        return Result<Found>.Fail(Outcome.Invalid,
                            $"declares a tree called '{tree.Name}'. fettle gives that name to the client's "
                            + $"{reserved} folder and finds the folder itself. Remove the tree, or give it "
                            + "another name", at);
        }
        else
        {
            if (Withheld(launch, home) is { } what)
                return Result<Found>.Fail(Outcome.Refused,
                    (file is null ? $"no {FileName} was found" : "no \"guardrails\" section declares a tree")
                    + $", and the launch folder {launch} is {what}, which fettle does not make a tree. Start fettle "
                    + "in a project folder, or pass --root PATH for read-only access, or --config PATH naming a "
                    + "file whose \"guardrails\" declares the trees", at);

            trees.Add(new TreeDecl(WorkName, launch, Permissions.Full));
        }

        foreach ((string key, Screened screen, string from) in screens)
        {
            Result<bool> applied = ApplyScreen(trees, key, screen, from);
            if (!applied.IsOk) return applied.Carry<Found>();
        }

        var found = new Found(file, trees)
        {
            Overlay = overlay,
            Machine = machine,
            Launch = launch,
            Levels = new Levels(tools, guardrails, screenings),
            Tasks = tasks,
            Replacements = replacements,
            Models = models,
            Generated = generated,
            Harness = new HarnessSettings(scratch, scratchRoot, clientRoot),
        };

        return Fill(found, at);
    }

    /// <summary>
    /// What the launch folder is when it may not be the implicit tree, or
    /// null when it may.
    ///
    /// <para><b>The root of a disk, the home folder and any folder above
    /// it are never the implicit tree.</b> A client that starts the server
    /// outside any project starts it in one of these: Claude Desktop has no
    /// project and starts it at the root. Making that folder the tree would
    /// hand the assistant the whole disk, read and write, with no file
    /// saying so. A declared tree is not checked here, because a person
    /// wrote it down. No home means no check, which is what a test on
    /// <see cref="Host.None"/> gets.</para>
    /// </summary>
    public static string? Withheld(string launch, string? home)
    {
        if (home is not { Length: > 0 }) return null;

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(launch));
        string own = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));

        if (Path.GetPathRoot(full) is { } root
            && Path.TrimEndingDirectorySeparator(root).Equals(full, Roots.PathComparison))
            return "the root of the disk";

        if (full.Equals(own, Roots.PathComparison)) return "your home folder";
        if (Roots.IsWithin(full, own)) return "above your home folder";

        return null;
    }

    /// <summary>The later file's entries over the earlier ones: a name in
    /// both is the later file's ENTIRELY, and a name only the later file
    /// has is added. Field-level merging would let a local file grant a
    /// permission by mentioning one key, and nobody reading the two files
    /// side by side could say what the result was.</summary>
    static void ByName<T>(List<T> into, IReadOnlyList<T> later, Func<T, string> name, StringComparison comparison)
    {
        foreach (T entry in later)
        {
            int at = into.FindIndex(e => name(e).Equals(name(entry), comparison));
            if (at >= 0) into[at] = entry;
            else into.Add(entry);
        }
    }

    /// <summary>
    /// Put one <c>screen</c> entry on the tree or scope it names.
    ///
    /// <para>The key is a tree name, or a tree name, a slash, and a scope
    /// path. The name must be a tree the boundary opens, and the scope
    /// must be one that tree declares. A screen naming nothing would
    /// screen nothing, and the person who wrote it would believe
    /// otherwise.</para>
    /// </summary>
    static Result<bool> ApplyScreen(List<TreeDecl> trees, string key, Screened screen, string file)
    {
        int slash = key.IndexOf('/');
        string treeName = slash < 0 ? key : key[..slash];
        string? scope = slash < 0 ? null : key[(slash + 1)..].TrimEnd('/');

        int index = trees.FindIndex(t => t.Name.Equals(treeName, Roots.PathComparison));
        if (index < 0)
            return Result<bool>.Fail(Outcome.Invalid,
                $"has a \"screen\" for '{treeName}', which is not a tree. The trees are: "
                + string.Join(", ", trees.Select(t => t.Name)), file);

        TreeDecl tree = trees[index];

        if (scope is null || scope.Length == 0)
        {
            trees[index] = tree with { Screen = screen };
            return Result<bool>.Ok(true);
        }

        var scopes = new List<ScopeDecl>(tree.Scopes ?? []);
        int at = scopes.FindIndex(s => s.Relative.Replace('\\', '/').TrimEnd('/').Equals(scope, Roots.PathComparison));
        if (at < 0)
            return Result<bool>.Fail(Outcome.Invalid,
                $"has a \"screen\" for '{key}', and tree '{tree.Name}' declares no scope '{scope}'. "
                + "Declare the scope under the tree in \"guardrails\", or screen the whole tree", file);

        scopes[at] = scopes[at] with { Screen = screen };
        trees[index] = tree with { Scopes = scopes };
        return Result<bool>.Ok(true);
    }

    // ---- reading one file ----

    enum Role { Project, Machine }

    /// <summary>What one file said, section by section.</summary>
    sealed record Sections(string File)
    {
        public bool Tools { get; init; }
        public IReadOnlyList<string>? Generated { get; init; }
        public Named? Scratch { get; init; }
        public Named? ScratchRoot { get; init; }
        public Named? ClientRoot { get; init; }

        public bool Guardrails { get; init; }
        public IReadOnlyList<TreeDecl> Trees { get; init; } = [];
        public IReadOnlyList<TaskDecl> Tasks { get; init; } = [];
        public IReadOnlyDictionary<string, string> Replacements { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public bool Screenings { get; init; }
        public string? Models { get; init; }
        public IReadOnlyList<(string Key, Screened Screen)> Screens { get; init; } = [];
    }

    /// <summary>The keys the old flat shape put at the top.</summary>
    static readonly string[] OldKeys = ["trees", "tasks", "replacements", "models"];

    static Result<Sections> Parse(string full, Role role)
    {
        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return Result<Sections>.Fail(Outcome.NotFound, "the file does not exist", full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<Sections>.Fail(Outcome.Denied, $"could not read the file: {e.Message}", full);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException e)
        {
            return Result<Sections>.Fail(Outcome.Invalid, $"is not valid JSON: {e.Message}", full);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Result<Sections>.Fail(Outcome.Invalid,
                    "must hold a JSON object. Its keys are any of: tools, guardrails, screenings", full);

            // The oldest shape meant something different: a root was
            // readable and writable in full. Reading it as anything else
            // would GRANT more than it says.
            if (root.TryGetProperty("roots", out _))
                return Result<Sections>.Fail(Outcome.Invalid, Migration, full);

            // The flat shape of the last release. Refused rather than
            // read, because a file that means one thing to the old version
            // and another to this one is a file whose boundary nobody can
            // state. The message carries the file's own entries rewritten.
            foreach (string old in OldKeys)
                if (root.TryGetProperty(old, out _))
                    return Result<Sections>.Fail(Outcome.Invalid, Rewrite(root, old), full);

            string here = Path.GetDirectoryName(full)!;
            var sections = new Sections(full);

            foreach (JsonProperty top in root.EnumerateObject())
            {
                switch (top.Name)
                {
                    case Levels.ToolsName:
                    {
                        Result<Sections> read = ReadTools(top.Value, here, full, sections);
                        if (!read.IsOk) return read;
                        sections = read.Value;
                        break;
                    }

                    case Levels.GuardrailsName:
                    {
                        if (role == Role.Machine)
                            return Result<Sections>.Fail(Outcome.Invalid,
                                "is the machine file, and it has a \"guardrails\" section. A tree, a task or an "
                                + "execute grant here would apply to every project on the machine, and no project "
                                + $"reviewed it. Move the section to a project's {FileName}", full);

                        Result<Sections> read = ReadGuardrails(top.Value, here, full, sections);
                        if (!read.IsOk) return read;
                        sections = read.Value;
                        break;
                    }

                    case Levels.ScreeningsName:
                    {
                        Result<Sections> read = ReadScreenings(top.Value, here, full, sections);
                        if (!read.IsOk) return read;
                        sections = read.Value;
                        break;
                    }

                    default:
                        return Result<Sections>.Fail(Outcome.Invalid,
                            $"has \"{top.Name}\" at the top. The top holds three sections, one per level: "
                            + "tools, guardrails, screenings", full);
                }
            }

            return Result<Sections>.Ok(sections);
        }
    }

    /// <summary>The keys a <c>tools</c> section takes.</summary>
    static readonly string[] ToolsKeys = ["generated", "scratch", "scratch-root", "client-root"];

    static Result<Sections> ReadTools(JsonElement value, string here, string file, Sections sections)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return Result<Sections>.Fail(Outcome.Invalid,
                $"has a \"tools\" that is not an object. It takes: {string.Join(", ", ToolsKeys)}", file);

        IReadOnlyList<string>? generated = null;
        Named? scratch = null, scratchRoot = null, clientRoot = null;

        foreach (JsonProperty entry in value.EnumerateObject())
        {
            switch (entry.Name)
            {
                case "generated":
                {
                    if (entry.Value.ValueKind != JsonValueKind.Array)
                        return Result<Sections>.Fail(Outcome.Invalid,
                            "has a \"generated\" under \"tools\" that is not an array of folder names", file);

                    var names = new List<string>();
                    foreach (JsonElement name in entry.Value.EnumerateArray())
                    {
                        if (name.ValueKind != JsonValueKind.String
                            || name.GetString() is not { Length: > 0 } one
                            || one.Contains('/') || one.Contains('\\'))
                            return Result<Sections>.Fail(Outcome.Invalid,
                                "has a \"generated\" entry that is not a folder name. Write each one as a "
                                + "name with no slash, such as \"bin\"", file);
                        names.Add(one);
                    }

                    generated = names;
                    break;
                }

                case "scratch":
                case "scratch-root":
                case "client-root":
                {
                    Result<string> path = ReadPath(entry.Value, $"\"{entry.Name}\" under \"tools\"", here, file);
                    if (!path.IsOk) return path.Carry<Sections>();

                    var named = new Named(path.Value, file);
                    if (entry.Name == "scratch") scratch = named;
                    else if (entry.Name == "scratch-root") scratchRoot = named;
                    else clientRoot = named;
                    break;
                }

                default:
                    return Result<Sections>.Fail(Outcome.Invalid,
                        $"has \"{entry.Name}\" under \"tools\". It takes: {string.Join(", ", ToolsKeys)}", file);
            }
        }

        return Result<Sections>.Ok(sections with
        {
            Tools = true,
            Generated = generated,
            Scratch = scratch,
            ScratchRoot = scratchRoot,
            ClientRoot = clientRoot,
        });
    }

    /// <summary>The keys a <c>guardrails</c> section takes.</summary>
    static readonly string[] GuardrailsKeys = ["trees", "tasks", "replacements"];

    static Result<Sections> ReadGuardrails(JsonElement value, string here, string file, Sections sections)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return Result<Sections>.Fail(Outcome.Invalid,
                $"has a \"guardrails\" that is not an object. It takes: {string.Join(", ", GuardrailsKeys)}", file);

        foreach (JsonProperty entry in value.EnumerateObject())
            if (!GuardrailsKeys.Contains(entry.Name, StringComparer.Ordinal))
                return Result<Sections>.Fail(Outcome.Invalid,
                    $"has \"{entry.Name}\" under \"guardrails\". It takes: {string.Join(", ", GuardrailsKeys)}", file);

        var decls = new List<TreeDecl>();
        if (value.TryGetProperty("trees", out JsonElement trees))
        {
            if (trees.ValueKind != JsonValueKind.Object)
                return Result<Sections>.Fail(Outcome.Invalid,
                    "has a \"trees\" under \"guardrails\" that is not an object. Each entry is a name and an "
                    + "object with a \"path\" in it", file);

            foreach (JsonProperty entry in trees.EnumerateObject())
            {
                Result<TreeDecl> one = ReadTree(entry, here, file);
                if (!one.IsOk) return one.Carry<Sections>();
                decls.Add(one.Value);
            }
        }

        Result<IReadOnlyList<TaskDecl>> tasks = ReadTasks(value, file);
        if (!tasks.IsOk) return tasks.Carry<Sections>();

        Result<IReadOnlyDictionary<string, string>> replacements = ReadReplacements(value, file);
        if (!replacements.IsOk) return replacements.Carry<Sections>();

        // The tasks are returned with their placeholders INTACT. Fill runs
        // after every file is merged, because the checked-in file declares
        // a task and the overlay supplies today's value.
        return Result<Sections>.Ok(sections with
        {
            Guardrails = true,
            Trees = decls,
            Tasks = tasks.Value,
            Replacements = replacements.Value,
        });
    }

    /// <summary>The keys a <c>screenings</c> section takes.</summary>
    static readonly string[] ScreeningsKeys = ["models", "screen"];

    static Result<Sections> ReadScreenings(JsonElement value, string here, string file, Sections sections)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return Result<Sections>.Fail(Outcome.Invalid,
                $"has a \"screenings\" that is not an object. It takes: {string.Join(", ", ScreeningsKeys)}", file);

        string? models = null;
        var screens = new List<(string, Screened)>();

        foreach (JsonProperty entry in value.EnumerateObject())
        {
            switch (entry.Name)
            {
                case "models":
                {
                    // Not checked for existence here: a file read on a
                    // machine without the models is not malformed. The
                    // refusal belongs at the disclosure, where it can name
                    // the category that wanted the model.
                    Result<string> path = ReadPath(entry.Value, "\"models\" under \"screenings\"", here, file);
                    if (!path.IsOk) return path.Carry<Sections>();
                    models = path.Value;
                    break;
                }

                case "screen":
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object)
                        return Result<Sections>.Fail(Outcome.Invalid,
                            "has a \"screen\" under \"screenings\" that is not an object. Each key is a tree "
                            + "name, or a tree name, a slash and a scope path", file);

                    foreach (JsonProperty one in entry.Value.EnumerateObject())
                    {
                        if (one.Name.Length == 0 || one.Name.StartsWith('/'))
                            return Result<Sections>.Fail(Outcome.Invalid,
                                "has a \"screen\" key that names no tree. Write the tree name first", file);

                        if (one.Name.Contains('\\'))
                            return Result<Sections>.Fail(Outcome.Invalid,
                                $"has a \"screen\" key '{one.Name}' written with a backslash. Write it with \"/\"", file);

                        Result<Screened> read = ReadScreen(one.Value, $"the \"screen\" for '{one.Name}'", file);
                        if (!read.IsOk) return read.Carry<Sections>();
                        screens.Add((one.Name, read.Value));
                    }
                    break;
                }

                default:
                    return Result<Sections>.Fail(Outcome.Invalid,
                        $"has \"{entry.Name}\" under \"screenings\". It takes: {string.Join(", ", ScreeningsKeys)}", file);
            }
        }

        return Result<Sections>.Ok(sections with { Screenings = true, Models = models, Screens = screens });
    }

    /// <summary>A path named in a file, resolved against the file's own
    /// folder.</summary>
    static Result<string> ReadPath(JsonElement value, string what, string here, string file)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } raw)
            return Result<string>.Fail(Outcome.Invalid, $"has a {what} that is not a non-empty string", file);

        if (Separator(raw, $"the {what} path", file) is { } bad) return Result<string>.Fail(bad);

        try
        {
            return Result<string>.Ok(Path.GetFullPath(Path.Combine(here, raw)));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<string>.Fail(Outcome.Invalid, $"has a {what} that is not a usable path: {e.Message}", file);
        }
    }

    // ---- the migration messages ----

    /// <summary>The one message a caller with the oldest shape sees, so
    /// it says the whole of what to do.</summary>
    public const string Migration =
        "uses \"roots\", which this version does not read. Rewrite "
        + "{\"roots\":{\"work\":\".\"}} as "
        + "{\"guardrails\":{\"trees\":{\"work\":{\"path\":\".\"}}}}. The tree holding this file "
        + "gets list read create update rename delete. Every other tree gets "
        + "list read unless \"can\" says more. execute is never a default.";

    /// <summary>
    /// The refusal for the flat shape, holding the file's own entries
    /// under the new sections, so the fix is to paste it.
    ///
    /// <para><c>trees</c>, <c>tasks</c> and <c>replacements</c> go under
    /// <c>guardrails</c>. <c>models</c> goes under <c>screenings</c>.
    /// Each <c>screen</c> comes out of its tree or scope and goes under
    /// <c>screenings</c>, keyed by the tree name, or the tree name, a
    /// slash and the scope path.</para>
    /// </summary>
    static string Rewrite(JsonElement root, string found)
    {
        JsonObject old = JsonNode.Parse(root.GetRawText(), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })!.AsObject();

        var guardrails = new JsonObject();
        var screen = new JsonObject();

        if (old["trees"] is JsonObject trees)
        {
            var moved = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> tree in trees)
            {
                JsonNode? copy = tree.Value?.DeepClone();
                if (copy is JsonObject entry)
                {
                    if (entry.Remove("screen", out JsonNode? s)) screen[tree.Key] = s;

                    if (entry["scopes"] is JsonObject scopes)
                        foreach (KeyValuePair<string, JsonNode?> scope in scopes)
                            if (scope.Value is JsonObject declared && declared.Remove("screen", out JsonNode? ss))
                                screen[$"{tree.Key}/{scope.Key.TrimEnd('/')}"] = ss;
                }
                moved[tree.Key] = copy;
            }
            guardrails["trees"] = moved;
        }

        if (old["tasks"] is { } tasks) guardrails["tasks"] = tasks.DeepClone();
        if (old["replacements"] is { } replacements) guardrails["replacements"] = replacements.DeepClone();

        var screenings = new JsonObject();
        if (old["models"] is { } models) screenings["models"] = models.DeepClone();
        if (screen.Count > 0) screenings["screen"] = screen;

        var rewritten = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> keep in old)
            if (keep.Key is Levels.ToolsName) rewritten[keep.Key] = keep.Value?.DeepClone();
        if (guardrails.Count > 0) rewritten[Levels.GuardrailsName] = guardrails;
        if (screenings.Count > 0) rewritten[Levels.ScreeningsName] = screenings;

        string json = rewritten.ToJsonString(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        return $"has \"{found}\" at the top, which is the shape this version no longer reads. "
            + "Rewrite the file with three sections, one per level. Your entries, rewritten: " + json;
    }

    // ---- the entries inside a section ----

    static Result<TreeDecl> ReadTree(JsonProperty entry, string here, string file)
    {
        if (entry.Value.ValueKind == JsonValueKind.String)
            return Result<TreeDecl>.Fail(Outcome.Invalid,
                $"tree '{entry.Name}' is written as a bare path; it needs an object, "
                + $"as {{\"{entry.Name}\":{{\"path\":\"{entry.Value.GetString()}\"}}}}", file);

        if (entry.Value.ValueKind != JsonValueKind.Object)
            return Result<TreeDecl>.Fail(Outcome.Invalid,
                $"tree '{entry.Name}' must be an object with a \"path\" in it", file);

        if (!entry.Value.TryGetProperty("path", out JsonElement pathValue)
            || pathValue.ValueKind != JsonValueKind.String
            || pathValue.GetString() is not { Length: > 0 } raw)
            return Result<TreeDecl>.Fail(Outcome.Invalid,
                $"tree '{entry.Name}' needs a \"path\" written as a non-empty string", file);

        if (Separator(raw, $"tree '{entry.Name}' has a \"path\" that", file) is { } badPath)
            return Result<TreeDecl>.Fail(badPath);

        // A screen is a screenings fact and lives in that section, so a
        // reader can see whether a project screens anything by reading
        // one place.
        if (entry.Value.TryGetProperty("screen", out _))
            return Result<TreeDecl>.Fail(Outcome.Invalid, ScreenMoved($"tree '{entry.Name}'", entry.Name), file);

        string path;
        try
        {
            // Combine leaves an absolute path alone, so a caller who
            // means one may still write one.
            path = Path.GetFullPath(Path.Combine(here, raw));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<TreeDecl>.Fail(Outcome.Invalid,
                $"tree '{entry.Name}' is not a usable path: {e.Message}", file);
        }

        // The default depends on WHERE the tree is, not on how it was
        // spelled: the folder this configuration sits in is the one the
        // author is working in, and everything else is somebody else's.
        bool isOwnFolder = Roots.IsWithin(path, here) && Roots.IsWithin(here, path);
        Permission can = isOwnFolder ? Permissions.Full : Permissions.ReadOnly;

        if (entry.Value.TryGetProperty("can", out JsonElement canValue))
        {
            Result<Permission> parsed = ReadCan(canValue, $"tree '{entry.Name}'", file);
            if (!parsed.IsOk) return parsed.Carry<TreeDecl>();
            can = parsed.Value;
        }

        var scopes = new List<ScopeDecl>();
        if (entry.Value.TryGetProperty("scopes", out JsonElement scopeValue))
        {
            if (scopeValue.ValueKind != JsonValueKind.Object)
                return Result<TreeDecl>.Fail(Outcome.Invalid,
                    $"tree '{entry.Name}' has \"scopes\" that is not an object of path to permissions", file);

            foreach (JsonProperty scope in scopeValue.EnumerateObject())
            {
                if (scope.Value.ValueKind != JsonValueKind.Object
                    || !scope.Value.TryGetProperty("can", out JsonElement scopeCan))
                    return Result<TreeDecl>.Fail(Outcome.Invalid,
                        $"scope '{scope.Name}' in tree '{entry.Name}' needs a \"can\" array, "
                        + "which may be empty to hide it", file);

                if (scope.Value.TryGetProperty("screen", out _))
                    return Result<TreeDecl>.Fail(Outcome.Invalid,
                        ScreenMoved($"scope '{scope.Name}' in tree '{entry.Name}'",
                            $"{entry.Name}/{scope.Name.TrimEnd('/')}"), file);

                Result<Permission> parsed = ReadCan(scopeCan, $"scope '{scope.Name}'", file);
                if (!parsed.IsOk) return parsed.Carry<TreeDecl>();

                if (Separator(scope.Name, $"scope '{scope.Name}' in tree '{entry.Name}'", file) is { } badScope)
                    return Result<TreeDecl>.Fail(badScope);

                // Null: the scope inherits the tree's screen until a
                // "screen" entry under "screenings" names it.
                scopes.Add(new ScopeDecl(scope.Name, parsed.Value, null));
            }
        }

        return Result<TreeDecl>.Ok(new TreeDecl(entry.Name, path, can, scopes, Screened.None));
    }

    static string ScreenMoved(string what, string key) =>
        $"{what} has a \"screen\". A screen now goes under \"screenings\", keyed by the tree or scope: "
        + $"{{\"screenings\":{{\"screen\":{{\"{key}\":true}}}}}}";

    /// <summary>
    /// The declared tasks of R7, read from the <c>guardrails</c> section.
    ///
    /// <para><b><c>run</c> is the command line, written as one string.</b>
    /// It is split once, by <see cref="Tasks.Split"/>, whose grammar is
    /// whitespace and double quotes and nothing else - no variables, no
    /// globbing, no operators, and no backslash escapes, so a Windows path
    /// is written as it is. The program is then launched with the resulting
    /// list, never with a command line for a shell to re-split.</para>
    ///
    /// <para><b>The file is trusted input, and R7.6 says so out loud.</b>
    /// Whoever can write this file chooses what a caller - a model
    /// included - is able to execute, which is why writing it is a
    /// person's act with an editor and why <c>execute</c> is never a
    /// default.</para>
    /// </summary>
    static Result<IReadOnlyList<TaskDecl>> ReadTasks(JsonElement section, string file)
    {
        if (!section.TryGetProperty("tasks", out JsonElement tasks))
            return Result<IReadOnlyList<TaskDecl>>.Ok([]);

        if (tasks.ValueKind != JsonValueKind.Object)
            return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                "has a \"tasks\" that is not an object of name to declaration", file);

        var declared = new List<TaskDecl>();
        foreach (JsonProperty entry in tasks.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
                return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                    $"task '{entry.Name}' needs an object with a \"run\" string in it, as "
                    + $"{{\"{entry.Name}\":{{\"run\":\"pwsh -File build.ps1\"}}}}", file);

            if (!entry.Value.TryGetProperty("run", out JsonElement run)
                || run.ValueKind != JsonValueKind.String
                || run.GetString() is not { } line)
                return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                    $"task '{entry.Name}' needs a \"run\" written as one string, the command line", file);

            Result<IReadOnlyList<string>> split = Tasks.Split(line);
            if (!split.IsOk)
                return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                    $"task '{entry.Name}': {split.Failure!.Message}", file);

            IReadOnlyList<string> command = split.Value;
            if (command.Count == 0)
                return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                    $"task '{entry.Name}' has an empty \"run\"; its first word is the program", file);

            string? cwd = null;
            if (entry.Value.TryGetProperty("cwd", out JsonElement where))
            {
                if (where.ValueKind != JsonValueKind.String
                    || where.GetString() is not { Length: > 0 } named)
                    return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                        $"task '{entry.Name}' has a \"cwd\" that is not a non-empty string", file);

                if (Separator(named, $"task '{entry.Name}' has a \"cwd\" that", file) is { } bad)
                    return Result<IReadOnlyList<TaskDecl>>.Fail(bad);

                cwd = named;
            }

            foreach (TaskDecl already in declared)
                if (already.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
                    return Result<IReadOnlyList<TaskDecl>>.Fail(Outcome.Invalid,
                        $"task '{entry.Name}' is declared twice", file);

            declared.Add(new TaskDecl(entry.Name, command, cwd, line));
        }

        return Result<IReadOnlyList<TaskDecl>>.Ok(declared);
    }

    /// <summary>
    /// A declared path is written with forward slashes, on every platform.
    ///
    /// <para><b>There is no formal standard for this - there is a
    /// convention, and it is git's:</b> relative, forward-slashed, no drive
    /// letter. On macOS and Linux a backslash is an ordinary character in a
    /// file name rather than a separator, so a checked-in <c>..\tree</c>
    /// does not name a parent there.</para>
    ///
    /// <para><b>Refused rather than converted</b>, because converting would
    /// make a file legitimately called <c>a\b</c> unreachable on the
    /// platforms where that is a legal name.</para>
    ///
    /// <para><b>Only a RELATIVE path is held to it.</b> An absolute path
    /// names a particular disk and cannot travel whatever its separators
    /// are, so <c>C:\work\tree</c> in a gitignored
    /// <c>.fettler.local.json</c> is left alone.</para>
    /// </summary>
    static Failure? Separator(string raw, string what, string file) =>
        raw.Contains('\\') && !Absolute(raw)
            ? new Failure(Outcome.Invalid,
                $"{what} is written with a backslash. Write a relative path with \"/\"; "
                + "it works on every platform", file)
            : null;

    /// <summary>Whether a declared path names a particular disk. A drive
    /// letter is recognised on every platform, because the question being
    /// asked is what the author MEANT.</summary>
    static bool Absolute(string raw) =>
        Path.IsPathRooted(raw)
        || (raw.Length > 1 && raw[1] == ':' && char.IsLetter(raw[0]))
        || raw.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// The declared replacements: a name, and the string a task's command
    /// line gets in its place.
    ///
    /// <para><b>The value comes from this file and never from the
    /// caller.</b> <c>run</c> still takes a task name and a timeout and
    /// nothing else, so a model cannot choose any part of what a command
    /// executes.</para>
    ///
    /// <para><b>They are not for secrets.</b> Every value is shown by
    /// <c>tasks</c>, deliberately, so that what is listed is what runs.</para>
    /// </summary>
    static Result<IReadOnlyDictionary<string, string>> ReadReplacements(JsonElement section, string file)
    {
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!section.TryGetProperty("replacements", out JsonElement replacements))
            return Result<IReadOnlyDictionary<string, string>>.Ok(declared);

        if (replacements.ValueKind != JsonValueKind.Object)
            return Result<IReadOnlyDictionary<string, string>>.Fail(Outcome.Invalid,
                "has a \"replacements\" that is not an object of name to string", file);

        foreach (JsonProperty entry in replacements.EnumerateObject())
        {
            if (!Tasks.IsName(entry.Name))
                return Result<IReadOnlyDictionary<string, string>>.Fail(Outcome.Invalid,
                    $"replacement '{entry.Name}' has a name no task could refer to; a name is "
                    + "letters, digits, dot, underscore or hyphen, and at least one of them", file);

            if (entry.Value.ValueKind != JsonValueKind.String || entry.Value.GetString() is not { } value)
                return Result<IReadOnlyDictionary<string, string>>.Fail(Outcome.Invalid,
                    $"replacement '{entry.Name}' needs its value written as a string", file);

            declared[entry.Name] = value;
        }

        return Result<IReadOnlyDictionary<string, string>>.Ok(declared);
    }

    /// <summary>
    /// The tasks with their placeholders filled from the replacements.
    ///
    /// <para><b>This happens once every file has been merged</b>, because
    /// the checked-in file declares the task and the gitignored one beside
    /// it supplies today's value.</para>
    /// </summary>
    static Result<Found> Fill(Found found, string file)
    {
        if (found.Tasks.Count == 0) return Result<Found>.Ok(found);

        var filled = new List<TaskDecl>(found.Tasks.Count);

        foreach (TaskDecl task in found.Tasks)
        {
            Result<IReadOnlyList<string>> command =
                Tasks.Fill(task.Command, found.Replacements, task.Name, file);
            if (!command.IsOk) return command.Carry<Found>();

            // Line is left exactly as declared, braces and all: it is what
            // TaskDecl.Display shows.
            filled.Add(task with { Command = command.Value });
        }

        return Result<Found>.Ok(found with { Tasks = filled });
    }

    /// <summary>
    /// The three forms of R1.4: <c>true</c> for every category,
    /// a bare list to include, and a list of <c>-</c> words to exclude.
    ///
    /// <para><b><c>true</c> means all of them</b>, so the failure mode of
    /// switching the screen on and forgetting to name a category does not
    /// exist. Excluding one takes a single word.</para>
    /// </summary>
    static Result<Screened> ReadScreen(JsonElement value, string what, string file)
    {
        if (value.ValueKind == JsonValueKind.True)
            return Result<Screened>.Ok(Screens.Everything);

        if (value.ValueKind == JsonValueKind.False)
            return Result<Screened>.Ok(Screened.None);

        if (value.ValueKind != JsonValueKind.Array)
            return Result<Screened>.Fail(Outcome.Invalid,
                $"{what} is not true, false, or an array of category "
                + $"words; they are: {string.Join(", ", Screens.All.Select(Screens.NameOf))}", file);

        var words = new List<string>();
        foreach (JsonElement word in value.EnumerateArray())
        {
            if (word.ValueKind != JsonValueKind.String)
                return Result<Screened>.Fail(Outcome.Invalid,
                    $"{what} lists a screening category that is not a word", file);
            words.Add(word.GetString()!);
        }

        Result<Screened> parsed = Screens.Parse(words);
        if (!parsed.IsOk)
            return Result<Screened>.Fail(Outcome.Invalid, $"{what} {parsed.Failure!.Message}", file);

        return parsed;
    }

    static Result<Permission> ReadCan(JsonElement value, string what, string file)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return Result<Permission>.Fail(Outcome.Invalid,
                $"{what} has a \"can\" that is not an array of permission words", file);

        var words = new List<string>();
        foreach (JsonElement word in value.EnumerateArray())
        {
            if (word.ValueKind != JsonValueKind.String)
                return Result<Permission>.Fail(Outcome.Invalid,
                    $"{what} lists a permission that is not a word", file);
            words.Add(word.GetString()!);
        }

        Result<Permission> parsed = Permissions.Parse(words);
        if (!parsed.IsOk)
            return Result<Permission>.Fail(Outcome.Invalid, $"{what}: {parsed.Failure!.Message}", file);

        return parsed;
    }
}
