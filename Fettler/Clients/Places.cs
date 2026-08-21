namespace Fettler.Clients;

/// <summary>Which of the two levels a configuration sits at.</summary>
public enum Level
{
    /// <summary>Per user, affecting every project on the machine.</summary>
    Global,

    /// <summary>Per tree, checked in beside the code it governs.</summary>
    Repo,
}

/// <summary>What a configuration file is for, which decides what is
/// looked for in it and what setup may write to it.</summary>
public enum Purpose
{
    /// <summary>Where an MCP server is registered.</summary>
    Servers,

    /// <summary>Where permissions and hooks live.</summary>
    Policy,

    /// <summary>Where an assistant's standing instructions live.</summary>
    Instructions,
}

/// <summary>One file that may be read, and what it is for.</summary>
public sealed record Place(string Client, Level Level, Purpose Purpose, string Path);

/// <summary>
/// The machine's own directories, taken as arguments rather than read
/// from the environment.
///
/// <para><b>6.1 depends on this and nothing else.</b> No test may read
/// the real machine's configuration - a suite that does is a suite that
/// passes only here, and the highest-consequence code in the tool would
/// be the least tested. Every path below is composed from these three,
/// so a fixture home is a complete substitute for a real one.</para>
/// </summary>
public sealed record Machine(string Home, string AppData, string Tree)
{
    public static Machine Real(string tree) => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support")
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        tree);
}

/// <summary>
/// Every configuration file Fettler will read or write, compiled in.
///
/// <para><b>This is a closed list, not an exemption.</b> The doctor has
/// to read files no tree contains, and setup has to write some of them -
/// which is the single exception to "nothing writes outside a tree". The
/// exception is kept honest by being a fixed list of well-known paths:
/// never a caller-supplied path, never a glob, never a directory walk.
/// Anything else would turn the one exception into a hole exactly the
/// shape of the boundary.</para>
///
/// <para><b>Paths not marked verified are the ones to distrust.</b>
/// Client configuration formats move faster than any document about them.
/// Those below were checked against this machine; the rest are written
/// from each client's own documentation and are the first thing to
/// re-check when a verdict looks wrong.</para>
/// </summary>
public static class Places
{
    public const string ClaudeCode = "claude-code";
    public const string ClaudeDesktop = "claude-desktop";
    public const string VsCodeCopilot = "vscode-copilot";
    public const string GitHubCopilot = "github-copilot";

    public static readonly string[] Clients =
        [ClaudeCode, ClaudeDesktop, VsCodeCopilot, GitHubCopilot];

    /// <summary>
    /// The whole list, for one machine and one tree.
    ///
    /// <para>Claude Code in VS Code is deliberately NOT a fifth client:
    /// it shares the CLI's registry exactly, so reporting it separately
    /// would double every verdict and let the two disagree. The extension
    /// being installed is a fact about the machine, reported as a note
    /// against claude-code rather than as a client of its own.</para>
    /// </summary>
    public static IReadOnlyList<Place> All(Machine machine)
    {
        string claude = Path.Combine(machine.Home, ".claude");

        return
        [
            // Claude Code, the CLI. Both paths verified on this machine.
            new(ClaudeCode, Level.Global, Purpose.Servers, Path.Combine(machine.Home, ".claude.json")),
            new(ClaudeCode, Level.Global, Purpose.Policy, Path.Combine(claude, "settings.json")),
            new(ClaudeCode, Level.Global, Purpose.Instructions, Path.Combine(claude, "CLAUDE.md")),

            new(ClaudeCode, Level.Repo, Purpose.Servers, Path.Combine(machine.Tree, ".mcp.json")),
            new(ClaudeCode, Level.Repo, Purpose.Policy, Path.Combine(machine.Tree, ".claude", "settings.json")),
            new(ClaudeCode, Level.Repo, Purpose.Policy, Path.Combine(machine.Tree, ".claude", "settings.local.json")),
            new(ClaudeCode, Level.Repo, Purpose.Instructions, Path.Combine(machine.Tree, "CLAUDE.md")),

            // Claude Desktop has no project scope at all, so its repo
            // level is always n/a rather than absent - a distinction the
            // verdicts exist to make.
            new(ClaudeDesktop, Level.Global, Purpose.Servers, DesktopConfig(machine)),

            // VS Code with Copilot Chat.
            new(VsCodeCopilot, Level.Global, Purpose.Servers, Path.Combine(CodeUser(machine), "mcp.json")),
            new(VsCodeCopilot, Level.Global, Purpose.Policy, Path.Combine(CodeUser(machine), "settings.json")),
            new(VsCodeCopilot, Level.Repo, Purpose.Servers, Path.Combine(machine.Tree, ".vscode", "mcp.json")),
            new(VsCodeCopilot, Level.Repo, Purpose.Policy, Path.Combine(machine.Tree, ".vscode", "settings.json")),

            // The GitHub Copilot coding agent is configured per
            // repository only.
            new(GitHubCopilot, Level.Repo, Purpose.Instructions,
                Path.Combine(machine.Tree, ".github", "copilot-instructions.md")),
        ];
    }

    static string DesktopConfig(Machine machine) => OperatingSystem.IsWindows()
        ? Path.Combine(machine.AppData, "Claude", "claude_desktop_config.json")
        : Path.Combine(machine.AppData, "Claude", "claude_desktop_config.json");

    static string CodeUser(Machine machine) => Path.Combine(machine.AppData, "Code", "User");

    /// <summary>
    /// Whether a path is one this tool may touch outside a tree.
    ///
    /// <para>Asked before every read and every write in this namespace,
    /// so the closed list is a check rather than a convention. A
    /// convention is what a later verb forgets.</para>
    /// </summary>
    public static bool Allows(Machine machine, string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (Place place in All(machine))
            if (Path.GetFullPath(place.Path).Equals(full, Core.Roots.PathComparison)) return true;

        // The backup setup writes beside a file it is about to change is
        // the one derived name allowed, and only beside a listed file.
        if (full.EndsWith(BackupSuffix, StringComparison.Ordinal))
            return Allows(machine, full[..^BackupSuffix.Length]);

        return false;
    }

    /// <summary>What a backup is called. Beside the original, so it is
    /// found by whoever needs it rather than in a directory they would
    /// have to be told about.</summary>
    public const string BackupSuffix = ".fettler-backup";

    /// <summary>The name Fettler registers itself under, in every
    /// client. One name everywhere, so a doctor looking for it does not
    /// need a table.</summary>
    public const string ServerName = "fettler";
}
