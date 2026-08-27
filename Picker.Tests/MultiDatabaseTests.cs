using Picker.Core;
using Xunit;

namespace Picker.Tests;

/// <summary>
/// More than one database, more than one server - the ETL shape, where
/// a boundary declares a staging database and a warehouse and a query
/// walks both.
///
/// <para>The capability is in the design from the start - the
/// configuration takes many servers and many databases, resolution
/// proves uniqueness across all of them, and a statement may join
/// across databases ON ONE SERVER because one connection runs it. These
/// tests are what make that claim checkable rather than remembered:
/// none of it was proven anywhere before they existed.</para>
/// </summary>
public sealed class MultiDatabaseTests
{
    static FakeCatalog BothCatalogs() => new FakeCatalog()
        .Holding("Sales", Fixture.Customers, Fixture.Orders, Fixture.AuditLog, Fixture.TopCustomers)
        .Holding("Finance", Fixture.Invoices);

    [Fact]
    public async Task AJoinAcrossTwoDatabasesOnOneServerRuns()
    {
        FakeCatalog catalog = BothCatalogs();
        using var bench = new Bench(
            Fixture.Boundary(Fixture.Sales(), Fixture.Finance()), catalog);

        Result<SelectAnswer> answer = await bench.SelectAsync(
            "SELECT o.total, i.amount FROM Sales.dbo.Orders AS o "
            + "JOIN Finance.dbo.Invoices AS i ON i.order_id = o.id");

        Assert.True(answer.IsOk, answer.Failure?.Message);
        Assert.Contains("[Sales].[dbo].[Orders]", catalog.Ran);
        Assert.Contains("[Finance].[dbo].[Invoices]", catalog.Ran);
    }

    [Fact]
    public async Task AStatementSpanningTwoServersIsRefusedNamingBoth()
    {
        using var bench = new Bench(
            Fixture.Servers(
                Fixture.Server("etl", Fixture.Sales()),
                Fixture.Server("warehouse", Fixture.Finance())),
            BothCatalogs());

        Result<SelectAnswer> answer = await bench.SelectAsync(
            "SELECT o.total, i.amount FROM Sales.dbo.Orders AS o "
            + "JOIN Finance.dbo.Invoices AS i ON i.order_id = o.id");

        Assert.False(answer.IsOk);
        Assert.Equal(Outcome.Refused, answer.Failure!.Outcome);
        Assert.Contains("one server per statement", answer.Failure.Message);
        Assert.Contains("etl", answer.Failure.Message);
        Assert.Contains("warehouse", answer.Failure.Message);
    }

    [Fact]
    public async Task ABareNameUniqueAcrossTheDatabasesResolves()
    {
        FakeCatalog catalog = BothCatalogs();
        using var bench = new Bench(
            Fixture.Boundary(Fixture.Sales(), Fixture.Finance()), catalog);

        Result<SelectAnswer> answer = await bench.SelectAsync(
            "SELECT amount FROM Invoices");

        Assert.True(answer.IsOk, answer.Failure?.Message);
        Assert.Contains("[Finance].[dbo].[Invoices]", catalog.Ran);
    }

    [Fact]
    public async Task ABareNameInTwoDatabasesIsRefusedNamingBothPlaces()
    {
        // Orders exists in Sales AND in Finance, so the bare name cannot
        // say which - and both are listable, so naming both discloses
        // nothing a listing had not.
        FakeCatalog catalog = new FakeCatalog()
            .Holding("Sales", Fixture.Customers, Fixture.Orders)
            .Holding("Finance", Fixture.Invoices, Fixture.Orders);
        using var bench = new Bench(
            Fixture.Boundary(Fixture.Sales(), Fixture.Finance()), catalog);

        Result<SelectAnswer> answer = await bench.SelectAsync("SELECT total FROM Orders");

        Assert.False(answer.IsOk);
        Assert.Equal(Outcome.Invalid, answer.Failure!.Outcome);
        Assert.Contains("Sales.dbo.Orders", answer.Failure.Message);
        Assert.Contains("Finance.dbo.Orders", answer.Failure.Message);
        Assert.Contains("qualify", answer.Failure.Message);
    }

    [Fact]
    public async Task TheSameDatabaseNameOnTwoServersIsRefusedWhenNamed()
    {
        // Both servers hold a database declared "Sales". The declared
        // name IS the name the rewrite sends, so no rename can fix this
        // inside one configuration - the refusal has to say so.
        using var bench = new Bench(
            Fixture.Servers(
                Fixture.Server("etl", Fixture.Sales()),
                Fixture.Server("warehouse", Fixture.Sales())),
            BothCatalogs());

        Result<Described> answer = await bench.DescribeAsync("Sales.dbo.Orders");

        Assert.False(answer.IsOk);
        Assert.Equal(Outcome.Invalid, answer.Failure!.Outcome);
        Assert.Contains("more than one server", answer.Failure.Message);
        Assert.Contains("etl", answer.Failure.Message);
        Assert.Contains("warehouse", answer.Failure.Message);
    }

    [Fact]
    public async Task ObjectsSweepsEveryDeclaredDatabaseOnEveryServer()
    {
        using var bench = new Bench(
            Fixture.Servers(
                Fixture.Server("etl", Fixture.Sales()),
                Fixture.Server("warehouse", Fixture.Finance())),
            BothCatalogs());

        Result<ObjectsAnswer> listed = await bench.ObjectsAsync(null);

        Assert.True(listed.IsOk, listed.Failure?.Message);
        Assert.Contains(listed.Value.Objects, o => o.Database == "Sales" && o.Name == "Orders");
        Assert.Contains(listed.Value.Objects, o => o.Database == "Finance" && o.Name == "Invoices");
        // And the hidden schema stayed hidden while both databases answered.
        Assert.DoesNotContain(listed.Value.Objects, o => o.Schema == "audit");
    }

    [Fact]
    public async Task DescribeReachesTheSecondDatabase()
    {
        using var bench = new Bench(
            Fixture.Boundary(Fixture.Sales(), Fixture.Finance()), BothCatalogs());

        Result<Described> described = await bench.DescribeAsync("Finance.dbo.Invoices");

        Assert.True(described.IsOk, described.Failure?.Message);
        Assert.Contains(described.Value.Columns, c => c.Name == "amount");
    }
}
