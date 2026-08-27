namespace Picker.Core;

/// <summary>
/// What may be done with an object, as a closed set of three.
///
/// <para><b>Closed is the point</b>, exactly as Fettler's Permission
/// documents it: a verb set that grows a member per operation is one
/// nobody can hold in mind, and one where adding an operation quietly
/// adds a hole. These three are named after what happens to the DATA.
/// There is no execute, no stored-procedure verb and no write verb - not
/// "not yet", but refused by the shape of the set, so adding one later
/// is a visible design act.</para>
///
/// <para><b>Absent <see cref="List"/> means hidden</b>, not merely
/// unlistable: an object in a scope that cannot be listed is refused in
/// the same words as an object that does not exist, so the difference
/// cannot be probed.</para>
/// </summary>
[Flags]
public enum Verb
{
    /// <summary>Nothing - which is what a database declared without
    /// <c>can</c> grants. Unlike a filesystem tree, whose default is
    /// read-only, a database may hold ten thousand objects nobody has
    /// looked at, so every verb here is opt-in.</summary>
    None = 0,

    /// <summary>The object appears in listings and messages. Absent
    /// means it does not exist as far as any caller can tell.</summary>
    List = 1 << 0,

    /// <summary>Rows may be read - tables and views only.</summary>
    Select = 1 << 1,

    /// <summary>DDL may be read - and for a view that means its columns
    /// generated from the catalog, never its stored text.</summary>
    Describe = 1 << 2,
}

/// <summary>
/// Reading and writing a verb set, in the Permissions idiom.
/// </summary>
public static class Verbs
{
    /// <summary>Every verb, in the order they are written and read, so a
    /// listing never depends on enum ordering by accident.</summary>
    public static readonly Verb[] All = [Verb.List, Verb.Select, Verb.Describe];

    public static string NameOf(Verb one) => one switch
    {
        Verb.List => "list",
        Verb.Select => "select",
        Verb.Describe => "describe",
        _ => "none",
    };

    /// <summary>A verb set as words, in the fixed order above. An empty
    /// set reads as <c>nothing</c> rather than as an empty string, so a
    /// report never has a blank where an answer goes.</summary>
    public static string Write(Verb can)
    {
        var names = new List<string>();
        foreach (Verb one in All)
            if (can.HasFlag(one)) names.Add(NameOf(one));
        return names.Count == 0 ? "nothing" : string.Join(' ', names);
    }

    /// <summary>
    /// Parse the words a configuration file uses, refusing anything not
    /// in the set rather than ignoring it - the Permissions.Parse
    /// precedent: a misspelt <c>"selct"</c> silently WITHHOLDS a verb,
    /// so the failure arrives much later as a refusal nobody can
    /// explain, pointing at a file that plainly grants it.
    /// </summary>
    public static Result<Verb> Parse(IEnumerable<string> words)
    {
        Verb can = Verb.None;

        foreach (string word in words)
        {
            bool known = false;
            foreach (Verb one in All)
                if (word.Equals(NameOf(one), StringComparison.OrdinalIgnoreCase))
                {
                    can |= one;
                    known = true;
                    break;
                }

            if (!known)
                return Result<Verb>.Fail(Outcome.Invalid,
                    $"'{word}' is not a verb; they are: "
                    + string.Join(", ", All.Select(NameOf)));
        }

        return Result<Verb>.Ok(can);
    }
}
