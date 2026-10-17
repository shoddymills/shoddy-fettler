using System.Buffers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Picker.Core;

namespace Picker.Cli;

/// <summary>What a command produced, without a console anywhere near it
/// so the tests can drive every verb with no process.</summary>
public sealed record CliResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>Whether the operation did not do what was asked. It is
    /// what makes an MCP answer an error, and it is held to the error
    /// ceiling.</summary>
    public bool Failed { get; init; }
}

/// <summary>
/// The command-line front end: the same operations the MCP server
/// offers, because both reach them through this one dispatcher.
///
/// <para><b>In machine-readable mode the complete result - failures
/// included - is written to stdout</b>, and stderr carries human
/// diagnostics only: Windows PowerShell 5.1 turns a native program's
/// redirected stderr into NativeCommandError records, so any design
/// needing <c>2&gt;&amp;1</c> to retrieve an answer is broken on the
/// primary development platform. Fettler's R3.7, kept whole.</para>
/// </summary>
public static class Command
{
    /// <summary>
    /// The writer options every answer goes through.
    ///
    /// <para><b>The relaxed encoder is a size decision</b>, Fettler's,
    /// for the same reason. The default escapes every character that
    /// could matter inside an HTML page, and everything non-ASCII, to a
    /// six-byte sequence, so a column of names with accents or markup
    /// costs up to six times its length. Nothing here is spliced into a
    /// page; it goes down a pipe as JSON, and what JSON itself requires
    /// is escaped either way.</para>
    /// </summary>
    public static readonly JsonWriterOptions Writing = new()
    {
        Indented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Version { get; } =
        typeof(Command).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public static async Task<CliResult> RunAsync(
        IReadOnlyList<string> argv, TextReader stdin, CancellationToken cancel = default)
    {
        Arguments args = Arguments.Parse(argv);
        bool json = args.Has("json");

        CliResult? early = Trivial(args);
        if (early is not null) return early;

        Result<Boundary> boundary = args.Value("config") is { } path
            ? ConfigFile.Open(path)
            : ConfigFile.Find(Directory.GetCurrentDirectory());

        if (!boundary.IsOk) return Failed(boundary.Failure!, json);

        using var bench = new Bench(boundary.Value);
        return await Run(bench, args, json, stdin, cancel).ConfigureAwait(false);
    }

    /// <summary>The same command against a bench that is already open -
    /// the MCP front end's door, which is what makes parity a property
    /// of the design rather than a promise.</summary>
    public static async Task<CliResult> RunAsync(
        IReadOnlyList<string> argv, TextReader stdin, Bench bench,
        CancellationToken cancel = default)
    {
        Arguments args = Arguments.Parse(argv);
        bool json = args.Has("json");

        CliResult? early = Trivial(args);
        if (early is not null) return early;

        return await Run(bench, args, json, stdin, cancel).ConfigureAwait(false);
    }

    static CliResult? Trivial(Arguments args)
    {
        if (args.Verb is "version" || args.Has("version"))
            return new CliResult(ExitCodes.Ok, "pick " + Version + Environment.NewLine, "");

        if (args.Verb.Length == 0 || args.Verb is "help" || args.Has("help"))
            return new CliResult(ExitCodes.Ok, Help(), "");

        return null;
    }

    static async Task<CliResult> Run(
        Bench bench, Arguments args, bool json, TextReader stdin, CancellationToken cancel)
    {
        IReadOnlyList<string> missing = args.FlagsMissingAValue;
        if (missing.Count > 0)
            return Failed(new Failure(Outcome.Invalid,
                $"{string.Join(", ", missing)} needs a value"), json);

        try
        {
            return Held(args.Verb switch
            {
                "catalogs" => Catalogs(bench, args, json),
                "objects" => await Objects(bench, args, json, cancel).ConfigureAwait(false),
                "select" => await Select(bench, args, json, stdin, cancel).ConfigureAwait(false),
                "describe" => await Describe(bench, args, json, cancel).ConfigureAwait(false),
                "doctor" => Doctor(bench, args, json),
                _ => Failed(new Failure(Outcome.Invalid,
                    $"no verb called '{args.Verb}'. Run pick help for the list"), json),
            }, json);
        }
        catch (OperationCanceledException)
        {
            return Failed(new Failure(Outcome.Refused, "the command was cancelled"), json);
        }
    }

    // ---- catalogs ----

    static CliResult Catalogs(Bench bench, Arguments args, bool json)
    {
        IReadOnlyList<string> unknown = args.UnknownFlags();
        if (unknown.Count > 0) return Unknown(unknown, json);

        CatalogsAnswer answer = bench.Catalogs();

        if (json)
            return Ok(Json(w =>
            {
                WriteLevels(w, answer.Levels);

                w.WriteStartArray("servers");
                foreach (ServerReport server in answer.Servers)
                {
                    w.WriteStartObject();
                    w.WriteString("name", server.Name);
                    w.WriteString("connect", server.Connect);
                    w.WriteBoolean("open", answer.Open.Contains(server.Name));
                    w.WriteStartArray("databases");
                    foreach (DatabaseReport database in server.Databases)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", database.Name);
                        w.WriteString("can", database.Can);
                        w.WriteString("screen", database.Screen);
                        if (database.Scopes.Count > 0)
                        {
                            w.WriteStartArray("scopes");
                            foreach (ScopeReport scope in database.Scopes)
                            {
                                w.WriteStartObject();
                                w.WriteString("object", scope.Pattern);
                                w.WriteString("can", scope.Can);
                                if (scope.Columns.Count > 0)
                                {
                                    w.WriteStartArray("columns");
                                    foreach (string word in scope.Columns) w.WriteStringValue(word);
                                    w.WriteEndArray();
                                }
                                if (scope.Screen is { } s) w.WriteString("screen", s);
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (answer.Models is { } models) w.WriteString("models", models);
                w.WriteString("source", answer.Origin);
            }));

        var text = new StringBuilder();
        AppendLevels(text, answer.Levels);
        text.AppendLine();

        foreach (ServerReport server in answer.Servers)
        {
            text.AppendLine($"{server.Name}  (connect: {server.Connect})");
            if (answer.Open.Contains(server.Name))
                text.AppendLine("  every database this login can reach; guardrails are off, so none is declared");
            foreach (DatabaseReport database in server.Databases)
            {
                text.AppendLine($"  {database.Name}: {database.Can}"
                    + (database.Screen == "nothing" ? "" : $"  [screen: {database.Screen}]"));
                foreach (ScopeReport scope in database.Scopes)
                    text.AppendLine($"    {scope.Pattern}: {scope.Can}"
                        + (scope.Columns.Count > 0
                            ? $"  columns: {string.Join(" ", scope.Columns)}" : "")
                        + (scope.Screen is { } s ? $"  [screen: {s}]" : ""));
            }
        }
        if (answer.Models is { } dir) text.AppendLine($"models: {dir}");
        text.AppendLine($"source: {answer.Origin}");
        return Ok(text.ToString());
    }

    // ---- objects ----

    static async Task<CliResult> Objects(
        Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        IReadOnlyList<string> unknown = args.UnknownFlags("after");
        if (unknown.Count > 0) return Unknown(unknown, json);

        if (Position(args) is not { } after)
            return Failed(new Failure(Outcome.Invalid,
                "after needs the next the last answer gave: a whole number"), json);

        var worst = new ObjectsAnswer([], true, int.MaxValue);
        int room = Budget.Ceiling - Envelope(w => WriteObjects(w, worst), HumanObjects(worst));

        Result<ObjectsAnswer> found = await bench.ObjectsAsync(args.At(0), cancel, after,
                o => Math.Max(Size.Json(w => WriteObject(w, o)) + 1, Size.Text(HumanObject(o))),
                room, NextCost)
            .ConfigureAwait(false);
        if (!found.IsOk) return Failed(found.Failure!, json);

        return Ok(json ? Json(w => WriteObjects(w, found.Value)) : HumanObjects(found.Value));
    }

    static void WriteObjects(Utf8JsonWriter w, ObjectsAnswer answer)
    {
        w.WriteStartArray("objects");
        foreach (ListedObject o in answer.Objects) WriteObject(w, o);
        w.WriteEndArray();
        w.WriteNumber("count", answer.Objects.Count);
        w.WriteBoolean("truncated", answer.Truncated);
        if (answer.Next is { } next) w.WriteString("next", next.ToString());
    }

    static void WriteObject(Utf8JsonWriter w, ListedObject o)
    {
        w.WriteStartObject();
        w.WriteString("database", o.Database);
        w.WriteString("schema", o.Schema);
        w.WriteString("name", o.Name);
        w.WriteString("type", o.IsView ? "view" : "table");
        w.WriteNumber("columns", o.Columns);
        w.WriteString("can", Verbs.Write(o.Can));
        w.WriteEndObject();
    }

    static string HumanObject(ListedObject o) =>
        $"{o.Database}.{o.Schema}.{o.Name}  {(o.IsView ? "view" : "table")}  {o.Columns} columns  "
        + $"{Verbs.Write(o.Can)}{Environment.NewLine}";

    static string HumanObjects(ObjectsAnswer answer)
    {
        var text = new StringBuilder();
        foreach (ListedObject o in answer.Objects) text.Append(HumanObject(o));
        text.AppendLine($"{answer.Objects.Count} objects" + (answer.Truncated ? " (truncated)" : ""));
        if (answer.Next is { } next) text.Append(MoreSentence(next.ToString()));
        return text.ToString();
    }

    // ---- select ----

    static async Task<CliResult> Select(
        Bench bench, Arguments args, bool json, TextReader stdin, CancellationToken cancel)
    {
        IReadOnlyList<string> unknown = args.UnknownFlags("limit", "stdin");
        if (unknown.Count > 0) return Unknown(unknown, json);

        string? sql = args.Has("stdin")
            ? await stdin.ReadToEndAsync(cancel).ConfigureAwait(false)
            : args.At(0);

        if (string.IsNullOrWhiteSpace(sql))
            return Failed(new Failure(Outcome.Invalid,
                "select needs the SQL: pick select \"SELECT ...\", or --stdin"), json);

        int? limit = args.Has("limit") ? args.Int("limit", SqlData.DefaultRowCap) : null;

        Result<SelectAnswer> ran = await bench.SelectAsync(sql, limit, cancel, new SelectMeter())
            .ConfigureAwait(false);
        if (!ran.IsOk) return Failed(ran.Failure!, json);

        SelectAnswer answer = ran.Value;
        return Ok(json ? Json(w => WriteSelect(w, answer)) : HumanSelect(answer));
    }

    /// <summary>A SELECT answer measured in both output modes, the larger
    /// of the two, so one cut serves both.</summary>
    sealed class SelectMeter : ISelectMeter
    {
        public int Envelope(IReadOnlyList<ColumnFact> columns, string ran, IReadOnlyList<string> objects)
        {
            // With no rows the count is one digit; the room for the real
            // count is added beside it.
            var empty = new SelectAnswer(new Table(columns, [], true), ran, objects);
            return Command.Envelope(w => WriteSelect(w, empty), HumanSelect(empty))
                   + Size.Text(int.MaxValue.ToString());
        }

        public int Row(IReadOnlyList<ColumnFact> columns, IReadOnlyList<Cell> row) => Math.Max(
            Size.Json(w => WriteRow(w, columns, row)) + 1,
            Size.Text(HumanRow(row)));
    }

    static void WriteSelect(Utf8JsonWriter w, SelectAnswer answer)
    {
        w.WriteStartArray("columns");
        foreach (ColumnFact c in answer.Table.Columns)
        {
            w.WriteStartObject();
            w.WriteString("name", c.Name);
            w.WriteString("type", c.Type);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("rows");
        foreach (IReadOnlyList<Cell> row in answer.Table.Rows) WriteRow(w, answer.Table.Columns, row);
        w.WriteEndArray();

        w.WriteNumber("count", answer.Table.Rows.Count);
        w.WriteBoolean("truncated", answer.Table.Truncated);
        w.WriteStartArray("objects");
        foreach (string o in answer.Objects) w.WriteStringValue(o);
        w.WriteEndArray();
        w.WriteString("ran", answer.Ran);
    }

    static void WriteRow(Utf8JsonWriter w, IReadOnlyList<ColumnFact> columns, IReadOnlyList<Cell> row)
    {
        w.WriteStartObject();
        for (int i = 0; i < columns.Count; i++) WriteCell(w, columns[i].Name, row[i]);
        w.WriteEndObject();
    }

    static string HumanRow(IReadOnlyList<Cell> row) =>
        string.Join("  ", row.Select(c => c.Text ?? "NULL")) + Environment.NewLine;

    static string HumanSelect(SelectAnswer answer)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Join("  ", answer.Table.Columns.Select(c => c.Name)));
        foreach (IReadOnlyList<Cell> row in answer.Table.Rows) text.Append(HumanRow(row));
        text.AppendLine($"{answer.Table.Rows.Count} rows"
            + (answer.Table.Truncated
                ? " (truncated: narrow with TOP, WHERE or OFFSET, or raise --limit)" : ""));
        return text.ToString();
    }

    static void WriteCell(Utf8JsonWriter w, string name, Cell cell)
    {
        switch (cell.Kind)
        {
            case CellKind.Null:
                w.WriteNull(name);
                break;
            case CellKind.Boolean:
                w.WriteBoolean(name, cell.Text == "true");
                break;
            case CellKind.Integer when long.TryParse(cell.Text, out long i):
                w.WriteNumber(name, i);
                break;
            case CellKind.Decimal when decimal.TryParse(
                cell.Text, System.Globalization.CultureInfo.InvariantCulture, out decimal d):
                w.WriteNumber(name, d);
                break;
            case CellKind.Float when double.TryParse(
                cell.Text, System.Globalization.CultureInfo.InvariantCulture, out double f):
                w.WriteNumber(name, f);
                break;
            default:
                w.WriteString(name, cell.Text ?? "");
                break;
        }
    }

    // ---- describe ----

    /// <summary>
    /// DESCRIBE, in pages. A table can have a thousand columns, which is
    /// more than one answer holds, so the columns, then the indexes, then
    /// the foreign keys are one sequence, and a page that stops short
    /// gives the position to carry on from. Every page carries the hash of
    /// the whole shape, so a caller can tell the object did not change
    /// between pages.
    /// </summary>
    static async Task<CliResult> Describe(
        Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        IReadOnlyList<string> unknown = args.UnknownFlags("after");
        if (unknown.Count > 0) return Unknown(unknown, json);

        string? name = args.At(0);
        if (name is null)
            return Failed(new Failure(Outcome.Invalid,
                "describe needs a name: pick describe Sales.dbo.Customers"), json);

        if (Position(args) is not { } after)
            return Failed(new Failure(Outcome.Invalid,
                "after needs the next the last answer gave: a whole number"), json);

        Result<Described> made = await bench.DescribeAsync(name, cancel).ConfigureAwait(false);
        if (!made.IsOk) return Failed(made.Failure!, json);

        Described whole = made.Value;

        // One sequence: columns, then indexes, then keys.
        var parts = new List<object>();
        parts.AddRange(whole.Columns);
        parts.AddRange(whole.Indexes);
        parts.AddRange(whole.ForeignKeys);
        IReadOnlyList<object> rest = [.. parts.Skip(after)];

        Described empty = whole with { Columns = [], Indexes = [], ForeignKeys = [] };
        int room = Budget.Ceiling - Envelope(w => WriteDescribed(w, empty, null), HumanDescribed(empty, null));

        int fit = Budget.Fit(rest, room,
            p => Math.Max(Size.Json(w => WritePart(w, p)) + 1, Size.Text(HumanPart(p))),
            _ => NextCost(parts.Count));
        fit = Math.Max(fit, Math.Min(1, rest.Count));

        IReadOnlyList<object> page = [.. rest.Take(fit)];
        int? next = after + fit < parts.Count ? after + fit : null;

        Described shown = whole with
        {
            Columns = [.. page.OfType<ColumnFact>()],
            Indexes = [.. page.OfType<IndexFact>()],
            ForeignKeys = [.. page.OfType<ForeignKeyFact>()],
        };

        return Ok(json ? Json(w => WriteDescribed(w, shown, next)) : HumanDescribed(shown, next));
    }

    static void WriteDescribed(Utf8JsonWriter w, Described answer, int? next)
    {
        w.WriteString("object", answer.Display);
        w.WriteString("type", answer.IsView ? "view" : "table");
        w.WriteStartArray("columns");
        foreach (ColumnFact c in answer.Columns) WritePart(w, c);
        w.WriteEndArray();
        w.WriteStartArray("indexes");
        foreach (IndexFact i in answer.Indexes) WritePart(w, i);
        w.WriteEndArray();
        w.WriteStartArray("foreign_keys");
        foreach (ForeignKeyFact k in answer.ForeignKeys) WritePart(w, k);
        w.WriteEndArray();
        w.WriteString("hash", answer.Hash);
        if (next is { } n) w.WriteString("next", n.ToString());
    }

    static void WritePart(Utf8JsonWriter w, object part)
    {
        w.WriteStartObject();
        switch (part)
        {
            case ColumnFact c:
                w.WriteString("name", c.Name);
                w.WriteString("type", c.Type);
                w.WriteBoolean("nullable", c.Nullable);
                break;

            case IndexFact i:
                w.WriteString("name", i.Name);
                w.WriteBoolean("unique", i.Unique);
                w.WriteBoolean("primary_key", i.PrimaryKey);
                w.WriteStartArray("columns");
                foreach (string c in i.Columns) w.WriteStringValue(c);
                w.WriteEndArray();
                break;

            case ForeignKeyFact k:
                w.WriteString("name", k.Name);
                w.WriteStartArray("columns");
                foreach (string c in k.Columns) w.WriteStringValue(c);
                w.WriteEndArray();
                if (k.Target is { } target)
                {
                    w.WriteString("references", target);
                    w.WriteStartArray("references_columns");
                    foreach (string c in k.TargetColumns) w.WriteStringValue(c);
                    w.WriteEndArray();
                }
                else
                {
                    w.WriteNull("references");
                }
                break;
        }
        w.WriteEndObject();
    }

    static string HumanPart(object part) => part switch
    {
        ColumnFact c => $"  {c.Name}  {c.Type}{(c.Nullable ? " null" : " not null")}{Environment.NewLine}",
        IndexFact i => $"  index {i.Name}"
            + (i.PrimaryKey ? " (primary key)" : i.Unique ? " (unique)" : "")
            + $": {string.Join(", ", i.Columns)}{Environment.NewLine}",
        ForeignKeyFact k => $"  fk {k.Name}: {string.Join(", ", k.Columns)} -> "
            + (k.Target is { } target
                ? $"{target} ({string.Join(", ", k.TargetColumns)})"
                : "(not shown)") + Environment.NewLine,
        _ => string.Empty,
    };

    static string HumanDescribed(Described answer, int? next)
    {
        var text = new StringBuilder();
        text.AppendLine($"{answer.Display}  ({(answer.IsView ? "view" : "table")})");
        foreach (ColumnFact c in answer.Columns) text.Append(HumanPart(c));
        foreach (IndexFact i in answer.Indexes) text.Append(HumanPart(i));
        foreach (ForeignKeyFact k in answer.ForeignKeys) text.Append(HumanPart(k));
        text.AppendLine($"  hash: {answer.Hash}");
        if (next is { } n) text.Append(MoreSentence(n.ToString()));
        return text.ToString();
    }

    // ---- doctor ----

    static CliResult Doctor(Bench bench, Arguments args, bool json)
    {
        IReadOnlyList<string> unknown = args.UnknownFlags();
        if (unknown.Count > 0) return Unknown(unknown, json);

        // R7.2 for pick: the levels first, then the checks. A level that
        // is off is one line, and its checks are not run.
        Levels levels = bench.Boundary.Levels;
        var lines = new List<(string Subject, bool Healthy, string Detail)>();
        foreach (string level in Levels.Names)
            lines.Add((level, true, levels.Describe(level)));
        lines.Add(("configuration", true, bench.Boundary.Origin));

        foreach (ServerDecl server in bench.Boundary.Servers)
        {
            Result<string> connect = server.Connect.Resolve();
            if (!connect.IsOk)
            {
                lines.Add(($"server {server.Name}", false, connect.Failure!.Message));
                continue;
            }

            // A bounded knock on the door: does a connection open? The
            // connection string never appears, whatever happens.
            try
            {
                var builder = new SqlConnectionStringBuilder(connect.Value);
                builder.ConnectTimeout = Math.Min(
                    builder.ConnectTimeout is > 0 and < 5 ? builder.ConnectTimeout : 5, 5);
                using var connection = new SqlConnection(builder.ConnectionString);
                connection.Open();
                lines.Add(($"server {server.Name}", true, server.Open
                    ? "connects (guardrails off: every database the login can reach)"
                    : $"connects ({server.Databases.Count} database(s) declared)"));
            }
            catch (Exception e) when (e is SqlException or InvalidOperationException or ArgumentException)
            {
                string message = e.Message;
                int cut = message.IndexOf('\n');
                if (cut > 0) message = message[..cut].TrimEnd();
                lines.Add(($"server {server.Name}", false, message));
            }
        }

        if (levels.ScreeningsOn)
        {
            if (bench.Boundary.Models is { } models)
                lines.Add(("models", Directory.Exists(models),
                    Directory.Exists(models) ? models : $"{models} does not exist"));

            string burler = Sidecar.Executable();
            lines.Add(("burler", File.Exists(burler),
                File.Exists(burler) ? burler : $"not installed (expected at {burler}). "
                    + "Only model-backed screening needs it"));
        }

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("version", Version);
                w.WriteStartArray("checks");
                foreach ((string subject, bool healthy, string detail) in lines)
                {
                    w.WriteStartObject();
                    w.WriteString("subject", subject);
                    w.WriteBoolean("healthy", healthy);
                    w.WriteString("detail", detail);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }));

        var text = new StringBuilder();
        text.AppendLine($"pick {Version}");
        foreach ((string subject, bool healthy, string detail) in lines)
            text.AppendLine($"  {(healthy ? "ok " : "!! ")}{subject}: {detail}");
        return Ok(text.ToString());
    }

    // ---- the ceiling ----

    /// <summary>
    /// The last check every answer passes on its way out, as Fettler's:
    /// an answer over its ceiling is never sent. Every verb cuts to fit
    /// before it writes, so reaching the replacement is a defect, and it
    /// says so with the size. A refusal over its ceiling keeps its outcome
    /// and subject, and its message is shortened with the cut said.
    /// </summary>
    public static CliResult Held(CliResult answer, bool json)
    {
        int ceiling = answer.Failed ? Budget.ErrorCeiling : Budget.Ceiling;
        int size = Size.Text(answer.Stdout + (json ? "" : answer.Stderr));
        if (size <= ceiling) return answer;

        if (answer.Failed && json && Refusal(answer.Stdout) is { } refusal)
        {
            string said = $" [{size:N0} bytes; the rest was left out to stay under the "
                          + $"{Budget.ErrorCeiling:N0}-byte limit for a refusal]";
            string Write(string message) => Json(w =>
            {
                w.WriteString("outcome", refusal.Outcome);
                w.WriteString("message", message);
                if (refusal.Subject is { } subject) w.WriteString("subject", subject);
            }, ok: false) + Environment.NewLine;

            int keep = Budget.Prefix(refusal.Message, Budget.ErrorCeiling, m => Size.Text(Write(m + said)));
            return answer with { Stdout = Write(refusal.Message[..keep] + said) };
        }

        if (answer.Failed && !json)
            return answer with
            {
                Stderr = answer.Stderr[..Budget.Prefix(answer.Stderr, Budget.ErrorCeiling - 2, Size.Text)]
                         + Environment.NewLine,
            };

        return Failed(new Failure(Outcome.Refused,
            $"the command ran, and its answer was {size:N0} bytes, over the {ceiling:N0}-byte ceiling, "
            + "so the answer was not sent. This is a pick defect: ask for less in one call (TOP, "
            + "WHERE, fewer columns, a narrower pattern), and report it"), json);
    }

    static (string Outcome, string Message, string? Subject)? Refusal(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("outcome", out JsonElement outcome)) return null;

            string message = root.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()! : "";
            string? subject = root.TryGetProperty("subject", out JsonElement s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : null;

            return (outcome.GetString() ?? "refused", message, subject);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The bytes an answer's envelope takes in whichever mode
    /// is larger, so the room left is the room both modes have.</summary>
    static int Envelope(Action<Utf8JsonWriter> json, string human) =>
        Math.Max(Size.Text(Json(json)) + Size.NewLine, Size.Text(human));

    /// <summary>What a <c>next</c> adds to an answer, in either mode.</summary>
    static int NextCost(int position) => Math.Max(
        Size.Text($",\"next\":\"{position}\""), Size.Text(MoreSentence(position.ToString())));

    static string MoreSentence(string position) =>
        $"... more; pass --after {position} to carry on{Environment.NewLine}";

    /// <summary>The <c>after</c> a caller passed, 0 when none, or null
    /// when it is not a whole number.</summary>
    static int? Position(Arguments args) =>
        args.Value("after") is not { } given ? 0
        : int.TryParse(given, System.Globalization.NumberStyles.None,
              System.Globalization.CultureInfo.InvariantCulture, out int n) ? n
        : null;

    // ---- rendering ----

    static void WriteLevels(Utf8JsonWriter w, Levels levels)
    {
        w.WriteStartObject("levels");
        foreach (string level in Levels.Names)
        {
            w.WriteStartObject(level);
            w.WriteBoolean("on", levels.IsOn(level));
            w.WriteStartArray("from");
            foreach (string file in levels.Sources(level)) w.WriteStringValue(file);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    static void AppendLevels(StringBuilder text, Levels levels)
    {
        text.AppendLine("levels");
        foreach (string level in Levels.Names)
            text.Append("  ").Append(level.PadRight(12)).AppendLine(levels.Describe(level));
    }

    static string Help() => """
        pick - SELECT and DESCRIBE in SQL Server databases. Three levels: tools (always
        on: a connection and the query gate), guardrails (declared databases, verbs,
        scopes, columns), screenings (the disclosure screen).

        Verbs
          catalogs                     the levels, then the servers and databases, and what each allows
          objects [PATTERN]            list tables and views the grant allows; PATTERN is
                                       schema.object or a bare object name, * inside a segment
          select "SQL" [--limit N]     run one SELECT (--stdin reads the SQL from stdin)
          describe NAME                list columns, keys and indexes; never a view's definition
          doctor                       check the configuration, the servers and the screening pieces
          version                      print the version

        Global flags
          --config PATH                use this .picker.json; otherwise the nearest one applies
          --json                       machine-readable answers on stdout, failures included

        .picker.json and .picker.local.json say what pick may do. pick does not
        write them; a person edits them.

        """ + Environment.NewLine;

    static CliResult Unknown(IReadOnlyList<string> unknown, bool json) =>
        Failed(new Failure(Outcome.Invalid,
            $"no such flag: {string.Join(", ", unknown)}. Run pick help for the list"), json);

    static CliResult Failed(Failure failure, bool json)
    {
        if (json)
        {
            string body = Json(w =>
            {
                w.WriteString("outcome", ExitCodes.NameOf(failure.Outcome));
                w.WriteString("message", failure.Message);
                if (failure.Subject is { } subject) w.WriteString("subject", subject);
            }, ok: false);
            return new CliResult(ExitCodes.Of(failure.Outcome), body + Environment.NewLine, "") { Failed = true };
        }

        return new CliResult(ExitCodes.Of(failure.Outcome), "",
            $"pick: {failure}" + Environment.NewLine) { Failed = true };
    }

    /// <summary>One machine-readable envelope: <c>ok</c> first, the body
    /// after, so a caller reads the verdict before the detail.</summary>
    static string Json(Action<Utf8JsonWriter> body, bool ok = true)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Writing))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", ok);
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static CliResult Ok(string stdout, bool newline = true) =>
        new(ExitCodes.Ok, newline && !stdout.EndsWith('\n')
            ? stdout + Environment.NewLine : stdout, "");
}

/// <summary>
/// The size of something once written, measured by writing it with the
/// same writer and options as the answer, so it agrees with the encoder
/// by construction rather than by a rule kept in step by hand.
/// </summary>
static class Size
{
    [ThreadStatic] static ArrayBufferWriter<byte>? scratch;

    /// <summary>The bytes <paramref name="write"/> produces. Not
    /// reentrant: a writer passed here must not measure anything
    /// itself.</summary>
    public static int Json(Action<Utf8JsonWriter> write)
    {
        ArrayBufferWriter<byte> buffer = scratch ??= new ArrayBufferWriter<byte>(4096);
        buffer.ResetWrittenCount();
        using (var w = new Utf8JsonWriter(buffer, Command.Writing)) write(w);
        return buffer.WrittenCount;
    }

    public static int Text(string text) => Encoding.UTF8.GetByteCount(text);

    public static readonly int NewLine = Environment.NewLine.Length;
}
