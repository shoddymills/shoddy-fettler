namespace Fettler.Core;

/// <summary>A path a configuration file named, and the file that named
/// it, so <c>roots</c> can say where a value came from.</summary>
public sealed record Named(string Path, string File);

/// <summary>What the <c>tools</c> sections said about the client's
/// folders. Each is null when no file named it.</summary>
/// <param name="Scratch">The folder to open as <c>scratch</c>, in place
/// of the one fettle would choose.</param>
/// <param name="ScratchRoot">The folder that holds every project's
/// scratch folders, in place of the places fettle probes.</param>
/// <param name="ClientRoot">The folder that holds the client's
/// per-project folders, in place of <c>~/.claude/projects</c>.</param>
public sealed record HarnessSettings(Named? Scratch = null, Named? ScratchRoot = null, Named? ClientRoot = null)
{
    public static readonly HarnessSettings None = new();
}

/// <summary>One folder the client made, opened as a tree, and how it was
/// found.</summary>
public sealed record HarnessTree(string Name, string Path, Permission Can, string How);

/// <summary>
/// The folders the client keeps for this project, found at every level
/// with nothing declared (R6).
///
/// <para><b>Three trees.</b> <c>scratch</c> is this session's scratch
/// folder, read and write. <c>tool-results</c> is where the client puts a
/// large tool answer and asks the model to read it back, read-only.
/// <c>memory</c> is the client's memory folder, read and write. Nothing
/// else in the client's folder is opened: the session transcripts beside
/// these are outside every tree.</para>
///
/// <para><b>Only this session's folder.</b> Older sessions sit beside it
/// under the same project folder. They are not trees, so a path into one
/// is refused in the same words as any path outside every tree.</para>
///
/// <para><b>Finding the session, in order.</b> First, a value in the
/// environment the client launched this process with that is exactly the
/// name of a session folder on disk. That is an exact match, so it is
/// never a guess. Second, when no value matches, the newest session
/// folder. A resumed session is the case the second route gets wrong,
/// and <c>roots</c> says which route was used so a person can see
/// it.</para>
///
/// <para><b>Absent means absent.</b> Each folder is probed for existence
/// and nothing more. A folder that does not exist yet is not a tree and
/// not a finding. It appears on the next call once the client makes it.</para>
/// </summary>
public static class Harness
{
    public const string ScratchName = "scratch";
    public const string ToolResultsName = "tool-results";
    public const string MemoryName = "memory";

    /// <summary>The names fettle gives the client's folders. A declared
    /// tree may not take one.</summary>
    public static readonly string[] Names = [ScratchName, ToolResultsName, MemoryName];

    const string ScratchpadName = "scratchpad";

    /// <summary>The client's folders for a project launched in
    /// <paramref name="launch"/>, in the order they are listed.</summary>
    public static IReadOnlyList<HarnessTree> Find(Host host, string launch, HarnessSettings settings)
    {
        var found = new List<HarnessTree>();
        IReadOnlyList<string> slugs = Slugs(launch);

        string? session = null;
        string? sessionHow = null;

        if (settings.Scratch is { } named)
        {
            if (Exists(named.Path))
                found.Add(new HarnessTree(ScratchName, named.Path, Permissions.Full,
                    $"named by \"scratch\" under \"tools\" in {named.File}"));
        }
        else
        {
            foreach (string root in ScratchRoots(host, settings.ScratchRoot))
            {
                string? project = FirstExisting(root, slugs);
                if (project is null) continue;

                (session, sessionHow) = Session(host, project);
                if (session is null) break;

                string pad = Path.Combine(project, session, ScratchpadName);
                if (Exists(pad))
                    found.Add(new HarnessTree(ScratchName, pad, Permissions.Full, sessionHow!));
                break;
            }
        }

        string? clientRoot = settings.ClientRoot?.Path
            ?? (host.Home is null ? null : Path.Combine(host.Home, ".claude", "projects"));

        if (clientRoot is not null && FirstExisting(clientRoot, slugs) is { } client)
        {
            string whence = settings.ClientRoot is { } c
                ? $"under the folder named by \"client-root\" under \"tools\" in {c.File}"
                : "in the client's folder for this project";

            if (session is null) (session, sessionHow) = Session(host, client);

            string? perSession = session is null ? null : Path.Combine(client, session, ToolResultsName);
            string direct = Path.Combine(client, ToolResultsName);

            if (perSession is not null && Exists(perSession))
                found.Add(new HarnessTree(ToolResultsName, perSession, Permissions.ReadOnly,
                    $"{whence}, in this session's folder; {sessionHow}"));
            else if (Exists(direct))
                found.Add(new HarnessTree(ToolResultsName, direct, Permissions.ReadOnly, whence));

            string memory = Path.Combine(client, MemoryName);
            if (Exists(memory))
                found.Add(new HarnessTree(MemoryName, memory, Permissions.Full, whence));
        }

        return found;
    }

    /// <summary>
    /// The folder names the client derives from a launch path.
    ///
    /// <para>Observed on this machine: <c>c:\github\shoddy-fettler</c>
    /// becomes <c>c--github-shoddy-fettler</c>, the separators and the
    /// drive colon each replaced by a hyphen. The client also replaces
    /// other characters that are not letters or digits in some versions,
    /// so that spelling is tried second. Each is probed for existence and
    /// the first that exists is used.</para>
    /// </summary>
    public static IReadOnlyList<string> Slugs(string launch)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(launch));

        var separators = new System.Text.StringBuilder(full.Length);
        var everything = new System.Text.StringBuilder(full.Length);
        foreach (char c in full)
        {
            separators.Append(c is '\\' or '/' or ':' ? '-' : c);
            everything.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }

        string first = separators.ToString();
        string second = everything.ToString();
        return first == second ? [first] : [first, second];
    }

    /// <summary>The folders that may hold every project's scratch
    /// folders, in the order they are tried.</summary>
    public static IReadOnlyList<string> ScratchRoots(Host host, Named? named)
    {
        if (named is not null) return [named.Path];

        var roots = new List<string>();
        if (host.Temp is { Length: > 0 } temp) roots.Add(Path.Combine(temp, "claude"));
        if (host.Tmp is { Length: > 0 } tmp && host.Uid is { Length: > 0 } uid)
            roots.Add(Path.Combine(tmp, "claude-" + uid));
        return roots;
    }

    /// <summary>
    /// Which session folder under a project folder is this session's, and
    /// how that was decided.
    ///
    /// <para>A session folder is named by a GUID. Other folders beside
    /// them, such as <c>memory</c>, are not sessions and are never
    /// chosen.</para>
    /// </summary>
    public static (string? Session, string? How) Session(Host host, string project)
    {
        List<DirectoryInfo> sessions;
        try
        {
            sessions = [.. new DirectoryInfo(project).EnumerateDirectories()
                .Where(d => Guid.TryParseExact(d.Name, "D", out _))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (null, null);
        }

        if (sessions.Count == 0) return (null, null);

        // Sorted, so the same environment always picks the same variable.
        foreach (KeyValuePair<string, string> variable in host.Environment.OrderBy(v => v.Key, StringComparer.Ordinal))
            foreach (string token in Tokens(variable.Value))
                foreach (DirectoryInfo folder in sessions)
                    if (folder.Name.Equals(token, StringComparison.OrdinalIgnoreCase))
                        return (folder.Name, $"this session's folder, named by the environment variable {variable.Key}");

        DirectoryInfo newest = sessions.OrderByDescending(d => d.CreationTimeUtc).ThenBy(d => d.Name, StringComparer.Ordinal).First();
        return (newest.Name, "the newest session folder, because no environment variable names one");
    }

    /// <summary>The runs of GUID characters in a value. A session id may
    /// arrive on its own or inside a path.</summary>
    static IEnumerable<string> Tokens(string value)
    {
        int start = -1;
        for (int i = 0; i <= value.Length; i++)
        {
            bool part = i < value.Length && (char.IsAsciiHexDigit(value[i]) || value[i] == '-');
            if (part && start < 0) start = i;
            if (!part && start >= 0)
            {
                if (i - start == 36) yield return value[start..i];
                start = -1;
            }
        }
    }

    static string? FirstExisting(string root, IReadOnlyList<string> slugs)
    {
        foreach (string slug in slugs)
        {
            string candidate = Path.Combine(root, slug);
            if (Exists(candidate)) return candidate;
        }
        return null;
    }

    static bool Exists(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
