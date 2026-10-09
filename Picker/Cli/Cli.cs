using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Picker.Core;

namespace Picker.Cli;

/// <summary>What a command produced, without a console anywhere near it
/// so the tests can drive every verb with no process.</summary>
public sealed record CliResult(int ExitCode, string Stdout, string Stderr);

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
    public static readonly JsonWriterOptions Writing = new() { Indented = false };

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
            return args.Verb switch
            {
                "catalogs" => Catalogs(bench, args, json),
                "objects" => await Objects(bench, args, json, cancel).ConfigureAwait(false),
                "select" => await Select(bench, args, json, stdin, cancel).ConfigureAwait(false),
                "describe" => await Describe(bench, args, json, cancel).ConfigureAwait(false),
                "doctor" => Doctor(bench, args, json),
                _ => Failed(new Failure(Outcome.Invalid,
                    $"no verb called '{args.Verb}'. Run pick help for the list"), json),
            };
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
                w.WriteStartArray("servers");
                foreach (ServerReport server in answer.Servers)
                {
                    w.WriteStartObject();
                    w.WriteString("name", server.Name);
                    w.WriteString("connect", server.Connect);
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
        foreach (ServerReport server in answer.Servers)
        {
            text.AppendLine($"{server.Name}  (connect: {server.Connect})");
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
        IReadOnlyList<string> unknown = args.UnknownFlags();
        if (unknown.Count > 0) return Unknown(unknown, json);

        Result<ObjectsAnswer> found =
            await bench.ObjectsAsync(args.At(0), cancel).ConfigureAwait(false);
        if (!found.IsOk) return Failed(found.Failure!, json);

        ObjectsAnswer answer = found.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteStartArray("objects");
                foreach (ListedObject o in answer.Objects)
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
                w.WriteEndArray();
                w.WriteNumber("count", answer.Objects.Count);
                w.WriteBoolean("truncated", answer.Truncated);
            }));

        var text = new StringBuilder();
        foreach (ListedObject o in answer.Objects)
            text.AppendLine($"{o.Database}.{o.Schema}.{o.Name}  "
                + $"{(o.IsView ? "view" : "table")}  {o.Columns} columns  {Verbs.Write(o.Can)}");
        text.AppendLine($"{answer.Objects.Count} objects"
            + (answer.Truncated ? " (truncated)" : ""));
        return Ok(text.ToString());
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

        Result<SelectAnswer> ran =
            await bench.SelectAsync(sql, limit, cancel).ConfigureAwait(false);
        if (!ran.IsOk) return Failed(ran.Failure!, json);

        SelectAnswer answer = ran.Value;

        if (json)
            return Ok(Json(w =>
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
                foreach (IReadOnlyList<Cell> row in answer.Table.Rows)
                {
                    w.WriteStartObject();
                    for (int i = 0; i < answer.Table.Columns.Count; i++)
                        WriteCell(w, answer.Table.Columns[i].Name, row[i]);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteNumber("count", answer.Table.Rows.Count);
                w.WriteBoolean("truncated", answer.Table.Truncated);
                w.WriteStartArray("objects");
                foreach (string o in answer.Objects) w.WriteStringValue(o);
                w.WriteEndArray();
                w.WriteString("ran", answer.Ran);
            }));

        var text = new StringBuilder();
        text.AppendLine(string.Join("  ", answer.Table.Columns.Select(c => c.Name)));
        foreach (IReadOnlyList<Cell> row in answer.Table.Rows)
            text.AppendLine(string.Join("  ", row.Select(c => c.Text ?? "NULL")));
        text.AppendLine($"{answer.Table.Rows.Count} rows"
            + (answer.Table.Truncated
                ? " (truncated: narrow with TOP, WHERE or OFFSET, or raise --limit)" : ""));
        return Ok(text.ToString());
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

    static async Task<CliResult> Describe(
        Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        IReadOnlyList<string> unknown = args.UnknownFlags();
        if (unknown.Count > 0) return Unknown(unknown, json);

        string? name = args.At(0);
        if (name is null)
            return Failed(new Failure(Outcome.Invalid,
                "describe needs a name: pick describe Sales.dbo.Customers"), json);

        Result<Described> made = await bench.DescribeAsync(name, cancel).ConfigureAwait(false);
        if (!made.IsOk) return Failed(made.Failure!, json);

        Described answer = made.Value;

        if (json)
            return Ok(Json(w =>
            {
                w.WriteString("object", answer.Display);
                w.WriteString("type", answer.IsView ? "view" : "table");
                w.WriteStartArray("columns");
                foreach (ColumnFact c in answer.Columns)
                {
                    w.WriteStartObject();
                    w.WriteString("name", c.Name);
                    w.WriteString("type", c.Type);
                    w.WriteBoolean("nullable", c.Nullable);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("indexes");
                foreach (IndexFact i in answer.Indexes)
                {
                    w.WriteStartObject();
                    w.WriteString("name", i.Name);
                    w.WriteBoolean("unique", i.Unique);
                    w.WriteBoolean("primary_key", i.PrimaryKey);
                    w.WriteStartArray("columns");
                    foreach (string c in i.Columns) w.WriteStringValue(c);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("foreign_keys");
                foreach (ForeignKeyFact k in answer.ForeignKeys)
                {
                    w.WriteStartObject();
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
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteString("hash", answer.Hash);
            }));

        var text = new StringBuilder();
        text.AppendLine($"{answer.Display}  ({(answer.IsView ? "view" : "table")})");
        foreach (ColumnFact c in answer.Columns)
            text.AppendLine($"  {c.Name}  {c.Type}{(c.Nullable ? " null" : " not null")}");
        foreach (IndexFact i in answer.Indexes)
            text.AppendLine($"  index {i.Name}"
                + (i.PrimaryKey ? " (primary key)" : i.Unique ? " (unique)" : "")
                + $": {string.Join(", ", i.Columns)}");
        foreach (ForeignKeyFact k in answer.ForeignKeys)
            text.AppendLine($"  fk {k.Name}: {string.Join(", ", k.Columns)} -> "
                + (k.Target is { } target
                    ? $"{target} ({string.Join(", ", k.TargetColumns)})"
                    : "(not shown)"));
        text.AppendLine($"  hash: {answer.Hash}");
        return Ok(text.ToString());
    }

    // ---- doctor ----

    static CliResult Doctor(Bench bench, Arguments args, bool json)
    {
        IReadOnlyList<string> unknown = args.UnknownFlags();
        if (unknown.Count > 0) return Unknown(unknown, json);

        var lines = new List<(string Subject, bool Healthy, string Detail)>
        {
            ("configuration", true, bench.Boundary.Origin),
        };

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
                lines.Add(($"server {server.Name}", true,
                    $"connects ({server.Databases.Count} database(s) declared)"));
            }
            catch (Exception e) when (e is SqlException or InvalidOperationException or ArgumentException)
            {
                string message = e.Message;
                int cut = message.IndexOf('\n');
                if (cut > 0) message = message[..cut].TrimEnd();
                lines.Add(($"server {server.Name}", false, message));
            }
        }

        if (bench.Boundary.Models is { } models)
            lines.Add(("models", Directory.Exists(models),
                Directory.Exists(models) ? models : $"{models} does not exist"));

        string burler = Sidecar.Executable();
        lines.Add(("burler", File.Exists(burler),
            File.Exists(burler) ? burler : $"not installed (expected at {burler}). "
                + "Only model-backed screening needs it"));

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

    // ---- rendering ----

    static string Help() => """
        pick - SELECT and DESCRIBE inside declared SQL Server databases

        Verbs
          catalogs                     list the declared servers and databases, and what each allows
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
            return new CliResult(ExitCodes.Of(failure.Outcome), body + Environment.NewLine, "");
        }

        return new CliResult(ExitCodes.Of(failure.Outcome), "",
            $"pick: {failure}" + Environment.NewLine);
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
