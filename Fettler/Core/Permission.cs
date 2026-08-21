namespace Fettler.Core;

/// <summary>
/// What may be done inside a tree, as a closed set.
///
/// <para><b>Closed is the point.</b> A permission model that grows a verb
/// per operation is one nobody can hold in mind, and one where adding an
/// operation quietly adds a hole. These seven cover every verb Fettler
/// has and every verb it is likely to grow, because they are named after
/// what happens to the FILE rather than after what the caller typed.</para>
///
/// <para><b>Absent <see cref="List"/> means hidden</b>, not merely
/// unlistable: a path in a scope that cannot be listed is refused in the
/// same words as a path outside every tree, so the difference between
/// "hidden" and "not there" cannot be probed.</para>
///
/// <para><b><see cref="Execute"/> is never a default</b>, in any tree,
/// including the one the configuration sits in. See
/// <see cref="RuleFiles"/> for why: a tree that is writable and
/// executable is a tree where the caller writes the task file and then
/// runs it, which defeats every other permission at once.</para>
/// </summary>
[Flags]
public enum Permission
{
    None = 0,

    /// <summary>Appears in <c>find</c> and <c>search</c>. Absent means
    /// the path does not exist as far as any caller can tell.</summary>
    List = 1 << 0,

    /// <summary>Content may be read.</summary>
    Read = 1 << 1,

    /// <summary>A file or directory that is not there may be made.</summary>
    Create = 1 << 2,

    /// <summary>An existing file's content may be changed.</summary>
    Update = 1 << 3,

    /// <summary>May be renamed, or moved within this scope or into a
    /// descendant of it.</summary>
    Rename = 1 << 4,

    /// <summary>May be removed - and moved OUT of this scope, which from
    /// here is the same thing.</summary>
    Delete = 1 << 5,

    /// <summary>A declared task may run with this as its working
    /// directory.</summary>
    Execute = 1 << 6,
}

/// <summary>
/// Reading and writing a permission set, and the two defaults.
/// </summary>
public static class Permissions
{
    /// <summary>What every tree other than the one the configuration sits
    /// in gets unless a configuration file says otherwise, and the most
    /// a command line can ever grant.</summary>
    public const Permission ReadOnly = Permission.List | Permission.Read;

    /// <summary>What the tree whose path is <c>.</c> gets by default.
    /// <b>Execute is not in it</b> and is not an oversight.</summary>
    public const Permission Full =
        Permission.List | Permission.Read | Permission.Create
        | Permission.Update | Permission.Rename | Permission.Delete;

    /// <summary>Every permission, in the order they are written and read,
    /// so a listing never depends on enum ordering by accident.</summary>
    public static readonly Permission[] All =
    [
        Permission.List, Permission.Read, Permission.Create,
        Permission.Update, Permission.Rename, Permission.Delete, Permission.Execute,
    ];

    public static string NameOf(Permission one) => one switch
    {
        Permission.List => "list",
        Permission.Read => "read",
        Permission.Create => "create",
        Permission.Update => "update",
        Permission.Rename => "rename",
        Permission.Delete => "delete",
        Permission.Execute => "execute",
        _ => "none",
    };

    /// <summary>A permission set as words, in the fixed order above.
    /// An empty set reads as <c>nothing</c> rather than as an empty
    /// string, so a report never has a blank where an answer goes.</summary>
    public static string Write(Permission can)
    {
        var names = new List<string>();
        foreach (Permission one in All)
            if (can.HasFlag(one)) names.Add(NameOf(one));
        return names.Count == 0 ? "nothing" : string.Join(' ', names);
    }

    /// <summary>
    /// Parse the words a configuration file uses, refusing anything not
    /// in the set rather than ignoring it.
    ///
    /// <para>Ignoring an unrecognised word is the same fault as ignoring
    /// an unknown flag, and worse here: a misspelt <c>"delte"</c> silently
    /// WITHHOLDS a permission, so the failure arrives much later as a
    /// refusal nobody can explain, pointing at a file that plainly grants
    /// it.</para>
    /// </summary>
    public static Result<Permission> Parse(IEnumerable<string> words)
    {
        Permission can = Permission.None;

        foreach (string word in words)
        {
            bool known = false;
            foreach (Permission one in All)
                if (word.Equals(NameOf(one), StringComparison.OrdinalIgnoreCase))
                {
                    can |= one;
                    known = true;
                    break;
                }

            if (!known)
                return Result<Permission>.Fail(Outcome.Invalid,
                    $"'{word}' is not a permission; they are: "
                    + string.Join(", ", All.Select(NameOf)));
        }

        return Result<Permission>.Ok(can);
    }
}

/// <summary>
/// One scope: a path, what may be done at or below it, and what is
/// screened on the way out of it.
///
/// <para><b><see cref="Screen"/> is nullable and <see cref="Can"/> is
/// not, and the difference is deliberate.</b> A scope must state its
/// permissions, because saying nothing about them would be the one
/// ambiguity the permission model refuses to have. A scope that says
/// nothing about SCREENING inherits the tree's, because screening was
/// added to a configuration format that already had scopes in it - every
/// scope written before it exists says nothing, and reading that silence
/// as "screen nothing here" would punch a hole in the first tree anybody
/// switched the screen on for.</para>
/// </summary>
public sealed record Scope(string Path, Permission Can, Screened? Screen = null);

/// <summary>
/// The permissions of one tree, and the scopes that narrow or widen them
/// at depth.
///
/// <para><b>The most specific scope containing a path decides, and it
/// REPLACES rather than adds.</b> Replacing is what makes a scope able to
/// take a permission away - "everything under requirements, except
/// cancelled, which is not there at all" - and a merging rule cannot
/// express that at any depth.</para>
///
/// <para>Pure: this compares strings and consults no filesystem, which is
/// what lets the whole matrix be tested without a disk.</para>
/// </summary>
public sealed class Grant
{
    readonly Scope[] scopes;

    /// <param name="can">What the tree grants where no scope applies.</param>
    /// <param name="scopes">Scopes, with ABSOLUTE paths. Order does not
    /// matter; the longest containing path wins.</param>
    public Grant(Permission can, IReadOnlyList<Scope>? scopes = null,
                 Screened screen = Screened.None)
    {
        Can = can;
        Screen = screen;
        this.scopes = scopes is null ? [] : [.. scopes];
    }

    /// <summary>What the tree grants outside every scope.</summary>
    public Permission Can { get; }

    /// <summary>What is screened out of the tree where no scope says
    /// otherwise. <see cref="Screened.None"/> - nothing screened - is
    /// what a tree that does not mention it gets.</summary>
    public Screened Screen { get; }

    public IReadOnlyList<Scope> Scopes => scopes;

    /// <summary>
    /// What may be done at one path, and the path of whatever decided it.
    ///
    /// <para>The governing path comes back beside the permission because
    /// a move has to know whether it is LEAVING the thing that granted
    /// the permission: going out of a scope needs <c>delete</c> even when
    /// the destination is inside the same tree.</para>
    /// </summary>
    public (Permission Can, Screened Screen, string Governing) At(string treePath, string fullPath)
    {
        Permission can = Can;
        Screened screen = Screen;
        string governing = treePath;

        foreach (Scope scope in scopes)
            if (Roots.IsWithin(scope.Path, fullPath) && scope.Path.Length > governing.Length)
            {
                can = scope.Can;

                // Falls back to the TREE's screen rather than to whatever
                // the last scope considered said. Only the longest match
                // survives the loop, so the answer is the winning scope's
                // own statement, or the tree's where it made none.
                screen = scope.Screen ?? Screen;
                governing = scope.Path;
            }

        return (can, screen, governing);
    }
}
