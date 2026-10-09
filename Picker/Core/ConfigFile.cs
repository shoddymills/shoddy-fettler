using System.Text.Json;
using System.Text.RegularExpressions;

namespace Picker.Core;

/// <summary>
/// Reading <c>.picker.json</c> and <c>.picker.local.json</c>: the files
/// that say what this tool may do.
///
/// <para><b>This tool does not write them - a person edits them</b>, and
/// serve re-reads both before every call so an edit binds on the next
/// request. The RuleFiles discipline, unchanged.</para>
///
/// <para><b>The two files divide by what belongs in a diff.</b> The
/// checked-in file declares the surface - servers, databases, verbs,
/// scopes, columns, screens - and is reviewed like the boundary it is.
/// The local, gitignored file holds what never belongs in a diff: the
/// models directory, which is per-machine, and connection strings,
/// which carry credentials. A password literal in the CHECKED-IN file
/// is refused at load - the Secrets argument, applied to this tool's
/// own configuration.</para>
///
/// <para><b>An unknown word anywhere refuses the load</b>, naming the
/// valid set - a misspelt key would otherwise silently withhold a grant
/// or a screen, and the failure would arrive much later as a refusal
/// nobody can explain.</para>
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
        + "that file; pick does not";

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

    sealed record Parsed(
        string Path,
        IReadOnlyList<ParsedServer> Servers,
        string? Models);

    sealed record ParsedServer(
        string Name, string? Connect, IReadOnlyList<DatabaseDecl> Databases);

    /// <summary>
    /// Merge the checked-in surface with the local supplements.
    ///
    /// <para><b>The local file cannot widen the surface.</b> It may name
    /// the models directory and supply <c>connect</c> for servers the
    /// main file declares - and nothing else. A database declared only
    /// locally would be a boundary nobody reviewed.</para>
    /// </summary>
    static Result<Boundary> Merge(Parsed main, Parsed? local, string origin)
    {
        var servers = new List<ServerDecl>();

        foreach (ParsedServer declared in main.Servers)
        {
            string? connect = declared.Connect;
            bool fromLocal = false;

            if (local is not null)
                foreach (ParsedServer supplement in local.Servers)
                    if (supplement.Name.Equals(declared.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (supplement.Databases.Count > 0)
                            return Result<Boundary>.Fail(Outcome.Invalid,
                                $"{LocalFileName} declares databases on '{declared.Name}'. The local "
                                + "file may supply models and connect only. Declare databases in "
                                + $"{FileName}",
                                local.Path);

                        if (supplement.Connect is { } supplied)
                        {
                            connect = supplied;
                            fromLocal = true;
                        }
                    }

            if (connect is null)
                return Result<Boundary>.Fail(Outcome.Invalid,
                    $"server '{declared.Name}' has no \"connect\". Name an environment "
                    + $"variable as env:NAME, or put the connection string in {LocalFileName}",
                    main.Path);

            Result<Connect> made = MakeConnect(connect, fromLocal, declared.Name, main.Path);
            if (!made.IsOk) return made.Carry<Boundary>();

            servers.Add(new ServerDecl(declared.Name, made.Value, declared.Databases));
        }

        if (local is not null)
            foreach (ParsedServer supplement in local.Servers)
                if (!main.Servers.Any(s => s.Name.Equals(supplement.Name, StringComparison.OrdinalIgnoreCase)))
                    return Result<Boundary>.Fail(Outcome.Invalid,
                        $"{LocalFileName} names a server '{supplement.Name}' that {FileName} does not "
                        + "declare. Declare servers in " + FileName,
                        local.Path);

        // The models directory is per-machine - usually local - and is
        // resolved against the file that named it.
        string? models = local?.Models ?? main.Models;
        if (models is not null)
        {
            string against = local?.Models is not null ? local.Path : main.Path;
            models = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(against)!, models));
        }

        if (servers.Count == 0)
            return Result<Boundary>.Fail(Outcome.Invalid,
                $"{FileName} declares no servers; there is nothing to open", main.Path);

        return Result<Boundary>.Ok(new Boundary(servers, models, origin));
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
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Result<Parsed>.Fail(Outcome.Invalid,
                    $"{Path.GetFileName(path)} is not a JSON object", path);

            var servers = new List<ParsedServer>();
            string? models = null;

            foreach (JsonProperty top in doc.RootElement.EnumerateObject())
            {
                switch (top.Name)
                {
                    case "servers":
                        if (top.Value.ValueKind != JsonValueKind.Object)
                            return Result<Parsed>.Fail(Outcome.Invalid,
                                $"\"servers\" is not an object in {Path.GetFileName(path)}", path);
                        foreach (JsonProperty server in top.Value.EnumerateObject())
                        {
                            Result<ParsedServer> parsed = ParseServer(server, path, isLocal);
                            if (!parsed.IsOk) return parsed.Carry<Parsed>();
                            servers.Add(parsed.Value);
                        }
                        break;

                    case "models":
                        if (top.Value.ValueKind != JsonValueKind.String
                            || top.Value.GetString() is not { Length: > 0 } dir)
                            return Result<Parsed>.Fail(Outcome.Invalid,
                                $"\"models\" is not a non-empty string in {Path.GetFileName(path)}; "
                                + "it names the directory holding the screening models", path);
                        models = dir;
                        break;

                    default:
                        return Result<Parsed>.Fail(Outcome.Invalid,
                            $"'{top.Name}' is not something {Path.GetFileName(path)} declares; "
                            + "it takes: servers, models", path);
                }
            }

            return Result<Parsed>.Ok(new Parsed(path, servers, models));
        }
    }

    static Result<ParsedServer> ParseServer(JsonProperty server, string path, bool isLocal)
    {
        if (server.Value.ValueKind != JsonValueKind.Object)
            return Result<ParsedServer>.Fail(Outcome.Invalid,
                $"server '{server.Name}' is not an object in {Path.GetFileName(path)}", path);

        string? connect = null;
        var databases = new List<DatabaseDecl>();

        foreach (JsonProperty entry in server.Value.EnumerateObject())
        {
            switch (entry.Name)
            {
                case "connect":
                    if (entry.Value.ValueKind != JsonValueKind.String
                        || entry.Value.GetString() is not { Length: > 0 } value)
                        return Result<ParsedServer>.Fail(Outcome.Invalid,
                            $"server '{server.Name}' has a \"connect\" that is not a non-empty string",
                            path);
                    connect = value;
                    break;

                case "databases":
                    if (entry.Value.ValueKind != JsonValueKind.Object)
                        return Result<ParsedServer>.Fail(Outcome.Invalid,
                            $"server '{server.Name}' has a \"databases\" that is not an object", path);
                    foreach (JsonProperty database in entry.Value.EnumerateObject())
                    {
                        Result<DatabaseDecl> parsed = ParseDatabase(database, server.Name, path);
                        if (!parsed.IsOk) return parsed.Carry<ParsedServer>();
                        databases.Add(parsed.Value);
                    }
                    break;

                default:
                    return Result<ParsedServer>.Fail(Outcome.Invalid,
                        $"'{entry.Name}' is not something a server declares; it takes: "
                        + "connect, databases", path);
            }
        }

        return Result<ParsedServer>.Ok(new ParsedServer(server.Name, connect, databases));
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
                {
                    Result<Screened> parsed = ReadScreen(entry.Value,
                        $"database '{database.Name}'", path);
                    if (!parsed.IsOk) return parsed.Carry<DatabaseDecl>();
                    screen = parsed.Value;
                    break;
                }

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
                        + "can, screen, scopes", path);
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
                {
                    Result<Screened> parsed = ReadScreen(entry.Value,
                        $"a scope in database '{database}'", path);
                    if (!parsed.IsOk) return parsed.Carry<ScopeDecl>();
                    screened = parsed.Value;
                    break;
                }

                default:
                    return Result<ScopeDecl>.Fail(Outcome.Invalid,
                        $"'{entry.Name}' is not something a scope declares; it takes: "
                        + "object, can, columns, screen", path);
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
