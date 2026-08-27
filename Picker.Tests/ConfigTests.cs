using Picker.Core;
using Xunit;

namespace Picker.Tests;

/// <summary>The two files: what they declare, what they refuse, and the
/// line between them.</summary>
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
              "servers": {
                "reporting": {
                  "connect": "env:REPORTING_SQL",
                  "databases": {
                    "Sales": {
                      "can": ["list", "select", "describe"],
                      "screen": ["identifiers"],
                      "scopes": [
                        { "object": "dbo.Customers",
                          "can": ["list", "select"],
                          "columns": ["-ssn", "-dob"] },
                        { "object": "ref.*",
                          "can": ["list", "select", "describe"],
                          "screen": false },
                        { "object": "audit.*", "can": [] }
                      ]
                    }
                  }
                }
              }
            }
            """);

        Assert.True(opened.IsOk, opened.Failure?.Message);
        ServerDecl server = Assert.Single(opened.Value.Servers);
        Assert.Equal("env:REPORTING_SQL", server.Connect.Display);

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
            { "servers": { "s": { "connect": "env:X",
              "databases": { "Sales": {} } } } }
            """);

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal(Verb.None, opened.Value.Servers[0].Databases[0].Can);
    }

    [Theory]
    [InlineData("""{ "servers": { "s": { "connect": "env:X", "databases": { "d": { "can": ["selct"] } } } } }""", "not a verb")]
    [InlineData("""{ "servers": { "s": { "connect": "env:X", "databases": { "d": { "screen": ["lgal"] } } } } }""", "not a screening category")]
    [InlineData("""{ "servers": { "s": { "connect": "env:X", "databases": { "d": { "screen": ["identifiers", "-legal"] } } } } }""", "cannot be combined")]
    [InlineData("""{ "servers": { "s": { "connect": "env:X", "databases": { "d": { "scopes": [ { "object": "dbo.T", "can": ["list"], "columns": ["a", "-b"] } ] } } } } }""", "cannot be combined")]
    [InlineData("""{ "wrong": {} }""", "not something")]
    [InlineData("""{ "servers": { "s": { "connect": "env:X", "typo": {} } } }""", "not something")]
    [InlineData("""{ "servers": { "s": { "connect": "env:X", "databases": { "d": { "scopes": [ { "object": "dbo.T" } ] } } } } }""", "no \"can\"")]
    [InlineData("""{ "servers": { "s": { "connect": "env:X", "databases": { "d": { "scopes": [ { "can": ["list"] } ] } } } } }""", "no \"object\"")]
    public void AnUnknownOrAmbiguousWordRefusesTheLoadNamingTheProblem(
        string json, string expected)
    {
        Result<Boundary> opened = Load(json);
        Assert.False(opened.IsOk);
        Assert.Contains(expected, opened.Failure!.Message);
    }

    [Fact]
    public void APasswordLiteralInTheCheckedInFileIsRefusedAtLoad()
    {
        Result<Boundary> opened = Load("""
            { "servers": { "s": {
              "connect": "Server=db;User Id=sa;Password=hunter2",
              "databases": { "d": {} } } } }
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
            { "servers": { "s": { "databases": { "d": { "can": ["list"] } } } } }
            """,
            """
            { "servers": { "s": { "connect": "Server=db;User Id=sa;Password=local-is-fine" } },
              "models": "models" }
            """);

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal("(local file)", opened.Value.Servers[0].Connect.Display);
        Assert.EndsWith("models", opened.Value.Models!);

        // Databases declared only locally are a boundary nobody
        // reviewed, and are refused as such.
        Result<Boundary> widened = Load(
            """
            { "servers": { "s": { "connect": "env:X", "databases": { "d": { "can": ["list"] } } } } }
            """,
            """
            { "servers": { "s": { "databases": { "extra": { "can": ["select"] } } } } }
            """);

        Assert.False(widened.IsOk);
        Assert.Contains("cannot widen", widened.Failure!.Message);
    }

    [Fact]
    public void AServerWithNoConnectAnywhereIsRefusedPointingAtBothOptions()
    {
        Result<Boundary> opened = Load("""
            { "servers": { "s": { "databases": { "d": {} } } } }
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
}
