using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R10.6 and R10.26: the boundary of R8, probed from every direction a
/// path can leave a root.
/// </summary>
public sealed class ContainmentTests
{
    [Fact]
    public void ClimbingOutWithDotDotIsRefused()
    {
        using var box = new Sandbox();

        foreach (string attempt in new[] { "../escape.txt", "a/../../escape.txt", "./../../etc/passwd" })
        {
            Result<ContainedPath> resolved = box.Roots.Resolve(attempt, Permission.Read);
            Assert.False(resolved.IsOk, $"'{attempt}' should not resolve");
            Assert.Equal(Outcome.OutsideRoot, resolved.Failure!.Outcome);
        }
    }

    [Fact]
    public void DotDotInsideTheRootIsFine()
    {
        using var box = new Sandbox();
        Directory.CreateDirectory(box.Full("a/b"));

        Result<ContainedPath> resolved = box.Roots.Resolve("a/b/../b", Permission.Read);
        Assert.True(resolved.IsOk, resolved.Failure?.Message);
    }

    [Fact]
    public void AnAbsolutePathElsewhereIsRefused()
    {
        using var box = new Sandbox();
        string elsewhere = OperatingSystem.IsWindows() ? @"C:\Windows\System32\drivers\etc\hosts" : "/etc/passwd";

        Result<ContainedPath> resolved = box.Roots.Resolve(elsewhere, Permission.Read);
        Assert.False(resolved.IsOk);
        Assert.Equal(Outcome.OutsideRoot, resolved.Failure!.Outcome);
    }

    /// <summary>
    /// R8.4: a probe is not a leak. A path outside the root answers the
    /// same way whether or not anything is there, so nothing about the
    /// world beyond the boundary can be inferred by asking.
    /// </summary>
    [Fact]
    public void OutsideThePathAnswersTheSameWhetherOrNotItExists()
    {
        using var box = new Sandbox();

        string real = Path.Combine(Path.GetTempPath(), $"fettle-probe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(real, "here");
        string absent = Path.Combine(Path.GetTempPath(), $"fettle-probe-{Guid.NewGuid():N}.txt");

        try
        {
            Result<ContainedPath> present = box.Roots.Resolve(real, Permission.Read);
            Result<ContainedPath> missing = box.Roots.Resolve(absent, Permission.Read);

            Assert.False(present.IsOk);
            Assert.False(missing.IsOk);
            Assert.Equal(missing.Failure!.Outcome, present.Failure!.Outcome);
            Assert.Equal(missing.Failure.Message, present.Failure.Message);
        }
        finally
        {
            File.Delete(real);
        }
    }

    /// <summary>
    /// R8.1's whole reason for existing: given <c>root/a/b</c> where
    /// <c>a</c> is a link out of the root, <c>b</c> is not itself a link
    /// and looks perfectly contained. Only resolving <c>a</c> on the way
    /// past catches it.
    /// </summary>
    [Fact]
    public void ALinkPlantedMidPathCannotSmuggleTheDestinationOut()
    {
        using var box = new Sandbox();

        string outside = Path.Combine(Path.GetTempPath(), $"fettle-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "not yours");

        try
        {
            Directory.CreateSymbolicLink(box.Full("bridge"), outside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Creating a symbolic link on Windows needs Developer Mode or
            // elevation - the very asymmetry R6.11 cites for never
            // creating one. The boundary is still asserted on macOS in CI.
            Directory.Delete(outside, true);
            return;
        }

        try
        {
            Result<ContainedPath> resolved = box.Roots.Resolve("bridge/secret.txt", Permission.Read);
            Assert.False(resolved.IsOk, "a link mid-path must not carry the destination out of the root");
            Assert.Equal(Outcome.OutsideRoot, resolved.Failure!.Outcome);
        }
        finally
        {
            try { Directory.Delete(box.Full("bridge")); } catch (IOException) { }
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void ARootThatDoesNotExistIsAStartupFailureNamingIt()
    {
        string absent = Path.Combine(Path.GetTempPath(), $"fettle-absent-{Guid.NewGuid():N}");

        Result<Core.Roots> opened = Core.Roots.Open([new TreeDecl("root", absent, Permissions.Full)]);

        Assert.False(opened.IsOk);
        Assert.Equal(Outcome.NotFound, opened.Failure!.Outcome);
        Assert.Contains(absent, opened.Failure.Path);
    }

    // ---- R8.7: several named roots ----

    [Fact]
    public void APathInASecondRootResolvesAndOneInNoRootDoesNot()
    {
        using var box = new Sandbox("scratch");
        File.WriteAllText(Path.Combine(box.Extra["scratch"], "draft.md"), "draft");

        Result<ContainedPath> inScratch = box.Roots.Resolve("scratch:draft.md", Permission.Read);
        Assert.True(inScratch.IsOk, inScratch.Failure?.Message);
        Assert.Equal("scratch", inScratch.Value.RootName);

        Result<ContainedPath> nowhere = box.Roots.Resolve(
            Path.Combine(Path.GetTempPath(), "fettle-nowhere", "x.txt"), Permission.Read);
        Assert.False(nowhere.IsOk);
        Assert.Equal(Outcome.OutsideRoot, nowhere.Failure!.Outcome);
    }

    [Fact]
    public async Task AMoveBetweenRootsSucceedsAndSaysThatItCrossed()
    {
        using var box = new Sandbox("scratch");
        File.WriteAllText(Path.Combine(box.Extra["scratch"], "draft.md"), "draft");

        Result<MoveReport> moved = await box.Bench.MoveAsync("scratch:draft.md", "root:notes.md", overwrite: false);

        Assert.True(moved.IsOk, moved.Failure?.Message);
        Assert.True(moved.Value.CrossedRoot, "a move between two roots must report that it crossed");
        Assert.Equal("draft", box.ReadText("notes.md"));
    }

    /// <summary>A drive letter is a volume and a root name is a root, and
    /// nothing has to guess which was meant - which is why a root name is
    /// two characters at minimum.</summary>
    [Fact]
    public void ASingleLetterCannotBeARootName()
    {
        using var box = new Sandbox();
        Result<Core.Roots> opened = Core.Roots.Open([new TreeDecl("c", box.Root, Permissions.Full)]);

        Assert.False(opened.IsOk);
        Assert.Equal(Outcome.Invalid, opened.Failure!.Outcome);
    }

    [WindowsFact]
    public void AWindowsDeviceNameIsRefusedWithAnExplanation()
    {
        using var box = new Sandbox();

        Result<ContainedPath> resolved = box.Roots.Resolve("CON", Permission.Read);
        Assert.False(resolved.IsOk);
        Assert.Equal(Outcome.Invalid, resolved.Failure!.Outcome);
        Assert.Contains("CON", resolved.Failure.Message);
    }
}
