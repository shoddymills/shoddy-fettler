using System.Reflection;
using System.Text;
using System.Text.Json;
using Fettler.Clients;
using Fettler.Core;

namespace Fettler.Cli;

/// <summary>What a command produced, without a console anywhere near it
/// so the tests can drive every verb with no process.</summary>
public sealed record CliResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>
    /// Whether the operation did not do what was asked: a refusal, a
    /// failure, a batch that stopped early. It is what makes an MCP
    /// answer an error, and it is not the exit code. <c>doctor</c> exits 1
    /// or 2 so a script can act on what it found, and finding something
    /// is doing what was asked.
    /// </summary>
    public bool Failed { get; init; }
}

/// <summary>
/// The command-line front end of R3.3, which is what lets a `.ps1` and a
/// `.sh` become three-line launchers instead of two copies of the same
/// logic.
///
/// <para><b>R3.7 is the clause that shapes this file.</b> In
/// machine-readable mode the complete result - failures included - is
/// written to stdout, and stderr carries human diagnostics only. That is
/// not a preference: Windows PowerShell 5.1 turns a native program's
/// redirected stderr into <c>NativeCommandError</c> records and sets
/// <c>$?</c> false even on exit code 0, so any design that needs
/// <c>2&gt;&amp;1</c> to retrieve an answer is broken on the primary
/// development platform.</para>
/// </summary>
public static partial class Command
{
    public static Task<CliResult> RunAsync(
        IReadOnlyList<string> argv, TextReader stdin, CancellationToken cancel = default) =>
        RunAsync(argv, stdin, Host.Real(), cancel);

    /// <summary>
    /// The same command on a given machine, which is how a test runs the
    /// whole front end without reading the real home or temp folders.
    ///
    /// <para>With no configuration found, the launch folder is the tree
    /// at the tools level, so every verb works, setup and doctor
    /// included. A configuration that is present and malformed still
    /// refuses, because falling back from a broken boundary is how a
    /// boundary silently widens.</para>
    /// </summary>
    public static async Task<CliResult> RunAsync(
        IReadOnlyList<string> argv, TextReader stdin, Host host, CancellationToken cancel = default)
    {
        Arguments args = Arguments.Parse(argv);
        bool json = args.Has("json");

        CliResult? early = Trivial(args, json);
        if (early is not null)
        {
            // R1.4: help opens with the levels that are on where it was
            // run. A boundary that will not open leaves the help as it is;
            // the next real verb names what is wrong.
            if (IsHelp(args) && args.DeclaredRoots(host) is { IsOk: true } here)
                return new CliResult(early.ExitCode, LevelsLine(here.Value.Levels) + early.Stdout, early.Stderr);
            return early;
        }

        Result<Roots> roots = args.DeclaredRoots(host);

        // setup and doctor read and write the compiled-in list of client
        // files and never resolve a path through a tree, so the one
        // refusal that is about the TREE - a launch folder fettle will not
        // make one, such as the home folder - does not stop them. A person
        // configures a machine from wherever their terminal is open.
        if (!roots.IsOk && args.Verb is "setup" or "doctor" && roots.Failure!.Outcome == Outcome.Refused)
            roots = args.DeclaredRoots(host, anywhere: true);

        if (!roots.IsOk) return Failed(roots.Failure!, json);

        using var bench = new Bench(roots.Value);
        return await Run(bench, args, json, stdin, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The same command against a boundary that is already open. The MCP
    /// front end uses this, which is what makes R3.4's parity a property
    /// of the design: both front ends reach the operations through one
    /// dispatcher and one renderer, so a capability reachable from a
    /// script and not from a model is not merely forbidden but
    /// unwritable.
    /// </summary>
    public static async Task<CliResult> RunAsync(
        IReadOnlyList<string> argv, TextReader stdin, Bench bench, CancellationToken cancel = default)
    {
        Arguments args = Arguments.Parse(argv);
        bool json = args.Has("json");

        CliResult? early = Trivial(args, json);
        if (early is not null) return early;

        return await Run(bench, args, json, stdin, cancel).ConfigureAwait(false);
    }

    static bool IsHelp(Arguments args) =>
        args.Verb.Length == 0 || args.Verb is "help" or "--help" or "-h" || args.Has("help");

    /// <summary>One line naming each level and whether it is on here.</summary>
    static string LevelsLine(Levels levels) =>
        "here: " + string.Join("; ", Levels.Names.Select(l => $"{l} {levels.Describe(l)}")) + Environment.NewLine + Environment.NewLine;

    static CliResult? Trivial(Arguments args, bool json)
    {
        // Version before help, and not the other way round: `--version`
        // parses as a flag and leaves no verb behind, so an empty-verb
        // test placed first swallows it and prints the help instead.
        if (args.Verb is "version" || args.Has("version"))
            return new CliResult(ExitCodes.Ok, "fettle " + Version + Environment.NewLine, string.Empty);

        if (args.Verb.Length == 0 || args.Verb is "help" or "--help" or "-h" || args.Has("help"))
            return new CliResult(ExitCodes.Ok, Help(), string.Empty);

        return null;
    }

    static async Task<CliResult> Run(
        Bench bench, Arguments args, bool json, TextReader stdin, CancellationToken cancel)
    {
        // R3.14: a flag no verb knows is refused here, before anything
        // runs, because ignoring one does not produce a failure - it
        // produces a WRONG ANSWER. A flag taking a value eats the
        // argument after it, so a misspelt `--gob "*.cs"` swallows the
        // pattern, and the command then searches the whole tree for
        // something else and reports it with complete confidence.
        IReadOnlyList<string> unknown = args.UnknownFlags;
        if (unknown.Count > 0)
            return Failed(new Failure(Outcome.Invalid,
                $"no such flag: {string.Join(", ", unknown)}. Run fettle help for the list"), json);

        // The same argument one step in: the flag is known and its value
        // is the thing that is missing. Second, so a typo at the end of a
        // line is still answered as a typo rather than as a missing value.
        IReadOnlyList<string> empty = args.FlagsMissingAValue;
        if (empty.Count > 0)
            return Failed(new Failure(Outcome.Invalid,
                $"{string.Join(", ", empty)} needs a value. Write --NAME= to pass an empty one"), json);

        try
        {
            return Held(await Dispatch(bench, args, json, stdin, cancel).ConfigureAwait(false), json);
        }
        catch (OperationCanceledException)
        {
            return Failed(new Failure(Outcome.Refused, "the command was cancelled"), json);
        }
    }

    /// <summary>
    /// The release this was built from, stamped by
    /// <c>Directory.Build.props</c> at the top of the repository, so that
    /// fettle, sparky and the mill all answer the same number.
    ///
    /// <para>It was a hardcoded constant, and reported 1.0.0 for as long
    /// as it existed while the repository shipped 2.x - which made
    /// RELEASING.md's own smoke test, <c>./fettle --version</c> on the
    /// downloaded archive, report a number that meant nothing.</para>
    ///
    /// <para>The build metadata after <c>+</c> is a commit hash, which is
    /// useful in the mill's banner and only noise in
    /// <c>doctor</c>'s first line, so it is trimmed here.</para>
    /// </summary>
    public static string Version { get; } =
        typeof(Command).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    static async Task<CliResult> Dispatch(
        Bench bench, Arguments args, bool json, TextReader stdin, CancellationToken cancel) =>
        args.Verb switch
        {
            "find" => Find(bench, args, json),
            "search" => Search(bench, args, json, stdin),
            "read" => Read(bench, args, json),
            "write" => await Write(bench, args, json, stdin, cancel).ConfigureAwait(false),
            "edit" => await Edit(bench, args, json, stdin, cancel).ConfigureAwait(false),
            "replace" => await Replace(bench, args, json, cancel).ConfigureAwait(false),
            "new" => await Simple(bench.NewFileAsync(args.At(0) ?? "", cancel), json,
                        r => $"created {r.Path.Display}").ConfigureAwait(false),
            "mkdir" => await Simple(bench.MakeDirectoryAsync(args.At(0) ?? "", cancel), json,
                        r => r.Created ? $"created {r.Path.Display}" : $"{r.Path.Display} was already there").ConfigureAwait(false),
            "move" => await Move(bench, args, json, cancel).ConfigureAwait(false),
            "copy" => await Copy(bench, args, json, cancel).ConfigureAwait(false),
            "delete" => await Delete(bench, args, json, cancel).ConfigureAwait(false),
            "extract" => await Extract(bench, args, json, cancel).ConfigureAwait(false),
            "exec" => await Exec(bench, args, json, cancel).ConfigureAwait(false),
            "tasks" => TaskList(bench, json),
            "roots" => RootList(bench, json),
            "doctor" => Diagnose(bench, args, json),
            "setup" => Setup(bench, args, json),
            "run" => await Run(bench, args, json, cancel).ConfigureAwait(false),
            "batch" => await Batch(bench, args, json, cancel).ConfigureAwait(false),
            _ => Failed(new Failure(Outcome.Invalid, $"no verb called '{args.Verb}'. Run fettle help for the list"), json),
        };

    // ---- read-only verbs ----

    /// <summary>One pattern per line, blank lines dropped and nothing
    /// trimmed - a trailing space can be part of a pattern. One line is
    /// one string, which is the rule a task's "run" array states as one
    /// element per argument: the same idea, in the format each file
    /// has.</summary>
    static void AddLines(List<string> into, string text)
    {
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            if (line.Length > 0) into.Add(line);
    }

    /// <summary>
    /// R8.8: a caller can ask what it is bounded to.
    ///
    /// <para>Without this the only way to discover the boundary is to be
    /// refused by it, which is learning by trial against a security
    /// check. It matters most on the MCP front end, where the model never
    /// sees the command line that declared the roots and so has no other
    /// way to know they exist, what they are called, or which one an
    /// unqualified path lands in.</para>
    /// </summary>
    static CliResult RootList(Bench bench, bool json)
    {
        Roots roots = bench.Roots;
        IReadOnlyList<string> names = roots.Names;

        if (json)
            return Ok(Json(w =>
            {
                // R7.1: the levels first, with the files that turned each
                // on, so a script can branch on them without reading the
                // trees.
                w.WriteStartObject("levels");
                foreach (string level in Levels.Names)
                {
                    w.WriteStartObject(level);
                    w.WriteBoolean("on", roots.Levels.IsOn(level));
                    w.WriteStartArray("from");
                    foreach (string file in roots.Levels.Sources(level)) w.WriteStringValue(file);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndObject();

                w.WriteStartArray("roots");
                foreach (string name in names)
                {
                    Grant grant = roots.GrantOf(name);
                    string full = roots.PathOf(name);

                    w.WriteStartObject();
                    w.WriteString("name", name);
                    w.WriteString("path", full);
                    w.WriteString("can", Permissions.Write(grant.Can));

                    // Reported only where it is on. A "screen": "nothing"
                    // against every tree would be noise on the answer
                    // almost every caller gets.
                    if (grant.Screen != Screened.None)
                        w.WriteString("screen", Screens.Write(grant.Screen));

                    WriteScreening(w, grant.Screen, roots.Models);

                    w.WriteBoolean("default", name.Equals(names[0], Core.Roots.PathComparison));

                    // A tree fettle opened itself says how it was found.
                    if (roots.HowFound(name) is { } how) w.WriteString("found", how);

                    // Read at the call, never cached, so a branch switched
                    // in a terminal shows on the next call.
                    if (roots.HowFound(name) is null && Checkouts.Of(full) is { } checkout)
                    {
                        w.WriteString("branch", checkout.Name);
                        w.WriteBoolean("detached", checkout.Detached);
                        w.WriteString("repository", checkout.Repository);
                    }

                    if (grant.Scopes.Count > 0)
                    {
                        w.WriteStartArray("scopes");
                        foreach (Scope scope in grant.Scopes)
                        {
                            w.WriteStartObject();
                            w.WriteString("path", Path.GetRelativePath(full, scope.Path).Replace('\\', '/'));
                            w.WriteString("can", Permissions.Write(scope.Can));

                            // What the scope ITSELF states. Its absence
                            // means "whatever the tree says".
                            if (scope.Screen is { } screened)
                            {
                                w.WriteString("screen", Screens.Write(screened));
                                WriteScreening(w, screened, roots.Models);
                            }

                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }

                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("count", names.Count);

                // Named even when nothing is installed under it. "Where
                // would a model go" and "is one there" are different
                // questions.
                if (roots.Models is { } where) w.WriteString("models", where);

                w.WriteString("source", roots.Origin);
            }));

        var text = new StringBuilder();
        int column = Math.Max(NameColumn(names), NameColumn(Levels.Names));

        // R7.1: the levels first. A level that is off says so in one
        // line, so a reader never has to infer it from an absence.
        text.AppendLine("levels");
        foreach (string level in Levels.Names)
            text.Append("  ").Append(level.PadRight(column)).AppendLine(roots.Levels.Describe(level));

        // B.9: what may be DONE in each tree, not only where it is.
        var declared = names.Where(n => roots.HowFound(n) is null).ToList();
        var found = names.Where(n => roots.HowFound(n) is not null).ToList();

        text.AppendLine().AppendLine("trees");
        foreach (string name in declared)
        {
            Grant grant = roots.GrantOf(name);
            string full = roots.PathOf(name);

            text.Append(name.PadRight(column)).Append(full)
                .AppendLine(name.Equals(names[0], Core.Roots.PathComparison) ? "  (default)" : "");
            text.Append(' ', column).Append("can: ").AppendLine(Permissions.Write(grant.Can));

            // A fact about this machine's checkout, and said to be one:
            // what a remote holds is a different question, and only a
            // fetch answers it.
            if (Checkouts.Of(full) is { } checkout)
            {
                text.Append(' ', column)
                    .Append(checkout.Detached ? "detached at " : "branch: ")
                    .Append(checkout.Name).AppendLine(" (local)");

                if (!checkout.Repository.Equals(full, Core.Roots.PathComparison))
                    text.Append(' ', column).Append("repository: ").AppendLine(checkout.Repository);
            }

            foreach (Scope scope in grant.Scopes)
                text.Append(' ', column)
                    .Append(Path.GetRelativePath(full, scope.Path).Replace('\\', '/'))
                    .Append("  can: ").AppendLine(Permissions.Write(scope.Can));
        }

        // The screens under their own heading, so whether a project
        // screens anything is one place to look.
        var screened = new List<(string Key, Screened Screen)>();
        foreach (string name in declared)
        {
            Grant grant = roots.GrantOf(name);
            string full = roots.PathOf(name);
            if (grant.Screen != Screened.None) screened.Add((name, grant.Screen));
            foreach (Scope scope in grant.Scopes)
                if (scope.Screen is { } s)
                    screened.Add(($"{name}/{Path.GetRelativePath(full, scope.Path).Replace('\\', '/')}", s));
        }

        if (screened.Count > 0 || roots.Models is not null)
        {
            text.AppendLine().AppendLine("screens");
            int keyColumn = Math.Max(column, NameColumn(screened.Select(s => s.Key)));

            foreach ((string key, Screened screen) in screened)
            {
                text.Append(key.PadRight(keyColumn)).AppendLine(Screens.Write(screen));
                foreach ((Screened category, string said) in Screening(screen, roots.Models))
                    text.Append(' ', keyColumn).Append(Screens.NameOf(category)).Append(": ").AppendLine(said);
            }

            if (roots.Models is { } models)
                text.Append("models".PadRight(keyColumn)).AppendLine(models);
        }

        // R4.7: the model tiers are inert until somebody names a
        // directory, and a screen listing them without one reads as a
        // protection that is running.
        if (roots.Models is null && AnythingNeedsAModel(bench))
            text.AppendLine().Append("no \"models\" directory is declared, so the ")
                .Append(ModelBackedWords())
                .AppendLine(" categories screen nothing. Only identifiers are screened");

        // R6.11 and R12.2: the folders fettle opened itself, together,
        // each with how it was found, so a wrong choice is seen at once.
        if (found.Count > 0)
        {
            text.AppendLine().AppendLine("found by fettle");
            foreach (string name in found)
            {
                text.Append(name.PadRight(column)).AppendLine(roots.PathOf(name));
                text.Append(' ', column).Append("can: ").AppendLine(Permissions.Write(roots.GrantOf(name).Can));
                text.Append(' ', column).Append("found: ").AppendLine(roots.HowFound(name));
            }
        }

        // Where the boundary came from: a caller who disagrees with it
        // has to know which file to open.
        text.AppendLine().Append("declared by ").AppendLine(roots.Origin);

        return Ok(text.ToString());
    }

    /// <summary>
    /// What is installed for each model-backed category a screen asks
    /// for, as the phrase <c>roots</c> prints against it.
    ///
    /// <para><b>Per category, because they fail independently.</b> A
    /// machine can easily hold the clinical model and not the legal one,
    /// and <c>screen: clinical legal</c> says nothing whatever about
    /// which - so a caller reading it cannot tell a category judged by a
    /// checkpoint from one judged by nothing at all.</para>
    ///
    /// <para>Empty when no models directory was declared. That is one
    /// fact about the whole configuration rather than a fact about this
    /// tree, so it is said once at the end instead of against every
    /// grant.</para>
    /// </summary>
    static IReadOnlyList<(Screened Category, string Said)> Screening(Screened screen, string? models)
    {
        var lines = new List<(Screened, string)>();
        if (models is null) return lines;

        foreach (Screened one in Screens.All)
        {
            if (one == Screened.Identifiers || !screen.HasFlag(one)) continue;

            IReadOnlyList<Installed.Model> installed = Installed.For(models, Screens.NameOf(one));

            lines.Add((one, installed.Count == 0
                ? "no model installed; reads here are refused"
                : string.Join(", ", installed)));
        }

        return lines;
    }

    static void WriteScreening(Utf8JsonWriter w, Screened screen, string? models)
    {
        IReadOnlyList<(Screened Category, string Said)> lines = Screening(screen, models);
        if (lines.Count == 0) return;

        w.WriteStartObject("screening");
        foreach ((Screened category, string said) in lines)
            w.WriteString(Screens.NameOf(category), said);
        w.WriteEndObject();
    }

    /// <summary>Whether anything in the configuration asked for a
    /// category a model has to judge. Somebody who never asked for one
    /// is not told about a directory they do not need.</summary>
    static bool AnythingNeedsAModel(Bench bench)
    {
        foreach (string name in bench.Roots.Names)
        {
            Grant grant = bench.Roots.GrantOf(name);
            if ((grant.Screen & Screens.ModelBacked) != Screened.None) return true;

            foreach (Scope scope in grant.Scopes)
                if (scope.Screen is { } screened
                    && (screened & Screens.ModelBacked) != Screened.None) return true;
        }

        return false;
    }

    /// <summary>The model-backed category words, derived rather than
    /// spelled out, so adding a category cannot leave this sentence
    /// quietly naming three of four.</summary>
    static string ModelBackedWords()
    {
        var words = new List<string>();
        foreach (Screened one in Screens.All)
            if (Screens.ModelBacked.HasFlag(one)) words.Add(Screens.NameOf(one));

        return string.Join(", ", words);
    }

    // ---- the machine, rather than the tree ----

    /// <summary>
    /// R8's exception, made narrow: the doctor reads a compiled-in list
    /// of well-known configuration paths and nothing else. The tree it
    /// reports on is the first declared tree, which is the one a
    /// repo-level configuration would sit in.
    /// </summary>
    static Machine MachineFor(Bench bench) =>
        Machine.Real(bench.Roots.PathOf(bench.Roots.Names[0]));

    static CliResult Diagnose(Bench bench, Arguments args, bool json)
    {
        Diagnosis diagnosis = Doctor.Examine(MachineFor(bench), bench.Roots, Version);

        string? only = args.Value("client");
        if (only is not null && !Places.Clients.Contains(only))
            return Failed(new Failure(Outcome.Invalid,
                $"no client called '{only}'. The clients are: {string.Join(", ", Places.Clients)}"), json);

        // --client narrows the REPORT, never the scan, so "it looked
        // fine" can never mean "it only looked at one".
        IReadOnlyList<ClientReport> shown = only is null
            ? diagnosis.Clients
            : [.. diagnosis.Clients.Where(c => c.Client == only)];

        // --hook: silent when nothing is wrong, and ALWAYS exit 0. A
        // diagnostic that can stop a session from starting will be
        // removed within a week.
        if (args.Has("hook"))
        {
            if (!diagnosis.AnyBroken && !diagnosis.AnyWarning) return Ok(string.Empty);

            var brief = new StringBuilder("fettle doctor:").AppendLine();
            foreach (ClientReport c in shown)
                if (c.Verdict is Verdict.Broken or Verdict.Absent)
                    brief.Append("  ").Append(c.Client).Append(' ').Append(Word(c.Reach))
                         .Append(": ").Append(Word(c.Verdict))
                         .Append(": ").AppendLine(c.Detail);

            foreach (Finding f in diagnosis.Findings.Where(f => f.Serious))
                brief.Append("  ").Append(f.Check).Append(": ").AppendLine(f.Message);

            brief.AppendLine("  run: fettle doctor");
            return Ok(brief.ToString());
        }

        if (json)
            return Ok(Json(w =>
            {
                w.WriteStartObject("installation");
                w.WriteString("binary", diagnosis.Install.Binary);
                w.WriteString("version", diagnosis.Install.Version);
                w.WriteBoolean("on_path", diagnosis.Install.OnPath);
                if (diagnosis.Install.PathBinary is { } p) w.WriteString("path_binary", p);
                w.WriteString("inventory_of", Doctor.InventoryOf);
                w.WriteEndObject();

                w.WriteStartObject("levels");
                foreach (string level in Levels.Names)
                {
                    w.WriteStartObject(level);
                    w.WriteBoolean("on", diagnosis.Levels.IsOn(level));
                    w.WriteStartArray("from");
                    foreach (string file in diagnosis.Levels.Sources(level)) w.WriteStringValue(file);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndObject();

                w.WriteStartArray("clients");
                foreach (ClientReport c in shown)
                {
                    w.WriteStartObject();
                    w.WriteString("client", c.Client);
                    w.WriteString("reach", Word(c.Reach));
                    w.WriteString("verdict", Word(c.Verdict));
                    w.WriteString("detail", c.Detail);
                    if (c.Where is { } where) w.WriteString("where", where);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("findings");
                foreach (Finding f in diagnosis.Findings)
                {
                    w.WriteStartObject();
                    w.WriteString("check", f.Check);
                    w.WriteString("level", f.Level);
                    w.WriteString("severity", f.Serious ? "serious" : "note");
                    w.WriteString("message", f.Message);
                    if (f.Where is { } where) w.WriteString("where", where);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("facts");
                foreach (Fact fact in diagnosis.Facts)
                {
                    w.WriteStartObject();
                    w.WriteString("level", fact.Level);
                    w.WriteString("text", fact.Text);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }), Code(diagnosis));

        var text = new StringBuilder();
        text.Append("fettle ").Append(diagnosis.Install.Version).Append("  ").AppendLine(diagnosis.Install.Binary);
        if (diagnosis.Install.OnPath)
            text.Append("on PATH: ").AppendLine(diagnosis.Install.PathBinary);
        else
        {
            // Said in the user's terms, with the exact command to type. A
            // registration that names a bare 'fettle' cannot start it, and
            // setup --command writes the full path instead.
            text.AppendLine("your assistant cannot find fettle.");
            List<string> installed = [.. shown
                .Where(c => c.Verdict != Verdict.NotApplicable)
                .Select(c => c.Client)
                .Distinct(StringComparer.Ordinal)];
            if (installed.Count == 0) installed.Add("CLIENT");
            foreach (string client in installed)
                text.Append("  Run: fettle setup tools ").Append(client)
                    .Append(" --command ").AppendLine(diagnosis.Install.Binary);
        }

        // R7.2: one section per level, each opening with whether it is on.
        // A level that is off is one line.
        foreach (string level in Levels.Names)
        {
            text.AppendLine().Append(level).Append("  ").AppendLine(diagnosis.Levels.Describe(level));

            if (level == Levels.ToolsName)
            {
                foreach (ClientReport c in shown)
                {
                    if (args.Has("quiet") && c.Verdict is Verdict.NotApplicable or Verdict.Healthy) continue;
                    text.Append("  ").Append(c.Client.PadRight(16)).Append(Word(c.Reach).PadRight(8))
                        .Append(Word(c.Verdict).PadRight(10))
                        .AppendLine(c.Detail);
                    if (c.Where is { } where) text.Append(' ', 26).AppendLine(where);
                }
            }

            if (!diagnosis.Levels.IsOn(level)) continue;

            if (level == Levels.GuardrailsName)
            {
                // A closed list that has fallen behind reports exactly as
                // clean as one that has not, so the date it was drawn is
                // part of the answer.
                text.Append("  tool inventory dated ").Append(Doctor.InventoryOf)
                    .AppendLine("; newer client tools are not checked");
            }

            foreach (Fact fact in diagnosis.Facts.Where(f => f.Level == level))
                text.Append("  ").AppendLine(fact.Text);

            foreach (Finding f in diagnosis.Findings.Where(f => f.Level == level))
            {
                text.Append("  ").Append(f.Serious ? "! " : "- ").Append(f.Check).Append("  ").AppendLine(f.Message);
                if (f.Where is { } where) text.Append(' ', 6).AppendLine(where);
            }
        }

        return new CliResult(Code(diagnosis), text.ToString(), string.Empty);

    }

    /// <summary>0 healthy or not applicable, 1 warnings only, 2 anything
    /// broken - so a script can act without reading prose.</summary>
    static int Code(Diagnosis diagnosis) =>
        diagnosis.AnyBroken ? 2 : diagnosis.AnyWarning ? 1 : 0;

    static string Word(Reach reach) => reach == Reach.Global ? "global" : "repo";

    /// <summary>The verdict as it reads in a column. "n/a" rather than
    /// "notapplicable", because the long form is both ugly and wider than
    /// the column, and n/a is what the report means.</summary>
    static string Word(Verdict verdict) => verdict switch
    {
        Verdict.NotApplicable => "n/a",
        Verdict.Absent => "absent",
        Verdict.Broken => "broken",
        _ => "healthy",
    };

    /// <summary>
    /// R7.4: <c>setup LEVEL CLIENT</c>. The level comes first, because
    /// what setup writes depends on it: <c>tools</c> registers, denies the
    /// built-in file tools and writes the tools paragraph;
    /// <c>guardrails</c> adds the shell denies and the guardrails
    /// paragraph; <c>screenings</c> checks burler and the models.
    /// </summary>
    static CliResult Setup(Bench bench, Arguments args, bool json)
    {
        string? level = args.At(0);

        // The old spelling named the client first. Saying the new one is
        // the whole of the fix, so the refusal shows it.
        if (level is not null && Places.Clients.Contains(level))
            return Failed(new Failure(Outcome.Invalid,
                $"setup takes the level first, then the client. Run: fettle setup tools {level}, "
                + $"fettle setup guardrails {level} or fettle setup screenings {level}"), json);

        if (level is null || !Levels.Names.Contains(level, StringComparer.Ordinal))
            return Failed(new Failure(Outcome.Invalid,
                $"setup needs a level and a client, as fettle setup tools claude-code. The levels are: "
                + $"{string.Join(", ", Levels.Names)}. The clients are: {string.Join(", ", Places.Clients)}"), json);

        if (args.At(1) is null && !args.Has("all"))
            return Failed(new Failure(Outcome.Invalid,
                $"setup {level} needs a client name or --all. The clients are: {string.Join(", ", Places.Clients)}"), json);

        if (args.Has("global") && args.Has("local"))
            return Failed(new Failure(Outcome.Invalid, "setup takes --global or --local, not both"), json);

        Machine machine = MachineFor(bench);
        Reach reach = args.Has("global") ? Reach.Global : Reach.Repo;

        IReadOnlyList<string> clients = args.Has("all") ? Places.Clients : [args.At(1)!];

        var changes = new List<Change>();
        var notes = new List<string>();

        foreach (string one in clients)
        {
            Result<Scaffolding> done = Scaffold.Apply(machine, new Scaffold.Options(
                one, reach,
                DryRun: args.Has("dry-run"),
                Force: args.Has("force"),
                Hooks: args.Has("hooks"),
                Deny: args.Has("deny"),
                Command: args.Value("command"),
                Level: level,
                Models: bench.Roots.Models,
                FirstTree: bench.Roots.Names[0]), Version);

            // --all skips what does not apply on this machine rather than
            // failing the run, which is the whole point of asking for all.
            if (!done.IsOk)
            {
                if (args.Has("all") && done.Failure!.Outcome == Outcome.Invalid) continue;
                return Failed(done.Failure!, json);
            }

            changes.AddRange(done.Value.Changes);
            notes.AddRange(done.Value.Notes);
        }

        // --all runs once per client, and a note about the machine is
        // the same note each time.
        notes = [.. notes.Distinct(StringComparer.Ordinal)];

        if (json)
            return Ok(Json(w =>
            {
                w.WriteBoolean("dry_run", args.Has("dry-run"));
                w.WriteStartArray("changes");
                foreach (Change c in changes)
                {
                    w.WriteStartObject();
                    w.WriteString("path", c.Path);
                    w.WriteString("what", c.What);
                    w.WriteBoolean("applied", c.Applied);
                    if (c.Backup is { } backup) w.WriteString("backup", backup);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("notes");
                foreach (string n in notes) w.WriteStringValue(n);
                w.WriteEndArray();
            }));

        var human = new StringBuilder();
        human.AppendLine(args.Has("dry-run") ? "would change:" : "changed:");
        foreach (Change c in changes)
        {
            human.Append("  ").Append(c.Path).Append("  ").AppendLine(c.What);
            if (c.Backup is { } backup) human.Append("     backed up to ").AppendLine(backup);
        }
        if (changes.Count == 0) human.AppendLine("  nothing; everything was already in place");

        foreach (string n in notes) human.Append("note: ").AppendLine(n);

        return Ok(human.ToString());
    }

    // ---- mutating verbs ----


    static async Task<CliResult> Write(Bench bench, Arguments args, bool json, TextReader stdin, CancellationToken cancel)
    {
        string? path = args.At(0);
        if (path is null) return Failed(new Failure(Outcome.Invalid, "write needs a path"), json);

        // R5.11's reasoning applied to write: the source of the content
        // is explicit, and never inferred. Falling back to stdin when
        // nothing was said made `fettle write PATH` sit and block on a
        // stream nobody was going to write to - the same hang that Tasks
        // deliberately prevents by closing a child's stdin, left
        // unguarded in Fettler's own front end.
        string text;
        if (args.Value("text") is { } inline) text = inline;
        else if (args.Value("text-file") is { } file)
        {
            try { text = File.ReadAllText(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failed(new Failure(Outcome.NotFound, $"cannot read --text-file: {e.Message}", file), json);
            }
        }
        else if (args.Has("stdin"))
            text = TextIo.WithoutMark(await stdin.ReadToEndAsync(cancel).ConfigureAwait(false));
        else return Failed(new Failure(Outcome.Invalid,
            "write needs the content: --stdin, --text TEXT or --text-file PATH"), json);


        Result<Saved> saved = await bench.WriteAsync(
            path, text, args.Has("overwrite"), args.Value("encoding"), args.Value("eol"), cancel,
            args.Has("allow-credential"))
            .ConfigureAwait(false);

        if (!saved.IsOk) return Failed(saved.Failure!, json);
        Saved s = saved.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("path", Show(bench, s.Path));
                w.WriteBoolean("created", s.Created);
                w.WriteNumber("bytes", s.Bytes);
                w.WriteString("encoding", s.EncodingName);
                w.WriteString("line_ending", TextIo.EndingName(s.LineEnding));
                w.WriteBoolean("line_ending_mixed", s.MixedEndings);
                w.WriteBoolean("final_newline", s.FinalNewline);
                w.WriteString("hash", s.Hash);
                WriteLost(w, s.MetadataLost);
            }));

        var human = new StringBuilder()
            .Append(s.Created ? "wrote " : "replaced ").Append(Show(bench, s.Path))
            .Append("  ").Append(s.Bytes).Append(" bytes  ").Append(s.EncodingName)
            .Append("  ").Append(TextIo.EndingName(s.LineEnding)).Append(s.MixedEndings ? " (MIXED)" : "")
            .Append("  final-newline: ").AppendLine(s.FinalNewline ? "yes" : "no");

        AppendLost(human, s.MetadataLost);
        return Ok(human.ToString());
    }

    static async Task<CliResult> Move(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (args.At(0) is not { } from || args.At(1) is not { } to)
            return Failed(new Failure(Outcome.Invalid, "move needs a source and a destination"), json);

        Result<MoveReport> moved = await bench.MoveAsync(from, to, args.Has("overwrite"), cancel).ConfigureAwait(false);
        if (!moved.IsOk) return Failed(moved.Failure!, json);
        MoveReport m = moved.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("from", Show(bench, m.From));
                w.WriteString("to", Show(bench, m.To));
                w.WriteBoolean("atomic", m.Atomic);
                w.WriteBoolean("case_only", m.CaseOnly);
                w.WriteBoolean("crossed_root", m.CrossedRoot);
                w.WriteBoolean("directory", m.Directory);
            }));

        var human = new StringBuilder()
            .Append("moved ").Append(Show(bench, m.From)).Append(" -> ").Append(Show(bench, m.To))
            .Append("  atomic: ").Append(m.Atomic ? "yes" : "no");
        if (m.CaseOnly) human.Append("  case-only rename");
        if (m.CrossedRoot) human.Append("  crossed roots");
        return Ok(human.AppendLine().ToString());
    }

    static async Task<CliResult> Delete(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (args.At(0) is not { } path)
            return Failed(new Failure(Outcome.Invalid, "delete needs a path"), json);

        Result<DeleteReport> deleted = await bench.DeleteAsync(path, args.Has("recursive"), args.Has("force"), cancel)
            .ConfigureAwait(false);

        if (!deleted.IsOk) return Failed(deleted.Failure!, json);
        DeleteReport d = deleted.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("path", Show(bench, d.Path));
                w.WriteNumber("files", d.Files);
                w.WriteNumber("directories", d.Directories);
            }));

        var human = new StringBuilder().Append("deleted ").Append(Show(bench, d.Path));
        if (d.Directories > 0)
            human.Append("  ").Append(d.Files).Append(d.Files == 1 ? " file  " : " files  ")
                 .Append(d.Directories).Append(d.Directories == 1 ? " directory" : " directories");
        return Ok(human.AppendLine().ToString());
    }

    static async Task<CliResult> Extract(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (args.At(0) is not { } archive)
            return Failed(new Failure(Outcome.Invalid, "extract needs an archive"), json);

        // Where it lands is stated, never inferred from the archive's own
        // name or from a working directory - there isn't one, and guessing
        // a destination for an operation that writes many files is the
        // wrong place to be helpful.
        if (args.Value("into") is not { Length: > 0 } into)
            return Failed(new Failure(Outcome.Invalid,
                "extract needs --into DIR"), json);

        Result<Unpacked> done = await bench.ExtractAsync(archive, into, args.Has("overwrite"), cancel)
            .ConfigureAwait(false);

        if (!done.IsOk) return Failed(done.Failure!, json);
        Unpacked u = done.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("archive", Show(bench, u.Archive));
                w.WriteString("into", Show(bench, u.Into));
                w.WriteNumber("members", u.Members);
                w.WriteNumber("bytes", u.Bytes);
            }));

        return Ok($"extracted {u.Members}{(u.Members == 1 ? " member" : " members")}"
            + $" ({u.Bytes} bytes) from {Show(bench, u.Archive)} into {Show(bench, u.Into)}"
            + Environment.NewLine);
    }

    static async Task<CliResult> Exec(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (args.At(0) is not { } path)
            return Failed(new Failure(Outcome.Invalid, "exec needs a path"), json);

        if (args.Has("on") == args.Has("off"))
            return Failed(new Failure(Outcome.Invalid, "exec needs --on or --off"), json);

        Result<ExecReport> set = await bench.SetExecutableAsync(path, args.Has("on"), cancel).ConfigureAwait(false);
        if (!set.IsOk) return Failed(set.Failure!, json);
        ExecReport e = set.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("path", Show(bench, e.Path));
                w.WriteBoolean("executable", e.Executable);
                w.WriteBoolean("supported", e.Supported);
            }));

        return Ok(e.Supported
            ? $"{Show(bench, e.Path)} executable: {(e.Executable ? "yes" : "no")}{Environment.NewLine}"
            : $"{Show(bench, e.Path)} unchanged: this filesystem has no execute bit{Environment.NewLine}");
    }

    // ---- rendering ----

    static async Task<CliResult> Simple<T>(Task<Result<T>> operation, bool json, Func<T, string> human)
    {
        Result<T> result = await operation.ConfigureAwait(false);
        if (!result.IsOk) return Failed(result.Failure!, json);

        if (!json) return Ok(human(result.Value) + Environment.NewLine);

        string message = human(result.Value);
        return Ok(Json(w => w.WriteString("detail", message)));
    }

    static CliResult Ok(string stdout) => new(ExitCodes.Ok, stdout, string.Empty);

    static CliResult Ok(string stdout, int code) => new(code, stdout, string.Empty);

    /// <summary>
    /// R3.7 in one place: a failure in machine-readable mode is a
    /// complete result on <b>stdout</b>, never on stderr, because a
    /// PowerShell 5.1 caller cannot retrieve stderr without turning it
    /// into a terminating error.
    /// </summary>
    static CliResult Failed(Failure failure, bool json)
    {
        int code = ExitCodes.Of(failure.Outcome);

        if (json)
            return new CliResult(code, JsonBody(w =>
            {
                w.WriteBoolean("ok", false);
                w.WriteString("outcome", ExitCodes.NameOf(failure.Outcome));
                w.WriteString("message", failure.Message);
                if (failure.Path is { Length: > 0 } p) w.WriteString("path", p);
            }), string.Empty) { Failed = true };

        // An empty path is no path. It reached the caller as a bare "()"
        // on the end of a refusal, which reads as a fact about the thing
        // refused rather than as a field nobody filled in.
        return new CliResult(code, string.Empty,
            $"refused: {failure.Message}"
            + (failure.Path is { Length: > 0 } at ? $" ({at})" : "") + Environment.NewLine) { Failed = true };
    }

    /// <summary>The width of a name column, wide enough for the widest
    /// name given. A fixed width is a fixed width until something reaches
    /// it: a name of exactly the column's size leaves no gap at all, and
    /// the name and the value it introduces run together into one
    /// word.</summary>
    static int NameColumn(IEnumerable<string> names)
    {
        int width = 12;
        foreach (string name in names) width = Math.Max(width, name.Length + 2);
        return width;
    }

    static string Json(Action<Utf8JsonWriter> body) => JsonBody(w =>
    {
        w.WriteBoolean("ok", true);
        body(w);
    });

    /// <summary>
    /// The writer options both front ends put their answers through.
    ///
    /// <para><b>The relaxed encoder is a SIZE decision, and a measured
    /// one.</b> System.Text.Json defaults to escaping every character
    /// that could matter inside an HTML document - <c>&lt;</c>,
    /// <c>&gt;</c>, <c>&amp;</c>, <c>'</c>, <c>+</c>, the quote - to a
    /// six-character <c>\uXXXX</c> sequence, and everything non-ASCII
    /// besides. Answering with an HTML page through that put HALF AGAIN
    /// as many characters on the wire as the file has bytes: a 33 KB page
    /// arrived as 51 KB, which was past the caller's ceiling, so it was
    /// rejected WHOLE and a one-call read cost three.</para>
    ///
    /// <para><b>"Unsafe" names one hazard, and it is not this one.</b> The
    /// relaxed encoder is unsafe to splice into an HTML document without
    /// encoding it again. Nothing here does that: this goes down a stdio
    /// JSON-RPC pipe, or to a terminal. Everything JSON itself requires -
    /// the quote, the backslash, the control characters - is escaped
    /// either way, and the stream is UTF-8 either way.</para>
    /// </summary>
    public static readonly JsonWriterOptions Writing = new()
    {
        Indented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static string JsonBody(Action<Utf8JsonWriter> body)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Writing))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + Environment.NewLine;
    }

    static void WriteFacts(Utf8JsonWriter w, FileFacts facts)
    {
        if (facts.Executable is { } x) w.WriteBoolean("executable", x);
        if (facts.ReadOnly) w.WriteBoolean("read_only", true);
        if (facts.Hidden) w.WriteBoolean("hidden", true);
        if (facts.Link != LinkKind.None)
        {
            w.WriteString("link", facts.Link == LinkKind.Junction ? "junction" : "symlink");
            if (facts.LinkTarget is { } t) w.WriteString("link_target", t);
        }
    }

    static void WriteLost(Utf8JsonWriter w, IReadOnlyList<string> lost)
    {
        if (lost.Count == 0) return;
        w.WriteStartArray("metadata_not_preserved");
        foreach (string l in lost) w.WriteStringValue(l);
        w.WriteEndArray();
    }

    static void AppendLost(StringBuilder text, IReadOnlyList<string> lost)
    {
        foreach (string l in lost)
            text.Append("   warning: not preserved: ").AppendLine(l);
    }

    /// <summary>Paths are written qualified only when more than one root
    /// is open, so a single-root caller sees exactly what it always
    /// did (R8.7).</summary>
    static string Show(Bench bench, ContainedPath path) =>
        bench.Roots.IsSingle ? path.Display : path.Qualified;

    static string Help() => """
        fettle - find, search, read and change files, with answers a script or a model
        can hand straight back. Three levels: tools (always on), guardrails, screenings.

        tools - always on, no file needed. With no .fettler.json, the folder you are in
        is the tree 'work'. fettle also opens the client's scratch, tool-results and
        memory folders, and the --root of any companion server.
          find PATTERN [--sort mtime] [--limit N] [--since TIME] [--after NEXT]
          search PATTERN [-e PATTERN]... [--glob G] [--literal] [--case-sensitive]
                 [--context N] [--limit N] [--count] [--files-only] [--no-documents]
                 [--pattern-file PATH] [--pattern-stdin] [--after NEXT]
          read PATH... [--from N] [--to N] [--tail N] [--column N] [--member NAME]
                 [--numbered]
                 Text, notebooks, PDFs, spreadsheets, Word, images, archives. Stops at
                 2000 lines a call. --to lifts the line cap.
          Every answer stops at 40,000 bytes, and 10,000 for a refusal. An answer
          that stops short gives its next: --after NEXT, or --from N [--column N].
          A line too long for one answer comes back in pieces. One image a call.
          write PATH (--stdin | --text S | --text-file PATH) [--overwrite]
                [--encoding E] [--eol lf|crlf]
          edit PATH [--expect HASH] [--dry-run] and one of: --replace S --with S
                 [--between 120-140] [--all] | --insert-after N --text S |
                 --delete 150-151 | --script FILE | --script-path PATH
          replace FIND WITH [--glob G] [--dry-run] [--after NEXT]
          new PATH | mkdir PATH | delete PATH [--recursive] [--force]
          move FROM TO [--overwrite] | copy FROM TO [--recursive] [--overwrite]
          extract ARCHIVE --into DIR [--overwrite] | exec PATH --on | --off
          batch --script FILE | --script-path PATH
          roots                 the levels, the trees, and what each tree allows
          doctor [--client NAME] [--quiet] [--hook]
                 The wiring, then a section per level. Findings only for levels on.
          setup tools CLIENT [--global | --local] [--dry-run] [--force] [--hooks] [--deny]
                 [--command PATH]
                 Registers, denies the built-in file tools and the output readers, and
                 writes the tools paragraph. --global offers the denies until --deny.
          serve                 run as an MCP server on stdin and stdout

        guardrails - on when .fettler.json has a "guardrails" section: declared trees,
        permissions, scopes, tasks, and the shell denies.
          tasks | run NAME [--timeout SECONDS]
                 run keeps the end of the output that fits in the answer, says
                 what it left out, and removes terminal colour codes.
          setup guardrails CLIENT [--global | --local] [--dry-run] [--hooks] [--deny]
          Permissions: list read create update rename delete execute. The tree holding
          the file gets all but execute. Every other tree gets list read.

        screenings - on when a file has a "screenings" section: the disclosure screen.
          setup screenings CLIENT [--local] [--dry-run]

        Flags for every verb: --config PATH, --root [NAME=]PATH (read-only), --no-config,
        --json, --include-generated, --exclude GLOB. Clients: claude-code,
        claude-desktop, vscode-copilot, github-copilot. A glob has *, ** and ?. A glob
        holding {, }, [ or ], or starting with !, is refused. fettle never writes
        .fettler.json or .fettler.local.json.

        Exit codes: 0 ok, 2 invalid, 3 not-found, 4 outside-root, 5 target-exists,
                    6 stale, 7 conflict, 8 refused, 9 denied, 10 timed-out,
                    11 governed, 12 credential, 13 screened.
                    doctor: 0 healthy, 1 warnings, 2 broken.

        Full reference: https://shoddymills.github.io/shoddy-fettler/fettle-reference.html

        """;
}

