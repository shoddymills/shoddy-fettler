using Fettler.Core;

namespace Fettler.Cli;

/// <summary>
/// A parsed command line: the verb, its positional arguments, and its
/// flags. Deliberately small - there is no options library here and no
/// need for one, because R1.2 forbids a dependency and the surface is a
/// verb, some paths and some flags.
///
/// <para>Flags are <c>--name</c> or <c>--name value</c>, and repeat where
/// the verb says they may (<c>--root</c>, <c>--exclude</c>, <c>-e</c>).
/// Anything after a bare <c>--</c> is positional whatever it looks like,
/// which is how a path beginning with a hyphen gets through.</para>
/// </summary>
public sealed class Arguments
{
    readonly Dictionary<string, List<string>> flags = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> positional = [];
    readonly List<string> valueless = [];
    readonly List<string> shortUnknown = [];

    public string Verb { get; private set; } = string.Empty;

    public IReadOnlyList<string> Positional => positional;

    /// <summary>Flags that take no value, so the parser does not eat the
    /// next argument as one. Everything else takes a value.</summary>
    static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "overwrite", "recursive", "force", "dry-run", "stdin", "literal",
        "case-sensitive", "include-generated", "count", "files-only", "sort-modified",
        "preserve-times", "all", "on", "off", "help", "version", "no-documents",

        // the credential override
        "allow-credential",

        // doctor and setup
        "quiet", "hook", "global", "local", "hooks", "deny",

        // R5.11: each names a stream rather than a value, so the parser
        // must not eat the following argument as one.
        "replace-stdin", "with-stdin", "text-stdin",

        // R8.9: names no value - it turns the search for a .fettler.json
        // off rather than pointing at a different one.
        "no-config",

        // R4.20: the search pattern, arriving as bytes rather than as an
        // argument some shell has already had an opinion about.
        "pattern-stdin",
    };

    /// <summary>
    /// Every flag any verb accepts, in one set, so that a flag no verb
    /// knows can be REFUSED rather than ignored (R3.14).
    ///
    /// <para>Ignoring one is not a small discourtesy. A flag that takes a
    /// value also eats the argument after it, so a misspelt
    /// <c>--gob PATTERN</c> silently swallows the pattern and the command
    /// then searches the whole tree for something else and reports the
    /// answer with complete confidence. A typo has to fail, because the
    /// alternative is not a failure but a wrong answer.</para>
    ///
    /// <para><b>The limit, stated rather than implied:</b> this is one
    /// set across all verbs, so it catches a misspelling and not a flag
    /// handed to the wrong verb - <c>read --overwrite</c> still passes.
    /// A table per verb would catch both and would have to be kept in
    /// step with the dispatcher by hand; one list that cannot drift from
    /// itself is worth more than a second list that can.</para>
    /// </summary>
    static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // the boundary, and the shape of the answer
        "root", "config", "no-config", "json", "include-generated", "exclude",
        "help", "version",

        // find
        "sort", "sort-modified", "since", "limit",

        // search
        "pattern", "pattern-file", "pattern-stdin", "glob", "in", "literal",
        "case-sensitive", "context", "count", "files-only", "no-documents",

        // read
        "from", "to", "tail", "member",

        // extract
        "into",

        // write
        "stdin", "overwrite", "encoding", "eol",

        // The credential override, offered here and by nothing else. A
        // model driving the MCP front end has no way to ask for it, in
        // the same way it is never offered `setup`: deciding that a
        // high-entropy string is not a secret is a judgement about your
        // own file, and a person makes it.
        "allow-credential",

        // edit, and the R5.11 spellings of the text it is given
        "expect", "dry-run", "between", "all", "insert-after", "delete",
        "script", "script-inline",
        "replace", "replace-file", "replace-stdin",
        "with", "with-file", "with-stdin",
        "text", "text-file", "text-stdin",

        // move, copy, delete, exec, run
        "recursive", "preserve-times", "force", "on", "off", "timeout",

        // doctor and setup, which look at the machine rather than a tree
        "client", "quiet", "hook", "global", "local", "hooks", "deny", "command",
    };

    /// <summary>Whether any verb accepts this flag. Public so a test can
    /// hold the help text and this set against each other: the help is
    /// the only place a caller learns the names, and a name that appears
    /// there and is refused here is worse than either alone.</summary>
    public static bool IsKnown(string flag) => Known.Contains(flag);

    /// <summary>Flags that were given and that no verb knows, spelt as
    /// they were written - one dash or two - because a caller looking for
    /// their typo needs to see the thing they typed.</summary>
    public IReadOnlyList<string> UnknownFlags
    {
        get
        {
            var unknown = new List<string>();
            foreach (string name in flags.Keys)
                if (!Known.Contains(name)) unknown.Add("--" + name);
            unknown.AddRange(shortUnknown);
            return unknown;
        }
    }

    /// <summary>Flags that take a value and were given none, because
    /// nothing at all followed them, spelt as they were written. Kept
    /// apart from the unknown ones so a misspelling at the end of a line
    /// is still answered as a misspelling.</summary>
    public IReadOnlyList<string> FlagsMissingAValue => valueless;

    public static Arguments Parse(IReadOnlyList<string> argv)
    {
        var parsed = new Arguments();
        bool literalFromHere = false;

        for (int i = 0; i < argv.Count; i++)
        {
            string a = argv[i];

            if (!literalFromHere && a == "--") { literalFromHere = true; continue; }

            if (!literalFromHere && a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
            {
                string name = a[2..];
                string? inline = null;

                int equals = name.IndexOf('=');
                if (equals > 0) { inline = name[(equals + 1)..]; name = name[..equals]; }

                if (Switches.Contains(name) && inline is null) { parsed.Add(name, "true"); continue; }

                if (inline is not null) { parsed.Add(name, inline); continue; }
                if (i + 1 < argv.Count) { parsed.Add(name, argv[++i]); continue; }

                // Nothing follows a flag that takes a value. Recorded and
                // refused before anything runs, never guessed at: taking
                // the switch value put the word "true" into a source file
                // through `--insert-after N --text`, which is R3.14's own
                // argument one step further in - the flag was known and it
                // was its VALUE that was missing, so the wrong answer
                // arrived as a wrong write instead. Windows PowerShell 5.1
                // reaches this by dropping an empty argument rather than
                // passing it, so `--text ''` lands here too; `--text=`
                // still says deliberately-empty and is untouched.
                parsed.valueless.Add("--" + name);
                parsed.Add(name, "true");
                continue;
            }

            // -e is the one short flag, and it is short because a search
            // carrying several patterns is a thing typed by hand.
            if (!literalFromHere && a == "-e")
            {
                if (i + 1 < argv.Count) { parsed.Add("pattern", argv[++i]); continue; }
                parsed.valueless.Add("-e");
                continue;
            }

            // R3.14 reaches the single dash too, and it took a wrong
            // answer to find that out. A one-dash argument used to fall
            // straight through to the positional list, so `search PATTERN
            // -i` - the spelling every grep in the world uses for
            // case-insensitive - was taken as a SECOND PATTERN, and the
            // search returned the union of two questions with complete
            // confidence. That is the exact failure the double-dash rule
            // exists to stop, one character short of being caught by it.
            //
            // A pattern or path that really does begin with a hyphen goes
            // through a bare `--`, or through -e, --pattern-file or
            // --pattern-stdin, which is where a shell cannot reach it
            // either.
            if (!literalFromHere && a.Length > 1 && a[0] == '-')
            {
                parsed.shortUnknown.Add(a);
                continue;
            }

            if (parsed.Verb.Length == 0 && !literalFromHere) parsed.Verb = a;
            else parsed.positional.Add(a);
        }

        return parsed;
    }

    void Add(string name, string value)
    {
        if (!flags.TryGetValue(name, out List<string>? values)) flags[name] = values = [];
        values.Add(value);
    }

    public bool Has(string name) => flags.ContainsKey(name);

    public string? Value(string name) =>
        flags.TryGetValue(name, out List<string>? v) && v.Count > 0 ? v[^1] : null;

    public IReadOnlyList<string> Values(string name) =>
        flags.TryGetValue(name, out List<string>? v) ? v : [];

    public int Int(string name, int fallback) =>
        int.TryParse(Value(name), out int n) ? n : fallback;

    public string? At(int index) => index < positional.Count ? positional[index] : null;

    /// <summary>
    /// The trees of R8.7, and where they come from when the command line
    /// does not say (R8.9).
    ///
    /// <para>Declared as <c>--root NAME=PATH</c>, repeatable. A bare
    /// <c>--root PATH</c> still works and is named <c>root</c>, so the
    /// single-tree form stays exactly as short as it was.</para>
    ///
    /// <para><b>The command line REPLACES the file rather than adding to
    /// it.</b> Merging would leave a caller unable to narrow the
    /// boundary: whatever they asked for, the tree's own configuration
    /// would still be open beside it, and "just this directory, please"
    /// would be unsayable. The personal overlay of
    /// <see cref="RootsFile.LocalFileName"/> DOES merge, and the
    /// difference is deliberate - that is one tree's configuration
    /// layered on itself, not a caller overriding a tree.</para>
    ///
    /// <para><b>--root grants <c>list read</c> and nothing else, with no
    /// override.</b> It stays because the MCP server has to be launched
    /// somehow and because ad-hoc reading is genuinely useful; it can no
    /// longer hand anybody write access. To write to a tree you put a
    /// file in it, which is a deliberate, reviewable act by a person
    /// inside the tree it governs - and it is the act a caller, model
    /// included, cannot perform, because
    /// <see cref="Fettler.Core.RuleFiles"/> refuses it.</para>
    ///
    /// <para><b>Finding no configuration is now a refusal.</b> It used to
    /// fall back to the current directory, which is the "searching
    /// around" this boundary exists to stop: a cwd above the intended
    /// tree contains the intended tree, so the fallback silently WIDENED
    /// the boundary exactly when nobody had stated one.</para>
    /// </summary>
    public Result<Roots> DeclaredRoots()
    {
        Result<Boundary> boundary = DeclaredBoundary();
        return boundary.IsOk ? Result<Roots>.Ok(boundary.Value.Roots) : boundary.Carry<Roots>();
    }

    /// <summary>
    /// <see cref="DeclaredRoots"/>, keeping hold of where the boundary
    /// came from. The CLI never asks - a process reads its configuration
    /// once by construction - but serve does: the file is what it
    /// re-reads before every request, and a boundary with no file behind
    /// it (--root) is the one that stays as launched.
    /// </summary>
    public Result<Boundary> DeclaredBoundary()
    {
        IReadOnlyList<string> declared = Values("root");
        if (declared.Count > 0)
        {
            var decls = new List<TreeDecl>();
            foreach (string entry in declared)
            {
                int equals = entry.IndexOf('=');

                // A bare Windows path has a colon in it and no equals, so
                // the split is on equals alone and never on the colon that
                // makes C:\ a volume.
                (string name, string path) = equals <= 0
                    ? (decls.Count == 0 ? "root" : $"root{decls.Count}", entry)
                    : (entry[..equals], entry[(equals + 1)..]);

                decls.Add(new TreeDecl(name, path, Permissions.ReadOnly));
            }

            return From(Roots.Open(decls, "--root on the command line, which grants list read only"), null);
        }

        if (Value("config") is { } named)
        {
            Result<RootsFile.Found> told = RootsFile.ReadWithOverlay(named);
            return told.IsOk
                ? From(Roots.Open(told.Value.Trees, told.Value.Origin, told.Value.Tasks, told.Value.Models), told.Value.File)
                : told.Carry<Boundary>();
        }

        if (!Has("no-config"))
        {
            Result<RootsFile.Found> found = RootsFile.Discover(Directory.GetCurrentDirectory());
            if (found.IsOk)
                return From(Roots.Open(found.Value.Trees, found.Value.Origin, found.Value.Tasks, found.Value.Models), found.Value.File);

            // Anything but NotFound means a configuration WAS there and
            // was wrong, and its own message says what.
            if (found.Failure!.Outcome != Outcome.NotFound) return found.Carry<Boundary>();
        }

        return Result<Boundary>.Fail(Outcome.Invalid, Roots.NothingDeclared);
    }

    static Result<Boundary> From(Result<Roots> opened, string? file) =>
        opened.IsOk
            ? Result<Boundary>.Ok(new Boundary(opened.Value, file))
            : opened.Carry<Boundary>();
}

/// <summary>The boundary and where it came from: the opened trees, and
/// the configuration file they were read from - null when --root
/// declared them on the command line, the one source with no file to
/// re-read.</summary>
public sealed record Boundary(Roots Roots, string? File);
