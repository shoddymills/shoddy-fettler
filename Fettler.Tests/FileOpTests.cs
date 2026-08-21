using System.Text;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R10.3, R10.7, R10.27 to R10.32: the file-level operations of R6 and
/// the platform hazards R9.2 collects.
/// </summary>
public sealed class FileOpTests
{
    /// <summary>R10.7 and R6.3. Both required platforms are
    /// case-insensitive and case-preserving, and the naive
    /// implementation either fails or silently does nothing on both.</summary>
    [Fact]
    public async Task ACaseOnlyRenameSucceeds()
    {
        using var box = new Sandbox();
        box.Write("glob.cs", "contents");

        Result<MoveReport> moved = await box.Bench.MoveAsync("glob.cs", "Glob.cs", overwrite: false);

        Assert.True(moved.IsOk, moved.Failure?.Message);
        Assert.True(moved.Value.CaseOnly, "the move must report that it was a case-only rename");

        string[] names = Directory.GetFiles(box.Root).Select(Path.GetFileName).ToArray()!;
        Assert.Contains("Glob.cs", names);
        Assert.DoesNotContain("glob.cs", names);
    }

    [Fact]
    public async Task MoveReportsWhetherItWasAtomicAndWorksOnADirectory()
    {
        using var box = new Sandbox();
        Directory.CreateDirectory(box.Full("tree/inner"));
        box.Write("tree/inner/a.txt", "a");

        Result<MoveReport> moved = await box.Bench.MoveAsync("tree", "moved", overwrite: false);

        Assert.True(moved.IsOk, moved.Failure?.Message);
        Assert.True(moved.Value.Directory);
        Assert.True(moved.Value.Atomic, "a move within one volume is a rename");
        Assert.True(File.Exists(box.Full("moved/inner/a.txt")));
    }

    // ---- R10.3: crash safety ----

    /// <summary>
    /// R4.3: interrupted at any point the write leaves either the
    /// original content or the new content, never a truncated file. The
    /// interruption is simulated by holding the destination open for
    /// exclusive use, which is what makes the replace fail part-way.
    /// </summary>
    [WindowsFact]
    public async Task AWriteThatCannotCompleteLeavesTheOriginalIntact()
    {
        using var box = new Sandbox();
        const string original = "the original content\n";
        box.Write("held.txt", original);

        using (File.Open(box.Full("held.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Result<Saved> written = await box.Bench.WriteAsync("held.txt", "replacement", overwrite: true);
            Assert.False(written.IsOk, "a write onto a file another process holds open must fail");
            Assert.Equal(Outcome.Denied, written.Failure!.Outcome);
        }

        Assert.Equal(original, box.ReadText("held.txt"));

        // And the staging file is cleaned up rather than left behind.
        Assert.Empty(Directory.GetFiles(box.Root, ".fettle-*.tmp"));
    }

    [Fact]
    public async Task AWriteRefusesToReplaceWithoutBeingTold()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "original");

        Result<Saved> refused = await box.Bench.WriteAsync("a.txt", "new", overwrite: false);

        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.TargetExists, refused.Failure!.Outcome);
        Assert.Equal("original", box.ReadText("a.txt"));
    }

    // ---- R10.20 and R6.4/R6.5/R6.6 ----

    [Fact]
    public async Task DeleteRefusesAPatternAndANonEmptyDirectoryWithoutRecursive()
    {
        using var box = new Sandbox();
        Directory.CreateDirectory(box.Full("scratch"));
        box.Write("scratch/a.tmp", "a");
        box.Write("scratch/b.tmp", "b");

        Result<DeleteReport> pattern = await box.Bench.DeleteAsync("scratch/*.tmp", recursive: false, force: false);
        Assert.False(pattern.IsOk);
        Assert.Equal(Outcome.Refused, pattern.Failure!.Outcome);
        Assert.Contains("pattern", pattern.Failure.Message);

        Result<DeleteReport> nonEmpty = await box.Bench.DeleteAsync("scratch", recursive: false, force: false);
        Assert.False(nonEmpty.IsOk);
        Assert.Equal(Outcome.Refused, nonEmpty.Failure!.Outcome);
        Assert.True(Directory.Exists(box.Full("scratch")));

        Result<DeleteReport> recursive = await box.Bench.DeleteAsync("scratch", recursive: true, force: false);
        Assert.True(recursive.IsOk, recursive.Failure?.Message);
        Assert.Equal(2, recursive.Value.Files);
        Assert.False(Directory.Exists(box.Full("scratch")));
    }

    [Fact]
    public async Task MoveRefusesAPattern()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "a");

        Result<MoveReport> refused = await box.Bench.MoveAsync("*.txt", "b.txt", overwrite: false);

        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.Refused, refused.Failure!.Outcome);
    }

    // ---- R10.27: the one permission bit ----

    /// <summary>
    /// R6.9. This repository tracks 40 files at mode 100755 - every
    /// build.sh, every build.ps1, both release scripts - so a copy that
    /// drops the bit breaks the macOS build R9.1 requires, and breaks it
    /// at run time far from whatever dropped it.
    /// </summary>
    [UnixFact]
    public async Task CopyAndMoveCarryTheExecutableBit()
    {
        // The attribute skips this off POSIX; the guard is what the
        // platform-compatibility analyzer reads, and it has to be here
        // rather than in the attribute for the build to stay warning-free.
        if (OperatingSystem.IsWindows()) return;

        using var box = new Sandbox();
        box.Write("build.sh", "#!/bin/sh\necho hello\n");

        Result<ExecReport> marked = await box.Bench.SetExecutableAsync("build.sh", executable: true);
        Assert.True(marked.IsOk, marked.Failure?.Message);
        Assert.True(marked.Value.Supported);

        Result<CopyReport> copied = await box.Bench.CopyAsync(
            "build.sh", "copy.sh", recursive: false, overwrite: false, preserveTimes: false);
        Assert.True(copied.IsOk, copied.Failure?.Message);

        Assert.True(File.GetUnixFileMode(box.Full("copy.sh")).HasFlag(UnixFileMode.UserExecute),
            "a copied script must still be executable");

        Result<MoveReport> moved = await box.Bench.MoveAsync("copy.sh", "moved.sh", overwrite: false);
        Assert.True(moved.IsOk, moved.Failure?.Message);
        Assert.True(File.GetUnixFileMode(box.Full("moved.sh")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void FindReportsTheExecutableBitAsAbsentRatherThanInventedOnWindows()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "a");

        FileFacts facts = Tree.Facts(box.Full("a.txt"), isDirectory: false);

        if (OperatingSystem.IsWindows()) Assert.Null(facts.Executable);
        else Assert.NotNull(facts.Executable);
    }

    [WindowsFact]
    public async Task ExecSaysItIsUnsupportedRatherThanPretending()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "a");

        Result<ExecReport> set = await box.Bench.SetExecutableAsync("a.txt", executable: true);

        Assert.True(set.IsOk, set.Failure?.Message);
        Assert.False(set.Value.Supported);
    }

    // ---- R10.29: the read-only attribute ----

    [WindowsFact]
    public async Task AReadOnlyFileIsRefusedByNameAndForceClearsIt()
    {
        using var box = new Sandbox();
        box.Write("locked.txt", "content");
        File.SetAttributes(box.Full("locked.txt"), FileAttributes.ReadOnly);

        Result<DeleteReport> refused = await box.Bench.DeleteAsync("locked.txt", recursive: false, force: false);
        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.Denied, refused.Failure!.Outcome);
        Assert.Contains("read-only", refused.Failure.Message);
        Assert.True(File.Exists(box.Full("locked.txt")));

        Result<DeleteReport> forced = await box.Bench.DeleteAsync("locked.txt", recursive: false, force: true);
        Assert.True(forced.IsOk, forced.Failure?.Message);
        Assert.False(File.Exists(box.Full("locked.txt")));
    }

    // ---- R10.28: metadata survival ----

    /// <summary>
    /// R4.18. NTFS alternate data streams carry the Mark-of-the-Web, so
    /// an edit that drops them weakens a security marker as a side
    /// effect of changing text.
    /// </summary>
    [WindowsFact]
    public async Task AnEditKeepsAnAlternateDataStream()
    {
        using var box = new Sandbox();
        box.Write("downloaded.txt", "original\n");

        const string mark = "[ZoneTransfer]\r\nZoneId=3\r\n";
        try
        {
            File.WriteAllText(box.Full("downloaded.txt") + ":Zone.Identifier", mark);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return;    // not NTFS: nothing to preserve, nothing to prove
        }

        Result<EditAnswer> edited = await box.Bench.EditAsync(
            [new FileEdits("downloaded.txt", null, [new Edit.Replace("original", "changed")])], dryRun: false);

        Assert.True(edited.IsOk, edited.Failure?.Message);
        Assert.Equal("changed\n", box.ReadText("downloaded.txt"));

        string carried = File.ReadAllText(box.Full("downloaded.txt") + ":Zone.Identifier");
        Assert.Equal(mark, carried);
    }

    /// <summary>R4.18's alternative: what could not be carried is named
    /// rather than swallowed. Nothing lost means an empty list.</summary>
    [Fact]
    public async Task AnOrdinaryEditReportsNothingLost()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "one\n");

        Result<EditAnswer> edited = await box.Bench.EditAsync(
            [new FileEdits("a.txt", null, [new Edit.Replace("one", "two")])], dryRun: false);

        Assert.True(edited.IsOk, edited.Failure?.Message);
        Assert.Empty(edited.Value.Files[0].MetadataLost);
    }

    // ---- R10.31: timestamps ----

    [Fact]
    public async Task APreservingCopyKeepsTheModifiedTimeAndAPlainOneDoesNot()
    {
        using var box = new Sandbox();
        box.Write("source.txt", "content");

        var past = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(box.Full("source.txt"), past);

        Assert.True((await box.Bench.CopyAsync("source.txt", "kept.txt", false, false, preserveTimes: true)).IsOk);
        Assert.True((await box.Bench.CopyAsync("source.txt", "fresh.txt", false, false, preserveTimes: false)).IsOk);

        Assert.Equal(past, File.GetLastWriteTimeUtc(box.Full("kept.txt")));
        Assert.NotEqual(past, File.GetLastWriteTimeUtc(box.Full("fresh.txt")));
    }

    // ---- R10.30: links ----

    [Fact]
    public async Task ACopiedTreeDoesNotFollowLinksAndSaysWhatItSkipped()
    {
        using var box = new Sandbox();
        Directory.CreateDirectory(box.Full("tree"));
        box.Write("tree/real.txt", "real");
        box.Write("target.txt", "target");

        try
        {
            File.CreateSymbolicLink(box.Full("tree/link.txt"), box.Full("target.txt"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;    // Windows without Developer Mode: R6.11's own asymmetry
        }

        Result<CopyReport> copied = await box.Bench.CopyAsync(
            "tree", "copy", recursive: true, overwrite: false, preserveTimes: false);

        Assert.True(copied.IsOk, copied.Failure?.Message);
        Assert.Equal(1, copied.Value.Files);
        Assert.Single(copied.Value.LinksSkipped);
        Assert.False(File.Exists(box.Full("copy/link.txt")));
    }

    // ---- R10.8: a decomposed filename round trip ----

    /// <summary>
    /// R9.2: macOS normalizes filenames toward decomposed form and
    /// Windows returns what was written, so a file created with a
    /// composed name has to be findable, re-openable and matchable by a
    /// pattern written in either form.
    /// </summary>
    [MacFact]
    public void AComposedFilenameIsFoundListedReopenedAndMatched()
    {
        using var box = new Sandbox();

        string composed = "café.txt";                 // é as one code point
        string decomposed = "café.txt";              // e followed by a combining acute
        box.WriteRaw(composed, Encoding.UTF8.GetBytes("content"));

        Result<Bounded<FoundFile>> found = box.Bench.FindChecked(new FindRequest(Pattern: "*.txt"));
        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Single(found.Value.Items);

        // Re-openable by the name that came back.
        Result<IReadOnlyList<ReadSlice>> read = box.Bench.Read(
            new ReadRequest([found.Value.Items[0].Path.Display]));
        Assert.True(read.IsOk, read.Failure?.Message);

        // And matched by a pattern written in the other normalization.
        Result<Glob> other = Glob.Compile(decomposed);
        Assert.True(other.IsOk);
        Assert.True(other.Value.Matches(found.Value.Items[0].Path.Display),
            "a pattern in the other normal form must still match");
    }
}
