using System.Text;

namespace Picker.Core;

/// <summary>
/// An object name as a caller typed it: up to three parts, because the
/// fourth is refused before anything else happens.
///
/// <para><b>There is no default database and no <c>USE</c>.</b> Fettler
/// has no working directory because a current directory is how a path
/// walks somewhere nobody intended; a session database is the same
/// hole. A one- or two-part name resolves only when exactly one
/// declared database contains a match, and a three-part name must name
/// a declared database - refused, when it does not, in the same words
/// as a table that does not exist, so the difference cannot be
/// probed.</para>
/// </summary>
public sealed record ObjectName(string? Database, string? Schema, string Name, string Typed)
{
    public override string ToString() => Typed;
}

/// <summary>
/// Parsing the names callers type: bracketed or bare, dot-separated,
/// at most three parts.
/// </summary>
public static class Names
{
    /// <summary>
    /// Split a typed name into its parts, honouring <c>[brackets]</c>.
    ///
    /// <para><b>A four-part name is refused here</b>, before resolution,
    /// because a linked server is a route off the declared surface - the
    /// exact analogue of a symlink out of a tree. The refusal is its own
    /// stated reason rather than not-found, because nothing on the
    /// declared surface is being probed by it.</para>
    /// </summary>
    public static Result<ObjectName> Parse(string typed)
    {
        Result<IReadOnlyList<string>> split = Split(typed);
        if (!split.IsOk) return split.Carry<ObjectName>();

        IReadOnlyList<string> parts = split.Value;

        return parts.Count switch
        {
            0 => Result<ObjectName>.Fail(Outcome.Invalid, "an empty name names nothing"),
            1 => Result<ObjectName>.Ok(new ObjectName(null, null, parts[0], typed)),
            2 => Result<ObjectName>.Ok(new ObjectName(null, parts[0], parts[1], typed)),
            3 => Result<ObjectName>.Ok(new ObjectName(parts[0], parts[1], parts[2], typed)),
            _ => Result<ObjectName>.Fail(Outcome.Refused,
                $"'{typed}' has four parts. A linked server is not reachable here", typed),
        };
    }

    /// <summary>Dot-split honouring brackets. An empty part - a doubled
    /// dot, a leading dot - is refused rather than guessed at.</summary>
    static Result<IReadOnlyList<string>> Split(string typed)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool inBrackets = false, hadBrackets = false;

        for (int i = 0; i < typed.Length; i++)
        {
            char c = typed[i];

            if (inBrackets)
            {
                // ]] inside brackets is an escaped ]; a lone ] closes.
                if (c == ']')
                {
                    if (i + 1 < typed.Length && typed[i + 1] == ']') { current.Append(']'); i++; }
                    else inBrackets = false;
                    continue;
                }
                current.Append(c);
                continue;
            }

            switch (c)
            {
                case '[':
                    inBrackets = true;
                    hadBrackets = true;
                    continue;
                case '.':
                    if (current.Length == 0 && !hadBrackets)
                        return Result<IReadOnlyList<string>>.Fail(Outcome.Invalid,
                            $"'{typed}' has an empty name part", typed);
                    parts.Add(current.ToString());
                    current.Clear();
                    hadBrackets = false;
                    continue;
                default:
                    current.Append(c);
                    continue;
            }
        }

        if (inBrackets)
            return Result<IReadOnlyList<string>>.Fail(Outcome.Invalid,
                $"'{typed}' opens a bracket it never closes", typed);

        if (current.Length == 0 && !hadBrackets)
            return Result<IReadOnlyList<string>>.Fail(Outcome.Invalid,
                $"'{typed}' has an empty name part", typed);

        parts.Add(current.ToString());
        return Result<IReadOnlyList<string>>.Ok(parts);
    }

    /// <summary>A name quoted the way T-SQL wants it back: bracketed,
    /// with any <c>]</c> doubled. Every name Picker writes into a query
    /// goes through here, so a creative object name cannot smuggle
    /// syntax.</summary>
    public static string Quote(string part) => "[" + part.Replace("]", "]]") + "]";

    /// <summary>The one wording for an object that is not there - and
    /// for an object that is hidden, which from the caller's side is the
    /// same fact. One factory rather than two call sites, so the two
    /// cases cannot drift into distinguishable sentences.</summary>
    public static Failure NotThere(string typed) => new(Outcome.NotFound,
        $"there is no table or view called '{typed}' in the declared databases. "
        + "Run objects to list what is here", typed);

    /// <summary>The one wording for a column that is not there - and for
    /// a column that is hidden. Same rule, same reason.</summary>
    public static Failure NoSuchColumn(string column, string onObject) => new(Outcome.NotFound,
        $"'{column}' is not a column of {onObject}. Run describe to see its columns", column);

    /// <summary>The unqualified twin: when a bare name resolves to
    /// nothing a caller may see, the refusal names NO object - naming
    /// one would let the choice of object be compared across probes,
    /// which is the leak the shared wording exists to close.</summary>
    public static Failure NoColumnAnywhere(string column) => new(Outcome.NotFound,
        $"'{column}' is not a column of any object in this query's FROM. Run describe "
        + "on them to see their columns", column);

    /// <summary>The system catalog's own refusal. sys and
    /// INFORMATION_SCHEMA would list every object on the database,
    /// declared or hidden - metadata IS disclosure here, and the
    /// sanctioned route answers it under the grant.</summary>
    public static Failure SystemCatalog(string typed) => new(Outcome.Refused,
        $"'{typed}' is the system catalog, which is not queryable here. "
        + "Use objects and describe instead", typed);

    /// <summary>True when a schema name reaches for the system catalog.</summary>
    public static bool IsSystemSchema(string? schema) =>
        schema is not null
        && (schema.Equals("sys", StringComparison.OrdinalIgnoreCase)
            || schema.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase));
}
