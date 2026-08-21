using System.Reflection;
using System.Text;
using System.Text.Json;
using Fettler.Clients;
using Fettler.Core;

namespace Fettler.Cli;

/// <summary>What a command produced, without a console anywhere near it
/// so the tests can drive every verb with no process.</summary>
public sealed record CliResult(int ExitCode, string Stdout, string Stderr);

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
public static class Command
{
    public static async Task<CliResult> RunAsync(
        IReadOnlyList<string> argv, TextReader stdin, CancellationToken cancel = default)
    {
        Arguments args = Arguments.Parse(argv);
        bool json = args.Has("json");

        CliResult? early = Trivial(args, json);
        if (early is not null) return early;

        Result<Roots> roots = args.DeclaredRoots();

        // setup and doctor configure a MACHINE; they do not work in a
        // tree. Neither resolves a path through the boundary - both reach
        // a compiled-in list of well-known configuration paths and
        // nothing else - so requiring a declared tree bought no
        // containment and cost the bootstrap entirely: setup carries the
        // scaffolder for .fettler.json, which made the file it would
        // write the file required to run it, and doctor refused to
        // diagnose the commonest broken state there is. On a fresh
        // machine no documented sequence worked.
        //
        // So these two, and only these two, fall back to the current
        // directory READ-ONLY. That is not the old implicit-cwd fallback
        // returning: it cannot widen anything, because read-only is all
        // it grants and no verb that touches a tree is reachable this
        // way. And only when NOTHING was declared - a configuration that
        // is present and malformed still refuses, because falling back
        // from a broken boundary is how a boundary silently widens.
        if (!roots.IsOk
            && args.Verb is "setup" or "doctor"
            && roots.Failure!.Message == Roots.NothingDeclared)
        {
            roots = Roots.Open(
                [new TreeDecl("work", Directory.GetCurrentDirectory(), Permissions.ReadOnly)],
                "the current directory, because setup and doctor configure a machine rather than work in a tree");
        }

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
                $"no such flag: {string.Join(", ", unknown)} - try: fettle help"), json);

        // The same argument one step in: the flag is known and its value
        // is the thing that is missing. Second, so a typo at the end of a
        // line is still answered as a typo rather than as a missing value.
        IReadOnlyList<string> empty = args.FlagsMissingAValue;
        if (empty.Count > 0)
            return Failed(new Failure(Outcome.Invalid,
                $"{string.Join(", ", empty)} needs a value, and nothing followed it"
                + " - write --NAME= for a deliberately empty one"), json);

        try
        {
            return await Dispatch(bench, args, json, stdin, cancel).ConfigureAwait(false);
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
            _ => Failed(new Failure(Outcome.Invalid, $"no verb called '{args.Verb}'; try: fettle help"), json),
        };

    // ---- read-only verbs ----

    static CliResult Find(Bench bench, Arguments args, bool json)
    {
        DateTimeOffset? since = null;
        if (args.Value("since") is { } s)
        {
            if (!DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
                return Failed(new Failure(Outcome.Invalid, $"--since is not a time this reads: {s}"), json);
            since = parsed;
        }

        var request = new FindRequest(
            Root: args.Value("in"),
            Pattern: args.At(0),
            IncludeGenerated: args.Has("include-generated"),
            Exclude: args.Values("exclude"),
            Since: since,
            Limit: args.Int("limit", 1000),
            ByModified: args.Value("sort") == "mtime" || args.Has("sort-modified"));

        Result<Bounded<FoundFile>> found = bench.FindChecked(request);
        if (!found.IsOk) return Failed(found.Failure!, json);

        Bounded<FoundFile> answer = found.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteStartArray("files");
                foreach (FoundFile f in answer.Items)
                {
                    w.WriteStartObject();
                    w.WriteString("path", Show(bench, f.Path));
                    w.WriteNumber("bytes", f.Bytes);
                    w.WriteString("modified", f.Modified.UtcDateTime.ToString("o"));
                    WriteFacts(w, f.Facts);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("count", answer.Count);
                w.WriteBoolean("truncated", answer.Truncated);
                w.WriteNumber("excluded", answer.Excluded);
            }));

        var text = new StringBuilder();
        foreach (FoundFile f in answer.Items)
            text.Append(Show(bench, f.Path).PadRight(48))
                .Append(f.Bytes.ToString().PadLeft(9))
                .Append("  ")
                .AppendLine(f.Modified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        text.Append(answer.Count).Append(answer.Count == 1 ? " file" : " files");
        if (answer.Excluded > 0) text.Append($" ({answer.Excluded} excluded: bin, obj, .git - pass --include-generated)");
        if (answer.Truncated) text.Append(" (limit reached; more files exist)");

        return Ok(text.AppendLine().ToString());
    }

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

    static CliResult Search(Bench bench, Arguments args, bool json, TextReader stdin)
    {
        var patterns = new List<string>(args.Values("pattern"));
        if (args.At(0) is { } first) patterns.Insert(0, first);
        for (int i = 1; i < args.Positional.Count; i++) patterns.Add(args.Positional[i]);

        // R4.20: the pattern is the argument a shell is likeliest to
        // mangle before Fettler ever sees it - quotes, backslashes and a
        // leading -- all mean something to somebody first - so it may
        // arrive as bytes instead. This is R5.11's argument about edit
        // text, applied where it turns out to matter just as much: an
        // eaten quote does not fail, it silently searches for something
        // else.
        if (args.Value("pattern-file") is { } file)
        {
            try { AddLines(patterns, File.ReadAllText(file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failed(new Failure(Outcome.NotFound,
                    $"cannot read --pattern-file: {e.Message}", file), json);
            }
        }

        if (args.Has("pattern-stdin")) AddLines(patterns, TextIo.WithoutMark(stdin.ReadToEnd()));

        if (patterns.Count == 0)
            return Failed(new Failure(Outcome.Invalid,
                "search needs a pattern: one written plainly, -e PATTERN, "
                + "--pattern-file PATH or --pattern-stdin"), json);

        Result<SearchAnswer> result = bench.Search(new SearchRequest(
            patterns,
            Root: args.Value("in"),
            Glob: args.Value("glob"),
            Kind: args.Has("literal") ? PatternKind.Literal : PatternKind.Regex,
            CaseSensitive: args.Has("case-sensitive"),
            Context: args.Int("context", 0),
            Limit: args.Int("limit", 200),
            IncludeGenerated: args.Has("include-generated"),
            Exclude: args.Values("exclude"),
            Documents: !args.Has("no-documents")));

        if (!result.IsOk) return Failed(result.Failure!, json);
        SearchAnswer answer = result.Value;

        bool countOnly = args.Has("count");
        bool filesOnly = args.Has("files-only");

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("kind", answer.Kind == PatternKind.Literal ? "literal" : "regex");
                w.WriteStartArray("patterns");
                foreach (string p in answer.Patterns) w.WriteStringValue(p);
                w.WriteEndArray();

                if (!countOnly)
                {
                    w.WriteStartArray("hits");
                    foreach (Hit h in answer.Hits)
                    {
                        w.WriteStartObject();
                        w.WriteString("path", Show(bench, h.Path));
                        w.WriteNumber("line", h.Line);
                        w.WriteNumber("column", h.Column);
                        if (h.Cite is { } cite) w.WriteString("cite", cite);
                        if (!filesOnly) w.WriteString("text", h.Text);
                        if (h.Context.Count > 0 && !filesOnly)
                        {
                            w.WriteStartArray("context");
                            foreach (ContextLine c in h.Context)
                            {
                                w.WriteStartObject();
                                w.WriteNumber("line", c.Number);
                                w.WriteString("text", c.Text);
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                w.WriteNumber("hits_count", answer.Hits.Count);
                w.WriteNumber("files", answer.Files);
                w.WriteNumber("files_searched", answer.FilesSearched);
                w.WriteBoolean("truncated", answer.Truncated);
                w.WriteNumber("excluded", answer.Excluded);
                if (answer.DocumentsSkipped > 0)
                    w.WriteNumber("documents_skipped", answer.DocumentsSkipped);
            }));

        var text = new StringBuilder();

        if (countOnly)
        {
            text.Append(answer.Hits.Count).Append(answer.Hits.Count == 1 ? " hit in " : " hits in ")
                .Append(answer.Files).AppendLine(answer.Files == 1 ? " file" : " files");
        }
        else if (filesOnly)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Hit h in answer.Hits)
                if (seen.Add(h.Path.Full)) text.AppendLine(Show(bench, h.Path));
            text.Append(seen.Count).AppendLine(seen.Count == 1 ? " file" : " files");
        }
        else
        {
            foreach (Hit h in answer.Hits)
            {
                text.Append(Show(bench, h.Path)).Append(':').Append(h.Line).Append(':').Append(h.Column);
                if (h.Cite is { } cite) text.Append("  (").Append(cite).Append(')');
                text.AppendLine();
                if (h.Context.Count > 0)
                    foreach (ContextLine c in h.Context)
                        text.Append(c.Number.ToString().PadLeft(5)).Append(" | ").AppendLine(c.Text);
                else
                    text.Append(h.Line.ToString().PadLeft(5)).Append(" | ").AppendLine(h.Text);
            }

            text.Append(answer.Hits.Count).Append(answer.Hits.Count == 1 ? " hit in " : " hits in ")
                .Append(answer.Files).Append(answer.Files == 1 ? " file" : " files");
            if (answer.Truncated) text.Append(" (limit reached; more matches exist)");

            // Said out loud rather than left to be inferred from a count
            // that looks complete. A search that skipped a document found
            // nothing in it, which is not the same as there being nothing
            // in it.
            if (answer.DocumentsSkipped > 0)
                text.Append("; ").Append(answer.DocumentsSkipped)
                    .Append(answer.DocumentsSkipped == 1 ? " document was" : " documents were")
                    .Append(" not looked inside");

            text.AppendLine();
        }

        return Ok(text.ToString());
    }

    static CliResult Read(Bench bench, Arguments args, bool json)
    {
        var paths = new List<string>(args.Positional);
        if (paths.Count == 0)
            return Failed(new Failure(Outcome.Invalid, "read needs at least one path"), json);

        int? from = args.Has("from") ? args.Int("from", 1) : null;
        int? to = args.Has("to") ? args.Int("to", int.MaxValue) : null;
        int? tail = args.Has("tail") ? args.Int("tail", 0) : null;
        string? member = args.Value("member");

        // Two different questions, so the contradiction is named rather
        // than resolved by precedence. Answering one of them silently is
        // R3.14's failure in another costume: not an error, a wrong answer.
        if (tail is not null && (from is not null || to is not null))
            return Failed(new Failure(Outcome.Invalid,
                "--tail is the end of the file and --from/--to is a named range;"
                + " ask for one or the other"), json);

        if (tail is < 1)
            return Failed(new Failure(Outcome.Invalid,
                "--tail counts lines back from the end, so it needs at least 1"), json);

        if (member is not null && paths.Count != 1)
            return Failed(new Failure(Outcome.Invalid,
                "--member names one entry inside one archive, so read one archive at a time"), json);

        Result<IReadOnlyList<ReadSlice>> result =
            bench.Read(new ReadRequest(paths, from, to, tail, member));
        if (!result.IsOk) return Failed(result.Failure!, json);

        if (json)
            return Ok(Json(w =>
            {
                w.WriteStartArray("files");
                foreach (ReadSlice s in result.Value)
                {
                    w.WriteStartObject();
                    w.WriteString("path", Show(bench, s.Path));
                    w.WriteString("kind", Typed.NameOf(s.Kind));

                    if (s.Image is { } image)
                    {
                        w.WriteString("mime_type", image.MimeType);
                        w.WriteNumber("width", image.Width);
                        w.WriteNumber("height", image.Height);
                        w.WriteNumber("bytes", image.Bytes);
                        w.WriteString("hash", s.Hash);
                        // The bytes travel base64 in the machine-readable
                        // answer. The MCP front end lifts them out into a
                        // native image part rather than leaving a caller
                        // to read base64 as prose.
                        w.WriteString("base64", Convert.ToBase64String(image.Content));
                        WriteFacts(w, s.Facts);
                        w.WriteEndObject();
                        continue;
                    }

                    w.WriteString("text", s.Text);
                    w.WriteNumber("from", s.From);
                    w.WriteNumber("to", s.To);
                    w.WriteNumber("lines", s.TotalLines);
                    w.WriteBoolean("truncated", s.Truncated);
                    if (s.Truncated) w.WriteNumber("lines_remaining", s.Remaining);
                    w.WriteString("encoding", s.EncodingName);
                    w.WriteString("line_ending", s.LineEnding);
                    w.WriteBoolean("line_ending_mixed", s.MixedEndings);
                    w.WriteBoolean("final_newline", s.FinalNewline);
                    w.WriteString("hash", s.Hash);
                    w.WriteNumber("bytes", s.Bytes);
                    WriteFacts(w, s.Facts);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("count", result.Value.Count);
            }));

        var text = new StringBuilder();
        foreach (ReadSlice s in result.Value)
        {
            if (s.Image is { } image)
            {
                // A terminal cannot show a picture, so it is told what
                // there is instead of being sent a screenful of base64.
                text.Append(Show(bench, s.Path)).Append("  ").Append(image.MimeType)
                    .Append("  ").Append(image.Width).Append('x').Append(image.Height)
                    .Append("  ").Append(image.Bytes).AppendLine(" bytes");
                continue;
            }

            text.Append(Show(bench, s.Path)).Append("  ").Append(Typed.NameOf(s.Kind))
                .Append("  ").Append(s.EncodingName)
                .Append("  ").Append(s.LineEnding).Append(s.MixedEndings ? " (MIXED)" : "")
                .Append("  final-newline: ").Append(s.FinalNewline ? "yes" : "no")
                .Append("  lines ").Append(s.From).Append('-').Append(s.To)
                .Append(" of ").AppendLine(s.TotalLines.ToString());

            int number = s.From;
            foreach (Line line in TextIo.SplitLines(s.Text))
                text.Append((number++).ToString().PadLeft(5)).Append(" | ").AppendLine(line.Text);

            // 9b.8: a truncated answer must never read as a complete one,
            // and must say the way to see the rest.
            if (s.Truncated)
                text.Append("... ").Append(s.Remaining)
                    .Append(" more line(s); pass --from ").Append(s.To + 1)
                    .AppendLine(" to go on, or --to for a range");
        }

        return Ok(text.ToString());
    }

    static CliResult TaskList(Bench bench, bool json)
    {
        Result<IReadOnlyList<TaskDecl>> tasks = bench.TaskList();
        if (!tasks.IsOk) return Failed(tasks.Failure!, json);

        if (json)
            return Ok(Json(w =>
            {
                w.WriteStartArray("tasks");
                foreach (TaskDecl t in tasks.Value)
                {
                    w.WriteStartObject();
                    w.WriteString("name", t.Name);
                    // Both spellings, deliberately. "run" is the line as
                    // DECLARED, placeholders and all, so the listing can
                    // be compared with the file; "command" is what would
                    // actually be launched. Showing only one of them
                    // leaves the caller trusting a listing that is wrong
                    // in one direction or the other.
                    w.WriteString("run", t.Display);
                    w.WriteStartArray("command");
                    foreach (string part in t.Command) w.WriteStringValue(part);
                    w.WriteEndArray();
                    if (t.WorkingDirectory is { } cwd) w.WriteString("cwd", cwd);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("count", tasks.Value.Count);
            }));

        if (tasks.Value.Count == 0)
            return Ok($"no tasks declared (no \"tasks\" in {RootsFile.FileName}){Environment.NewLine}");

        var text = new StringBuilder();
        int column = NameColumn(tasks.Value.Select(t => t.Name));
        foreach (TaskDecl t in tasks.Value)
            text.Append(t.Name.PadRight(column)).AppendLine(t.Display);

        return Ok(text.ToString());
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
        IReadOnlyList<string> names = bench.Roots.Names;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteStartArray("roots");
                foreach (string name in names)
                {
                    Grant grant = bench.Roots.GrantOf(name);
                    string full = bench.Roots.PathOf(name);

                    w.WriteStartObject();
                    w.WriteString("name", name);
                    w.WriteString("path", full);
                    w.WriteString("can", Permissions.Write(grant.Can));

                    // Reported only where it is on. A "screen": "nothing"
                    // against every tree would be noise on the answer
                    // almost every caller gets, and would read as a
                    // feature that had failed rather than one nobody
                    // asked for.
                    if (grant.Screen != Screened.None)
                        w.WriteString("screen", Screens.Write(grant.Screen));

                    WriteScreening(w, grant.Screen, bench.Roots.Models);

                    w.WriteBoolean("default", name.Equals(names[0], Core.Roots.PathComparison));

                    if (grant.Scopes.Count > 0)
                    {
                        w.WriteStartArray("scopes");
                        foreach (Scope scope in grant.Scopes)
                        {
                            w.WriteStartObject();
                            w.WriteString("path", Path.GetRelativePath(full, scope.Path).Replace('\\', '/'));
                            w.WriteString("can", Permissions.Write(scope.Can));

                            // A scope that said nothing inherits, so what
                            // is reported here is what the scope ITSELF
                            // states - and its absence means "whatever the
                            // tree says" rather than "nothing".
                            if (scope.Screen is { } screened)
                            {
                                w.WriteString("screen", Screens.Write(screened));
                                WriteScreening(w, screened, bench.Roots.Models);
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
                // questions, and somebody fixing the second needs the
                // first.
                if (bench.Roots.Models is { } where) w.WriteString("models", where);

                w.WriteString("source", bench.Roots.Origin);
            }));

        // B.9: what may be DONE in each tree, not only where it is. A
        // boundary a caller can see only by being refused by it is one
        // they learn by trial against a security check.
        var text = new StringBuilder();
        int column = NameColumn(names);
        foreach (string name in names)
        {
            Grant grant = bench.Roots.GrantOf(name);
            string full = bench.Roots.PathOf(name);

            text.Append(name.PadRight(column)).Append(full)
                .AppendLine(name.Equals(names[0], Core.Roots.PathComparison) ? "  (default)" : "");
            text.Append(' ', column).Append("can: ").AppendLine(Permissions.Write(grant.Can));

            if (grant.Screen != Screened.None)
            {
                text.Append(' ', column).Append("screen: ").AppendLine(Screens.Write(grant.Screen));

                foreach ((Screened category, string said)
                         in Screening(grant.Screen, bench.Roots.Models))
                    text.Append(' ', column).Append("  ").Append(Screens.NameOf(category))
                        .Append(": ").AppendLine(said);
            }

            foreach (Scope scope in grant.Scopes)
            {
                text.Append(' ', column)
                    .Append(Path.GetRelativePath(full, scope.Path).Replace('\\', '/'))
                    .Append("  can: ").Append(Permissions.Write(scope.Can));

                if (scope.Screen is { } screened)
                    text.Append("  screen: ").Append(Screens.Write(screened));

                text.AppendLine();

                // Only where the scope states a screen of its own. One
                // that says nothing inherits the tree's, and repeating
                // the tree's models under every scope would bury the
                // scopes that actually differ.
                if (scope.Screen is { } stated)
                    foreach ((Screened category, string said)
                             in Screening(stated, bench.Roots.Models))
                        text.Append(' ', column).Append("    ").Append(Screens.NameOf(category))
                            .Append(": ").AppendLine(said);
            }
        }

        // R4.7: the model tiers are inert until somebody names a
        // directory, and a screen listing them without one reads as a
        // protection that is running. Said once, about the whole
        // configuration, and only where a category needing a model was
        // actually asked for.
        if (bench.Roots.Models is null && AnythingNeedsAModel(bench))
            text.AppendLine().Append("no \"models\" directory is declared, so ")
                .Append(ModelBackedWords())
                .AppendLine(" screen nothing here - only the identifier patterns run");

        // Where the boundary came from, not only what it is: a caller
        // who disagrees with it has to know which file to open.
        text.AppendLine().Append("declared by ").AppendLine(bench.Roots.Origin);

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
                ? "no model installed - reads here will refuse"
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
                $"no client called '{only}'; they are: {string.Join(", ", Places.Clients)}"), json);

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
                    brief.Append("  ").Append(c.Client).Append(' ').Append(Word(c.Level))
                         .Append(": ").Append(Word(c.Verdict))
                         .Append(" - ").AppendLine(c.Detail);

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

                w.WriteStartArray("clients");
                foreach (ClientReport c in shown)
                {
                    w.WriteStartObject();
                    w.WriteString("client", c.Client);
                    w.WriteString("level", Word(c.Level));
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
                    w.WriteString("severity", f.Serious ? "serious" : "note");
                    w.WriteString("message", f.Message);
                    if (f.Where is { } where) w.WriteString("where", where);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }), Code(diagnosis));

        var text = new StringBuilder();
        text.Append("fettle ").Append(diagnosis.Install.Version).Append("  ").AppendLine(diagnosis.Install.Binary);
        text.Append("on PATH: ").AppendLine(diagnosis.Install.OnPath
            ? diagnosis.Install.PathBinary
            : "no - every registration naming a bare 'fettle' will fail to launch");

        // A closed list that has fallen behind reports exactly as clean
        // as one that has not, so the date it was drawn is part of the
        // answer rather than a footnote in the source.
        text.Append("tool inventory drawn ").Append(Doctor.InventoryOf)
            .AppendLine("; check 2.8 says nothing about a tool added to the client since");
        text.AppendLine();

        foreach (ClientReport c in shown)
        {
            if (args.Has("quiet") && c.Verdict is Verdict.NotApplicable or Verdict.Healthy) continue;
            text.Append(c.Client.PadRight(16)).Append(Word(c.Level).PadRight(8))
                .Append(Word(c.Verdict).PadRight(10))
                .AppendLine(c.Detail);
            if (c.Where is { } where) text.Append(' ', 24).AppendLine(where);
        }

        if (diagnosis.Findings.Count > 0)
        {
            text.AppendLine().AppendLine("what could let the assistant go round the boundary:");
            foreach (Finding f in diagnosis.Findings)
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

    static string Word(Level level) => level == Level.Global ? "global" : "repo";

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

    static CliResult Setup(Bench bench, Arguments args, bool json)
    {
        if (args.At(0) is not { } client && !args.Has("all"))
            return Failed(new Failure(Outcome.Invalid,
                $"setup needs a client, or --all; the clients are: {string.Join(", ", Places.Clients)}"), json);

        if (args.Has("global") && args.Has("local"))
            return Failed(new Failure(Outcome.Invalid, "setup takes --global or --local, not both"), json);

        Machine machine = MachineFor(bench);
        Level level = args.Has("global") ? Level.Global : Level.Repo;

        IReadOnlyList<string> clients = args.Has("all") ? Places.Clients : [args.At(0)!];

        var changes = new List<Change>();
        var notes = new List<string>();

        foreach (string one in clients)
        {
            Result<Scaffolding> done = Scaffold.Apply(machine, new Scaffold.Options(
                one, level,
                DryRun: args.Has("dry-run"),
                Force: args.Has("force"),
                Hooks: args.Has("hooks"),
                Deny: args.Has("deny"),
                Command: args.Value("command")), Version);

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
        if (changes.Count == 0) human.AppendLine("  nothing; it was already as it should be");

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
            "write needs its content named: --stdin, --text TEXT, or --text-file PATH"), json);


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

    static async Task<CliResult> Edit(Bench bench, Arguments args, bool json, TextReader stdin, CancellationToken cancel)
    {
        Result<IReadOnlyList<FileEdits>> batch = EditScript.Build(args, stdin);
        if (!batch.IsOk) return Failed(batch.Failure!, json);

        Result<EditAnswer> applied = await bench.EditAsync(batch.Value, args.Has("dry-run"), cancel,
                args.Has("allow-credential"))
            .ConfigureAwait(false);

        if (!applied.IsOk) return Failed(applied.Failure!, json);
        EditAnswer answer = applied.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteBoolean("dry_run", answer.DryRun);
                w.WriteStartArray("plan");
                foreach (PlannedEdit p in answer.Plan)
                {
                    w.WriteStartObject();
                    w.WriteString("path", p.Path);
                    w.WriteNumber("edit", p.Index);
                    w.WriteNumber("line", p.Line);
                    w.WriteString("description", p.Description);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("files");
                foreach (EditedFile f in answer.Files)
                {
                    w.WriteStartObject();
                    w.WriteString("path", Show(bench, f.Path));
                    w.WriteNumber("edits", f.EditsApplied);
                    w.WriteString("hash", f.Hash);
                    w.WriteBoolean("line_ending_mixed", f.MixedEndings);
                    WriteLost(w, f.MetadataLost);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }));

        var human = new StringBuilder();
        if (answer.DryRun)
        {
            human.Append("would apply ").Append(answer.Plan.Count)
                 .Append(answer.Plan.Count == 1 ? " edit" : " edits").AppendLine();
            foreach (PlannedEdit p in answer.Plan)
                human.Append("   edit ").Append(p.Index).Append("  ").Append(p.Path)
                     .Append(" line ").Append(p.Line).Append("  ").AppendLine(p.Description);
        }
        else
        {
            int edits = 0;
            foreach (EditedFile f in answer.Files) edits += f.EditsApplied;
            human.Append("applied ").Append(edits).Append(edits == 1 ? " edit to " : " edits to ")
                 .Append(answer.Files.Count).AppendLine(answer.Files.Count == 1 ? " file" : " files");
            foreach (EditedFile f in answer.Files)
            {
                human.Append("   ").Append(Show(bench, f.Path)).Append("  ").Append(f.Hash[..12])
                     .AppendLine(f.MixedEndings ? "  (MIXED line endings)" : "");
                AppendLost(human, f.MetadataLost);
            }
        }

        return Ok(human.ToString());
    }

    static async Task<CliResult> Replace(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        string? find = args.At(0);
        string? with = args.At(1);
        if (find is null || with is null)
            return Failed(new Failure(Outcome.Invalid, "replace needs the text to find and the text to put there"), json);

        Result<ReplaceAnswer> result = await bench.ReplaceAsync(
            find, with, args.Value("glob"), args.Value("in"), args.Has("dry-run"),
            args.Has("include-generated"), cancel).ConfigureAwait(false);

        if (!result.IsOk) return Failed(result.Failure!, json);
        ReplaceAnswer answer = result.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteBoolean("dry_run", answer.DryRun);
                w.WriteNumber("occurrences", answer.TotalOccurrences);
                w.WriteStartArray("files");
                foreach (ReplacePlan p in answer.Files)
                {
                    w.WriteStartObject();
                    w.WriteString("path", p.Path);
                    w.WriteNumber("occurrences", p.Occurrences);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("count", answer.Files.Count);
            }));

        var human = new StringBuilder()
            .Append(answer.DryRun ? "would change " : "changed ")
            .Append(answer.Files.Count).Append(answer.Files.Count == 1 ? " file, " : " files, ")
            .Append(answer.TotalOccurrences)
            .AppendLine(answer.TotalOccurrences == 1 ? " occurrence" : " occurrences");

        foreach (ReplacePlan p in answer.Files)
            human.Append("   ").Append(p.Path.PadRight(44)).AppendLine(p.Occurrences.ToString());

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

    static async Task<CliResult> Copy(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (args.At(0) is not { } from || args.At(1) is not { } to)
            return Failed(new Failure(Outcome.Invalid, "copy needs a source and a destination"), json);

        Result<CopyReport> copied = await bench.CopyAsync(
            from, to, args.Has("recursive"), args.Has("overwrite"), args.Has("preserve-times"), cancel)
            .ConfigureAwait(false);

        if (!copied.IsOk) return Failed(copied.Failure!, json);
        CopyReport c = copied.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("from", Show(bench, c.From));
                w.WriteString("to", Show(bench, c.To));
                w.WriteNumber("files", c.Files);
                w.WriteNumber("directories", c.Directories);
                w.WriteNumber("bytes", c.Bytes);
                w.WriteBoolean("crossed_root", c.CrossedRoot);
                w.WriteStartArray("links_skipped");
                foreach (string s in c.LinksSkipped) w.WriteStringValue(s);
                w.WriteEndArray();
            }));

        var human = new StringBuilder()
            .Append("copied ").Append(Show(bench, c.From)).Append(" -> ").Append(Show(bench, c.To))
            .Append("  ").Append(c.Files).Append(c.Files == 1 ? " file" : " files");
        if (c.Directories > 0) human.Append("  ").Append(c.Directories).Append(c.Directories == 1 ? " directory" : " directories");
        if (c.CrossedRoot) human.Append("  crossed roots");
        human.AppendLine();

        foreach (string skipped in c.LinksSkipped)
            human.Append("   not copied (a link): ").AppendLine(skipped);

        return Ok(human.ToString());
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
                "extract needs --into DIR, naming where the members land"), json);

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

    static async Task<CliResult> Run(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (args.At(0) is not { } name)
            return Failed(new Failure(Outcome.Invalid, "run needs the name of a declared task"), json);

        // A task is declared whole, so nothing is composed at the call.
        // Dropping an extra word silently would run something other than
        // what the caller asked for and answer 0, which is the wrong-answer
        // shape R3.14 exists to prevent, one argument further in.
        if (args.Positional.Count > 1)
            return Failed(new Failure(Outcome.Invalid,
                $"run takes a task name and nothing else; '{args.At(1)}' is extra. "
                + "A task is declared whole and takes no arguments - declare a second "
                + "task for the second thing you want run."), json);

        var timeout = TimeSpan.FromSeconds(args.Int("timeout", 0));
        Result<TaskRun> ran = await bench.RunAsync(name, timeout, cancel).ConfigureAwait(false);
        if (!ran.IsOk) return Failed(ran.Failure!, json);
        TaskRun r = ran.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("task", r.Name);
                if (r.Command is { } launched)
                {
                    w.WriteStartArray("command");
                    foreach (string part in launched) w.WriteStringValue(part);
                    w.WriteEndArray();
                }
                w.WriteNumber("exit_code", r.ExitCode);
                w.WriteString("stdout", r.Stdout);
                w.WriteString("stderr", r.Stderr);
                w.WriteNumber("seconds", Math.Round(r.Duration.TotalSeconds, 3));
                w.WriteBoolean("timed_out", r.TimedOut);
            }));

        var human = new StringBuilder()
            .Append(r.Name).Append(": exit ").Append(r.ExitCode)
            .Append(" in ").Append(r.Duration.TotalSeconds.ToString("0.0")).AppendLine("s");

        if (r.Stdout.Length > 0) human.Append("[stdout] ").AppendLine(r.Stdout.TrimEnd());
        if (r.Stderr.Length > 0) human.Append("[stderr] ").AppendLine(r.Stderr.TrimEnd());

        // The task's own exit code is not this command's: `run` succeeded
        // in running it. A caller wanting the child's code reads it from
        // the answer, which is why it is in both output modes.
        return Ok(human.ToString());
    }

    static async Task<CliResult> Batch(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        Result<BatchAnswer> ran;
        if (args.Value("script-inline") is { } inline)
            ran = await BatchScript.RunInlineAsync(bench, inline, cancel).ConfigureAwait(false);
        else if (args.Value("script") is { } script)
            ran = await BatchScript.RunAsync(bench, script, cancel).ConfigureAwait(false);
        else
            return Failed(new Failure(Outcome.Invalid, "batch needs --script FILE"), json);

        if (!ran.IsOk) return Failed(ran.Failure!, json);
        BatchAnswer answer = ran.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteStartArray("operations");
                foreach (BatchStep s in answer.Steps)
                {
                    w.WriteStartObject();
                    w.WriteNumber("index", s.Index);
                    w.WriteString("op", s.Op);
                    w.WriteString("outcome", ExitCodes.NameOf(s.Outcome));
                    w.WriteString("detail", s.Detail);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("completed", answer.Completed);
                w.WriteNumber("count", answer.Steps.Count);
                w.WriteBoolean("stopped_early", answer.StoppedEarly);
            }));

        var human = new StringBuilder();
        foreach (BatchStep s in answer.Steps)
            human.Append(s.Index.ToString().PadLeft(2)).Append("  ").Append(s.Op.PadRight(8))
                 .Append(ExitCodes.NameOf(s.Outcome).PadRight(14)).AppendLine(s.Detail);

        human.Append(answer.Steps.Count).Append(answer.Steps.Count == 1 ? " operation, " : " operations, ")
             .Append(answer.Completed).AppendLine(" ok");

        if (answer.StoppedEarly)
            human.AppendLine("stopped at the first failure; the operations above it were done and are not undone");

        return new CliResult(answer.StoppedEarly ? ExitCodes.Refused : ExitCodes.Ok, human.ToString(), string.Empty);
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
            }), string.Empty);

        // An empty path is no path. It reached the caller as a bare "()"
        // on the end of a refusal, which reads as a fact about the thing
        // refused rather than as a field nobody filled in.
        return new CliResult(code, string.Empty,
            $"refused: {failure.Message}"
            + (failure.Path is { Length: > 0 } at ? $" ({at})" : "") + Environment.NewLine);
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

    static string JsonBody(Action<Utf8JsonWriter> body)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
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
            text.Append("   warning: could not carry over ").AppendLine(l);
    }

    /// <summary>Paths are written qualified only when more than one root
    /// is open, so a single-root caller sees exactly what it always
    /// did (R8.7).</summary>
    static string Show(Bench bench, ContainedPath path) =>
        bench.Roots.IsSingle ? path.Display : path.Qualified;

    static string Help() => """
        fettle - find, search, read, write, edit, move, copy, delete, unpack an archive,
         and run a declared task.

        Global flags, accepted by every verb:
          --config PATH        the .fettler.json to use, rather than searching
          --root PATH          a tree for READING only, repeatable as NAME=PATH.
                               It cannot grant write at any level: to write to a
                               tree, put a .fettler.json in it. With neither flag,
                               the nearest .fettler.json at or above the current
                               directory declares the trees, and its paths are read
                               relative to ITSELF - so the boundary is the same from
                               anywhere inside the tree. With none of those, fettle
                               refuses: there is no implicit current directory.
          --no-config          do not search for one, so a command refuses as it
                               would from outside any configured tree
          --json               the machine-readable result, complete, on stdout
          --include-generated  do not skip bin, obj, .git, artifacts, node_modules
          --exclude GLOB       skip more, repeatable

        Verbs:
          find PATTERN [--sort mtime] [--limit N] [--since TIME]
          search PATTERN [-e PATTERN]... [--glob G] [--literal] [--case-sensitive]
                         [--context N] [--limit N] [--count] [--files-only]
                         [--no-documents]
                 A pattern may instead arrive as --pattern-file PATH or
                 --pattern-stdin, one per line, so a shell never gets to
                 eat a quote out of it.
                 A document in the sweep - a PDF - is rendered to text and
                 searched like anything else, and a hit in one cites its page.
                 --no-documents leaves them shut. Either way the answer says
                 how many were not looked inside, because a search that never
                 opened a file found nothing in it, which is not the same as
                 there being nothing in it.
          read PATH... [--from N] [--to N] [--tail N] [--member NAME]
                 Text, notebooks, PDFs, .xlsx/.xlsm workbooks, .docx/.docm
                 documents, images and archives. Stops at 2000 lines
                 and says how many are left; --to asks for more. --tail N is the
                 LAST n lines - for a log or a build transcript, so reaching the
                 end of one costs no arithmetic and no second call.
                 A .zip, .tar, .tar.gz or .tgz reads as its MANIFEST: every
                 member, its size, and whether it carries the execute bit.
                 --member NAME reads one entry out of it, decoded like any other
                 file, without unpacking anything. A lone .gz reads as whatever
                 is underneath it.
          extract ARCHIVE --into DIR [--overwrite]
                 Unpack into a declared tree. Refused WHOLE if any member would
                 land outside a tree, needs a permission it has not got, or is a
                 link - so an archive never lands half-unpacked. Carries the
                 execute bit. Never writes over anything without --overwrite.
          write PATH --stdin [--overwrite] [--encoding E] [--eol lf|crlf]
          edit PATH [--expect HASH] [--dry-run] and one of:
                 --replace S --with S [--between 120-140] [--all]
                 --insert-after N --text S
                 --delete 150-151
                 --script FILE
               Any of --replace, --with and --text may instead be given as
               --NAME-file PATH or --NAME-stdin, so text with quotes and
               newlines in it never has to be escaped onto a command line.
          replace FIND WITH [--glob G] [--dry-run]
          new PATH
          mkdir PATH
          move FROM TO [--overwrite]
          copy FROM TO [--recursive] [--overwrite] [--preserve-times]
          delete PATH [--recursive] [--force]
          exec PATH --on | --off
          tasks                 the declared tasks and the command line each runs
          roots                 the trees, their paths, and what may be done in each
          run NAME [--timeout SECONDS]
                 A task is declared whole and takes NO arguments from its
                 caller. Declare a second task rather than composing one.
                 A command line may name values the configuration declares
                 under "replacements", written {like-this}; the value is
                 the file's and an undeclared name is refused.
          batch --script FILE
          doctor [--client NAME] [--quiet] [--hook]
                 whether this tool is wired into each assistant client, at both
                 levels, and what still lets the boundary be gone round
          setup CLIENT | --all [--global | --local] [--dry-run] [--force]
                [--hooks] [--deny] [--command PATH]
                 clients: claude-code, claude-desktop, vscode-copilot, github-copilot
          serve                 become the MCP front end on stdio

        Permissions, granted only by a .fettler.json:
          list read create update rename delete execute
          The tree that file sits in gets all but execute; every other tree gets
          list read. execute is never a default anywhere. A scope inside a tree
          replaces what the tree grants, so it can take a permission away, and a
          scope with no `list` is not there at all as far as find and search go.
          .fettler.json and .fettler.local.json say what this tool may do - the
          trees, and the tasks - and it does not write them. See
          docs/fettler.html.

        An unknown flag is refused, never ignored - one dash or two: ignoring
        one would eat the argument after it, or be read as a second pattern,
        and answer a different question confidently. -e is the only short
        flag there is. A pattern or path that starts with a hyphen goes after
        a bare --, or through --pattern-file or --pattern-stdin.

        Exit codes: 0 ok, 2 invalid, 3 not-found, 4 outside-root, 5 target-exists,
                    6 stale, 7 conflict, 8 refused, 9 denied, 10 timed-out,
                    11 governed, 12 credential. doctor answers 0, 1 for warnings,
                    2 for broken.

        """;
}

