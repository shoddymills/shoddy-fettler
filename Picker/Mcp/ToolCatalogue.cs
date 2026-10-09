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
        new("catalogs", "List the declared servers and databases, what each grants and screens, and which file declared them. Call this first. Opens no connection.", """
            {"type":"object","properties":{}}
            """),

        new("objects", "List tables and views matching a pattern, limited to the ones the grant allows. PATTERN is schema.object or a bare object name; * stays inside its segment.", """
            {"type":"object","properties":{
              "pattern":{"type":"string","description":"schema.object or a bare object name; * stays inside its segment; omit for everything"}
            }}
            """),

        new("select", "Run one SELECT statement. Tables and views only. Every name must resolve inside the declared databases, and every column must be allowed by the grant; * expands to the allowed columns. There is no default database and no USE: qualify names, or use a name that is unique. Rows come back as records with the column list. A truncated answer says so; narrow it with TOP, WHERE or OFFSET.", """
            {"type":"object","required":["sql"],"properties":{
              "sql":{"type":"string","description":"one SELECT statement"},
              "limit":{"type":"integer","description":"maximum rows for this call; the answer says if it was reached"}
            }}
            """),

        new("describe", "List the columns, keys and indexes of one table or view, limited by the grant. A view's definition text is never returned. The answer carries a hash, so a later call can check for changes cheaply.", """
            {"type":"object","required":["name"],"properties":{
              "name":{"type":"string","description":"the object: Customers, dbo.Customers, or Sales.dbo.Customers"}
            }}
            """),

        new("doctor", "Report whether the configuration loads, whether each declared server accepts a connection, and whether the screening pieces are installed. Changes nothing. Never prints a credential.", """
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
