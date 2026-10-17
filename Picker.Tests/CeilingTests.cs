using System.Text;
using System.Text.Json;
using Picker.Cli;
using Picker.Core;
using Picker.Mcp;
using Xunit;

namespace Picker.Tests;

/// <summary>
/// The ceiling every answer is held to, the same one Fettler holds:
/// 40,000 bytes of UTF-8 as written, 10,000 for a refusal, and a way on
/// from every listing that stops short.
/// </summary>
public sealed class CeilingTests
{
    static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    sealed record Called(string Text, bool IsError)
    {
        public JsonElement Answer => Parse(Text);
    }

    static async Task<Called> Call(Bench bench, string tool, object arguments)
    {
        using var server = new McpServer(bench, TextReader.Null, TextWriter.Null);
        string message = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments },
        });

        string? reply = await server.AnswerAsync(message);
        JsonElement result = Parse(reply!).GetProperty("result");

        return new Called(
            result.GetProperty("content")[0].GetProperty("text").GetString()!,
            result.TryGetProperty("isError", out JsonElement e) && e.GetBoolean());
    }

    /// <summary>Markup, quotes and accented letters: what a text column
    /// holds, and what an encoder that escapes for HTML turns into six
    /// bytes a character.</summary>
    static string Wide(int length)
    {
        var text = new StringBuilder();
        while (text.Length < length) text.Append("<b>\"Zoë\" & Ünïcode</b> ");
        return text.ToString(0, length);
    }

    static readonly IReadOnlyList<ColumnFact> TwoColumns =
        [new ColumnFact("id", "int", false), new ColumnFact("total", "decimal(10,2)", false)];

    [Fact]
    public async Task WideRowsAreCutToTheCeilingAndSaySo()
    {
        FakeCatalog catalog = Fixture.Catalog();
        catalog.Answer = new Table(TwoColumns,
            [.. Enumerable.Range(0, 2000).Select(i => (IReadOnlyList<Cell>)
                [new Cell(CellKind.Integer, i.ToString()), new Cell(CellKind.Text, Wide(1000))])],
            false);
        using Bench bench = Fixture.Bench(catalog: catalog);

        Called selected = await Call(bench, "select",
            new { sql = "SELECT id, total FROM Sales.dbo.Orders", limit = 10_000 });
        JsonElement answer = selected.Answer;

        Assert.False(selected.IsError, selected.Text);
        Assert.True(Bytes(selected.Text) <= Budget.Ceiling, $"{Bytes(selected.Text)} bytes");
        Assert.True(answer.GetProperty("truncated").GetBoolean());

        int rows = answer.GetProperty("rows").GetArrayLength();
        Assert.InRange(rows, 1, 1999);
        Assert.Equal(rows, answer.GetProperty("count").GetInt32());

        CliResult human = await Command.RunAsync(
            ["select", "SELECT id, total FROM Sales.dbo.Orders", "--limit", "10000"],
            new StringReader(""), bench);
        Assert.True(Bytes(human.Stdout) <= Budget.Ceiling);
    }

    /// <summary>A row cannot be split across answers, so one too wide
    /// for any answer is refused, and the refusal says what to do.</summary>
    [Fact]
    public async Task ARowTooWideForAnyAnswerIsRefusedAndSaysWhy()
    {
        FakeCatalog catalog = Fixture.Catalog();
        IReadOnlyList<ColumnFact> thirty = [.. Enumerable.Range(0, 30).Select(i => new ColumnFact($"c{i}", "nvarchar(max)", true))];
        catalog.Answer = new Table(thirty,
            [[.. Enumerable.Range(0, 30).Select(_ => new Cell(CellKind.Text, Wide(2000)))]],
            false);
        using Bench bench = Fixture.Bench(catalog: catalog);

        Called selected = await Call(bench, "select", new { sql = "SELECT id FROM Sales.dbo.Orders" });

        Assert.True(selected.IsError);
        Assert.True(Bytes(selected.Text) <= Budget.ErrorCeiling);
        Assert.Contains("row", selected.Answer.GetProperty("message").GetString());
    }

    static CatalogObject Long(int i) => new("dbo", $"table_{i:D4}_{new string('t', 90)}", false,
        [new CatalogColumn("id", "int", false, 1)]);

    [Fact]
    public async Task AListingOfManyObjectsPagesUnderTheCeiling()
    {
        FakeCatalog catalog = new FakeCatalog().Holding("Sales", [.. Enumerable.Range(0, 400).Select(Long)]);
        using Bench bench = Fixture.Bench(new DatabaseDecl("Sales", Fixture.Everything, Screened.None, []), catalog);

        var names = new List<string>();
        object arguments = new { };

        for (int calls = 0; ; calls++)
        {
            Assert.True(calls < 100, "next never ran out");

            Called listed = await Call(bench, "objects", arguments);
            Assert.True(Bytes(listed.Text) <= Budget.Ceiling, $"{Bytes(listed.Text)} bytes");

            foreach (JsonElement o in listed.Answer.GetProperty("objects").EnumerateArray())
                names.Add(o.GetProperty("name").GetString()!);

            if (!listed.Answer.TryGetProperty("next", out JsonElement next)) break;
            arguments = new { after = next.GetString() };
        }

        Assert.Equal(400, names.Count);
        Assert.Equal(400, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ADescribeOfAThousandColumnsPagesUnderTheCeiling()
    {
        var wide = new CatalogObject("dbo", "Wide", false,
            [.. Enumerable.Range(1, 1024).Select(i => new CatalogColumn($"column_{i:D4}_{new string('c', 60)}", "nvarchar(80)", true, i))]);
        FakeCatalog catalog = new FakeCatalog().Holding("Sales", wide);
        using Bench bench = Fixture.Bench(new DatabaseDecl("Sales", Fixture.Everything, Screened.None, []), catalog);

        var columns = new List<string>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        object arguments = new { name = "Sales.dbo.Wide" };

        for (int calls = 0; ; calls++)
        {
            Assert.True(calls < 100, "next never ran out");

            Called described = await Call(bench, "describe", arguments);
            Assert.True(Bytes(described.Text) <= Budget.Ceiling, $"{Bytes(described.Text)} bytes");

            foreach (JsonElement c in described.Answer.GetProperty("columns").EnumerateArray())
                columns.Add(c.GetProperty("name").GetString()!);
            hashes.Add(described.Answer.GetProperty("hash").GetString()!);

            if (!described.Answer.TryGetProperty("next", out JsonElement next)) break;
            arguments = new { name = "Sales.dbo.Wide", after = next.GetString() };
        }

        Assert.Equal(1024, columns.Count);
        Assert.Equal(1024, columns.Distinct(StringComparer.Ordinal).Count());
        Assert.Single(hashes);
    }

    [Fact]
    public async Task ARefusalIsAnErrorUnderTheErrorCeiling()
    {
        using Bench bench = Fixture.Bench();

        Called refused = await Call(bench, "select", new { sql = "DELETE FROM Sales.dbo.Orders" });

        Assert.True(refused.IsError);
        Assert.False(refused.Answer.GetProperty("ok").GetBoolean());
        Assert.True(Bytes(refused.Text) <= Budget.ErrorCeiling);
    }

    [Fact]
    public async Task EveryToolDeclaresTheCeilingToTheClient()
    {
        using Bench bench = Fixture.Bench();
        using var server = new McpServer(bench, TextReader.Null, TextWriter.Null);

        string? reply = await server.AnswerAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        foreach (JsonElement tool in Parse(reply!).GetProperty("result").GetProperty("tools").EnumerateArray())
            Assert.Equal(Budget.Ceiling, tool.GetProperty("_meta")
                .GetProperty("anthropic/maxResultSizeChars").GetInt32());
    }

    [Fact]
    public void AnAnswerOverTheCeilingIsNeverSent()
    {
        string big = "{\"ok\":true,\"text\":\"" + new string('x', Budget.Ceiling) + "\"}\n";

        CliResult held = Command.Held(new CliResult(ExitCodes.Ok, big, ""), json: true);

        Assert.NotEqual(ExitCodes.Ok, held.ExitCode);
        Assert.True(held.Failed);
        Assert.True(Bytes(held.Stdout) <= Budget.ErrorCeiling);
        Assert.Contains("ceiling", Parse(held.Stdout).GetProperty("message").GetString());
    }
}
