using System.Runtime.InteropServices;

namespace Fettler.Core;

/// <summary>
/// The facts about the machine that decide where the client's folders
/// are and where the machine file is.
///
/// <para><b>Taken as an argument, never read from inside the core.</b>
/// A test that read the real home folder or the real temp folder would
/// pass on one machine only. <see cref="None"/> is a machine with none
/// of these folders, so a test that does not ask for them gets none.</para>
/// </summary>
/// <param name="Home">The user's home folder, or null for none.</param>
/// <param name="Temp">The system temp folder, or null for none.</param>
/// <param name="Uid">The numeric user id on macOS and Linux, used for
/// <c>/tmp/claude-&lt;uid&gt;</c>. Null on Windows.</param>
/// <param name="MachineFile">Where the machine file would be. It need not
/// exist.</param>
/// <param name="Environment">The process environment. Read to find the
/// session the client launched this process for.</param>
/// <param name="Tmp">The folder that holds <c>claude-&lt;uid&gt;</c> on macOS
/// and Linux. <c>/tmp</c> on a real machine.</param>
/// <param name="AppData">The per-user application data folder, where
/// Claude Desktop and VS Code keep their configuration.</param>
public sealed record Host(
    string? Home,
    string? Temp,
    string? Uid,
    string? MachineFile,
    IReadOnlyDictionary<string, string> Environment,
    string? Tmp = null,
    string? AppData = null)
{
    /// <summary>A machine with no home, no temp folder and no machine
    /// file. Nothing is found on it.</summary>
    public static readonly Host None = new(null, null, null, null,
        new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>The machine this process runs on.</summary>
    public static Host Real()
    {
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) environment[key] = value;

        return new Host(
            home.Length == 0 ? null : home,
            Path.GetTempPath(),
            OperatingSystem.IsWindows() ? null : UnixUid(),
            MachineFilePath(home),
            environment,
            OperatingSystem.IsWindows() ? null : "/tmp",
            AppDataPath(home));
    }

    static string? AppDataPath(string home)
    {
        if (OperatingSystem.IsWindows())
        {
            string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            return appData.Length == 0 ? null : appData;
        }

        if (home.Length == 0) return null;

        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support")
            : System.Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
                ? config
                : Path.Combine(home, ".config");
    }

    /// <summary>
    /// Where the machine file lives: <c>%APPDATA%\fettler\fettler.json</c>
    /// on Windows, and <c>~/.config/fettler/fettler.json</c> elsewhere,
    /// or under <c>XDG_CONFIG_HOME</c> when that is set.
    ///
    /// <para><b>Not <c>~/.fettler.json</c>.</b> The walk upward looks for
    /// <c>.fettler.json</c>, and a project under the home folder would
    /// find a machine file of that name and take the whole home folder
    /// as its tree.</para>
    /// </summary>
    static string? MachineFilePath(string home)
    {
        if (OperatingSystem.IsWindows())
        {
            string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            return appData.Length == 0 ? null : Path.Combine(appData, MachineFolder, MachineFileName);
        }

        string? config = System.Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (config is { Length: > 0 }) return Path.Combine(config, MachineFolder, MachineFileName);

        return home.Length == 0 ? null : Path.Combine(home, ".config", MachineFolder, MachineFileName);
    }

    public const string MachineFolder = "fettler";

    public const string MachineFileName = "fettler.json";

    [DllImport("libc", EntryPoint = "getuid")]
    static extern uint GetUid();

    static string? UnixUid()
    {
        try
        {
            return GetUid().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
