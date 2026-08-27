namespace Picker.Core;

/// <summary>
/// Which columns of an object exist for this caller.
///
/// <para>The grammar is the screen's grammar, on purpose - one shape
/// fewer to learn: <c>true</c> (or silence) means every column, a bare
/// list includes, a <c>-</c> list excludes, and mixing the two in one
/// list is refused as ambiguous.</para>
///
/// <para><b>A hidden column does not exist</b> - not in a select list,
/// not in a WHERE, not inside an expression, not in DESCRIBE's answer.
/// Filtering output alone was rejected in the requirements because
/// <c>WHERE ssn = '...'</c> discloses through behaviour without ever
/// returning the column.</para>
/// </summary>
public sealed class ColumnRule
{
    readonly HashSet<string>? include;
    readonly HashSet<string>? exclude;

    ColumnRule(HashSet<string>? include, HashSet<string>? exclude)
    {
        this.include = include;
        this.exclude = exclude;
    }

    /// <summary>Every column exists - what an object nobody scoped gets.</summary>
    public static readonly ColumnRule All = new(null, null);

    /// <summary>True when no column is being hidden.</summary>
    public bool Everything => include is null && exclude is null;

    /// <summary>Whether this column exists for the caller.</summary>
    public bool Allows(string column) =>
        include is not null ? include.Contains(column)
        : exclude is null || !exclude.Contains(column);

    /// <summary>The configured words, for reports. Empty when everything
    /// is allowed.</summary>
    public IReadOnlyList<string> Words =>
        include is not null ? [.. include.Order(StringComparer.OrdinalIgnoreCase)]
        : exclude is not null ? [.. exclude.Order(StringComparer.OrdinalIgnoreCase).Select(c => "-" + c)]
        : [];

    /// <summary>The screen-list forms: a bare list includes, a
    /// <c>-</c> list excludes, mixing refused. Column names cannot be
    /// verified against the object here - the catalog is not in the room
    /// when a configuration loads - so a misspelt column is caught at
    /// first resolution instead, and reported against the file.</summary>
    public static Result<ColumnRule> Parse(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return Result<ColumnRule>.Fail(Outcome.Invalid,
                "an empty \"columns\" list would hide every column; leave it out to allow "
                + "them all, or name the columns to include");

        bool anyBare = false, anyMinus = false;
        foreach (string raw in words)
        {
            if (raw.StartsWith('-')) anyMinus = true;
            else anyBare = true;
        }

        if (anyBare && anyMinus)
            return Result<ColumnRule>.Fail(Outcome.Invalid,
                "mixes included and excluded columns in one list, and the two cannot be "
                + "combined: write [\"name\", \"city\"] for only those to exist, or "
                + "[\"-ssn\"] for everything except that");

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in words)
        {
            string word = raw.StartsWith('-') ? raw[1..] : raw;
            if (word.Length == 0)
                return Result<ColumnRule>.Fail(Outcome.Invalid,
                    "a column word is empty");
            set.Add(word);
        }

        return Result<ColumnRule>.Ok(anyMinus ? new ColumnRule(null, set) : new ColumnRule(set, null));
    }
}

/// <summary>
/// One scope: an object pattern, what may be done with what it matches,
/// which columns exist, and what is screened on the way out.
///
/// <para><b><see cref="Screen"/> is nullable and <see cref="Can"/> is
/// not, for Fettler's exact reason.</b> A scope must state its verbs; a
/// scope that says nothing about SCREENING inherits the database's,
/// because reading silence as "screen nothing here" would punch a hole
/// in the first database anybody switched the screen on for. Writing
/// <c>"screen": false</c> is how a scope takes screening away.</para>
/// </summary>
public sealed record ScopeDecl(
    ObjectPattern Pattern, Verb Can, ColumnRule Columns, Screened? Screen);

/// <summary>
/// A two-part object pattern: <c>schema.object</c>, with <c>*</c>
/// staying inside its segment - the glob rule, applied to a catalog.
/// </summary>
public sealed class ObjectPattern
{
    readonly string schema;
    readonly string name;

    ObjectPattern(string schema, string name, string typed)
    {
        this.schema = schema;
        this.name = name;
        Typed = typed;
    }

    public string Typed { get; }

    /// <summary>How many of the two segments are exact. The most
    /// specific matching scope decides, and this is the score: an exact
    /// object beats a wildcard one, and an exact schema breaks the
    /// tie.</summary>
    public int Specificity =>
        (name.Contains('*') ? 0 : 2) + (schema.Contains('*') ? 0 : 1);

    public static Result<ObjectPattern> Parse(string typed)
    {
        string[] parts = typed.Split('.');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            return Result<ObjectPattern>.Fail(Outcome.Invalid,
                $"'{typed}' is not an object pattern; write schema.object, with * "
                + "staying inside its segment - dbo.Customers, audit.*");

        return Result<ObjectPattern>.Ok(new ObjectPattern(parts[0], parts[1], typed));
    }

    public bool Matches(string objectSchema, string objectName) =>
        Segment(schema, objectSchema) && Segment(name, objectName);

    /// <summary>One segment against one word: <c>*</c> matches any run,
    /// everything else matches itself, case-insensitively - the same
    /// semantics the file glob gives a path segment.</summary>
    static bool Segment(string pattern, string word)
    {
        return At(0, 0);

        bool At(int p, int w)
        {
            while (true)
            {
                if (p == pattern.Length) return w == word.Length;
                if (pattern[p] == '*')
                {
                    for (int skip = word.Length; skip >= w; skip--)
                        if (At(p + 1, skip)) return true;
                    return false;
                }
                if (w == word.Length) return false;
                if (char.ToUpperInvariant(pattern[p]) != char.ToUpperInvariant(word[w])) return false;
                p++; w++;
            }
        }
    }

    public override string ToString() => Typed;
}

/// <summary>
/// One declared database: what it grants, what it screens, and the
/// scopes that narrow or widen that at depth.
/// </summary>
public sealed record DatabaseDecl(
    string Name, Verb Can, Screened Screen, IReadOnlyList<ScopeDecl> Scopes)
{
    /// <summary>
    /// What may be done with one object, and everything else the
    /// governing declaration says about it.
    ///
    /// <para><b>The most specific matching scope decides, and it
    /// REPLACES rather than adds</b> - replacing is what lets a scope
    /// take a verb away. Specificity is the pattern's exact-segment
    /// score; a tie goes to the scope declared first, so the answer
    /// never depends on dictionary order.</para>
    /// </summary>
    public (Verb Can, Screened Screen, ColumnRule Columns, string Governing) At(
        string schema, string name)
    {
        Verb can = Can;
        Screened screen = Screen;
        ColumnRule columns = ColumnRule.All;
        string governing = Name;
        int best = -1;

        foreach (ScopeDecl scope in Scopes)
        {
            if (!scope.Pattern.Matches(schema, name)) continue;
            if (scope.Pattern.Specificity <= best) continue;

            best = scope.Pattern.Specificity;
            can = scope.Can;
            columns = scope.Columns;

            // The database's screen rather than whatever the last scope
            // considered said: only the winning scope's own statement
            // survives, or the database's where it made none.
            screen = scope.Screen ?? Screen;
            governing = $"{Name} scope {scope.Pattern.Typed}";
        }

        return (can, screen, columns, governing);
    }
}

/// <summary>
/// One declared server: a name, the way to connect to it, and the
/// databases declared on it. Everything else the server holds does not
/// exist.
/// </summary>
public sealed record ServerDecl(
    string Name, Connect Connect, IReadOnlyList<DatabaseDecl> Databases);

/// <summary>
/// How a connection string is obtained - and the one place credential
/// hygiene is enforced.
///
/// <para><b>A checked-in literal carrying a password is refused at
/// load.</b> The connect value in the shared file names an environment
/// variable (<c>env:NAME</c>) or is supplied by the local, gitignored
/// file; a password in the reviewed file is the Secrets argument applied
/// to this tool's own configuration.</para>
/// </summary>
public sealed record Connect(string Value, bool FromEnvironment)
{
    /// <summary>The connection string, resolved now. Environment
    /// variables are read at connect time rather than load time, so a
    /// rotated credential binds without a restart.</summary>
    public Result<string> Resolve()
    {
        if (!FromEnvironment) return Result<string>.Ok(Value);

        string? found = Environment.GetEnvironmentVariable(Value);
        return found is { Length: > 0 }
            ? Result<string>.Ok(found)
            : Result<string>.Fail(Outcome.Invalid,
                $"the environment variable '{Value}' named by \"connect\" is not set; "
                + "set it to the connection string, credentials included", Value);
    }

    /// <summary>Never the connection string, whatever it holds - for
    /// reports and doctor lines.</summary>
    public string Display => FromEnvironment ? $"env:{Value}" : "(local file)";
}

/// <summary>
/// The whole declared surface: the servers, and where the screening
/// models sit on this machine.
/// </summary>
public sealed record Boundary(
    IReadOnlyList<ServerDecl> Servers, string? Models, string Origin)
{
    /// <summary>Every declared database, with its server - the flat list
    /// resolution searches.</summary>
    public IEnumerable<(ServerDecl Server, DatabaseDecl Database)> Databases()
    {
        foreach (ServerDecl server in Servers)
            foreach (DatabaseDecl database in server.Databases)
                yield return (server, database);
    }
}
