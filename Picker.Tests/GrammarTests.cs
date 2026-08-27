using Picker.Core;
using Xunit;

namespace Picker.Tests;

/// <summary>The closed sets and their parsers: verbs, screens, columns,
/// patterns, names.</summary>
public sealed class GrammarTests
{
    [Fact]
    public void TheVerbSetIsClosedAndAMisspellingIsRefusedNotIgnored()
    {
        Result<Verb> good = Verbs.Parse(["list", "select", "describe"]);
        Assert.True(good.IsOk);
        Assert.Equal(Verb.List | Verb.Select | Verb.Describe, good.Value);

        Result<Verb> bad = Verbs.Parse(["list", "selct"]);
        Assert.False(bad.IsOk);
        Assert.Contains("'selct' is not a verb", bad.Failure!.Message);

        // No execute, no write - not by absence of a word but by the
        // parser refusing it.
        Assert.False(Verbs.Parse(["execute"]).IsOk);
        Assert.False(Verbs.Parse(["update"]).IsOk);
    }

    [Fact]
    public void AnEmptyVerbListReadsAsNothing()
    {
        Result<Verb> none = Verbs.Parse([]);
        Assert.True(none.IsOk);
        Assert.Equal(Verb.None, none.Value);
        Assert.Equal("nothing", Verbs.Write(none.Value));
    }

    [Fact]
    public void ColumnListsIncludeOrExcludeAndMixingIsRefused()
    {
        ColumnRule include = Fixture.Columns("name", "city");
        Assert.True(include.Allows("name"));
        Assert.True(include.Allows("NAME"));
        Assert.False(include.Allows("ssn"));

        ColumnRule exclude = Fixture.Columns("-ssn", "-dob");
        Assert.True(exclude.Allows("name"));
        Assert.False(exclude.Allows("ssn"));
        Assert.False(exclude.Allows("SSN"));

        Result<ColumnRule> mixed = ColumnRule.Parse(["name", "-ssn"]);
        Assert.False(mixed.IsOk);
        Assert.Contains("cannot be combined", mixed.Failure!.Message);

        Result<ColumnRule> empty = ColumnRule.Parse([]);
        Assert.False(empty.IsOk);
    }

    [Fact]
    public void TheScreenGrammarIsFettlersOwnRenamedWordsIncluded()
    {
        Assert.Equal(Screens.Everything, Screens.Parse([]).Value | Screens.Everything);
        Assert.Equal(Screened.Identifiers | Screened.Clinical,
            Screens.Parse(["identifiers", "clinical"]).Value);
        Assert.Equal(Screens.Everything & ~Screened.Scientific,
            Screens.Parse(["-scientific"]).Value);

        Result<Screened> mixed = Screens.Parse(["identifiers", "-scientific"]);
        Assert.False(mixed.IsOk);

        // The old words are refused WITH DIRECTIONS, never translated.
        Result<Screened> old = Screens.Parse(["phi"]);
        Assert.False(old.IsOk);
        Assert.Contains("clinical", old.Failure!.Message);
    }

    [Theory]
    [InlineData("dbo.Customers", "dbo", "Customers", true)]
    [InlineData("dbo.Customers", "dbo", "customers", true)]   // case-insensitive
    [InlineData("audit.*", "audit", "Anything", true)]
    [InlineData("audit.*", "dbo", "Anything", false)]
    [InlineData("*.Customers", "sales", "Customers", true)]
    [InlineData("dbo.Cust*", "dbo", "Customers", true)]
    [InlineData("dbo.Cust*", "dbo", "Orders", false)]
    public void ObjectPatternsStayInsideTheirSegment(
        string pattern, string schema, string name, bool matches)
    {
        Result<ObjectPattern> parsed = ObjectPattern.Parse(pattern);
        Assert.True(parsed.IsOk);
        Assert.Equal(matches, parsed.Value.Matches(schema, name));
    }

    [Fact]
    public void AnObjectPatternNeedsItsTwoSegments()
    {
        Assert.False(ObjectPattern.Parse("Customers").IsOk);
        Assert.False(ObjectPattern.Parse("a.b.c").IsOk);
        Assert.False(ObjectPattern.Parse(".b").IsOk);
    }

    [Fact]
    public void TheMostSpecificScopeWins()
    {
        DatabaseDecl db = new("Sales", Verb.List, Screened.None,
        [
            Fixture.Scope("dbo.*", Verb.List | Verb.Select),
            Fixture.Scope("dbo.Customers", Verb.List),
        ]);

        Assert.Equal(Verb.List, db.At("dbo", "Customers").Can);
        Assert.Equal(Verb.List | Verb.Select, db.At("dbo", "Orders").Can);
        Assert.Equal(Verb.List, db.At("other", "Thing").Can);
    }

    [Fact]
    public void AScopeReplacesRatherThanAddsAndSilenceInheritsTheScreen()
    {
        DatabaseDecl db = new("Sales", Fixture.Everything, Screened.Identifiers,
        [
            Fixture.Scope("dbo.Tickets", Verb.List | Verb.Select, screen: Screens.Everything),
            Fixture.Scope("ref.*", Fixture.Everything, screen: Screened.None),
            Fixture.Scope("dbo.Customers", Verb.List | Verb.Select),
        ]);

        // Stated screen replaces; "screen": false takes it away; silence
        // inherits the database's.
        Assert.Equal(Screens.Everything, db.At("dbo", "Tickets").Screen);
        Assert.Equal(Screened.None, db.At("ref", "Codes").Screen);
        Assert.Equal(Screened.Identifiers, db.At("dbo", "Customers").Screen);
    }

    [Theory]
    [InlineData("Customers", null, null, "Customers")]
    [InlineData("dbo.Customers", null, "dbo", "Customers")]
    [InlineData("Sales.dbo.Customers", "Sales", "dbo", "Customers")]
    [InlineData("[Sales].[dbo].[Customers]", "Sales", "dbo", "Customers")]
    [InlineData("[odd].[na.me]", null, "odd", "na.me")]
    public void NamesParseByPartsBracketsHonoured(
        string typed, string? database, string? schema, string name)
    {
        Result<ObjectName> parsed = Names.Parse(typed);
        Assert.True(parsed.IsOk, parsed.Failure?.Message);
        Assert.Equal(database, parsed.Value.Database);
        Assert.Equal(schema, parsed.Value.Schema);
        Assert.Equal(name, parsed.Value.Name);
    }

    [Fact]
    public void AFourPartNameIsRefusedAsARouteOffTheSurface()
    {
        Result<ObjectName> linked = Names.Parse("OtherServer.Sales.dbo.Customers");
        Assert.False(linked.IsOk);
        Assert.Equal(Outcome.Refused, linked.Failure!.Outcome);
        Assert.Contains("linked server", linked.Failure.Message);
    }

    [Fact]
    public void QuotingDoublesTheClosingBracket()
    {
        Assert.Equal("[plain]", Names.Quote("plain"));
        Assert.Equal("[odd]]name]", Names.Quote("odd]name"));
    }
}
