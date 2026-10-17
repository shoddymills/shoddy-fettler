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

        new("objects", "List tables and views matching a pattern, limited to the ones the grant allows. PATTERN is schema.object or a bare object name; * stays inside its segment. An answer that stops short gives next; pass it back as after for the rest.", """
            {"type":"object","properties":{
              "pattern":{"type":"string","description":"schema.object or a bare object name; * stays inside its segment; omit for everything"},
              "after":{"type":"string","description":"the next the last answer gave"}
            }}
            """),

        new("select", "Run one SELECT statement. Tables and views only. Every name must resolve inside the declared databases, and every column must be allowed by the grant; * expands to the allowed columns. There is no default database and no USE: qualify names, or use a name that is unique. Rows come back as records with the column list. Every answer stops at 40,000 bytes. A truncated answer says so; narrow it with TOP, WHERE or OFFSET. A single row too wide for one answer is refused; select fewer columns or a substring.", """
            {"type":"object","required":["sql"],"properties":{
              "sql":{"type":"string","description":"one SELECT statement"},
              "limit":{"type":"integer","description":"maximum rows for this call; the answer says if it was reached"}
            }}
            """),

        new("describe", "List the columns, keys and indexes of one table or view, limited by the grant. A view's definition text is never returned. The answer carries a hash of the whole shape, so a later call can check for changes cheaply. An answer that stops short gives next; pass it back as after for the rest.", """
            {"type":"object","required":["name"],"properties":{
              "name":{"type":"string","description":"the object: Customers, dbo.Customers, or Sales.dbo.Customers"},
              "after":{"type":"string","description":"the next the last answer gave"}
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

            // The ceiling, said to the client as well, as Fettler says it.
            w.WriteStartObject("_meta");
            w.WriteNumber("anthropic/maxResultSizeChars", Budget.Ceiling);
            w.WriteEndObject();

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>The arguments of one call against the tool's own schema,
    /// the one the client was shown, so the two cannot disagree.</summary>
    static Failure? Check(Tool tool, JsonElement arguments)
    {
        using JsonDocument schema = JsonDocument.Parse(tool.Schema);
        JsonElement properties = schema.RootElement.TryGetProperty("properties", out JsonElement p) ? p : default;
        List<string> names = properties.ValueKind == JsonValueKind.Object
            ? [.. properties.EnumerateObject().Select(x => x.Name)]
            : [];
        string takes = names.Count == 0 ? "no arguments" : string.Join(", ", names);

        if (arguments.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            if (arguments.ValueKind != JsonValueKind.Object)
                return new Failure(Outcome.Invalid, $"{tool.Name} takes named arguments: {takes}");

            foreach (JsonProperty given in arguments.EnumerateObject())
            {
                if (!properties.TryGetProperty(given.Name, out JsonElement spec))
                    return new Failure(Outcome.Invalid,
                        $"{tool.Name} does not take '{given.Name}'. It takes: {takes}");

                string type = spec.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
                bool right = type switch
                {
                    "string" => given.Value.ValueKind == JsonValueKind.String,
                    "integer" => given.Value.ValueKind == JsonValueKind.Number && given.Value.TryGetInt64(out _),
                    _ => true,
                };
                if (!right)
                    return new Failure(Outcome.Invalid,
                        $"{tool.Name}: '{given.Name}' takes {(type == "integer" ? "a whole number, such as 50" : "text")}");
            }
        }

        if (schema.RootElement.TryGetProperty("required", out JsonElement required))
            foreach (JsonElement r in required.EnumerateArray())
            {
                string key = r.GetString()!;
                if (!(arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(key, out _)))
                    return new Failure(Outcome.Invalid, $"{tool.Name} needs '{key}'");
            }

        return null;
    }

    public static async Task<Result<string>> InvokeAsync(
        Bench bench, string name, JsonElement arguments, CancellationToken cancel,
        Action<bool>? isError = null)
    {
        Tool? known = null;
        foreach (Tool tool in Tools) if (tool.Name == name) { known = tool; break; }
        if (known is null)
            return Result<string>.Fail(Outcome.Invalid, $"no tool called '{name}'");

        // Do no harm, as Fettler: an argument the tool does not take, one
        // it needs and lacks, or one of the wrong type is refused before
        // anything reaches the server.
        if (Check(known, arguments) is { } wrong)
        {
            isError?.Invoke(true);
            return Result<string>.Ok(Command.Refuse(wrong).Stdout.TrimEnd());
        }

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
                Flag("after", "after");
                break;
            case "select":
                Positional("sql");
                Flag("limit", "limit");
                break;
            case "describe":
                Positional("name");
                Flag("after", "after");
                break;
        }

        CliResult result = await Command
            .RunAsync(argv, new StringReader(stdin), bench, cancel).ConfigureAwait(false);

        // The complete machine-readable answer, failures included, is
        // already on stdout - so the tool result is that text VERBATIM
        // whichever way it went, and the server marks isError when the
        // operation did not do what was asked.
        isError?.Invoke(result.Failed);
        return Result<string>.Ok(result.Stdout.TrimEnd());
    }
}
