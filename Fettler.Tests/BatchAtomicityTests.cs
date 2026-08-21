// Copyright (c) 2026 Stephen Vincent Foster.
// Licensed under the MIT License. See the LICENSE file in the project root.

using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// What a multi-file <c>replace</c> promises when it goes wrong, asserted
/// rather than described.
///
/// <para>docs/fettler.html makes this claim in a table, and the claim has
/// two halves that are NOT the same strength. The page said "nothing is
/// written at all" without qualification, and the code disagreed with it -
/// <see cref="Editor"/> resolves the whole batch before writing anything,
/// and then writes file by file, with a message for the case where the
/// fifteenth write fails after fourteen have already landed. That message
/// could not exist if the unqualified claim were true.</para>
///
/// <para>So both halves are pinned here:</para>
/// <list type="bullet">
/// <item>a RESOLUTION failure writes nothing, and the tree is untouched;</item>
/// <item>a WRITE failure says which file stopped it and how many were
/// already done, so the survivors are identified rather than guessed.</item>
/// </list>
///
/// <para>The second is the one that had no test, which is how the page came
/// to overstate it in the first place.</para>
/// </summary>
public sealed class BatchAtomicityTests
{
    const string Find = "Alpha";
    const string With = "Beta";

    /// <summary>
    /// The strong half. A permission the tree does not grant is found while
    /// resolving, which is before the first byte moves - so every file in
    /// the batch is exactly as it was, including the ones that would have
    /// been written first.
    /// </summary>
    [Fact]
    public async Task AResolutionFailureLeavesEveryFileExactlyAsItWas()
    {
        // Readable, listable, and NOT updatable. The scope replaces what
        // the tree grants rather than adding to it, so this withholds
        // update inside `frozen` and nothing else.
        using var box = new Sandbox(Permissions.Full,
        [
            new ScopeDecl("frozen", Permission.List | Permission.Read),
        ]);

        box.Write("one.cs", "class Alpha { }\n");
        box.Write("two.cs", "class Alpha { }\n");
        box.Write("frozen/three.cs", "class Alpha { }\n");

        Result<ReplaceAnswer> answer = await box.Bench.ReplaceAsync(
            Find, With, glob: null, root: null, dryRun: false, includeGenerated: false);

        Assert.False(answer.IsOk, "a file the tree may not update must stop the batch");

        // THE ASSERTION THAT MATTERS. Not the outcome - the disk. one.cs and
        // two.cs are perfectly writable and would have been written first if
        // the writing had begun at all.
        foreach (string path in new[] { "one.cs", "two.cs", "frozen/three.cs" })
        {
            Assert.Equal("class Alpha { }\n", box.ReadText(path).Replace("\r\n", "\n"));
            Assert.DoesNotContain(With, box.ReadText(path), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The half that is a report rather than a guarantee. A write can fail
    /// for reasons no tool prevents - a full disk, a file another process
    /// holds - and when it does, earlier files are already written. What is
    /// promised is that you are told, precisely.
    ///
    /// <para>The file to block is chosen from the DRY RUN's own plan, not
    /// guessed: the plan lists the files in the order the writer will take
    /// them, so blocking the second one puts exactly one write in front of
    /// the failure on any filesystem, whatever order the walk produced.</para>
    /// </summary>
    [Fact]
    public async Task AWriteThatFailsPartWaySaysHowManyWereAlreadyWritten()
    {
        // One directory each, because the only way to deny a write on a
        // POSIX filesystem is to deny it on the directory: rename(2) does
        // not consult the destination FILE's mode, so a read-only file
        // would be replaced quite happily.
        using var box = new Sandbox();
        box.Write("a/one.cs", "class Alpha { }\n");
        box.Write("b/two.cs", "class Alpha { }\n");
        box.Write("c/three.cs", "class Alpha { }\n");

        Result<ReplaceAnswer> planned = await box.Bench.ReplaceAsync(
            Find, With, glob: null, root: null, dryRun: true, includeGenerated: false);
        Assert.True(planned.IsOk, planned.Failure?.Message);
        Assert.Equal(3, planned.Value.Files.Count);

        string first = planned.Value.Files[0].Path;
        string blocked = planned.Value.Files[1].Path;
        string never = planned.Value.Files[2].Path;

        Block(box.Full(blocked));
        try
        {
            Result<ReplaceAnswer> answer = await box.Bench.ReplaceAsync(
                Find, With, glob: null, root: null, dryRun: false, includeGenerated: false);

            Assert.False(answer.IsOk, "the blocked write must fail the operation");
            Assert.Equal(Outcome.Denied, answer.Failure!.Outcome);

            // It names how many landed...
            Assert.Contains("1 file(s) were already replaced before this one failed",
                answer.Failure.Message, StringComparison.Ordinal);
            // ...and which file it stopped at, so the two together identify
            // the survivors without re-reading the tree.
            Assert.Equal(blocked, answer.Failure.Path);

            // And the disk agrees with the report, which is the whole point:
            // a count that did not match what happened would be worse than
            // no count at all.
            Assert.Contains(With, box.ReadText(first), StringComparison.Ordinal);
            Assert.DoesNotContain(With, box.ReadText(blocked), StringComparison.Ordinal);
            Assert.DoesNotContain(With, box.ReadText(never), StringComparison.Ordinal);
        }
        finally
        {
            Unblock(box.Full(blocked));
        }
    }

    /// <summary>
    /// A dry run writes nothing, however many files it plans - the claim
    /// the page leans on when it says the run says what will happen before
    /// the first byte moves.
    /// </summary>
    [Fact]
    public async Task ADryRunPlansEveryFileAndWritesNone()
    {
        using var box = new Sandbox();
        box.Write("one.cs", "class Alpha { }\n");
        box.Write("two.cs", "class Alpha, Alpha { }\n");

        Result<ReplaceAnswer> answer = await box.Bench.ReplaceAsync(
            Find, With, glob: null, root: null, dryRun: true, includeGenerated: false);

        Assert.True(answer.IsOk, answer.Failure?.Message);
        Assert.True(answer.Value.DryRun);
        Assert.Equal(2, answer.Value.Files.Count);
        Assert.Equal(3, answer.Value.TotalOccurrences);
        // A dry run reports a plan and an empty written-list, which is the
        // distinction the page rests on: planned is not written.
        Assert.Empty(answer.Value.Written);

        Assert.DoesNotContain(With, box.ReadText("one.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain(With, box.ReadText("two.cs"), StringComparison.Ordinal);
    }

    // ---- making one write fail, on either platform ----
    //
    // Two mechanisms because the platforms refuse differently, and neither
    // is a simulation: both are exactly what a real machine does when a
    // file cannot be replaced.

    static void Block(string full)
    {
        if (OperatingSystem.IsWindows())
        {
            // SafeWrite reads this attribute deliberately (R6.10) and
            // refuses by name, rather than letting the replace throw
            // something that names nothing.
            File.SetAttributes(full, File.GetAttributes(full) | FileAttributes.ReadOnly);
        }
        else
        {
            // No write bit on the directory, so the temp file cannot be
            // created beside the target and the replace never begins.
            string dir = Path.GetDirectoryName(full)!;
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    static void Unblock(string full)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(full, File.GetAttributes(full) & ~FileAttributes.ReadOnly);
        }
        else
        {
            string dir = Path.GetDirectoryName(full)!;
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
    }
}
