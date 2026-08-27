using Picker.Core;
using Xunit;

namespace Picker.Tests;

/// <summary>
/// The query gate, proven against a catalog of literals: the statement
/// discipline, the reference walk, the column proof, the star rewrite,
/// and above all the wording rule - hidden and absent answer alike.
/// </summary>
public sealed class GateTests
{
    static Task<Result<SelectAnswer>> Run(string sql, Bench? bench = null) =>
        (bench ?? Fixture.Bench()).SelectAsync(sql);

    // ---- acceptance 1: out-of-scope refuses in the not-there words ----

    [Fact]
    public async Task AnUndeclaredDatabaseAndAMissingTableAnswerInTheSameWords()
    {
        Result<SelectAnswer> undeclared = await Run("SELECT id FROM ThisDb.dbo.MyTable");
        Result<SelectAnswer> missing = await Run("SELECT id FROM Sales.dbo.NoSuchTable");

        Assert.False(undeclared.IsOk);
        Assert.False(missing.IsOk);
        Assert.Equal(Outcome.NotFound, undeclared.Failure!.Outcome);
        Assert.Equal(Outcome.NotFound, missing.Failure!.Outcome);

        // The same sentence with only the typed name changing - so the
        // difference cannot be probed.
        Assert.Equal(
            undeclared.Failure.Message.Replace("ThisDb.dbo.MyTable", "X"),
            missing.Failure.Message.Replace("Sales.dbo.NoSuchTable", "X"));
    }

    [Fact]
    public async Task AHiddenObjectAnswersInTheSameWordsAsAMissingOne()
    {
        Result<SelectAnswer> hidden = await Run("SELECT id FROM Sales.audit.Log");
        Result<SelectAnswer> missing = await Run("SELECT id FROM Sales.audit.Missing");

        Assert.False(hidden.IsOk);
        Assert.False(missing.IsOk);
        Assert.Equal(
            hidden.Failure!.Message.Replace("Sales.audit.Log", "X"),
            missing.Failure!.Message.Replace("Sales.audit.Missing", "X"));
    }

    // ---- acceptance 2: each refusal has its own stated reason ----

    [Theory]
    [InlineData("SELECT * FROM OtherServer.Sales.dbo.Customers", "linked server")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1')", "OPENROWSET")]
    [InlineData("EXEC sp_who", "only a SELECT")]
    [InlineData("SELECT id INTO #t FROM Sales.dbo.Orders", "write wearing a SELECT")]
    [InlineData("SELECT 1; SELECT 2", "one statement per call")]
    [InlineData("SELECT name FROM sys.tables", "system catalog")]
    [InlineData("SELECT * FROM INFORMATION_SCHEMA.TABLES", "system catalog")]
    [InlineData("UPDATE Sales.dbo.Orders SET total = 0", "only a SELECT")]
    [InlineData("USE Sales", "only a SELECT")]
    [InlineData("SELECT @@VERSION", "session state")]
    [InlineData("SELECT OBJECT_DEFINITION(1)", "system catalog")]
    [InlineData("SELECT dbo.MyFunction(1)", "user function")]
    [InlineData("SELECT id FROM Sales.dbo.Orders WITH (NOLOCK)", "hints")]
    public async Task EachForbiddenShapeIsRefusedWithItsOwnReason(string sql, string expected)
    {
        Result<SelectAnswer> refused = await Run(sql);
        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.Refused, refused.Failure!.Outcome);
        Assert.Contains(expected, refused.Failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhatDoesNotParseIsInvalidNotRefused()
    {
        Result<SelectAnswer> broken = await Run("SELECT FROM WHERE");
        Assert.False(broken.IsOk);
        Assert.Equal(Outcome.Invalid, broken.Failure!.Outcome);
    }

    // ---- acceptance 3: hidden columns, everywhere ----

    [Theory]
    [InlineData("SELECT ssn FROM Sales.dbo.Customers")]
    [InlineData("SELECT name FROM Sales.dbo.Customers WHERE ssn = '1'")]
    [InlineData("SELECT name FROM Sales.dbo.Customers ORDER BY ssn")]
    [InlineData("SELECT ssn + '' AS x FROM Sales.dbo.Customers")]
    [InlineData("SELECT c.ssn FROM Sales.dbo.Customers AS c")]
    [InlineData("SELECT name FROM Sales.dbo.Customers GROUP BY ssn")]
    [InlineData("SELECT (SELECT TOP 1 ssn FROM Sales.dbo.Customers) FROM Sales.dbo.Orders")]
    public async Task AHiddenColumnRefusesWhereverItAppears(string sql)
    {
        Result<SelectAnswer> refused = await Run(sql);
        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.NotFound, refused.Failure!.Outcome);
        Assert.Contains("'ssn' is not a column of", refused.Failure.Message);
    }

    [Fact]
    public async Task AHiddenColumnAndAMissingColumnAnswerInTheSameWords()
    {
        Result<SelectAnswer> hidden = await Run("SELECT ssn FROM Sales.dbo.Customers");
        Result<SelectAnswer> missing = await Run("SELECT nothere FROM Sales.dbo.Customers");

        Assert.Equal(
            hidden.Failure!.Message.Replace("'ssn'", "'X'"),
            missing.Failure!.Message.Replace("'nothere'", "'X'"));
    }

    [Fact]
    public async Task SelectStarExpandsToTheColumnsThatExistHere()
    {
        var catalog = Fixture.Catalog();
        Result<SelectAnswer> ran = await Run("SELECT * FROM Sales.dbo.Customers",
            Fixture.Bench(catalog: catalog));

        Assert.True(ran.IsOk, ran.Failure?.Message);
        Assert.Contains("[id]", catalog.Ran);
        Assert.Contains("[name]", catalog.Ran);
        Assert.Contains("[city]", catalog.Ran);
        Assert.DoesNotContain("ssn", catalog.Ran, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dob", catalog.Ran, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("*", catalog.Ran);
    }

    // ---- the rewrite ----

    [Fact]
    public async Task EveryReferenceRunsThreePartAndBracketed()
    {
        var catalog = Fixture.Catalog();
        Result<SelectAnswer> ran = await Run("SELECT id FROM Orders",
            Fixture.Bench(catalog: catalog));

        Assert.True(ran.IsOk, ran.Failure?.Message);
        Assert.Contains("[Sales].[dbo].[Orders]", catalog.Ran);
    }

    [Fact]
    public async Task AnUnqualifiedNameResolvesOnlyWhenUnique()
    {
        // The same object name in two declared databases cannot resolve
        // bare - the refusal names both candidates, which are listable.
        DatabaseDecl second = new("Archive", Fixture.Everything, Screened.None, []);
        var catalog = Fixture.Catalog().Holding("Archive", Fixture.Orders);
        var bench = new Bench(
            Fixture.Boundary(Fixture.Sales(), second), catalog);

        Result<SelectAnswer> ambiguous = await Run("SELECT id FROM Orders", bench);
        Assert.False(ambiguous.IsOk);
        Assert.Equal(Outcome.Invalid, ambiguous.Failure!.Outcome);
        Assert.Contains("Sales.dbo.Orders", ambiguous.Failure.Message);
        Assert.Contains("Archive.dbo.Orders", ambiguous.Failure.Message);

        Result<SelectAnswer> qualified = await Run("SELECT id FROM Archive.dbo.Orders", bench);
        Assert.True(qualified.IsOk, qualified.Failure?.Message);
    }

    [Fact]
    public async Task AHiddenObjectNeitherResolvesNorMakesANameAmbiguous()
    {
        // audit.Log is hidden; a bare 'Log' must answer not-there, not
        // "did you mean the hidden one".
        Result<SelectAnswer> bare = await Run("SELECT id FROM Log");
        Assert.False(bare.IsOk);
        Assert.Equal(Outcome.NotFound, bare.Failure!.Outcome);
    }

    // ---- shapes that must pass ----

    [Theory]
    [InlineData("SELECT id, name FROM Sales.dbo.Customers")]
    [InlineData("SELECT TOP 5 name FROM Sales.dbo.Customers ORDER BY name")]
    [InlineData("SELECT c.name, o.total FROM Sales.dbo.Customers c JOIN Sales.dbo.Orders o ON o.customer_id = c.id")]
    [InlineData("WITH big AS (SELECT customer_id, SUM(total) AS t FROM Sales.dbo.Orders GROUP BY customer_id) SELECT t FROM big")]
    [InlineData("SELECT name FROM (SELECT name FROM Sales.dbo.Customers) AS inner1")]
    [InlineData("SELECT name FROM Sales.dbo.Customers WHERE id IN (SELECT customer_id FROM Sales.dbo.Orders)")]
    [InlineData("SELECT name FROM Sales.dbo.Customers UNION SELECT name FROM Sales.dbo.TopCustomers")]
    [InlineData("SELECT name FROM Sales.dbo.Customers ORDER BY name OFFSET 5 ROWS FETCH NEXT 5 ROWS ONLY")]
    [InlineData("SELECT COUNT(*) AS n FROM Sales.dbo.Orders")]
    [InlineData("SELECT name, ROW_NUMBER() OVER (ORDER BY name) AS rn FROM Sales.dbo.Customers")]
    [InlineData("SELECT total FROM Sales.dbo.TopCustomers")]
    public async Task OrdinarySelectShapesPass(string sql)
    {
        Result<SelectAnswer> ran = await Run(sql);
        Assert.True(ran.IsOk, ran.Failure?.Message);
    }

    [Fact]
    public async Task SelectVerbIsCheckedPerObject()
    {
        // Customers grants select; a scope granting only list refuses
        // select BY NAME, because the object is listable - that is the
        // difference between hidden and refused.
        DatabaseDecl db = new("Sales", Fixture.Everything, Screened.None,
        [
            Fixture.Scope("dbo.Orders", Verb.List),
        ]);

        Result<SelectAnswer> refused = await Run("SELECT id FROM Sales.dbo.Orders",
            Fixture.Bench(db));
        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.Refused, refused.Failure!.Outcome);
        Assert.Contains("select is not granted on Sales.dbo.Orders", refused.Failure.Message);
        Assert.Contains("it grants: list", refused.Failure.Message);
    }

    [Fact]
    public async Task TheStarOverAJoinExpandsBothSidesPermittedColumnsOnly()
    {
        var catalog = Fixture.Catalog();
        Result<SelectAnswer> ran = await Run(
            "SELECT * FROM Sales.dbo.Customers c JOIN Sales.dbo.Orders o ON o.customer_id = c.id",
            Fixture.Bench(catalog: catalog));

        Assert.True(ran.IsOk, ran.Failure?.Message);
        Assert.Contains("[c].[name]", catalog.Ran);
        Assert.Contains("[o].[total]", catalog.Ran);
        Assert.DoesNotContain("ssn", catalog.Ran, StringComparison.OrdinalIgnoreCase);
    }
}
