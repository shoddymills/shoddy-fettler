using Picker.Core;
using Xunit;

namespace Picker.Tests;

/// <summary>The two files: the three sections, what they declare, what
/// they refuse, and the line between the checked-in file and the local
/// one.</summary>
public sealed class ConfigTests : IDisposable
{
    readonly string dir;

    public ConfigTests()
    {
        dir = Directory.CreateTempSubdirectory("picker-test-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }

    Result<Boundary> Load(string main, string? local = null)
    {
        string path = Path.Combine(dir, ConfigFile.FileName);
        File.WriteAllText(path, main);
        if (local is not null)
            File.WriteAllText(Path.Combine(dir, ConfigFile.LocalFileName), local);
        return ConfigFile.Open(path);
    }

    [Fact]
    public void AWholeDeclarationLoads()
    {
        Result<Boundary> opened = Load("""
            {
              "tools": { "servers": { "reporting": { "connect": "env:REPORTING_SQL" } } },
              "guardrails": {
                "servers": {
                  "reporting": {
                    "databases": {
                      "Sales": {
                        "can": ["list", "select", "describe"],
                        "scopes": [
                          { "object": "dbo.Customers",
                            "can": ["list", "select"],
                            "columns": ["-ssn", "-dob"] },
                          { "object": "ref.*",
                            "can": ["list", "select", "describe"] },
                          { "object": "audit.*", "can": [] }
                        ]
                      }
                    }
                  }
                }
              },
              "screenings": { "screen": { "Sales": ["identifiers"], "Sales/ref.*": false } }
            }
            """);

        Assert.True(opened.IsOk, opened.Failure?.Message);
        ServerDecl server = Assert.Single(opened.Value.Servers);
        Assert.Equal("env:REPORTING_SQL", server.Connect.Display);
        Assert.False(server.Open);
        Assert.True(opened.Value.Levels.GuardrailsOn);
        Assert.True(opened.Value.Levels.ScreeningsOn);

        DatabaseDecl sales = Assert.Single(server.Databases);
        Assert.Equal(Screened.Identifiers, sales.Screen);
        Assert.Equal(3, sales.Scopes.Count);

        (Verb can, Screened screen, ColumnRule columns, _) = sales.At("dbo", "Customers");
        Assert.Equal(Verb.List | Verb.Select, can);
        Assert.False(columns.Allows("ssn"));
        Assert.Equal(Screened.Identifiers, screen);          // silence inherits
        Assert.Equal(Screened.None, sales.At("ref", "Codes").Screen);
        Assert.Equal(Verb.None, sales.At("audit", "Log").Can);
    }

    [Fact]
    public void ADatabaseThatSaysNothingGrantsNothing()
    {
        Result<Boundary> opened = Load("""
            { "tools": { "servers": { "s": { "connect": "env:X" } } },
              "guardrails": { "servers": { "s": { "databases": { "Sales": {} } } } } }
            """);

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal(Verb.None, opened.Value.Servers[0].Databases[0].Can);
    }

    const string Tools = """ "tools": { "servers": { "s": { "connect": "env:X" } } } """;

    [Theory]
    [InlineData("""{ "tools": { "servers": { "s": { "connect": "env:X" } } }, "guardrails": { "servers": { "s": { "databases": { "d": { "can": ["selct"] } } } } } }""", "not a verb")]
    [InlineData("""{ "tools": { "servers": { "s": { "connect": "env:X" } } }, "guardrails": { "servers": { "s": { "databases": { "d": { } } } } }, "screenings": { "screen": { "d": ["lgal"] } } }""", "not a screening category")]
    [InlineData("""{ "tools": { "servers": { "s": { "connect": "env:X" } } }, "guardrails": { "servers": { "s": { "databases": { "d": { } } } } }, "screenings": { "screen": { "d": ["identifiers", "-legal"] } } }""", "mixes included and excluded")]
    [InlineData("""{ "tools": { "servers": { "s": { "connect": "env:X" } } }, "guardrails": { "servers": { "s": { "databases": { "d": { "scopes": [ { "object": "dbo.T", "can": ["list"], "columns": ["a", "-b"] } ] } } } } } }""", "mixes included and excluded")]
    [InlineData("""{ "wrong": {} }""", "not something")]
    [InlineData("""{ "tools": { "servers": { "s": { "connect": "env:X", "typo": {} } } } }""", "not something")]
    [InlineData("""{ "tools": { "servers": { "s": { "connect": "env:X" } } }, "guardrails": { "servers": { "s": { "databases": { "d": { "scopes": [ { "object": "dbo.T" } ] } } } } } }""", "no \"can\"")]
    [InlineData("""{ "tools": { "servers": { "s": { "connect": "env:X" } } }, "guardrails": { "servers": { "s": { "databases": { "d": { "scopes": [ { "can": ["list"] } ] } } } } } }""", "no \"object\"")]
    [InlineData("""{ "tools": { "servers": { "s": { "connect": "env:X" } } }, "guardrails": { "servers": { "s": { "databases": { "d": { "screen": true } } } } } }""", "\"screenings\"")]
    [InlineData("""{ "tools": { "servers": { "s": { "connect": "env:X", "databases": {} } } } }""", "\"guardrails\"")]
    [InlineData("""{ "tools": { "servers": { "s": { "connect": "env:X" } } }, "guardrails": { "servers": { "other": { "databases": {} } } } }""", "does not declare")]
    [InlineData("""{ "tools": { "servers": { "s": { "connect": "env:X" } } }, "screenings": { "screen": { "Sales/dbo.T": true } } }""", "guardrails level")]
    public void AnUnknownOrAmbiguousWordRefusesTheLoadNamingTheProblem(
        string json, string expected)
    {
        Result<Boundary> opened = Load(json);
        Assert.False(opened.IsOk);
        Assert.Contains(expected, opened.Failure!.Message);
    }

    /// <summary>R8.6: the flat shape is refused with its own entries under
    /// the new sections.</summary>
    [Fact]
    public void TheFlatShapeIsRefusedWithItsOwnEntriesRewritten()
    {
        Result<Boundary> opened = Load("""
            { "servers": { "s": { "connect": "env:X",
                "databases": { "Sales": { "can": ["list"], "screen": true } } } },
              "models": "models" }
            """);

        Assert.False(opened.IsOk);
        string message = opened.Failure!.Message;
        Assert.Contains("\"tools\":{\"servers\":{\"s\":{\"connect\":\"env:X\"}}}", message);
        Assert.Contains("\"guardrails\":{\"servers\":{\"s\":{\"databases\":{\"Sales\":{\"can\":[\"list\"]}}}}}", message);
        Assert.Contains("\"screenings\":{\"models\":\"models\",\"screen\":{\"Sales\":true}}", message);
    }

    [Fact]
    public void APasswordLiteralInTheCheckedInFileIsRefusedAtLoad()
    {
        Result<Boundary> opened = Load("""
            { "tools": { "servers": { "s": { "connect": "Server=db;User Id=sa;Password=hunter2" } } } }
            """);

        Assert.False(opened.IsOk);
        Assert.Contains("password", opened.Failure!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ConfigFile.LocalFileName, opened.Failure.Message);
    }

    [Fact]
    public void TheLocalFileSuppliesConnectAndModelsAndCannotWidenTheSurface()
    {
        Result<Boundary> opened = Load(
            """
            { "tools": { "servers": { "s": {} } },
              "guardrails": { "servers": { "s": { "databases": { "d": { "can": ["list"] } } } } } }
            """,
            """
            { "tools": { "servers": { "s": { "connect": "Server=db;User Id=sa;Password=local-is-fine" } } },
              "screenings": { "models": "models" } }
            """);

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal("(local file)", opened.Value.Servers[0].Connect.Display);
        Assert.EndsWith("models", opened.Value.Models!);

        // Databases declared only locally are a boundary nobody
        // reviewed, and are refused as such.
        foreach (string widening in new[]
        {
            """{ "tools": { "servers": { "s": { "databases": { "extra": { "can": ["select"] } } } } } }""",
            """{ "guardrails": { "servers": { "s": { "databases": { "extra": { "can": ["select"] } } } } } }""",
            """{ "screenings": { "screen": { "d": false } } }""",
        })
        {
            Result<Boundary> widened = Load(
                """
                { "tools": { "servers": { "s": { "connect": "env:X" } } },
                  "guardrails": { "servers": { "s": { "databases": { "d": { "can": ["list"] } } } } } }
                """,
                widening);

            Assert.False(widened.IsOk);
            Assert.Contains("may supply models and connect only", widened.Failure!.Message);
        }
    }

    [Fact]
    public void AServerWithNoConnectAnywhereIsRefusedPointingAtBothOptions()
    {
        Result<Boundary> opened = Load("""
            { "tools": { "servers": { "s": {} } } }
            """);

        Assert.False(opened.IsOk);
        Assert.Contains("env:NAME", opened.Failure!.Message);
        Assert.Contains(ConfigFile.LocalFileName, opened.Failure.Message);
    }

    [Fact]
    public void AnEnvironmentConnectResolvesAtUseNotAtLoad()
    {
        var connect = new Connect("PICKER_TEST_UNSET_VARIABLE", FromEnvironment: true);
        Result<string> resolved = connect.Resolve();
        Assert.False(resolved.IsOk);
        Assert.Contains("PICKER_TEST_UNSET_VARIABLE", resolved.Failure!.Message);
        Assert.Equal("env:PICKER_TEST_UNSET_VARIABLE", connect.Display);
    }

    // ---- R8.2: the tools level is a connection and nothing else ----

    /// <summary>A tools-only file leaves guardrails and screenings off and
    /// every server open.</summary>
    [Fact]
    public void AToolsOnlyFileOpensEveryServer()
    {
        Result<Boundary> opened = Load($$"""{ {{Tools}} }""");

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.True(opened.Value.Servers[0].Open);
        Assert.Empty(opened.Value.Servers[0].Databases);
        Assert.False(opened.Value.Levels.GuardrailsOn);
        Assert.False(opened.Value.Levels.ScreeningsOn);
    }

    /// <summary>Acceptance criterion 10: with a tools-only file, catalogs
    /// says guardrails and screenings are off, and a SELECT runs through
    /// the gate against a database the file never named.</summary>
    [Fact]
    public async Task AtTheToolsLevelASelectRunsAgainstADatabaseNobodyDeclared()
    {
        Result<Boundary> opened = Load($$"""{ {{Tools}} }""");
        Assert.True(opened.IsOk, opened.Failure?.Message);

        using var bench = new Bench(opened.Value, Fixture.Catalog());

        CatalogsAnswer catalogs = bench.Catalogs();
        Assert.False(catalogs.Levels.GuardrailsOn);
        Assert.False(catalogs.Levels.ScreeningsOn);
        Assert.Equal(["s"], catalogs.Open);

        Result<SelectAnswer> selected = await bench.SelectAsync("SELECT name FROM Customers");
        Assert.True(selected.IsOk, selected.Failure?.Message);
        Assert.Contains("[Sales].[dbo].[Customers]", selected.Value.Ran);

        // The gate is unchanged at the tools level.
        Result<SelectAnswer> system = await bench.SelectAsync("SELECT name FROM sys.objects");
        Assert.False(system.IsOk);
        Result<SelectAnswer> write = await bench.SelectAsync("DELETE FROM Customers");
        Assert.False(write.IsOk);
    }

    /// <summary>A screen at the tools level names a database, and applies
    /// when the server lists it.</summary>
    [Fact]
    public async Task AToolsLevelScreenAppliesToTheListedDatabase()
    {
        Result<Boundary> opened = Load($$"""{ {{Tools}}, "screenings": { "screen": { "Sales": ["identifiers"] } } }""");
        Assert.True(opened.IsOk, opened.Failure?.Message);

        using var bench = new Bench(opened.Value, Fixture.Catalog());
        Result<ObjectsAnswer> listed = await bench.ObjectsAsync("Customers");

        Assert.True(listed.IsOk, listed.Failure?.Message);
        Assert.Equal(Screened.Identifiers, opened.Value.OpenScreens["Sales"]);
    }
}
