using System.Reflection;
using System.Text.Json;
using Picker.Cli;
using Picker.Core;
using Picker.Mcp;
using Xunit;

namespace Picker.Tests;

/// <summary>
/// The two front ends and the constraints that are only true if
/// something checks: the reference set, the core's layering, and
/// front-end parity.
/// </summary>
public sealed class FrontEndTests
{
    /// <summary>The allowlist Picker.csproj permits, spelled out here as
    /// well as in the csproj, so adding a package means changing a test
    /// on purpose rather than watching one stop failing.</summary>
    static readonly string[] Allowed =
    [
        "Microsoft.SqlServer.TransactSql.ScriptDom",
        "Microsoft.Data.SqlClient",
    ];

    [Fact]
    public void TheShippedAssembliesReferenceNothingOutsideTheirOwnLane()
    {
        string[] ours = ["Picker", "pick"];

        foreach (string name in ours)
        {
            Assembly assembly = name == "Picker"
                ? typeof(Bench).Assembly
                : typeof(Pick.Program).Assembly;

            foreach (AssemblyName referenced in assembly.GetReferencedAssemblies())
            {
                string reference = referenced.Name ?? "";

                bool framework =
                    reference.StartsWith("System", StringComparison.Ordinal)
                    || reference.StartsWith("Microsoft.Win32.", StringComparison.Ordinal)
                    || reference is "netstandard" or "mscorlib" or "Microsoft.CSharp";

                Assert.False(reference.StartsWith("Fettler", StringComparison.Ordinal)
                    || reference == "burler",
                    $"{name} references {reference}: Picker shares this repository and "
                    + "nothing else - the wire, where there is one, is the whole contract");

                Assert.True(ours.Contains(reference) || framework
                    || Allowed.Any(a => reference.StartsWith(a, StringComparison.Ordinal)),
                    $"{name} references {reference}, which is neither Picker's own, "
                    + "nor the framework, nor on the allowlist");
            }
        }
    }

    /// <summary>The core is callable code with no protocol and no
    /// console in it: its public surface may not take or return a
    /// System.Text.Json type. Its internals may parse JSON - the
    /// configuration is JSON - but a core that SPEAKS a protocol has
    /// stopped being one.</summary>
    [Fact]
    public void TheCoreNamesNoProtocolOnItsSurface()
    {
        foreach (Type type in typeof(Bench).Assembly.GetTypes())
        {
            if (type.Namespace?.StartsWith("Picker.Core", StringComparison.Ordinal) != true)
                continue;

            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly))
            {
                Assert.False(method.ReturnType.FullName?.Contains("System.Text.Json") == true,
                    $"{type.Name}.{method.Name} answers a System.Text.Json type; the core must not");

                foreach (ParameterInfo parameter in method.GetParameters())
                    Assert.False(
                        parameter.ParameterType.FullName?.Contains("System.Text.Json") == true,
                        $"{type.Name}.{method.Name} takes a System.Text.Json type; the core must not");
            }
        }
    }

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task EveryOperationIsReachableFromBothFrontEndsAndAnswersEquivalently()
    {
        using Bench bench = Fixture.Bench();
        using var server = new McpServer(bench, TextReader.Null, TextWriter.Null);

        string? listed = await server.AnswerAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.NotNull(listed);

        string[] tools = Parse(listed!).GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();

        string[] verbs = ["catalogs", "objects", "select", "describe", "doctor"];

        foreach (string verb in verbs) Assert.Contains(verb, tools);
        foreach (string tool in tools) Assert.Contains(tool, verbs);

        // One operation answers the same thing through both doors.
        string? called = await server.AnswerAsync("""
            {"jsonrpc":"2.0","id":2,"method":"tools/call",
             "params":{"name":"objects","arguments":{"pattern":"dbo.*"}}}
            """);

        string throughMcp = Parse(called!).GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!;

        CliResult throughCli = await Command.RunAsync(
            ["objects", "dbo.*", "--json"], new StringReader(""), bench);

        Assert.Equal(throughCli.Stdout.TrimEnd(), throughMcp);
    }

    [Fact]
    public async Task AHiddenObjectIsNotARowInAnyListing()
    {
        using Bench bench = Fixture.Bench();

        CliResult listed = await Command.RunAsync(
            ["objects", "--json"], new StringReader(""), bench);
        Assert.Equal(ExitCodes.Ok, listed.ExitCode);

        JsonElement answer = Parse(listed.Stdout);
        string[] names = answer.GetProperty("objects").EnumerateArray()
            .Select(o => $"{o.GetProperty("schema").GetString()}.{o.GetProperty("name").GetString()}")
            .ToArray();

        Assert.Contains("dbo.Customers", names);
        Assert.Contains("dbo.Orders", names);
        Assert.DoesNotContain("audit.Log", names);
    }

    [Fact]
    public async Task DescribeOmitsHiddenColumnsAndCarriesAHash()
    {
        using Bench bench = Fixture.Bench();

        CliResult described = await Command.RunAsync(
            ["describe", "Sales.dbo.Customers", "--json"], new StringReader(""), bench);
        Assert.Equal(ExitCodes.Ok, described.ExitCode);

        JsonElement answer = Parse(described.Stdout);
        string[] columns = answer.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()!).ToArray();

        Assert.Contains("name", columns);
        Assert.DoesNotContain("ssn", columns);
        Assert.DoesNotContain("dob", columns);
        Assert.DoesNotContain("ssn", described.Stdout);
        Assert.NotEmpty(answer.GetProperty("hash").GetString()!);

        // A view describes as a table does: no text field exists at all.
        CliResult view = await Command.RunAsync(
            ["describe", "Sales.dbo.TopCustomers", "--json"], new StringReader(""), bench);
        Assert.Equal(ExitCodes.Ok, view.ExitCode);
        Assert.Equal("view", Parse(view.Stdout).GetProperty("type").GetString());
        Assert.DoesNotContain("definition", view.Stdout);
    }

    [Fact]
    public async Task DescribeOfAHiddenObjectAndAMissingObjectAnswerAlike()
    {
        using Bench bench = Fixture.Bench();

        CliResult hidden = await Command.RunAsync(
            ["describe", "Sales.audit.Log", "--json"], new StringReader(""), bench);
        CliResult missing = await Command.RunAsync(
            ["describe", "Sales.audit.Absent", "--json"], new StringReader(""), bench);

        Assert.Equal(ExitCodes.NotFound, hidden.ExitCode);
        Assert.Equal(ExitCodes.NotFound, missing.ExitCode);
        Assert.Equal(
            hidden.Stdout.Replace("Sales.audit.Log", "X"),
            missing.Stdout.Replace("Sales.audit.Absent", "X"));
    }

    [Fact]
    public async Task AFailureInMachineReadableModeArrivesCompleteOnStdout()
    {
        using Bench bench = Fixture.Bench();

        CliResult failed = await Command.RunAsync(
            ["select", "DELETE FROM Sales.dbo.Orders", "--json"], new StringReader(""), bench);

        Assert.Equal(ExitCodes.Refused, failed.ExitCode);
        Assert.Empty(failed.Stderr);
        Assert.NotEmpty(failed.Stdout);

        JsonElement answer = Parse(failed.Stdout);
        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal("refused", answer.GetProperty("outcome").GetString());
        Assert.NotEmpty(answer.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task AnUnknownFlagIsRefusedBeforeAnythingRuns()
    {
        using Bench bench = Fixture.Bench();

        CliResult refused = await Command.RunAsync(
            ["objects", "--gob", "dbo.*", "--json"], new StringReader(""), bench);

        Assert.Equal(ExitCodes.Invalid, refused.ExitCode);
        Assert.Contains("--gob", Parse(refused.Stdout).GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("version")]
    public async Task VersionAnswersTheVersionAndNotTheHelp(string spelling)
    {
        CliResult said = await Command.RunAsync(
            [spelling], new StringReader(""), Fixture.Bench());

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.StartsWith("pick ", said.Stdout);
        Assert.DoesNotContain("Global flags", said.Stdout);
    }

    [Fact]
    public async Task CatalogsAnswersTheDeclaredSurfaceWithoutAConnection()
    {
        // The fixture's connect points nowhere; catalogs must answer
        // anyway, because "what is declared" is a question about the
        // file, and the person asking it may be asking why the server
        // is down.
        using Bench bench = Fixture.Bench();

        CliResult listed = await Command.RunAsync(
            ["catalogs", "--json"], new StringReader(""), bench);
        Assert.Equal(ExitCodes.Ok, listed.ExitCode);

        JsonElement answer = Parse(listed.Stdout);
        JsonElement server = answer.GetProperty("servers")[0];
        Assert.Equal("test", server.GetProperty("name").GetString());

        JsonElement sales = server.GetProperty("databases")[0];
        Assert.Equal("Sales", sales.GetProperty("name").GetString());
        Assert.Equal("list select describe", sales.GetProperty("can").GetString());
    }
}
