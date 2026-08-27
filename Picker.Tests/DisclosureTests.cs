using Picker.Core;
using Xunit;

namespace Picker.Tests;

/// <summary>
/// The screen over result sets: deny on detection, fail closed when a
/// screen that was asked for cannot run, serve when it may - proven
/// with a fake standing where burler would.
/// </summary>
public sealed class DisclosureTests
{
    static Table Rows(params string[] values) => new(
        [new ColumnFact("v", "nvarchar(80)", true)],
        [.. values.Select(v => (IReadOnlyList<Cell>)[new Cell(CellKind.Text, v)])],
        false);

    static Bench ScreenedBench(Screened screen, FakeCatalog catalog, IScreener? screener = null)
        => Fixture.Bench(Fixture.Sales(screen), catalog, screener);

    [Fact]
    public async Task ASyntheticSsnRefusesNamingCategoryAndCountAndQuotingNothing()
    {
        var catalog = Fixture.Catalog();
        catalog.Answer = Rows("the file says 219-09-9999 in the note");

        Result<SelectAnswer> refused = await ScreenedBench(Screened.Identifiers, catalog)
            .SelectAsync("SELECT name FROM Sales.dbo.Customers");

        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.Screened, refused.Failure!.Outcome);
        Assert.Contains("1 in identifiers", refused.Failure.Message);
        Assert.DoesNotContain("219-09-9999", refused.Failure.Message);
    }

    [Fact]
    public async Task CleanRowsAreServedThroughTheSameScreen()
    {
        var catalog = Fixture.Catalog();
        catalog.Answer = Rows("nothing regulated here");

        Result<SelectAnswer> served = await ScreenedBench(Screened.Identifiers, catalog)
            .SelectAsync("SELECT name FROM Sales.dbo.Customers");

        Assert.True(served.IsOk, served.Failure?.Message);
    }

    [Fact]
    public async Task AScopeWhoseScreenIsFalseServesWhatTheDatabaseWouldRefuse()
    {
        DatabaseDecl sales = new("Sales", Fixture.Everything, Screened.Identifiers,
        [
            Fixture.Scope("dbo.Orders", Fixture.Everything, screen: Screened.None),
        ]);

        var catalog = Fixture.Catalog();
        catalog.Answer = Rows("219-09-9999");

        Result<SelectAnswer> served = await Fixture.Bench(sales, catalog)
            .SelectAsync("SELECT id FROM Sales.dbo.Orders");
        Assert.True(served.IsOk, served.Failure?.Message);

        Result<SelectAnswer> refused = await Fixture.Bench(sales, catalog)
            .SelectAsync("SELECT name FROM Sales.dbo.Customers");
        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.Screened, refused.Failure!.Outcome);
    }

    [Fact]
    public async Task AJoinIsScreenedForTheUnionOfItsScopesScreens()
    {
        // Orders is unscreened; joining screened Customers screens the
        // whole response, because the rows disclose from both.
        DatabaseDecl sales = new("Sales", Fixture.Everything, Screened.None,
        [
            Fixture.Scope("dbo.Customers", Fixture.Everything, screen: Screened.Identifiers),
        ]);

        var catalog = Fixture.Catalog();
        catalog.Answer = Rows("219-09-9999");

        Result<SelectAnswer> refused = await Fixture.Bench(sales, catalog).SelectAsync(
            "SELECT o.id FROM Sales.dbo.Orders o "
            + "JOIN Sales.dbo.Customers c ON c.id = o.customer_id");

        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.Screened, refused.Failure!.Outcome);
    }

    [Fact]
    public async Task AModelBackedScreenThatCannotRunRefusesRatherThanServing()
    {
        var catalog = Fixture.Catalog();
        catalog.Answer = Rows("a clinical note");

        var dead = new FakeScreener
        {
            Answer = (_, _) => Result<IReadOnlyList<ScreenFinding>>.Fail(
                Outcome.Screened, "the screening sidecar failed: it died"),
        };

        Result<SelectAnswer> refused = await ScreenedBench(
                Screened.Clinical, catalog, dead)
            .SelectAsync("SELECT name FROM Sales.dbo.Customers");

        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.Screened, refused.Failure!.Outcome);
        Assert.Contains("could not be screened", refused.Failure.Message);
        Assert.Contains("sidecar", refused.Failure.Message);
    }

    [Fact]
    public async Task WithNoModelsDirectoryTierOneIsTheWholeScreenAndAPassServes()
    {
        // Clinical is asked for, no sidecar exists (models undeclared,
        // screener null): tier one judged it alone, and a clean payload
        // is served - a person who has not set the second tier up is not
        // a second tier that failed.
        var catalog = Fixture.Catalog();
        catalog.Answer = Rows("an ordinary row");

        Result<SelectAnswer> served = await ScreenedBench(
                Screened.Clinical | Screened.Identifiers, catalog)
            .SelectAsync("SELECT name FROM Sales.dbo.Customers");

        Assert.True(served.IsOk, served.Failure?.Message);
    }

    [Fact]
    public async Task AModelDetectionDeniesLikeATierOneOne()
    {
        var catalog = Fixture.Catalog();
        catalog.Answer = Rows("patient text the model flags");

        var flagging = new FakeScreener
        {
            Answer = (_, asked) => Result<IReadOnlyList<ScreenFinding>>.Ok(
                [new ScreenFinding(Screened.Clinical, "model"),
                 new ScreenFinding(Screened.Clinical, "model")]),
        };

        Result<SelectAnswer> refused = await ScreenedBench(
                Screened.Clinical, catalog, flagging)
            .SelectAsync("SELECT name FROM Sales.dbo.Customers");

        Assert.False(refused.IsOk);
        Assert.Contains("2 in clinical", refused.Failure!.Message);
    }

    [Fact]
    public async Task DescribeLeavesThroughTheSameSeam()
    {
        // A column NAME that trips tier one proves the describe payload
        // passes the screen - and that the seam is one seam.
        var withOddColumn = new FakeCatalog().Holding("Sales",
            new CatalogObject("dbo", "Odd", false,
            [
                new CatalogColumn("someone@example.com", "int", false, 1),
            ]));

        Result<Described> refused = await Fixture
            .Bench(Fixture.Sales(Screened.Identifiers), withOddColumn)
            .DescribeAsync("Sales.dbo.Odd");

        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.Screened, refused.Failure!.Outcome);
    }
}
