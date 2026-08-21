using System.Text;

namespace Fettler.Core;

/// <summary>One scope inside a tree, as declared: a path relative to the
/// tree, what may be done at or below it, and what is screened out of
/// it. A null <see cref="Screen"/> is a scope that said nothing and
/// inherits the tree's - see <see cref="Scope"/> for why silence cannot
/// mean "screen nothing here".</summary>
public sealed record ScopeDecl(string Relative, Permission Can, Screened? Screen = null);

/// <summary>
/// One tree as the caller declared it, before it is opened.
///
/// <para><b>A tree, not a repository.</b> A folder with no version
/// control in it is the ordinary case rather than an exception, and
/// neither the configuration, the default names nor the documentation
/// assumes otherwise.</para>
/// </summary>
public sealed record TreeDecl(
    string Name,
    string Path,
    Permission Can,
    IReadOnlyList<ScopeDecl>? Scopes = null,
    Screened Screen = Screened.None);

/// <summary>
/// A path that has been proved to lie inside a declared tree, and that
/// carries what may be done there. Nothing in the core accepts a bare
/// string for a path: an operation takes one of these, which is how
/// R8.2's "checked for every operation" is kept by the type system rather
/// than by remembering.
/// </summary>
public sealed record ContainedPath(
    string RootName,
    string Full,
    string Relative,
    Permission Can,
    string Governing,
    Screened Screen = Screened.None)
{
    /// <summary>How the path is written back to a caller. R9.2 requires
    /// one separator form on output whatever arrived on input, and
    /// forward slash is the one both platforms read.</summary>
    public string Display => Relative.Replace('\\', '/');

    /// <summary>The qualified form, used whenever more than one tree is
    /// open so a caller can hand the answer straight back (R4.9).</summary>
    public string Qualified => $"{RootName}:{Display}";
}

/// <summary>
/// The boundary of R8: one or more named trees, what may be done in each,
/// and the rule that every path lies inside one of them.
///
/// <para><b>Containment is decided by walking down from the volume
/// (R8.1), not by inspecting the deepest component.</b> Given
/// <c>root/a/b</c> where <c>a</c> is a link out of the tree, <c>b</c> is
/// not itself a link and looks perfectly contained; only resolving
/// <c>a</c> on the way past catches it. Every existing component is
/// resolved to its final target and re-checked, and that includes
/// Windows junctions, which are a second kind of reparse point that
/// build tooling plants routinely (R6.11).</para>
///
/// <para><b>Permission is decided at the same moment as containment</b>,
/// and by the same call. A verb asks to resolve a path FOR something -
/// to read it, to update it, to delete it - and gets a
/// <see cref="ContainedPath"/> back only if that is allowed. There is no
/// way to obtain one without saying what it is for, which is what stops
/// a verb added later from forgetting to check.</para>
///
/// <para><b>The limit, stated rather than implied (R8.3):</b> this
/// compares paths. It does not defend against another process replacing
/// a component between the check and the operation that follows it.
/// Closing that needs operating-system support this does not have. The
/// boundary is a boundary, not a defence against a hostile process
/// sharing the tree.</para>
/// </summary>
public sealed class Roots
{
    /// <summary>Case-insensitive on Windows and macOS, case-sensitive
    /// elsewhere, matching the filesystem being guarded (R8.6). The
    /// wrong choice here is a hole rather than an inconvenience:
    /// <c>ROOT/../root/x</c> compares as outside and is inside.</summary>
    public static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal sealed record OpenTree(string Name, string Full, Grant Grant);

    readonly List<OpenTree> trees;

    Roots(List<OpenTree> trees, string? origin, IReadOnlyList<TaskDecl> tasks, string? models)
    {
        this.trees = trees;
        Origin = origin ?? "the command line";
        Tasks = tasks;
        Models = models;
    }

    /// <summary>
    /// Where the screening models live, or null when the configuration
    /// named no directory.
    ///
    /// <para><b>Null is the load-bearing state, not an unset field.</b>
    /// The models are never shipped - four quantised ones run about
    /// 400 MB against a download measured in single-digit MB - so a
    /// person names a directory they populated themselves. Until they
    /// have, <see cref="Screen"/>'s first tier is the whole screen, and a
    /// screened tree still refuses what that tier finds. Once a directory
    /// IS named, a screened category whose model is not in it refuses the
    /// disclosure rather than quietly falling back to tier one, because
    /// at that point somebody has said they expect the model to be
    /// there.</para>
    /// </summary>
    public string? Models { get; }

    /// <summary>The tasks the configuration declared, in the order it
    /// declared them.
    ///
    /// <para>A boundary built from <c>--root</c> has none, and that falls
    /// out rather than being enforced: a command line declares no file,
    /// and a file is the only thing that can say what may run.</para></summary>
    public IReadOnlyList<TaskDecl> Tasks { get; }

    /// <summary>Where these trees were declared, in words, so <c>roots</c>
    /// can say it. A caller looking at a refusal needs to know which
    /// boundary refused them before they can change it, and on the MCP
    /// front end the command line is not something the model ever
    /// saw.</summary>
    public string Origin { get; }

    /// <summary>The declared names, in the order they were declared. The
    /// first is the default for an unqualified relative path.</summary>
    public IReadOnlyList<string> Names => trees.ConvertAll(t => t.Name);

    /// <summary>True when only one tree is open, in which case paths are
    /// written bare and R8.7's qualified form never appears.</summary>
    public bool IsSingle => trees.Count == 1;

    /// <summary>The absolute, link-resolved path of a declared tree.</summary>
    public string PathOf(string name) => TreeNamed(name).Full;

    /// <summary>What the tree grants where no scope applies, and the
    /// scopes that change it. <c>roots</c> reports this, because a
    /// boundary a caller cannot read is one they can only learn by being
    /// refused (R8.8, B.9).</summary>
    public Grant GrantOf(string name) => TreeNamed(name).Grant;

    OpenTree TreeNamed(string name)
    {
        foreach (OpenTree tree in trees)
            if (tree.Name.Equals(name, PathComparison)) return tree;
        throw new ArgumentException($"no tree called {name}", nameof(name));
    }

    /// <summary>
    /// Open the declared trees, or fail naming the one that was wrong.
    ///
    /// <para>A tree the caller named that does not exist is a startup
    /// failure (R8.5) rather than a directory Fettler creates: a
    /// directory the caller named is one they meant, so a typo is
    /// reported instead of materialised.</para>
    /// </summary>
    public static Result<Roots> Open(
        IReadOnlyList<TreeDecl> declared, string? origin = null, IReadOnlyList<TaskDecl>? tasks = null,
        string? models = null)
    {
        if (declared.Count == 0)
            return Result<Roots>.Fail(Outcome.Invalid, NothingDeclared);

        var opened = new List<OpenTree>();
        foreach (TreeDecl d in declared)
        {
            if (!IsValidName(d.Name))
                return Result<Roots>.Fail(Outcome.Invalid,
                    $"tree name '{d.Name}' is not usable: use two or more characters, " +
                    "starting with a letter, from letters, digits, dot, underscore and hyphen");

            foreach (OpenTree existing in opened)
                if (existing.Name.Equals(d.Name, PathComparison))
                    return Result<Roots>.Fail(Outcome.Invalid, $"tree name '{d.Name}' was declared twice");

            string full;
            try
            {
                full = Path.GetFullPath(d.Path);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Result<Roots>.Fail(Outcome.Invalid, $"tree '{d.Name}' is not a usable path: {e.Message}", d.Path);
            }

            if (!Directory.Exists(full))
                return Result<Roots>.Fail(Outcome.NotFound,
                    $"tree '{d.Name}' does not exist", full);

            // The tree itself may be reached through a link. Resolve it
            // once here so every later comparison is against the real
            // tree rather than against a name for it.
            string resolved = TrimTrailingSeparator(FinalTarget(full) ?? full);

            var scopes = new List<Scope>();
            foreach (ScopeDecl s in d.Scopes ?? [])
            {
                string at;
                try { at = Path.GetFullPath(Path.Combine(resolved, s.Relative)); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return Result<Roots>.Fail(Outcome.Invalid,
                        $"scope '{s.Relative}' in tree '{d.Name}' is not a usable path: {e.Message}", resolved);
                }

                // A scope that is not inside its own tree grants nothing
                // and would silently never apply, so it is a mistake in
                // the configuration rather than a harmless no-op.
                if (!IsWithin(resolved, at))
                    return Result<Roots>.Fail(Outcome.Invalid,
                        $"scope '{s.Relative}' is not inside tree '{d.Name}'", at);

                scopes.Add(new Scope(TrimTrailingSeparator(at), s.Can, s.Screen));
            }

            opened.Add(new OpenTree(d.Name, resolved, new Grant(d.Can, scopes, d.Screen)));
        }

        return Result<Roots>.Ok(new Roots(opened, origin, tasks ?? [], models));
    }

    /// <summary>The refusal when nothing at all was declared. Its own
    /// constant because it has to name the way out: a boundary that
    /// refuses without saying how to widen it is a wall.</summary>
    public const string NothingDeclared =
        "no tree was declared, so there is nothing to work in. Put a "
        + RootsFile.FileName + " in the folder you mean, holding "
        + "{\"trees\":{\"work\":{\"path\":\".\"}}}, or pass --config PATH, or "
        + "--root PATH for read-only access.";

    // ---- resolution, which is containment and permission together ----

    /// <summary>
    /// Prove that a caller-supplied path lies inside a tree AND that what
    /// the caller intends to do there is allowed, or refuse.
    ///
    /// <para>R8.4 requires that a probe not be a leak, so a path outside
    /// every tree is answered identically whether or not anything is
    /// there - and a path in a scope with no <see cref="Permission.List"/>
    /// is answered in exactly the same words, so hidden and absent cannot
    /// be told apart.</para>
    /// </summary>
    public Result<ContainedPath> Resolve(string given, Permission need)
    {
        Result<ContainedPath> located = Locate(given);
        if (!located.IsOk) return located;
        return Allow(located.Value, need);
    }

    /// <summary>
    /// The write case, where which permission is needed depends on
    /// whether anything is there: <see cref="Permission.Create"/> for a
    /// new file, <see cref="Permission.Update"/> for one that exists.
    /// </summary>
    public Result<ContainedPath> ResolveForWrite(string given)
    {
        Result<ContainedPath> located = Locate(given);
        if (!located.IsOk) return located;

        bool exists = File.Exists(located.Value.Full) || Directory.Exists(located.Value.Full);
        return Allow(located.Value, exists ? Permission.Update : Permission.Create);
    }

    /// <summary>
    /// Check an already-resolved path against a further permission,
    /// without walking it again.
    ///
    /// <para>Needed by <c>move</c>, which cannot know both its
    /// permissions until both ends are resolved: leaving a scope needs
    /// <see cref="Permission.Delete"/> on the source, and whether it is
    /// leaving is only knowable once the destination is known.</para>
    /// </summary>
    public static Result<ContainedPath> Allow(ContainedPath path, Permission need)
    {
        // B.13 first, and before the permission check, because no
        // permission level changes the answer. A caller told "you lack
        // update" would go looking for the grant that fixes it, and there
        // is none.
        if (Mutates(need) && RuleFiles.Governs(path.Full))
            return RuleFiles.Refuse<ContainedPath>(path.Display);

        if (need != Permission.None && !path.Can.HasFlag(need))
            return Result<ContainedPath>.Fail(Outcome.Refused,
                $"that needs {Permissions.Write(need)} here, and this "
                + $"{(path.Governing.Length == 0 ? "tree" : "scope")} grants "
                + $"{Permissions.Write(path.Can)}; ask for the roots to see the boundary",
                path.Display);

        return Result<ContainedPath>.Ok(path);
    }

    /// <summary>
    /// The same check, but for an operation that reaches EVERYTHING
    /// below a directory: a recursive delete, or a recursive copy.
    ///
    /// <para>Without this a scope is protected only against being named.
    /// <c>requirements</c> may grant delete while
    /// <c>requirements/cancelled</c> grants nothing, and a recursive
    /// delete of the parent would take the child with it - the exact
    /// arrangement the model exists to express, defeated by the one verb
    /// that does not name what it touches.</para>
    ///
    /// <para><b>The refusal names no path, and that is deliberate.</b>
    /// Naming the scope would tell a caller where a hidden one is. It
    /// costs one bit - that something below is protected - which is a
    /// price worth paying to refuse a destructive operation out loud
    /// rather than silently leaving part of it undone.</para>
    /// </summary>
    public Result<ContainedPath> AllowThroughout(ContainedPath path, Permission need)
    {
        Result<ContainedPath> here = Allow(path, need);
        if (!here.IsOk) return here;

        foreach (Scope scope in GrantOf(path.RootName).Scopes)
            if (IsWithin(path.Full, scope.Path)
                && !scope.Path.Equals(path.Full, PathComparison)
                && !scope.Can.HasFlag(need))
                return Result<ContainedPath>.Fail(Outcome.Refused,
                    $"something inside this is governed by a scope that does not grant "
                    + $"{Permissions.Write(need)}; name the parts you mean instead",
                    path.Display);

        return here;
    }

    static bool Mutates(Permission need) =>
        (need & (Permission.Create | Permission.Update | Permission.Rename | Permission.Delete))
        != Permission.None;

    /// <summary>Containment and visibility, with no permission asked for.
    /// Every public entry point goes through it and then through
    /// <see cref="Allow"/>, so there is one walk and one gate.</summary>
    Result<ContainedPath> Locate(string given)
    {
        if (string.IsNullOrWhiteSpace(given))
            return Result<ContainedPath>.Fail(Outcome.Invalid, "an empty path is not a path");

        (string? treeName, string rest) = SplitRootPrefix(given);

        if (treeName is null && Path.IsPathRooted(rest))
            return LocateAbsolute(rest);

        string name = treeName ?? trees[0].Name;
        OpenTree tree = TreeNamed(name);

        // An absolute path written after a tree prefix is only sensible
        // if it lands in that tree; treat it as absolute and check.
        if (Path.IsPathRooted(rest))
        {
            Result<ContainedPath> abs = LocateAbsolute(rest);
            if (abs.IsOk && !abs.Value.RootName.Equals(name, PathComparison))
                return Outside();
            return abs;
        }

        return Walk(tree, rest);
    }

    /// <summary>Resolve a path that is already anchored to a volume, by
    /// finding which tree contains it. Order matters only for nested
    /// trees, where the longest match is the honest answer.</summary>
    Result<ContainedPath> LocateAbsolute(string absolute)
    {
        string full;
        try { full = Path.GetFullPath(absolute); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<ContainedPath>.Fail(Outcome.Invalid, $"not a usable path: {e.Message}");
        }

        // Longest match wins, which matters only when one tree is nested
        // inside another: the deeper name is the honest answer for a path
        // that lies in both.
        OpenTree? best = null;
        foreach (OpenTree tree in trees)
            if (IsWithin(tree.Full, full) && (best is null || tree.Full.Length > best.Full.Length))
                best = tree;

        if (best is null) return Outside();

        return Walk(best, Relativize(best.Full, full));
    }

    /// <summary>
    /// The walk of R8.1: down from the tree, one component at a time,
    /// resolving every link on the way and re-checking containment after
    /// each step.
    /// </summary>
    Result<ContainedPath> Walk(OpenTree tree, string relative)
    {
        string current = tree.Full;

        foreach (string part in relative.Split('/', '\\'))
        {
            if (part.Length == 0 || part == ".") continue;

            if (part == "..")
            {
                string? parent = Path.GetDirectoryName(current);
                if (parent is null) return Outside();
                current = parent;
                if (!IsWithin(tree.Full, current)) return Outside();
                continue;
            }

            Result<string> named = CheckComponent(part);
            if (!named.IsOk) return named.Carry<ContainedPath>();

            current = Path.Combine(current, part);

            // Resolve this component if it is a link of any kind, then
            // re-check. A link planted here is exactly what R8.1 exists
            // to catch, and it is caught on the way past rather than at
            // the end.
            LinkState link = Inspect(current);
            if (link.IsLink)
            {
                if (link.Target is null)
                    return Result<ContainedPath>.Fail(Outcome.Refused,
                        "a link on this path cannot be resolved, so it cannot be shown to stay inside the tree",
                        current);

                current = link.Target;
                if (!IsWithin(tree.Full, current)) return Outside();
            }
        }

        (Permission can, Screened screen, string governing) = tree.Grant.At(tree.Full, current);

        // B.6: no list is not "you may not enumerate it", it is "it is
        // not there". Answered by the same constant as a path outside
        // every tree, so the two cannot be told apart by probing.
        if (!can.HasFlag(Permission.List)) return Outside();

        return Result<ContainedPath>.Ok(new ContainedPath(
            tree.Name, current, Relativize(tree.Full, current), can,
            governing.Equals(tree.Full, PathComparison) ? string.Empty : governing, screen));
    }

    /// <summary>
    /// What may be done at a path the walk has already produced - used by
    /// enumeration, which reaches files by walking a directory rather
    /// than by resolving a name.
    /// </summary>
    public ContainedPath Describe(string treeName, string full, string relative)
    {
        OpenTree tree = TreeNamed(treeName);
        (Permission can, Screened screen, string governing) = tree.Grant.At(tree.Full, full);
        return new ContainedPath(tree.Name, full, relative, can,
            governing.Equals(tree.Full, PathComparison) ? string.Empty : governing, screen);
    }

    static Result<ContainedPath> Outside() =>
        // One message, whatever is or is not there (R8.4), and the same
        // one for a hidden scope.
        Result<ContainedPath>.Fail(Outcome.OutsideRoot,
            "the path is outside every declared tree; ask for the roots to see the boundary");

    // ---- names and components ----

    static bool IsValidName(string name) =>
        // Two characters minimum, so a Windows drive letter can never be
        // mistaken for a tree prefix: 'C:' is a volume and 'repo:' is a
        // tree, and nothing has to guess which was meant.
        name.Length >= 2
        && char.IsAsciiLetter(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    (string? Name, string Remainder) SplitRootPrefix(string given)
    {
        int colon = given.IndexOf(':');
        if (colon <= 0) return (null, given);

        string candidate = given[..colon];
        foreach (OpenTree tree in trees)
            if (tree.Name.Equals(candidate, PathComparison))
                return (tree.Name, given[(colon + 1)..]);

        return (null, given);
    }

    /// <summary>
    /// Names that are legal on macOS and uncreatable on Windows, refused
    /// with an explanation rather than met as an obscure OS error (R9.2).
    /// </summary>
    static readonly string[] ReservedOnWindows =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    static Result<string> CheckComponent(string part)
    {
        if (!OperatingSystem.IsWindows()) return Result<string>.Ok(part);

        if (part.EndsWith('.') || part.EndsWith(' '))
            return Result<string>.Fail(Outcome.Invalid,
                $"'{part}' ends with a dot or a space, which Windows cannot create", part);

        string stem = part.Split('.')[0];
        foreach (string reserved in ReservedOnWindows)
            if (stem.Equals(reserved, StringComparison.OrdinalIgnoreCase))
                return Result<string>.Fail(Outcome.Invalid,
                    $"'{part}' uses the Windows device name {reserved}, which cannot be a filename", part);

        return Result<string>.Ok(part);
    }

    // ---- links ----

    readonly record struct LinkState(bool IsLink, string? Target);

    /// <summary>
    /// Whether a path is a link, and where it finally points.
    ///
    /// <para>Both a symbolic link and a Windows junction answer here:
    /// they are both reparse points, and a boundary that resolves only
    /// one has a hole exactly where a tree is most likely to have one
    /// (R6.11). A link whose target cannot be resolved reports as a link
    /// with no target, and the caller refuses - a dangling link is not
    /// proof of anything, and treating it as an ordinary name would let
    /// a write follow it out of the tree.</para>
    /// </summary>
    static LinkState Inspect(string path)
    {
        string? immediate;
        try
        {
            // LinkTarget rather than ResolveLinkTarget, because it
            // answers for a DANGLING link too - one whose entry exists
            // and whose target does not. Exists() follows the link and
            // says no for those, which would let a dangling link out of
            // the tree by looking like an ordinary name.
            immediate = new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new LinkState(false, null);
        }

        // A path that is not there is not a link, and must not be treated
        // as one: every create, every write and every move names a
        // destination that does not exist yet.
        if (immediate is null) return new LinkState(false, null);

        return new LinkState(true, FinalTarget(path));
    }

    static string? FinalTarget(string path)
    {
        try
        {
            FileSystemInfo? final =
                File.ResolveLinkTarget(path, returnFinalTarget: true)
                ?? Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return final?.FullName;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ---- comparison ----

    /// <summary>
    /// Whether one path lies inside another, compared the way the
    /// filesystem would (R8.6) and only at a separator boundary, so
    /// <c>/rootless</c> is not inside <c>/root</c>.
    /// </summary>
    public static bool IsWithin(string root, string candidate)
    {
        string r = Normalize(TrimTrailingSeparator(root));
        string c = Normalize(TrimTrailingSeparator(candidate));

        if (c.Equals(r, PathComparison)) return true;

        return c.Length > r.Length
            && c.StartsWith(r, PathComparison)
            && (c[r.Length] == Path.DirectorySeparatorChar || c[r.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Composed form for comparison only, never for the path handed back.
    ///
    /// <para>macOS normalizes filenames toward decomposed form and
    /// Windows returns what was written, so a name the caller typed and
    /// the same name read back off the disk can differ byte for byte
    /// while naming one file (R9.2). Comparing without this makes a
    /// contained path look outside its own tree.</para>
    /// </summary>
    static string Normalize(string s) =>
        s.IsNormalized(NormalizationForm.FormC) ? s : s.Normalize(NormalizationForm.FormC);

    static string TrimTrailingSeparator(string path)
    {
        if (path.Length <= 1) return path;
        // A volume root keeps its separator: C:\ and / are not C: and "".
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? path : trimmed;
    }

    static string Relativize(string root, string full)
    {
        if (full.Equals(root, PathComparison)) return string.Empty;
        string rel = Path.GetRelativePath(root, full);
        return rel == "." ? string.Empty : rel;
    }
}
