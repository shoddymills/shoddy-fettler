using System.Text;
using System.Text.Json;

namespace Picker.Core;

/// <summary>Runs one approved SELECT. Split from the catalog interface
/// so the gate and the grant matrix are provable with a catalog made of
/// literals, while the select path stays honest about needing a
/// server.</summary>
public interface IQuerySource
{
    Task<Result<Table>> QueryAsync(
        ServerDecl server, string sql, int rowCap, CancellationToken cancel);
}

/// <summary>Answers DESCRIBE's index and key questions. Separate for
/// the same reason: a fake catalog can describe columns without a
/// server, and simply has no constraints to report.</summary>
public interface IConstraintSource
{
    Task<Result<(IReadOnlyList<IndexFact> Indexes, IReadOnlyList<ForeignKeyFact> ForeignKeys)>>
        ConstraintsAsync(ResolvedObject on, CancellationToken cancel);
}

/// <summary>What one server declares, rendered for a report.</summary>
public sealed record ServerReport(
    string Name, string Connect, IReadOnlyList<DatabaseReport> Databases);

public sealed record DatabaseReport(
    string Name, string Can, string Screen, IReadOnlyList<ScopeReport> Scopes);

public sealed record ScopeReport(
    string Pattern, string Can, IReadOnlyList<string> Columns, string? Screen);

public sealed record CatalogsAnswer(
    IReadOnlyList<ServerReport> Servers, string? Models, string Origin);

public sealed record ObjectsAnswer(IReadOnlyList<ListedObject> Objects, bool Truncated);

public sealed record SelectAnswer(
    Table Table, string Ran, IReadOnlyList<string> Objects);

/// <summary>
/// The engine: one boundary, one resolver, one screener, and the four
/// operations - built once per request in the serve path, so a
/// configuration edit binds on the very next call.
///
/// <para><b>Every answer that discloses anything leaves through
/// <see cref="Disclosure.Check"/></b> - rows, described DDL, object
/// listings - so a verb added later has to come through the same seam
/// to disclose at all.</para>
/// </summary>
public sealed class Bench : IDisposable
{
    /// <summary>The most rows a listing returns before saying so.</summary>
    public const int ListingCap = 500;

    readonly Boundary boundary;
    readonly ICatalogSource source;
    readonly IScreener? screener;
    readonly bool ownsScreener;
    readonly Resolver resolver;

    /// <param name="source">Null means real SQL Server access with the
    /// defaults; the tests hand in fakes.</param>
    /// <param name="screener">Null means the real sidecar when the
    /// boundary names a models directory, and tier one alone when it
    /// does not - the same rule Fettler applies.</param>
    public Bench(Boundary boundary, ICatalogSource? source = null, IScreener? screener = null)
    {
        this.boundary = boundary;
        this.source = source ?? new SqlData();

        if (screener is not null)
        {
            this.screener = screener;
        }
        else
        {
            this.screener = Sidecar.For(boundary.Models);
            ownsScreener = this.screener is not null;
        }

        resolver = new Resolver(boundary, this.source);
    }

    public Boundary Boundary => boundary;

    public void Dispose()
    {
        if (ownsScreener && screener is IDisposable owned) owned.Dispose();
    }

    // ---- catalogs ----

    /// <summary>The declared surface: every server, database, scope and
    /// screen - the roots answer, translated. No connection is opened to
    /// answer it, so it works while a server is down, which is exactly
    /// when somebody asks what is declared.</summary>
    public CatalogsAnswer Catalogs()
    {
        var servers = new List<ServerReport>();

        foreach (ServerDecl server in boundary.Servers)
        {
            var databases = new List<DatabaseReport>();
            foreach (DatabaseDecl database in server.Databases)
            {
                var scopes = new List<ScopeReport>();
                foreach (ScopeDecl scope in database.Scopes)
                    scopes.Add(new ScopeReport(
                        scope.Pattern.Typed,
                        Verbs.Write(scope.Can),
                        scope.Columns.Words,
                        scope.Screen is { } s ? Screens.Write(s) : null));

                databases.Add(new DatabaseReport(
                    database.Name, Verbs.Write(database.Can),
                    Screens.Write(database.Screen), scopes));
            }

            servers.Add(new ServerReport(server.Name, server.Connect.Display, databases));
        }

        return new CatalogsAnswer(servers, boundary.Models, boundary.Origin);
    }

    // ---- objects ----

    /// <summary>
    /// Tables and views matching a pattern, filtered by <c>list</c> -
    /// the find answer, translated. A hidden object is not a row here,
    /// and not a count either.
    /// </summary>
    public async Task<Result<ObjectsAnswer>> ObjectsAsync(
        string? pattern, CancellationToken cancel = default)
    {
        ObjectPattern? shaped = null;
        string? bare = null;

        if (pattern is { Length: > 0 })
        {
            if (pattern.Contains('.'))
            {
                Result<ObjectPattern> parsed = ObjectPattern.Parse(pattern);
                if (!parsed.IsOk) return parsed.Carry<ObjectsAnswer>();
                shaped = parsed.Value;
            }
            else
            {
                bare = pattern;
            }
        }

        var listed = new List<ListedObject>();
        bool truncated = false;
        Screened screen = Screened.None;

        foreach ((ServerDecl server, DatabaseDecl database) in boundary.Databases())
        {
            Result<CatalogSnapshot> snapshot =
                await resolver.SnapshotAsync(server, database, cancel).ConfigureAwait(false);
            if (!snapshot.IsOk) return snapshot.Carry<ObjectsAnswer>();

            foreach (CatalogObject o in snapshot.Value.Objects
                .OrderBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (shaped is not null && !shaped.Matches(o.Schema, o.Name)) continue;
                if (bare is not null && !BareMatches(bare, o.Name)) continue;

                (Verb can, Screened screened, ColumnRule columns, _) = database.At(o.Schema, o.Name);
                if (!can.HasFlag(Verb.List)) continue;

                if (listed.Count >= ListingCap)
                {
                    truncated = true;
                    break;
                }

                int visible = o.Columns.Count(c => columns.Allows(c.Name));
                listed.Add(new ListedObject(
                    database.Name, o.Schema, o.Name, o.IsView, visible, can));
                screen |= screened;
            }

            if (truncated) break;
        }

        var payload = new StringBuilder();
        foreach (ListedObject o in listed)
            payload.Append(o.Database).Append('.').Append(o.Schema).Append('.')
                .Append(o.Name).Append('\n');

        if (Disclosure.Check(payload.ToString(), screen, screener) is { } refused)
            return Result<ObjectsAnswer>.Fail(refused);

        return Result<ObjectsAnswer>.Ok(new ObjectsAnswer(listed, truncated));
    }

    static bool BareMatches(string pattern, string name)
    {
        // The same one-segment semantics the scope pattern uses; a
        // pattern without a dot names objects in any schema.
        Result<ObjectPattern> parsed = ObjectPattern.Parse("*." + pattern);
        return parsed.IsOk && parsed.Value.Matches("anything", name);
    }

    // ---- select ----

    public async Task<Result<SelectAnswer>> SelectAsync(
        string sql, int? limit = null, CancellationToken cancel = default)
    {
        Result<Approved> approved =
            await Gate.ApproveAsync(sql, resolver, cancel).ConfigureAwait(false);
        if (!approved.IsOk) return approved.Carry<SelectAnswer>();

        if (source is not IQuerySource queries)
            return Result<SelectAnswer>.Fail(Outcome.Invalid,
                "this bench has no server behind it, so a SELECT has nowhere to run");

        // One server per statement: the rewrite made every reference
        // three-part, and three parts name a database on ONE server.
        var servers = new List<ServerDecl>();
        foreach (ResolvedObject o in approved.Value.Objects)
            if (!servers.Contains(o.Server)) servers.Add(o.Server);

        if (servers.Count > 1)
            return Result<SelectAnswer>.Fail(Outcome.Refused,
                "one server per statement: this query reaches "
                + string.Join(" and ", servers.Select(s => s.Name))
                + ", and a connection speaks to one of them");

        ServerDecl on = servers.Count == 1 ? servers[0] : boundary.Servers[0];

        int rowCap = Math.Clamp(limit ?? SqlData.DefaultRowCap, 1, 10_000);
        Result<Table> table = await queries
            .QueryAsync(on, approved.Value.Sql, rowCap, cancel).ConfigureAwait(false);
        if (!table.IsOk) return table.Carry<SelectAnswer>();

        // The payload under judgement is the serialized response,
        // exactly as a caller would see it - cell values in row context.
        if (Disclosure.Check(
            Serialized(table.Value), approved.Value.Screen, screener) is { } refused)
            return Result<SelectAnswer>.Fail(refused);

        var displays = new List<string>();
        foreach (ResolvedObject o in approved.Value.Objects)
            if (!displays.Contains(o.Display)) displays.Add(o.Display);

        return Result<SelectAnswer>.Ok(
            new SelectAnswer(table.Value, approved.Value.Sql, displays));
    }

    /// <summary>The canonical serialization the screen judges: JSON
    /// records, column names included, built the same way whichever
    /// front end asked.</summary>
    static string Serialized(Table table)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (IReadOnlyList<Cell> row in table.Rows)
            {
                writer.WriteStartObject();
                for (int i = 0; i < table.Columns.Count; i++)
                    writer.WriteString(table.Columns[i].Name, row[i].Text ?? "");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ---- describe ----

    public async Task<Result<Described>> DescribeAsync(
        string name, CancellationToken cancel = default)
    {
        Result<ObjectName> parsed = Names.Parse(name);
        if (!parsed.IsOk) return parsed.Carry<Described>();

        Result<ResolvedObject> resolved = await resolver
            .ResolveAsync(parsed.Value, Verb.Describe, cancel).ConfigureAwait(false);
        if (!resolved.IsOk) return resolved.Carry<Described>();

        ResolvedObject on = resolved.Value;

        IReadOnlyList<IndexFact> indexes = [];
        IReadOnlyList<ForeignKeyFact> keys = [];
        if (source is IConstraintSource constraints)
        {
            var asked = await constraints.ConstraintsAsync(on, cancel).ConfigureAwait(false);
            if (!asked.IsOk) return asked.Carry<Described>();
            (indexes, keys) = asked.Value;
        }

        var columns = new List<ColumnFact>();
        foreach (CatalogColumn c in on.Permitted)
            columns.Add(new ColumnFact(c.Name, c.Type, c.Nullable));

        // The hash names this shape - permitted columns and shown
        // constraints - so a later describe can answer "unchanged"
        // cheaply, and a mismatch says to look again.
        var shape = new StringBuilder();
        shape.Append(on.Display).Append(on.Object.IsView ? ":view" : ":table").Append('\n');
        foreach (ColumnFact c in columns)
            shape.Append(c.Name).Append(' ').Append(c.Type)
                .Append(c.Nullable ? " null" : " not-null").Append('\n');
        foreach (IndexFact i in indexes)
            shape.Append("ix ").Append(i.Name).Append(' ')
                .Append(string.Join(",", i.Columns)).Append('\n');
        foreach (ForeignKeyFact k in keys)
            shape.Append("fk ").Append(k.Name).Append(' ')
                .Append(string.Join(",", k.Columns)).Append('\n');

        string hash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(shape.ToString())));

        var described = new Described(
            on.Display, on.Object.IsView, columns, indexes, keys, hash);

        if (Disclosure.Check(shape.ToString(), on.Screen, screener, on.Display) is { } refused)
            return Result<Described>.Fail(refused);

        return Result<Described>.Ok(described);
    }
}
