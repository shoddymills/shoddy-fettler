using System.Text.Json;
using Fettler.Core;

namespace Fettler.Cli;

/// <summary>
/// Turning the two script-carrying flags into core requests.
///
/// <para><b>A script file is read as an argument, not as a tree
/// operation.</b> It arrives from the shell that invoked <c>fettle</c>,
/// the same way argv does, so it is read directly rather than resolved
/// through R8's boundary - which guards what Fettler <em>operates on</em>,
/// not what its caller hands it. The MCP front end never takes this
/// path: there, a batch arrives inside the request as structured data,
/// so there is no file and no question.</para>
/// </summary>
/// <summary>
/// Where an edit's text comes from: the flag itself, a file, or stdin.
///
/// <para><b>R5.11, and it exists because the tool was awkward to use on
/// its own documentation.</b> Supplying a paragraph of prose meant
/// hand-escaping it into JSON - every quote, every newline - which is
/// error-prone enough that the honest way to do it was to write a
/// program that generated the edit script. Needing a second language to
/// drive the editing tool is a defect in the editing tool, not a clever
/// workaround, and a reader following the documentation would hit the
/// same wall without necessarily having that second language.</para>
///
/// <para>So <c>--replace</c>, <c>--with</c> and <c>--text</c> each gain
/// a <c>-file</c> and a <c>-stdin</c> spelling. The text then travels as
/// bytes on a stream and no escaping happens anywhere.</para>
/// </summary>
sealed class TextSupply(Arguments args, TextReader stdin)
{
    bool stdinTaken;

    public Result<string?> Read(string name)
    {
        if (args.Value(name) is { } inline) return Result<string?>.Ok(inline);

        if (args.Value(name + "-file") is { } file)
        {
            try { return Result<string?>.Ok(File.ReadAllText(file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Result<string?>.Fail(Outcome.NotFound,
                    $"cannot read --{name}-file: {e.Message}", file);
            }
        }

        if (args.Has(name + "-stdin"))
        {
            // Stdin is one stream and can be spent once. Two flags
            // claiming it would silently give the second one nothing,
            // which is the kind of quiet wrong answer this tool exists
            // to avoid.
            if (stdinTaken)
                return Result<string?>.Fail(Outcome.Invalid,
                    "only one of --replace-stdin, --with-stdin and --text-stdin can be used at a time");

            stdinTaken = true;
            return Result<string?>.Ok(TextIo.WithoutMark(stdin.ReadToEnd()));
        }

        return Result<string?>.Ok(null);
    }
}

public static class EditScript
{
    /// <summary>
    /// The three shapes R5 asks for: one scoped replacement from flags,
    /// a batch from a file, and a batch spanning files from the same
    /// file (R5.7).
    /// </summary>
    public static Result<IReadOnlyList<FileEdits>> Build(Arguments args, TextReader stdin)
    {
        // The MCP front end carries its script inside the request rather
        // than in a file, so both spellings reach one parser.
        if (args.Value("script-inline") is { } inline) return FromJson(Scripts.Parse(inline), args);
        if (args.Value("script") is { } script) return FromJson(Scripts.Load(script), args);

        string? path = args.At(0);
        if (path is null)
            return Result<IReadOnlyList<FileEdits>>.Fail(Outcome.Invalid, "edit needs a path");

        var supply = new TextSupply(args, stdin);

        Result<(int? From, int? To)> range = Range(args);
        if (!range.IsOk) return range.Carry<IReadOnlyList<FileEdits>>();

        Result<Edit> edit = OneEdit(args, supply, range.Value);
        if (!edit.IsOk) return edit.Carry<IReadOnlyList<FileEdits>>();

        return Result<IReadOnlyList<FileEdits>>.Ok([
            new FileEdits(path, args.Value("expect"), [edit.Value])
        ]);
    }

    static Result<Edit> OneEdit(Arguments args, TextSupply supply, (int? From, int? To) range)
    {
        // Delete needs no text at all, so it is tested first and cannot
        // be confused with an empty replacement.
        if (args.Value("delete") is { } lines)
        {
            string[] parts = lines.Split(["..", "-", ":"], StringSplitOptions.RemoveEmptyEntries);
            if (!int.TryParse(parts[0], out int from))
                return Result<Edit>.Fail(Outcome.Invalid, $"--delete wants a line or a range like 150-151 (got '{lines}')");

            int to = parts.Length > 1 && int.TryParse(parts[1], out int t) ? t : from;
            return Result<Edit>.Ok(new Edit.DeleteLines(from, to));
        }

        if (args.Has("insert-after"))
        {
            Result<string?> text = supply.Read("text");
            if (!text.IsOk) return text.Carry<Edit>();

            if (text.Value is null)
                return Result<Edit>.Fail(Outcome.Invalid,
                    "--insert-after needs --text, --text-file or --text-stdin");

            return Result<Edit>.Ok(new Edit.InsertAfter(args.Int("insert-after", 0), text.Value));
        }

        Result<string?> find = supply.Read("replace");
        if (!find.IsOk) return find.Carry<Edit>();

        if (find.Value is null)
            return Result<Edit>.Fail(Outcome.Invalid,
                "edit needs --replace (or --replace-file, or --replace-stdin), "
                + "or --insert-after, or --delete, or --script FILE");

        Result<string?> with = supply.Read("with");
        if (!with.IsOk) return with.Carry<Edit>();

        return Result<Edit>.Ok(new Edit.Replace(
            find.Value, with.Value ?? string.Empty, range.From, range.To, args.Has("all")));
    }

    /// <summary>
    /// <c>--between 120-140</c> or <c>--between 120..140</c>, and
    /// <c>--from</c>/<c>--to</c> as the longer spelling. One token, so
    /// there is no ambiguity about whether the second number is a flag
    /// value or the next positional argument - which is exactly the
    /// class of shell confusion this tool exists to avoid.
    /// </summary>
    static Result<(int? From, int? To)> Range(Arguments args)
    {
        if (args.Value("between") is { } between)
        {
            string[] parts = between.Split(["..", "-", ":"], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b))
                return Result<(int?, int?)>.Fail(Outcome.Invalid,
                    $"--between wants two line numbers like 120-140 (got '{between}')");

            return Result<(int?, int?)>.Ok((a, b));
        }

        int? from = args.Has("from") ? args.Int("from", 1) : null;
        int? to = args.Has("to") ? args.Int("to", int.MaxValue) : null;
        return Result<(int?, int?)>.Ok((from, to));
    }

    static Result<IReadOnlyList<FileEdits>> FromJson(Result<JsonDocument> parsed, Arguments args)
    {
        if (!parsed.IsOk) return parsed.Carry<IReadOnlyList<FileEdits>>();

        using JsonDocument document = parsed.Value;
        JsonElement root = document.RootElement;

        // A batch spanning files (R5.7), or one file named on the command
        // line - the same document shape either way.
        if (root.TryGetProperty("files", out JsonElement files))
        {
            var batch = new List<FileEdits>();
            foreach (JsonElement file in files.EnumerateArray())
            {
                if (!file.TryGetProperty("path", out JsonElement p) || p.GetString() is not { } path)
                    return Result<IReadOnlyList<FileEdits>>.Fail(Outcome.Invalid,
                        "every entry in \"files\" needs a \"path\"");

                Result<IReadOnlyList<Edit>> edits = Edits(file);
                if (!edits.IsOk) return edits.Carry<IReadOnlyList<FileEdits>>();

                batch.Add(new FileEdits(path, Text(file, "expect"), edits.Value));
            }

            return Result<IReadOnlyList<FileEdits>>.Ok(batch);
        }

        if (args.At(0) is not { } single)
            return Result<IReadOnlyList<FileEdits>>.Fail(Outcome.Invalid,
                "edit needs a path, or a script with a \"files\" array");

        Result<IReadOnlyList<Edit>> one = Edits(root);
        if (!one.IsOk) return one.Carry<IReadOnlyList<FileEdits>>();

        return Result<IReadOnlyList<FileEdits>>.Ok([
            new FileEdits(single, args.Value("expect") ?? Text(root, "expect"), one.Value)
        ]);
    }

    static Result<IReadOnlyList<Edit>> Edits(JsonElement owner)
    {
        if (!owner.TryGetProperty("edits", out JsonElement edits) || edits.ValueKind != JsonValueKind.Array)
            return Result<IReadOnlyList<Edit>>.Fail(Outcome.Invalid, "the script needs an \"edits\" array");

        var built = new List<Edit>();
        int index = 0;

        foreach (JsonElement e in edits.EnumerateArray())
        {
            index++;

            // R5.11 inside a script too: "replaceFile", "withFile" and
            // "textFile" name a file holding the text, so a script that
            // moves a paragraph does not have to carry the paragraph
            // escaped into JSON.
            Result<string?> find = Sourced(e, "replace");
            if (!find.IsOk) return find.Carry<IReadOnlyList<Edit>>();

            if (find.Value is { } finding)
            {
                Result<string?> with = Sourced(e, "with");
                if (!with.IsOk) return with.Carry<IReadOnlyList<Edit>>();

                built.Add(new Edit.Replace(
                    finding, with.Value ?? string.Empty,
                    Number(e, "from"), Number(e, "to"),
                    e.TryGetProperty("all", out JsonElement all) && all.ValueKind == JsonValueKind.True));
                continue;
            }

            if (Number(e, "insertAfter") is { } after)
            {
                Result<string?> text = Sourced(e, "text");
                if (!text.IsOk) return text.Carry<IReadOnlyList<Edit>>();

                built.Add(new Edit.InsertAfter(after, text.Value ?? string.Empty));
                continue;
            }

            if (Number(e, "deleteFrom") is { } deleteFrom)
            {
                built.Add(new Edit.DeleteLines(deleteFrom, Number(e, "deleteTo") ?? deleteFrom));
                continue;
            }

            return Result<IReadOnlyList<Edit>>.Fail(Outcome.Invalid,
                $"edit {index} is none of replace, insertAfter or deleteFrom");
        }

        return Result<IReadOnlyList<Edit>>.Ok(built);
    }

    /// <summary>A script field, either inline or naming a file that
    /// holds it (R5.11). Null means the field was not given at all,
    /// which is different from being given as empty.</summary>
    static Result<string?> Sourced(JsonElement owner, string name)
    {
        if (Text(owner, name) is { } inline) return Result<string?>.Ok(inline);

        if (Text(owner, name + "File") is not { } file) return Result<string?>.Ok(null);

        try { return Result<string?>.Ok(File.ReadAllText(file)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<string?>.Fail(Outcome.NotFound, $"cannot read \"{name}File\": {e.Message}", file);
        }
    }

    internal static string? Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static int? Number(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    internal static bool Flag(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}

public sealed record BatchStep(int Index, string Op, Outcome Outcome, string Detail);

public sealed record BatchAnswer(IReadOnlyList<BatchStep> Steps, int Completed, bool StoppedEarly);

/// <summary>
/// The <c>batch</c> of R3.12: several operations in one exchange,
/// because the exchange is what costs.
///
/// <para><b>It is ordered, not transactional, and says so.</b> R5.3's
/// all-or-nothing rule governs a batch of <em>edits</em>, which can be
/// resolved entirely before anything is written; a batch of arbitrary
/// operations cannot, because a <c>mkdir</c> followed by a <c>write</c>
/// has no meaningful undo and pretending otherwise would be the more
/// dangerous promise. What it guarantees instead: the operations run in
/// the order given, the first failure stops the rest, and the result
/// names exactly which ones completed.</para>
///
/// <para><b>And it expresses nothing else.</b> No conditionals, no
/// variables, no operation's output feeding another's input. Each of
/// those is individually reasonable and collectively a scripting
/// language arrived at by accident; R7's declared tasks is where a
/// script belongs.</para>
/// </summary>
public static class BatchScript
{
    public static Task<Result<BatchAnswer>> RunAsync(Bench bench, string script, CancellationToken cancel) =>
        RunAsync(bench, Scripts.Load(script), cancel);

    public static Task<Result<BatchAnswer>> RunInlineAsync(Bench bench, string json, CancellationToken cancel) =>
        RunAsync(bench, Scripts.Parse(json), cancel);

    static async Task<Result<BatchAnswer>> RunAsync(
        Bench bench, Result<JsonDocument> parsed, CancellationToken cancel)
    {
        if (!parsed.IsOk) return parsed.Carry<BatchAnswer>();

        using JsonDocument document = parsed.Value;
        if (!document.RootElement.TryGetProperty("operations", out JsonElement operations)
            || operations.ValueKind != JsonValueKind.Array)
            return Result<BatchAnswer>.Fail(Outcome.Invalid, "the script needs an \"operations\" array");

        var steps = new List<BatchStep>();
        int index = 0, completed = 0;

        foreach (JsonElement operation in operations.EnumerateArray())
        {
            index++;
            string op = EditScript.Text(operation, "op") ?? "";
            string path = EditScript.Text(operation, "path") ?? "";

            Result<string> done = await One(bench, op, path, operation, cancel).ConfigureAwait(false);

            if (!done.IsOk)
            {
                steps.Add(new BatchStep(index, op, done.Failure!.Outcome, done.Failure.Message));
                return Result<BatchAnswer>.Ok(new BatchAnswer(steps, completed, StoppedEarly: true));
            }

            completed++;
            steps.Add(new BatchStep(index, op, Outcome.Ok, done.Value));
        }

        return Result<BatchAnswer>.Ok(new BatchAnswer(steps, completed, StoppedEarly: false));
    }

    static async Task<Result<string>> One(
        Bench bench, string op, string path, JsonElement operation, CancellationToken cancel)
    {
        bool overwrite = EditScript.Flag(operation, "overwrite");

        switch (op)
        {
            case "mkdir":
                return Describe(await bench.MakeDirectoryAsync(path, cancel).ConfigureAwait(false),
                    r => r.Path.Display);

            case "new":
                return Describe(await bench.NewFileAsync(path, cancel).ConfigureAwait(false),
                    r => r.Path.Display);

            case "write":
                return Describe(await bench.WriteAsync(
                        path, EditScript.Text(operation, "text") ?? "", overwrite,
                        EditScript.Text(operation, "encoding"), EditScript.Text(operation, "eol"), cancel)
                        .ConfigureAwait(false),
                    r => $"{r.Path.Display}  {r.Bytes} bytes");

            case "move":
                return Describe(await bench.MoveAsync(
                        path, EditScript.Text(operation, "to") ?? "", overwrite, cancel).ConfigureAwait(false),
                    r => $"{r.From.Display} -> {r.To.Display}  atomic: {(r.Atomic ? "yes" : "no")}");

            case "copy":
                return Describe(await bench.CopyAsync(
                        path, EditScript.Text(operation, "to") ?? "",
                        EditScript.Flag(operation, "recursive"), overwrite,
                        EditScript.Flag(operation, "preserveTimes"), cancel).ConfigureAwait(false),
                    r => $"{r.From.Display} -> {r.To.Display}  {r.Files} file(s)");

            case "delete":
                return Describe(await bench.DeleteAsync(
                        path, EditScript.Flag(operation, "recursive"),
                        EditScript.Flag(operation, "force"), cancel).ConfigureAwait(false),
                    r => $"{r.Path.Display}  {r.Files} file(s)");

            case "exec":
                return Describe(await bench.SetExecutableAsync(
                        path, EditScript.Flag(operation, "executable"), cancel).ConfigureAwait(false),
                    r => $"{r.Path.Display} executable: {(r.Executable ? "yes" : "no")}");

            default:
                return Result<string>.Fail(Outcome.Invalid,
                    $"no operation called '{op}'; batch does mkdir, new, write, move, copy, delete and exec");
        }
    }

    static Result<string> Describe<T>(Result<T> result, Func<T, string> describe) =>
        result.IsOk ? Result<string>.Ok(describe(result.Value)) : result.Carry<string>();
}

static class Scripts
{
    public static Result<JsonDocument> Load(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path), path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<JsonDocument>.Fail(Outcome.NotFound, $"cannot read the script: {e.Message}", path);
        }
    }

    public static Result<JsonDocument> Parse(string text, string? where = null)
    {
        try
        {
            return Result<JsonDocument>.Ok(JsonDocument.Parse(text,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }));
        }
        catch (JsonException e)
        {
            return Result<JsonDocument>.Fail(Outcome.Invalid, $"the script is not JSON: {e.Message}", where);
        }
    }
}
