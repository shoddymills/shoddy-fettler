using Microsoft.Data.SqlClient;
using Picker.Core;
using Xunit;

namespace Picker.Tests;

/// <summary>
/// A test that needs a real SQL Server, and says so when there is none.
///
/// <para>LocalDB is the zero-install instance a Windows development
/// machine carries; CI provisions a container and points the same tests
/// at it through PICKER_TEST_SQL. A skip nobody can see is a test that
/// quietly stopped existing, so the reason prints.</para>
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (LocalDb.Connect is null)
            Skip = $"needs SQL Server: neither PICKER_TEST_SQL nor {LocalDb.Instance} answered";
    }
}

public static class LocalDb
{
    public const string Instance = @"(localdb)\MSSQLLocalDB";

    /// <summary>The connection string a live server answered on, probed
    /// once per run - or null, which skips every test that needs one.</summary>
    public static readonly string? Connect = Probe();

    static string? Probe()
    {
        string candidate = Environment.GetEnvironmentVariable("PICKER_TEST_SQL")
            ?? $"Server={Instance};Integrated Security=true;Connect Timeout=5;TrustServerCertificate=true";

        try
        {
            using var connection = new SqlConnection(candidate);
            connection.Open();
            return candidate;
        }
        catch (Exception e) when (e is SqlException or InvalidOperationException
                                      or ArgumentException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// The whole path against a live server: catalog read, gate, rewrite,
/// execution, describe - everything the fakes prove, proven again with
/// SQL Server on the other end.
/// </summary>
public sealed class LocalDbTests : IDisposable
{
    readonly string database = $"picker_test_{Guid.NewGuid():N}";
    readonly bool created;

    public LocalDbTests()
    {
        if (LocalDb.Connect is null) return;

        Run($"CREATE DATABASE [{database}]");
        Run($"""
            CREATE TABLE [{database}].dbo.Customers (
                id int NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
                name nvarchar(80) NOT NULL,
                city nvarchar(40) NULL,
                ssn char(11) NULL)
            """);
        Run($"""
            INSERT INTO [{database}].dbo.Customers (id, name, city, ssn) VALUES
                (1, N'Ada', N'Leeds', '219-09-9999'),
                (2, N'Ben', N'Batley', NULL),
                (3, N'Cas', NULL, NULL)
            """);
        created = true;
    }

    public void Dispose()
    {
        if (!created) return;
        try
        {
            Run($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            Run($"DROP DATABASE [{database}]");
        }
        catch (SqlException)
        {
            // A leaked picker_test_* database is visible and disposable;
            // failing the run over the cleanup would hide the result.
        }
    }

    static void Run(string sql)
    {
        using var connection = new SqlConnection(LocalDb.Connect);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    Bench Bench() => new(new Boundary(
        [new ServerDecl("local", new Connect(LocalDb.Connect!, false),
        [
            new DatabaseDecl(database, Fixture.Everything, Screened.None,
            [
                Fixture.Scope("dbo.Customers", Fixture.Everything, Fixture.Columns("-ssn")),
            ]),
        ])],
        null, "the LocalDB fixture"));

    [SqlServerFact]
    public async Task ASelectRunsEndToEndWithTheStarExpandedPastTheHiddenColumn()
    {
        using Bench bench = Bench();

        Result<SelectAnswer> ran = await bench.SelectAsync(
            $"SELECT * FROM [{database}].dbo.Customers ORDER BY id");

        Assert.True(ran.IsOk, ran.Failure?.Message);
        Assert.Equal(3, ran.Value.Table.Rows.Count);
        Assert.Equal(["id", "name", "city"],
            ran.Value.Table.Columns.Select(c => c.Name).ToArray());
        Assert.DoesNotContain("ssn", ran.Value.Ran, StringComparison.OrdinalIgnoreCase);

        // Typed cells: an int is an integer, a NULL is a null.
        Assert.Equal(CellKind.Integer, ran.Value.Table.Rows[0][0].Kind);
        Assert.Equal("Ada", ran.Value.Table.Rows[0][1].Text);
        Assert.Equal(CellKind.Null, ran.Value.Table.Rows[2][2].Kind);
    }

    [SqlServerFact]
    public async Task TheRowCapBitesAndSaysSo()
    {
        using Bench bench = Bench();

        Result<SelectAnswer> ran = await bench.SelectAsync(
            $"SELECT name FROM [{database}].dbo.Customers", limit: 2);

        Assert.True(ran.IsOk, ran.Failure?.Message);
        Assert.Equal(2, ran.Value.Table.Rows.Count);
        Assert.True(ran.Value.Table.Truncated);
    }

    [SqlServerFact]
    public async Task AHiddenColumnIsRefusedBeforeTheServerEverSeesTheQuery()
    {
        using Bench bench = Bench();

        Result<SelectAnswer> refused = await bench.SelectAsync(
            $"SELECT ssn FROM [{database}].dbo.Customers");

        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.NotFound, refused.Failure!.Outcome);
    }

    [SqlServerFact]
    public async Task DescribeReadsTheRealCatalogAndOmitsTheHiddenColumn()
    {
        using Bench bench = Bench();

        Result<Described> described = await bench.DescribeAsync(
            $"{database}.dbo.Customers");

        Assert.True(described.IsOk, described.Failure?.Message);
        Assert.Equal(["id", "name", "city"],
            described.Value.Columns.Select(c => c.Name).ToArray());
        Assert.Contains(described.Value.Indexes,
            i => i.PrimaryKey && i.Columns.SequenceEqual(["id"]));
        Assert.NotEmpty(described.Value.Hash);
    }

    [SqlServerFact]
    public async Task ObjectsListsTheRealTable()
    {
        using Bench bench = Bench();

        Result<ObjectsAnswer> listed = await bench.ObjectsAsync("dbo.*");

        Assert.True(listed.IsOk, listed.Failure?.Message);
        ListedObject customers = Assert.Single(listed.Value.Objects,
            o => o.Name == "Customers");
        Assert.Equal(3, customers.Columns);      // ssn is not a count either
    }
}
