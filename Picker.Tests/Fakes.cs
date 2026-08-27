using Picker.Core;
using Xunit;

namespace Picker.Tests;

/// <summary>
/// A catalog made of literals, standing where SQL Server would: the
/// grant matrix, the resolver and the whole gate are proven against
/// this, with no server in the room.
/// </summary>
public sealed class FakeCatalog : ICatalogSource, IQuerySource, IConstraintSource
{
    readonly Dictionary<string, CatalogSnapshot> databases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The SQL the bench actually sent to "the server", so a
    /// test can assert the rewrite - three-part names, expanded stars -
    /// rather than trust it.</summary>
    public string? Ran { get; private set; }

    /// <summary>What the fake answers to any query.</summary>
    public Table Answer { get; set; } = new([], [], false);

    public FakeCatalog Holding(string database, params CatalogObject[] objects)
    {
        databases[database] = new CatalogSnapshot(
            database, objects, CatalogSnapshot.HashOf(objects));
        return this;
    }

    public Task<Result<CatalogSnapshot>> LoadAsync(
        ServerDecl server, DatabaseDecl database, CancellationToken cancel) =>
        Task.FromResult(databases.TryGetValue(database.Name, out CatalogSnapshot? held)
            ? Result<CatalogSnapshot>.Ok(held)
            : Result<CatalogSnapshot>.Fail(Outcome.Denied,
                $"the fake has no database called {database.Name}"));

    public Task<Result<Table>> QueryAsync(
        ServerDecl server, string sql, int rowCap, CancellationToken cancel)
    {
        Ran = sql;
        return Task.FromResult(Result<Table>.Ok(Answer));
    }

    public Task<Result<(IReadOnlyList<IndexFact> Indexes, IReadOnlyList<ForeignKeyFact> ForeignKeys)>>
        ConstraintsAsync(ResolvedObject on, CancellationToken cancel) =>
        Task.FromResult(
            Result<(IReadOnlyList<IndexFact>, IReadOnlyList<ForeignKeyFact>)>.Ok(([], [])));
}

/// <summary>A screener whose verdicts a test scripts, for the
/// fail-closed cases a real model would bury.</summary>
public sealed class FakeScreener : IScreener
{
    public Func<string, Screened, Result<IReadOnlyList<ScreenFinding>>>? Answer { get; set; }

    public Result<IReadOnlyList<ScreenFinding>> Inspect(string payload, Screened categories) =>
        Answer is not null
            ? Answer(payload, categories)
            : Result<IReadOnlyList<ScreenFinding>>.Ok([]);
}

/// <summary>The standing fixture most tests share: one server, two
/// databases, and the scope shapes the requirements name.</summary>
public static class Fixture
{
    public static CatalogObject Customers => new("dbo", "Customers", false,
    [
        new CatalogColumn("id", "int", false, 1),
        new CatalogColumn("name", "nvarchar(80)", false, 2),
        new CatalogColumn("city", "nvarchar(40)", true, 3),
        new CatalogColumn("ssn", "char(11)", true, 4),
        new CatalogColumn("dob", "date", true, 5),
    ]);

    public static CatalogObject Orders => new("dbo", "Orders", false,
    [
        new CatalogColumn("id", "int", false, 1),
        new CatalogColumn("customer_id", "int", false, 2),
        new CatalogColumn("total", "decimal(10,2)", false, 3),
    ]);

    public static CatalogObject AuditLog => new("audit", "Log", false,
    [
        new CatalogColumn("id", "int", false, 1),
        new CatalogColumn("entry", "nvarchar(max)", true, 2),
    ]);

    public static CatalogObject TopCustomers => new("dbo", "TopCustomers", true,
    [
        new CatalogColumn("name", "nvarchar(80)", false, 1),
        new CatalogColumn("total", "decimal(10,2)", false, 2),
    ]);

    public static CatalogObject Invoices => new("dbo", "Invoices", false,
    [
        new CatalogColumn("id", "int", false, 1),
        new CatalogColumn("order_id", "int", false, 2),
        new CatalogColumn("amount", "decimal(10,2)", false, 3),
    ]);

    public static ScopeDecl Scope(
        string pattern, Verb can, ColumnRule? columns = null, Screened? screen = null)
    {
        Result<ObjectPattern> parsed = ObjectPattern.Parse(pattern);
        Assert.True(parsed.IsOk, parsed.Failure?.Message);
        return new ScopeDecl(parsed.Value, can, columns ?? ColumnRule.All, screen);
    }

    public static ColumnRule Columns(params string[] words)
    {
        Result<ColumnRule> parsed = ColumnRule.Parse(words);
        Assert.True(parsed.IsOk, parsed.Failure?.Message);
        return parsed.Value;
    }

    public const Verb Everything = Verb.List | Verb.Select | Verb.Describe;

    /// <summary>Sales, as the requirements draw it: Customers with ssn
    /// and dob hidden, Orders open, audit.* not there at all.</summary>
    public static DatabaseDecl Sales(Screened screen = Screened.None) => new(
        "Sales", Everything, screen,
        [
            Scope("dbo.Customers", Everything, Columns("-ssn", "-dob")),
            Scope("audit.*", Verb.None),
        ]);

    public static Boundary Boundary(params DatabaseDecl[] databases) => new(
        [new ServerDecl("test", new Connect("Server=nowhere", false), databases)],
        null, "the fixture");

    /// <summary>A second declared database, ETL-shaped: everything
    /// granted, no scopes narrowing it.</summary>
    public static DatabaseDecl Finance(Screened screen = Screened.None) =>
        new("Finance", Everything, screen, []);

    public static ServerDecl Server(string name, params DatabaseDecl[] databases) =>
        new(name, new Connect($"Server={name}", false), databases);

    public static Boundary Servers(params ServerDecl[] servers) =>
        new(servers, null, "the fixture");

    public static FakeCatalog Catalog() => new FakeCatalog()
        .Holding("Sales", Customers, Orders, AuditLog, TopCustomers);

    public static Bench Bench(
        DatabaseDecl? database = null, FakeCatalog? catalog = null, IScreener? screener = null)
        => new(Boundary(database ?? Sales()), catalog ?? Catalog(), screener);
}
