using System.Runtime.InteropServices;

namespace Fettler.Core;

/// <summary>What kind of link a path is, if it is one (R6.11).</summary>
public enum LinkKind
{
    None,
    Symlink,
    Junction,
}

/// <summary>
/// What Fettler reports about a file besides its content: the one
/// permission bit it keeps (R6.9), the attributes it reads rather than
/// obeys (R4.19, R6.10), and whether the path is a link (R6.11).
/// </summary>
public sealed record FileFacts(
    bool? Executable,
    bool ReadOnly,
    bool Hidden,
    LinkKind Link,
    string? LinkTarget)
{
    public static readonly FileFacts Unknown = new(null, false, false, LinkKind.None, null);
}

/// <summary>One file, as <c>find</c> answers it (R4.6).</summary>
public sealed record FoundFile(
    ContainedPath Path,
    long Bytes,
    DateTimeOffset Modified,
    FileFacts Facts);

/// <summary>
/// Which directories enumeration skips, and how a caller changes its
/// mind (R4.11).
///
/// <para>The defaults are by <b>name</b> and never by attribute: a
/// dotfile is an ordinary file and a Windows hidden file is an ordinary
/// file, both matched normally, because R4.19 requires attributes never
/// decide what a pattern matches. What is skipped is what a build wrote,
/// which is a fact about the name and not about a bit.</para>
/// </summary>
public sealed record Excludes(IReadOnlyList<string> Names, IReadOnlyList<Glob> Extra)
{
    /// <summary>
    /// Measured on the repository Fettler was written for: the working
    /// tree holds 2794 files against 833 tracked, and <c>**/*.cs</c>
    /// matches 196 files where 119 are wanted - the other 77 are
    /// generated under bin and obj. Walking them is not merely slower,
    /// it returns generated code as though it were source, and a caller
    /// acting on those hits edits files the next build overwrites.
    /// </summary>
    public static readonly string[] Generated = [".git", "bin", "obj", "artifacts", "node_modules"];

    public static readonly Excludes Default = new(Generated, []);

    public static readonly Excludes Nothing = new([], []);

    public bool SkipsDirectory(string name)
    {
        foreach (string n in Names)
            if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public bool SkipsPath(string relative)
    {
        foreach (Glob g in Extra)
            if (g.Matches(relative)) return true;
        return false;
    }
}

/// <summary>How a listing was cut short, so a truncated answer never
/// reads as a complete one (R4.10).</summary>
public sealed record Bounded<T>(IReadOnlyList<T> Items, bool Truncated, int Excluded)
{
    public int Count => Items.Count;
}

/// <summary>
/// Walking a root, and the <c>find</c> of R4.6 and R4.17 over it.
/// </summary>
public static class Tree
{
    /// <summary>
    /// Every file under a root, in a deterministic order, with what the
    /// exclusions left out counted rather than silently dropped.
    ///
    /// <para><b>Each file is yielded exactly once (R9.2).</b> The tree is
    /// enumerated, not searched once per pattern, so no arrangement of
    /// patterns can produce the same file twice - which is the property a
    /// pattern-driven implementation has to work to keep and this one
    /// gets by construction.</para>
    /// </summary>
    public static Bounded<FoundFile> Find(
        Roots roots,
        string rootName,
        Glob? pattern,
        Excludes excludes,
        DateTimeOffset? since,
        int limit,
        bool byModified)
    {
        string root = roots.PathOf(rootName);
        var found = new List<FoundFile>();
        int excluded = 0;

        // Defect 1.3: start where the pattern says, rather than walking
        // everything and discarding it. A prefix naming a directory that
        // is not there means no file can match, so the walk is the empty
        // one rather than the whole tree.
        string from = root;
        if (pattern is not null && pattern.FixedPrefix is { Length: > 0 } prefix)
        {
            string start = Path.Combine(root, prefix.Replace('/', Path.DirectorySeparatorChar));
            if (!Roots.IsWithin(root, start) || !Directory.Exists(start))
                return new Bounded<FoundFile>([], false, 0);
            from = start;
        }

        Walk(roots, root, from, rootName, excludes, found, ref excluded, pattern, since);

        // R4.12: order before any limit is applied, so two runs over an
        // unchanged tree return the same hits in the same order. A
        // directory enumeration has no guaranteed order on either
        // platform, so without this the limit selects an arbitrary
        // subset that changes between runs for no visible reason.
        found.Sort(byModified
            ? (a, b) =>
            {
                int t = b.Modified.CompareTo(a.Modified);
                return t != 0 ? t : string.CompareOrdinal(a.Path.Display, b.Path.Display);
            }
        : (a, b) => string.CompareOrdinal(a.Path.Display, b.Path.Display));

        bool truncated = limit > 0 && found.Count > limit;
        if (truncated) found.RemoveRange(limit, found.Count - limit);

        return new Bounded<FoundFile>(found, truncated, excluded);
    }

    static void Walk(
        Roots roots, string root, string directory, string rootName, Excludes excludes,
        List<FoundFile> into, ref int excluded, Glob? pattern, DateTimeOffset? since)
    {
        Grant grant = roots.GrantOf(rootName);
        bool scoped = grant.Scopes.Count > 0;

        // B.6: absent `list` is not "may not be enumerated", it is "is
        // not there". Nothing hidden reaches the results, the excluded
        // COUNT, or the time the walk takes - a total that moved when a
        // scope was hidden would be a way to probe for it.
        bool Listable(string full) => !scoped || grant.At(root, full).Can.HasFlag(Permission.List);

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A directory the platform will not open is not a reason to
            // abandon the walk; it contributes nothing and the rest of
            // the tree is still the caller's answer.
            return;
        }

        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);
            bool isDirectory;
            try { isDirectory = Directory.Exists(entry); }
            catch (IOException) { continue; }

            if (isDirectory)
            {
                // Before the exclusion count, so a hidden directory adds
                // nothing to any total.
                if (!Listable(entry)) continue;

                if (excludes.SkipsDirectory(name)) { excluded += CountUnder(entry); continue; }

                // A directory that is a link is not descended into: the
                // tree below it belongs to wherever it points, and
                // walking it would report the same files under two names
                // and risk a cycle.
                if (Facts(entry, isDirectory: true).Link != LinkKind.None) continue;

                Walk(roots, root, entry, rootName, excludes, into, ref excluded, pattern, since);
                continue;
            }

            if (!Listable(entry)) continue;

            string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
            if (excludes.SkipsPath(relative)) { excluded++; continue; }
            if (pattern is not null && !pattern.Matches(relative)) continue;

            FileInfo info;
            try { info = new FileInfo(entry); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (since is not null && modified <= since.Value) continue;

            into.Add(new FoundFile(
                roots.Describe(rootName, entry, relative),
                Safe(() => info.Length, 0L),
                modified,
                Facts(entry, isDirectory: false)));
        }
    }

    static int CountUnder(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return fallback; }
    }

    /// <summary>
    /// What a path is, besides its content. The executable bit is the one
    /// permission Fettler keeps (R6.9) and is reported as absent rather
    /// than invented on Windows, where NTFS has no such bit to report.
    /// </summary>
    public static FileFacts Facts(string path, bool isDirectory)
    {
        bool? executable = null;
        bool readOnly = false, hidden = false;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                FileAttributes a = File.GetAttributes(path);
                readOnly = a.HasFlag(FileAttributes.ReadOnly);
                hidden = a.HasFlag(FileAttributes.Hidden);
            }
            else
            {
                UnixFileMode mode = File.GetUnixFileMode(path);
                executable = mode.HasFlag(UnixFileMode.UserExecute);
                readOnly = !mode.HasFlag(UnixFileMode.UserWrite);
                hidden = Path.GetFileName(path).StartsWith('.');
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }

        (LinkKind kind, string? target) = LinkOf(path, isDirectory);
        return new FileFacts(executable, readOnly, hidden, kind, target);
    }

    const uint IoReparseTagMountPoint = 0xA0000003;

    static (LinkKind Kind, string? Target) LinkOf(string path, bool isDirectory)
    {
        try
        {
            FileSystemInfo? target = isDirectory
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: false)
                : File.ResolveLinkTarget(path, returnFinalTarget: false);

            if (target is null) return (LinkKind.None, null);

            // A junction and a symbolic link are both reparse points and
            // .NET reports both the same way. They are told apart by the
            // reparse tag, which matters because a junction is what build
            // tooling plants and a reader deserves to know which it has.
            LinkKind kind = OperatingSystem.IsWindows() && ReparseTag(path) == IoReparseTagMountPoint
                ? LinkKind.Junction
                : LinkKind.Symlink;

            return (kind, target.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (LinkKind.None, null);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Win32FindData
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;          // the reparse tag, when the file is one
        public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstFileW(string fileName, out Win32FindData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FindClose(IntPtr handle);

    static uint ReparseTag(string path)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = FindFirstFileW(path, out Win32FindData data);
            return handle == new IntPtr(-1) || handle == IntPtr.Zero ? 0 : data.Reserved0;
        }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
        finally
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) FindClose(handle);
        }
    }
}
