using System.Text.Json;
using Picker.Cli;
using Picker.Core;

namespace Picker.Mcp;

/// <summary>
/// The tools a model sees, and the one place they are turned into work.
///
/// <para><b>The two front ends share every operation, and this is how
/// that is kept true rather than promised</b>: a tool call is marshalled
/// into the same argument vector the command line takes and handed to
/// the same dispatcher, in machine-readable mode. The MCP answer is
/// byte-identical to what <c>pick VERB --json</c> prints, so a
/// capability available to a model and not to a script is impossible to
/// write rather than merely forbidden. The Fettler arrangement,
/// kept whole.</para>
/// </summary>
public static class ToolCatalogue
{
    sealed record Tool(string Name, string Summary, string Schema);

    static readonly Tool[] Tools =
    [
        new("catalogs", "The declared servers and databases, what each grants and screens, and where the configuration came from. Ask this first, rather than discovering the boundary by being refused. No connection is opened to answer it.", """
            {"type":"object","properties":{}}
            """),

        new("objects", "Tables and views matching a pattern, filtered by the grant - an object outside it is not a row here and not a count either. PATTERN is schema.object or a bare object name, with * staying inside its segment.", """
            {"type":"object","properties":{
              "pattern":{"type":"string","description":"schema.object or a bare object name; * stays inside its segment; omit for everything"}
            }}
            """),

        new("select", "Run ONE SELECT statement through the gate: tables and views only, every name resolved inside the declared databases, columns proven against the grant, * expanded to the columns that exist here. There is no default database and no USE - qualify names, or let a unique name resolve. Rows come back as records with the column list stated; a truncated answer says so, and TOP/WHERE/OFFSET are the remedy.", """
            {"type":"object","required":["sql"],"properties":{
              "sql":{"type":"string","description":"one SELECT statement; anything else is refused with the reason"},
              "limit":{"type":"integer","description":"row cap for this call; the answer says when it bites"}
            }}
            """),

        new("describe", "Columns, keys and indexes for one table or view, generated from the catalog and filtered by the grant. A view describes exactly as a table does - its stored definition text is never served, under any grant. The answer carries a hash so a later call can prove 'unchanged' cheaply.", """
            {"type":"object","required":["name"],"properties":{
              "name":{"type":"string","description":"the object: Customers, dbo.Customers, or Sales.dbo.Customers"}
            }}
            """),

        new("doctor", "Whether the configuration loads, whether each declared server answers a bounded connection attempt, and whether the screening pieces are in place. Diagnoses; never changes anything; never prints a credential.", """
            {"type":"object","properties":{}}
            """),
    ];

    public static void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("tools");

        foreach (Tool tool in Tools)
        {
            w.WriteStartObject();
            w.WriteString("name", tool.Name);
            w.WriteString("description", tool.Summary);
            w.WritePropertyName("inputSchema");
            using JsonDocument schema = JsonDocument.Parse(tool.Schema);
            schema.RootElement.WriteTo(w);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static async Task<Result<string>> InvokeAsync(
        Bench bench, string name, JsonElement arguments, CancellationToken cancel,
        Action<bool>? isError = null)
    {
        bool known = false;
        foreach (Tool tool in Tools) if (tool.Name == name) { known = true; break; }
        if (!known)
            return Result<string>.Fail(Outcome.Invalid, $"no tool called '{name}'");

        var argv = new List<string> { name, "--json" };
        string stdin = string.Empty;

        void Positional(string key)
        {
            if (arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty(key, out JsonElement v)
                && v.ValueKind == JsonValueKind.String)
                argv.Add(v.GetString()!);
        }

        void Flag(string flag, string key)
        {
            if (arguments.ValueKind != JsonValueKind.Object) return;
            if (!arguments.TryGetProperty(key, out JsonElement v)) return;
            if (v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                argv.Add($"--{flag}");
                argv.Add(v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString());
            }
        }

        switch (name)
        {
            case "objects":
                Positional("pattern");
                break;
            case "select":
                Positional("sql");
                Flag("limit", "limit");
                break;
            case "describe":
                Positional("name");
                break;
        }

        CliResult result = await Command
            .RunAsync(argv, new StringReader(stdin), bench, cancel).ConfigureAwait(false);

        // The complete machine-readable answer, failures included, is
        // already on stdout - so the tool result is that text VERBATIM
        // whichever way it went, and the server marks isError from the
        // exit code.
        isError?.Invoke(result.ExitCode != ExitCodes.Ok);
        return Result<string>.Ok(result.Stdout.TrimEnd());
    }
}
