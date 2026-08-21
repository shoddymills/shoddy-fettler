using System.Text;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// A throwaway tree with a Bench open on it. Every test that touches
/// disk gets its own, so nothing a test writes can reach another one -
/// and so the suite can run concurrently without the tests disagreeing
/// about what is on disk.
/// </summary>
public sealed class Sandbox : IDisposable
{
    public Sandbox(params string[] extraRoots)
        : this(Permissions.Full, null, extraRoots) { }

    /// <summary>
    /// A sandbox whose main tree grants exactly what a test says, with
    /// scopes if it needs them. The ordinary case is
    /// <see cref="Permissions.Full"/>, which is what the tree holding the
    /// configuration gets, so most tests do not mention permissions at
    /// all - and the ones that DO are testing the boundary rather than
    /// tripping over it.
    /// </summary>
    public Sandbox(Permission can, IReadOnlyList<ScopeDecl>? scopes = null, params string[] extraRoots)
    {
        Root = Directory.CreateTempSubdirectory("fettle-test-").FullName;

        var decls = new List<TreeDecl> { new("root", Root, can, scopes) };
        foreach (string name in extraRoots)
        {
            string path = Path.Combine(Path.GetTempPath(), $"fettle-{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            Extra[name] = path;
            decls.Add(new TreeDecl(name, path, Permissions.Full));
        }

        this.declared = decls;

        Result<Core.Roots> opened = Core.Roots.Open(decls);
        Assert.True(opened.IsOk, opened.Failure?.Message);
        Roots = opened.Value;
        Bench = new Bench(Roots);
    }

    readonly List<TreeDecl> declared;

    public string Root { get; }

    public Dictionary<string, string> Extra { get; } = [];

    public Core.Roots Roots { get; private set; }

    public Bench Bench { get; private set; }

    /// <summary>
    /// Declare tasks, the way a <c>.fettler.json</c> does.
    ///
    /// <para>Reopening the boundary is the point rather than the cost: a
    /// task is part of what a boundary declares, so a test that declares
    /// one states the same thing the file states.</para>
    /// </summary>
    public void Declare(params TaskDecl[] tasks)
    {
        Result<Core.Roots> opened = Core.Roots.Open(declared, null, tasks);
        Assert.True(opened.IsOk, opened.Failure?.Message);

        Bench.Dispose();
        Roots = opened.Value;
        Bench = new Bench(Roots);
    }

    /// <summary>
    /// Re-open the boundary with a screen on the main tree, the way a
    /// <c>.fettler.json</c> with a "screen" in it does.
    ///
    /// <para><paramref name="screener"/> stands in for the model host.
    /// Passing one is how R7.1's fail-closed cases are reached without a
    /// model: leaving it null and naming <paramref name="models"/> gets
    /// the real sidecar instead, which is how "burler is not installed"
    /// is tested as the thing it actually is.</para>
    /// </summary>
    public void Screen(Screened screen, IScreener? screener = null,
                       IReadOnlyList<ScopeDecl>? scopes = null, string? models = null)
    {
        var decls = new List<TreeDecl>(declared);
        decls[0] = decls[0] with { Screen = screen, Scopes = scopes ?? decls[0].Scopes };

        Result<Core.Roots> opened = Core.Roots.Open(decls, null, null, models);
        Assert.True(opened.IsOk, opened.Failure?.Message);

        Bench.Dispose();
        Roots = opened.Value;
        Bench = new Bench(Roots, screener);
    }

    public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public ContainedPath Path_(string relative)
    {
        Result<ContainedPath> p = Roots.Resolve(relative, Permission.Read);
        Assert.True(p.IsOk, p.Failure?.Message);
        return p.Value;
    }

    /// <summary>Write a file with exact bytes, so a test can state the
    /// encoding and line endings it means rather than hoping.</summary>
    public string WriteRaw(string relative, byte[] bytes)
    {
        string full = Full(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    public string Write(string relative, string text)
    {
        return WriteRaw(relative, new UTF8Encoding(false).GetBytes(text));
    }

    public string ReadText(string relative) => File.ReadAllText(Full(relative));

    public byte[] ReadRaw(string relative) => File.ReadAllBytes(Full(relative));

    public void Dispose()
    {
        Bench.Dispose();
        Discard(Root);
        foreach (string path in Extra.Values) Discard(path);
    }

    static void Discard(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;

            // A test that made something read-only would otherwise leave
            // its own temp tree behind on Windows.
            if (OperatingSystem.IsWindows())
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(file, FileAttributes.Normal); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A test that needs a platform this machine may not be.
///
/// <para>R9.3 requires the hazards run on both platforms in CI, and
/// several of them cannot run on the other one at all - a POSIX execute
/// bit, an APFS case-only rename, a decomposed filename, an NTFS
/// alternate data stream. Those tests exist, are attributed with what
/// they need, and <b>say why they skipped</b>. A skip nobody can see is
/// a test that quietly stopped existing.</para>
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "needs Windows: NTFS attributes, streams, junctions";
    }
}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "needs a POSIX filesystem: the execute bit";
    }
}

public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "needs macOS: decomposed filenames, extended attributes, APFS";
    }
}
