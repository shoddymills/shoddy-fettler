using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Picker.Core;

/// <summary>
/// Reading <c>.picker.json</c> and <c>.picker.local.json</c>: the files
/// that say what this tool may do.
///
/// <para><b>Three sections, one per level, the same words Fettler
/// uses.</b> <c>tools</c> holds the servers and their <c>connect</c>.
/// <c>guardrails</c> holds the databases on each server, their verbs,
/// scopes and column rules. <c>screenings</c> holds the models directory
/// and the screens. A file with only <c>tools</c> is the tools level: a
/// connection and nothing else declared, so every database the login can
/// reach is there, through the gate unchanged.</para>
///
/// <para><b>This tool does not write them - a person edits them</b>, and
/// serve re-reads both before every call so an edit binds on the next
/// request.</para>
///
/// <para><b>The two files divide by what belongs in a diff.</b> The
/// checked-in file declares the surface and is reviewed like the boundary
/// it is. The local, gitignored file holds what never belongs in a diff:
/// the models directory, which is per-machine, and connection strings,
/// which carry credentials. It may supply those and nothing else. A
/// password literal in the CHECKED-IN file is refused at load.</para>
///
/// <para><b>An unknown word anywhere refuses the load</b>, naming the
/// valid set - a misspelt key would otherwise silently withhold a grant
/// or a screen.</para>
/// </summary>
public static class ConfigFile
{
    public const string FileName = ".picker.json";
    public const string LocalFileName = ".picker.local.json";

    static readonly Regex Credential = new(
        @"(?i)\b(password|pwd)\s*=", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Find the configuration upward from a directory, the way the tree
    /// file is found: the nearest <c>.picker.json</c> governs.
    /// </summary>
    public static Result<Boundary> Find(string startDirectory)
    {
        string? dir = Path.GetFullPath(startDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, FileName);
            if (File.Exists(candidate)) return Open(candidate);
            dir = Path.GetDirectoryName(dir);
        }

        return Result<Boundary>.Fail(Outcome.Invalid, NothingDeclared);
    }

    public const string NothingDeclared =
        "no " + FileName + " found at or above the current directory. A person writes "
        + "that file; pick does not. The smallest one is "
        + "{\"tools\":{\"servers\":{\"main\":{\"connect\":\"env:MAIN_SQL\"}}}}";

    /// <summary>
    /// Open one configuration file, merging its local twin when one sits
    /// beside it.
    /// </summary>
    public static Result<Boundary> Open(string path)
    {
        string full = Path.GetFullPath(path);

        Result<Parsed> main = Parse(full, isLocal: false);
        if (!main.IsOk) return main.Carry<Boundary>();

        string localPath = Path.Combine(Path.GetDirectoryName(full)!, LocalFileName);
        Parsed? local = null;
        if (File.Exists(localPath))
        {
            Result<Parsed> read = Parse(localPath, isLocal: true);
            if (!read.IsOk) return read.Carry<Boundary>();
            local = read.Value;
        }

        return Merge(main.Value, local, full);
    }

    // ---- the shapes one file parses into ----

    sealed record Parsed(string Path)
    {
        public bool Tools { get; init; }
        public IReadOnlyList<(string Name, string? Connect)> Servers { get; init; } = [];

        public bool Guardrails { get; init; }
        public IReadOnlyList<(string Server, IReadOnlyList<DatabaseDecl> Databases)> Guarded { get; init; } = [];

        public bool Screenings { get; init; }
        public string? Models { get; init; }
        public IReadOnlyList<(string Key, Screened Screen)> Screens { get; init; } = [];
    }

    /// <summary>
    /// Merge the checked-in surface with the local supplements.
    ///
    /// <para><b>The local file cannot widen the surface.</b> It may name
    /// the models directory and supply <c>connect</c> for servers the
    /// main file declares - and nothing else.</para>
    ///
    /// <para><b>Guardrails on means today's surface</b>: the databases a
    /// server declares are the only ones there. <b>Guardrails off</b>
    /// means each server is open: its databases are the ones the login
    /// can reach, found when a call needs them.</para>
    /// </summary>
    static Result<Boundary> Merge(Parsed main, Parsed? local, string origin)
    {
        var servers = new List<ServerDecl>();

        foreach ((string name, string? declaredConnect) in main.Servers)
        {
            string? connect = declaredConnect;
            bool fromLocal = false;

            if (local is not null)
                foreach ((string supplied, string? localConnect) in local.Servers)
                    if (supplied.Equals(name, StringComparison.OrdinalIgnoreCase) && localConnect is not null)
                    {
                        connect = localConnect;
                        fromLocal = true;
                    }

            if (connect is null)
                return Result<Boundary>.Fail(Outcome.Invalid,
                    $"server '{name}' has no \"connect\". Name an environment "
                    + $"variable as env:NAME, or put the connection string in {LocalFileName}",
                    main.Path);

            Result<Connect> made = MakeConnect(connect, fromLocal, name, main.Path);
            if (!made.IsOk) return made.Carry<Boundary>();

            IReadOnlyList<DatabaseDecl> databases = [];
            foreach ((string server, IReadOnlyList<DatabaseDecl> declared) in main.Guarded)
                if (server.Equals(name, StringComparison.OrdinalIgnoreCase)) databases = declared;

            servers.Add(new ServerDecl(name, made.Value, databases, Open: !main.Guardrails));
        }

        foreach ((string server, _) in main.Guarded)
            if (!main.Servers.Any(s => s.Name.Equals(server, StringComparison.OrdinalIgnoreCase)))
                return Result<Boundary>.Fail(Outcome.Invalid,
                    $"\"guardrails\" declares databases on '{server}', which \"tools\" does not declare. "
                    + "Declare the server under \"tools\" with its \"connect\"", main.Path);

        if (local is not null)
            foreach ((string supplied, _) in local.Servers)
                if (!main.Servers.Any(s => s.Name.Equals(supplied, StringComparison.OrdinalIgnoreCase)))
                    return Result<Boundary>.Fail(Outcome.Invalid,
                        $"{LocalFileName} names a server '{supplied}' that {FileName} does not "
                        + "declare. Declare servers in " + FileName,
                        local.Path);

        if (servers.Count == 0)
            return Result<Boundary>.Fail(Outcome.Invalid,
                $"{FileName} declares no servers; there is nothing to open. Declare one under \"tools\"",
                main.Path);

        // The models directory is per-machine - usually local - and is
        // resolved against the file that named it.
        string? models = local?.Models ?? main.Models;
        if (models is not null)
        {
            string against = local?.Models is not null ? local.Path : main.Path;
            models = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(against)!, models));
        }

        var openScreens = new Dictionary<string, Screened>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, Screened screen) in main.Screens)
        {
            Result<bool> applied = main.Guardrails
                ? ApplyScreen(servers, key, screen, main.Path)
                : OpenScreen(openScreens, key, screen, main.Path);
            if (!applied.IsOk) return applied.Carry<Boundary>();
        }

        var tools = new List<string>();
        var guardrails = new List<string>();
        var screenings = new List<string>();
        Parsed[] parts = local is null ? [main] : [main, local];
        foreach (Parsed part in parts)
        {
            if (part.Tools) tools.Add(part.Path);
            if (part.Guardrails) guardrails.Add(part.Path);
            if (part.Screenings) screenings.Add(part.Path);
        }

        return Result<Boundary>.Ok(new Boundary(servers, models, origin)
        {
            Levels = new Levels(tools, guardrails, screenings),
            OpenScreens = openScreens,
        });
    }

    /// <summary>
    /// Put one <c>screen</c> entry on the database or scope it names. The
    /// key is a database name, or a database name, a slash, and a scope's
    /// object pattern as the scope writes it. A screen naming nothing
    /// would screen nothing while saying it screens something.
    /// </summary>
    static Result<bool> ApplyScreen(List<ServerDecl> servers, string key, Screened screen, string path)
    {
        int slash = key.IndexOf('/');
        string database = slash < 0 ? key : key[..slash];
        string? scope = slash < 0 ? null : key[(slash + 1)..];
        bool found = false;

        for (int s = 0; s < servers.Count; s++)
        {
            var databases = new List<DatabaseDecl>(servers[s].Databases);
            for (int d = 0; d < databases.Count; d++)
            {
                if (!databases[d].Name.Equals(database, StringComparison.OrdinalIgnoreCase)) continue;

                if (scope is null)
                {
                    databases[d] = databases[d] with { Screen = screen };
                    found = true;
                    continue;
                }

                var scopes = new List<ScopeDecl>(databases[d].Scopes);
                int at = scopes.FindIndex(x => x.Pattern.Typed.Equals(scope, StringComparison.OrdinalIgnoreCase));
                if (at < 0)
                    return Result<bool>.Fail(Outcome.Invalid,
                        $"has a \"screen\" for '{key}', and database '{database}' declares no scope "
                        + $"'{scope}'. Declare the scope under \"guardrails\", or screen the whole database", path);

                scopes[at] = scopes[at] with { Screen = screen };
                databases[d] = databases[d] with { Scopes = scopes };
                found = true;
            }

            servers[s] = servers[s] with { Databases = databases };
        }

        return found
            ? Result<bool>.Ok(true)
            : Result<bool>.Fail(Outcome.Invalid,
                $"has a \"screen\" for '{database}', which no server declares under \"guardrails\"", path);
    }

    /// <summary>At the tools level there are no scopes, so a screen names
    /// a database, and applies to it when the server lists it.</summary>
    static Result<bool> OpenScreen(Dictionary<string, Screened> into, string key, Screened screen, string path)
    {
        if (key.Contains('/'))
            return Result<bool>.Fail(Outcome.Invalid,
                $"has a \"screen\" for '{key}', which names a scope. Scopes belong to the guardrails "
                + "level, which is off. Screen the whole database, or declare the scope under \"guardrails\"", path);

        into[key] = screen;
        return Result<bool>.Ok(true);
    }

    static Result<Connect> MakeConnect(string value, bool fromLocal, string server, string mainPath)
    {
        if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            string name = value[4..];
            return name.Length == 0
                ? Result<Connect>.Fail(Outcome.Invalid,
                    $"server '{server}' has \"connect\": \"env:\" with no variable name after it",
                    mainPath)
                : Result<Connect>.Ok(new Connect(name, FromEnvironment: true));
        }

        // A literal is fine where it is gitignored. In the checked-in
        // file a literal carrying a credential is refused - not moved,
        // not warned about, refused - because the reviewed file is the
        // one that ends up in history and in every clone.
        if (!fromLocal && Credential.IsMatch(value))
            return Result<Connect>.Fail(Outcome.Invalid,
                $"server '{server}' has a \"connect\" with a password in {FileName}, "
                + $"which is checked in. Put the connection string in {LocalFileName}, or "
                + "name an environment variable as env:NAME", mainPath);

        return Result<Connect>.Ok(new Connect(value, FromEnvironment: false));
    }

    // ---- one file ----

    static Result<Parsed> Parse(string path, bool isLocal)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<Parsed>.Fail(Outcome.Invalid,
                $"{Path.GetFileName(path)} could not be read: {e.Message}", path);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException e)
        {
            return Result<Parsed>.Fail(Outcome.Invalid,
                $"{Path.GetFileName(path)} is not JSON: {e.Message}", path);
        }

        using (doc)
        {
            string file = Path.GetFileName(path);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Result<Parsed>.Fail(Outcome.Invalid,
                    $"{file} is not a JSON object", path);

            // The flat shape of the last release, refused with its own
            // entries rewritten, so the fix is to paste the message.
            foreach (string old in new[] { "servers", "models" })
                if (doc.RootElement.TryGetProperty(old, out _))
                    return Result<Parsed>.Fail(Outcome.Invalid, Rewrite(doc.RootElement, old, file), path);

            var parsed = new Parsed(path);

            foreach (JsonProperty top in doc.RootElement.EnumerateObject())
            {
                switch (top.Name)
                {
                    case Levels.ToolsName:
                    {
                        Result<Parsed> read = ReadTools(top.Value, path, isLocal, parsed);
                        if (!read.IsOk) return read;
                        parsed = read.Value;
                        break;
                    }

                    case Levels.GuardrailsName:
                    {
                        if (isLocal)
                            return Result<Parsed>.Fail(Outcome.Invalid, LocalOnly, path);
                        Result<Parsed> read = ReadGuardrails(top.Value, path, parsed);
                        if (!read.IsOk) return read;
                        parsed = read.Value;
                        break;
                    }

                    case Levels.ScreeningsName:
                    {
                        Result<Parsed> read = ReadScreenings(top.Value, path, isLocal, parsed);
                        if (!read.IsOk) return read;
                        parsed = read.Value;
                        break;
                    }

                    default:
                        return Result<Parsed>.Fail(Outcome.Invalid,
                            $"'{top.Name}' is not something {file} declares; it takes three sections, one "
                            + "per level: tools, guardrails, screenings", path);
                }
            }

            return Result<Parsed>.Ok(parsed);
        }
    }

    const string LocalOnly =
        LocalFileName + " may supply models and connect only. Declare servers, databases and "
        + "screens in " + FileName;

    static Result<Parsed> ReadTools(JsonElement value, string path, bool isLocal, Parsed parsed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return Result<Parsed>.Fail(Outcome.Invalid, "\"tools\" is not an object; it takes: servers", path);

        var servers = new List<(string, string?)>();
        foreach (JsonProperty entry in value.EnumerateObject())
        {
            if (entry.Name != "servers")
                return Result<Parsed>.Fail(Outcome.Invalid,
                    $"'{entry.Name}' is not something \"tools\" declares; it takes: servers", path);

            if (entry.Value.ValueKind != JsonValueKind.Object)
                return Result<Parsed>.Fail(Outcome.Invalid,
                    "\"servers\" under \"tools\" is not an object", path);

            foreach (JsonProperty server in entry.Value.EnumerateObject())
            {
                if (server.Value.ValueKind != JsonValueKind.Object)
                    return Result<Parsed>.Fail(Outcome.Invalid,
                        $"server '{server.Name}' is not an object in {Path.GetFileName(path)}", path);

                string? connect = null;
                foreach (JsonProperty field in server.Value.EnumerateObject())
                {
                    if (field.Name == "databases")
                        return Result<Parsed>.Fail(Outcome.Invalid, isLocal
                            ? $"{LocalFileName} declares databases on '{server.Name}'. {LocalOnly}"
                            : $"server '{server.Name}' declares \"databases\" under \"tools\". Databases go "
                              + "under \"guardrails\", as {\"guardrails\":{\"servers\":{\""
                              + server.Name + "\":{\"databases\":{...}}}}}", path);

                    if (field.Name != "connect")
                        return Result<Parsed>.Fail(Outcome.Invalid,
                            $"'{field.Name}' is not something a server declares under \"tools\"; it takes: connect",
                            path);

                    if (field.Value.ValueKind != JsonValueKind.String
                        || field.Value.GetString() is not { Length: > 0 } text)
                        return Result<Parsed>.Fail(Outcome.Invalid,
                            $"server '{server.Name}' has a \"connect\" that is not a non-empty string", path);
                    connect = text;
                }

                servers.Add((server.Name, connect));
            }
        }

        return Result<Parsed>.Ok(parsed with { Tools = true, Servers = servers });
    }

    static Result<Parsed> ReadGuardrails(JsonElement value, string path, Parsed parsed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return Result<Parsed>.Fail(Outcome.Invalid, "\"guardrails\" is not an object; it takes: servers", path);

        var guarded = new List<(string, IReadOnlyList<DatabaseDecl>)>();
        foreach (JsonProperty entry in value.EnumerateObject())
        {
            if (entry.Name != "servers" || entry.Value.ValueKind != JsonValueKind.Object)
                return Result<Parsed>.Fail(Outcome.Invalid,
                    $"'{entry.Name}' is not something \"guardrails\" declares; it takes: servers", path);

            foreach (JsonProperty server in entry.Value.EnumerateObject())
            {
                if (server.Value.ValueKind != JsonValueKind.Object)
                    return Result<Parsed>.Fail(Outcome.Invalid,
                        $"server '{server.Name}' under \"guardrails\" is not an object", path);

                var databases = new List<DatabaseDecl>();
                foreach (JsonProperty field in server.Value.EnumerateObject())
                {
                    if (field.Name == "connect")
                        return Result<Parsed>.Fail(Outcome.Invalid,
                            $"server '{server.Name}' has a \"connect\" under \"guardrails\". A connection "
                            + "is the tools level: put it under \"tools\"", path);

                    if (field.Name != "databases")
                        return Result<Parsed>.Fail(Outcome.Invalid,
                            $"'{field.Name}' is not something a server declares under \"guardrails\"; it "
                            + "takes: databases", path);

                    if (field.Value.ValueKind != JsonValueKind.Object)
                        return Result<Parsed>.Fail(Outcome.Invalid,
                            $"server '{server.Name}' has a \"databases\" that is not an object", path);

                    foreach (JsonProperty database in field.Value.EnumerateObject())
                    {
                        Result<DatabaseDecl> one = ParseDatabase(database, server.Name, path);
                        if (!one.IsOk) return one.Carry<Parsed>();
                        databases.Add(one.Value);
                    }
                }

                guarded.Add((server.Name, databases));
            }
        }

        return Result<Parsed>.Ok(parsed with { Guardrails = true, Guarded = guarded });
    }

    static Result<Parsed> ReadScreenings(JsonElement value, string path, bool isLocal, Parsed parsed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return Result<Parsed>.Fail(Outcome.Invalid,
                "\"screenings\" is not an object; it takes: models, screen", path);

        string? models = null;
        var screens = new List<(string, Screened)>();

        foreach (JsonProperty entry in value.EnumerateObject())
        {
            switch (entry.Name)
            {
                case "models":
                    if (entry.Value.ValueKind != JsonValueKind.String
                        || entry.Value.GetString() is not { Length: > 0 } dir)
                        return Result<Parsed>.Fail(Outcome.Invalid,
                            $"\"models\" is not a non-empty string in {Path.GetFileName(path)}; "
                            + "it names the directory holding the screening models", path);
                    models = dir;
                    break;

                case "screen":
                    if (isLocal)
                        return Result<Parsed>.Fail(Outcome.Invalid, LocalOnly, path);

                    if (entry.Value.ValueKind != JsonValueKind.Object)
                        return Result<Parsed>.Fail(Outcome.Invalid,
                            "\"screen\" under \"screenings\" is not an object. Each key is a database "
                            + "name, or a database name, a slash and a scope's object pattern", path);

                    foreach (JsonProperty one in entry.Value.EnumerateObject())
                    {
                        if (one.Name.Length == 0 || one.Name.StartsWith('/'))
                            return Result<Parsed>.Fail(Outcome.Invalid,
                                "a \"screen\" key names no database. Write the database name first", path);

                        Result<Screened> read = ReadScreen(one.Value, $"the \"screen\" for '{one.Name}'", path);
                        if (!read.IsOk) return read.Carry<Parsed>();
                        screens.Add((one.Name, read.Value));
                    }
                    break;

                default:
                    return Result<Parsed>.Fail(Outcome.Invalid,
                        $"'{entry.Name}' is not something \"screenings\" declares; it takes: models, screen", path);
            }
        }

        return Result<Parsed>.Ok(parsed with { Screenings = true, Models = models, Screens = screens });
    }

    /// <summary>
    /// The refusal for the flat shape, holding the file's own entries
    /// under the new sections: <c>connect</c> under <c>tools</c>, the
    /// databases under <c>guardrails</c>, and <c>models</c> and every
    /// <c>screen</c> under <c>screenings</c>.
    /// </summary>
    static string Rewrite(JsonElement root, string found, string file)
    {
        JsonObject old = JsonNode.Parse(root.GetRawText())!.AsObject();

        var tools = new JsonObject();
        var guarded = new JsonObject();
        var screen = new JsonObject();

        if (old["servers"] is JsonObject servers)
            foreach (KeyValuePair<string, JsonNode?> server in servers)
            {
                if (server.Value is not JsonObject entry) continue;

                var connection = new JsonObject();
                if (entry["connect"] is { } connect) connection["connect"] = connect.DeepClone();
                tools[server.Key] = connection;

                if (entry["databases"] is not JsonObject databases) continue;

                var moved = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> database in databases)
                {
                    JsonNode? copy = database.Value?.DeepClone();
                    if (copy is JsonObject db)
                    {
                        if (db.Remove("screen", out JsonNode? s)) screen[database.Key] = s;

                        if (db["scopes"] is JsonArray scopes)
                            foreach (JsonNode? scope in scopes)
                                if (scope is JsonObject declared
                                    && declared["object"]?.GetValue<string>() is { } pattern
                                    && declared.Remove("screen", out JsonNode? ss))
                                    screen[$"{database.Key}/{pattern}"] = ss;
                    }
                    moved[database.Key] = copy;
                }
                guarded[server.Key] = new JsonObject { ["databases"] = moved };
            }

        var rewritten = new JsonObject();
        if (tools.Count > 0) rewritten[Levels.ToolsName] = new JsonObject { ["servers"] = tools };
        if (guarded.Count > 0) rewritten[Levels.GuardrailsName] = new JsonObject { ["servers"] = guarded };

        var screenings = new JsonObject();
        if (old["models"] is { } models) screenings["models"] = models.DeepClone();
        if (screen.Count > 0) screenings["screen"] = screen;
        if (screenings.Count > 0) rewritten[Levels.ScreeningsName] = screenings;

        string json = rewritten.ToJsonString(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        return $"{file} has \"{found}\" at the top, which is the shape this version no longer reads. "
            + "Rewrite it with three sections, one per level. Your entries, rewritten: " + json;
    }


    static Result<DatabaseDecl> ParseDatabase(JsonProperty database, string server, string path)
    {
        if (database.Value.ValueKind != JsonValueKind.Object)
            return Result<DatabaseDecl>.Fail(Outcome.Invalid,
                $"database '{database.Name}' on '{server}' is not an object", path);

        Verb can = Verb.None;
        Screened screen = Screened.None;
        var scopes = new List<ScopeDecl>();

        foreach (JsonProperty entry in database.Value.EnumerateObject())
        {
            switch (entry.Name)
            {
                case "can":
                {
                    Result<Verb> parsed = ReadVerbs(entry.Value,
                        $"database '{database.Name}'", path);
                    if (!parsed.IsOk) return parsed.Carry<DatabaseDecl>();
                    can = parsed.Value;
                    break;
                }

                case "screen":
                    return Result<DatabaseDecl>.Fail(Outcome.Invalid,
                        $"database '{database.Name}' has a \"screen\". A screen now goes under "
                        + $"\"screenings\", keyed by the database: {{\"screenings\":{{\"screen\":{{\"{database.Name}\":true}}}}}}",
                        path);

                case "scopes":
                {
                    if (entry.Value.ValueKind != JsonValueKind.Array)
                        return Result<DatabaseDecl>.Fail(Outcome.Invalid,
                            $"database '{database.Name}' has a \"scopes\" that is not an array", path);
                    foreach (JsonElement one in entry.Value.EnumerateArray())
                    {
                        Result<ScopeDecl> scope = ParseScope(one, database.Name, path);
                        if (!scope.IsOk) return scope.Carry<DatabaseDecl>();
                        scopes.Add(scope.Value);
                    }
                    break;
                }

                default:
                    return Result<DatabaseDecl>.Fail(Outcome.Invalid,
                        $"'{entry.Name}' is not something a database declares; it takes: "
                        + "can, scopes", path);
            }
        }

        return Result<DatabaseDecl>.Ok(new DatabaseDecl(database.Name, can, screen, scopes));
    }

    static Result<ScopeDecl> ParseScope(JsonElement scope, string database, string path)
    {
        if (scope.ValueKind != JsonValueKind.Object)
            return Result<ScopeDecl>.Fail(Outcome.Invalid,
                $"a scope in database '{database}' is not an object", path);

        ObjectPattern? pattern = null;
        Verb? can = null;
        ColumnRule columns = ColumnRule.All;
        Screened? screened = null;

        foreach (JsonProperty entry in scope.EnumerateObject())
        {
            switch (entry.Name)
            {
                case "object":
                {
                    if (entry.Value.ValueKind != JsonValueKind.String
                        || entry.Value.GetString() is not { Length: > 0 } typed)
                        return Result<ScopeDecl>.Fail(Outcome.Invalid,
                            $"a scope in database '{database}' has an \"object\" that is not "
                            + "a non-empty string", path);
                    Result<ObjectPattern> parsed = ObjectPattern.Parse(typed);
                    if (!parsed.IsOk) return parsed.Carry<ScopeDecl>();
                    pattern = parsed.Value;
                    break;
                }

                case "can":
                {
                    Result<Verb> parsed = ReadVerbs(entry.Value,
                        $"a scope in database '{database}'", path);
                    if (!parsed.IsOk) return parsed.Carry<ScopeDecl>();
                    can = parsed.Value;
                    break;
                }

                case "columns":
                {
                    if (entry.Value.ValueKind == JsonValueKind.True) break;
                    if (entry.Value.ValueKind != JsonValueKind.Array)
                        return Result<ScopeDecl>.Fail(Outcome.Invalid,
                            $"a scope in database '{database}' has a \"columns\" that is not "
                            + "true or an array of column words", path);
                    Result<IReadOnlyList<string>> words = ReadWords(entry.Value,
                        $"a scope in database '{database}'", "columns", path);
                    if (!words.IsOk) return words.Carry<ScopeDecl>();
                    Result<ColumnRule> rule = ColumnRule.Parse(words.Value);
                    if (!rule.IsOk)
                        return Result<ScopeDecl>.Fail(rule.Failure!.Outcome,
                            $"a scope in database '{database}' {rule.Failure.Message}", path);
                    columns = rule.Value;
                    break;
                }

                case "screen":
                    return Result<ScopeDecl>.Fail(Outcome.Invalid,
                        $"a scope in database '{database}' has a \"screen\". A screen now goes under "
                        + "\"screenings\", keyed by the database, a slash and the scope's object pattern",
                        path);

                default:
                    return Result<ScopeDecl>.Fail(Outcome.Invalid,
                        $"'{entry.Name}' is not something a scope declares; it takes: "
                        + "object, can, columns", path);
            }
        }

        if (pattern is null)
            return Result<ScopeDecl>.Fail(Outcome.Invalid,
                $"a scope in database '{database}' has no \"object\"", path);

        // A scope must state its verbs - saying nothing about them would
        // be the one ambiguity the model refuses to have. Screening is
        // different: silence inherits (see ScopeDecl).
        if (can is null)
            return Result<ScopeDecl>.Fail(Outcome.Invalid,
                $"scope '{pattern.Typed}' in database '{database}' has no \"can\". Write the "
                + "verbs it allows; [] allows nothing", path);

        return Result<ScopeDecl>.Ok(new ScopeDecl(pattern, can.Value, columns, screened));
    }

    static Result<Verb> ReadVerbs(JsonElement value, string what, string path)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return Result<Verb>.Fail(Outcome.Invalid,
                $"{what} has a \"can\" that is not an array of verb words; they are: "
                + string.Join(", ", Verbs.All.Select(Verbs.NameOf)), path);

        Result<IReadOnlyList<string>> words = ReadWords(value, what, "can", path);
        if (!words.IsOk) return words.Carry<Verb>();

        Result<Verb> parsed = Verbs.Parse(words.Value);
        return parsed.IsOk
            ? parsed
            : Result<Verb>.Fail(parsed.Failure!.Outcome, $"{what}: {parsed.Failure.Message}", path);
    }

    static Result<Screened> ReadScreen(JsonElement value, string what, string path)
    {
        if (value.ValueKind == JsonValueKind.True)
            return Result<Screened>.Ok(Screens.Everything);

        if (value.ValueKind == JsonValueKind.False)
            return Result<Screened>.Ok(Screened.None);

        if (value.ValueKind != JsonValueKind.Array)
            return Result<Screened>.Fail(Outcome.Invalid,
                $"{what} has a \"screen\" that is not true, false, or an array of category "
                + $"words; they are: {string.Join(", ", Screens.All.Select(Screens.NameOf))}", path);

        Result<IReadOnlyList<string>> words = ReadWords(value, what, "screen", path);
        if (!words.IsOk) return words.Carry<Screened>();

        Result<Screened> parsed = Screens.Parse(words.Value);
        return parsed.IsOk
            ? parsed
            : Result<Screened>.Fail(parsed.Failure!.Outcome, $"{what}: {parsed.Failure.Message}", path);
    }

    static Result<IReadOnlyList<string>> ReadWords(
        JsonElement array, string what, string key, string path)
    {
        var words = new List<string>();
        foreach (JsonElement one in array.EnumerateArray())
        {
            if (one.ValueKind != JsonValueKind.String)
                return Result<IReadOnlyList<string>>.Fail(Outcome.Invalid,
                    $"{what} has a \"{key}\" entry that is not a word", path);
            words.Add(one.GetString()!);
        }
        return Result<IReadOnlyList<string>>.Ok(words);
    }
}
