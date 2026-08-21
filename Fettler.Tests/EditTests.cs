using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R10.4, R10.5, R10.23 and R10.33: what an edit batch refuses, and the
/// assertion that a refusal writes <b>nothing</b>.
/// </summary>
public sealed class EditTests
{
    [Fact]
    public async Task AScopedReplacementApplies()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "alpha\nbeta\ngamma\n");

        Result<EditAnswer> done = await box.Bench.EditAsync(
            [new FileEdits("a.txt", null, [new Edit.Replace("beta", "BETA")])], dryRun: false);

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.Equal("alpha\nBETA\ngamma\n", box.ReadText("a.txt"));
    }

    /// <summary>
    /// R5.3 and R10.4: an ambiguous edit is refused and the file is not
    /// written at all. Requiring global uniqueness is what R5.4's line
    /// range exists to rescue, and this is the failure it rescues from.
    /// </summary>
    [Fact]
    public async Task AnAmbiguousEditIsRefusedAndNothingIsWritten()
    {
        using var box = new Sandbox();
        const string original = "x = 1\ny = 1\nz = 1\n";
        box.Write("a.txt", original);

        Result<EditAnswer> done = await box.Bench.EditAsync(
            [new FileEdits("a.txt", null, [new Edit.Replace("= 1", "= 2")])], dryRun: false);

        Assert.False(done.IsOk);
        Assert.Equal(Outcome.Conflict, done.Failure!.Outcome);
        Assert.Contains("3", done.Failure.Message);
        Assert.Equal(original, box.ReadText("a.txt"));
    }

    [Fact]
    public async Task ALineRangeDisambiguatesWithoutQuotingMoreContext()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "x = 1\ny = 1\nz = 1\n");

        Result<EditAnswer> done = await box.Bench.EditAsync(
            [new FileEdits("a.txt", null, [new Edit.Replace("= 1", "= 2", FromLine: 2, ToLine: 2)])],
            dryRun: false);

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.Equal("x = 1\ny = 2\nz = 1\n", box.ReadText("a.txt"));
    }

    /// <summary>R10.23: a batch spanning four files with one bad edit
    /// leaves all four unchanged and names the bad edit (R5.7).</summary>
    [Fact]
    public async Task ACrossFileBatchWithOneBadEditWritesNoneOfThem()
    {
        using var box = new Sandbox();
        for (int i = 1; i <= 4; i++) box.Write($"f{i}.txt", "keep me\n");

        Result<EditAnswer> done = await box.Bench.EditAsync([
            new FileEdits("f1.txt", null, [new Edit.Replace("keep me", "changed")]),
            new FileEdits("f2.txt", null, [new Edit.Replace("keep me", "changed")]),
            new FileEdits("f3.txt", null, [new Edit.Replace("absent", "changed")]),   // the bad one
            new FileEdits("f4.txt", null, [new Edit.Replace("keep me", "changed")]),
        ], dryRun: false);

        Assert.False(done.IsOk);
        Assert.Equal(Outcome.NotFound, done.Failure!.Outcome);
        Assert.Contains("edit 1", done.Failure.Message);

        for (int i = 1; i <= 4; i++)
            Assert.Equal("keep me\n", box.ReadText($"f{i}.txt"));
    }

    [Fact]
    public async Task OverlappingEditsInOneBatchAreRefused()
    {
        using var box = new Sandbox();
        const string original = "abcdefgh\n";
        box.Write("a.txt", original);

        Result<EditAnswer> done = await box.Bench.EditAsync(
            [new FileEdits("a.txt", null, [
                new Edit.Replace("abcde", "X"),
                new Edit.Replace("cdefg", "Y"),
            ])], dryRun: false);

        Assert.False(done.IsOk);
        Assert.Equal(Outcome.Conflict, done.Failure!.Outcome);
        Assert.Equal(original, box.ReadText("a.txt"));
    }

    /// <summary>R5.2: edits do not perturb each other's addressing, so
    /// two edits in one batch both address the file as read.</summary>
    [Fact]
    public async Task TwoEditsInOneBatchBothAddressTheFileAsRead()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "one\ntwo\nthree\n");

        Result<EditAnswer> done = await box.Bench.EditAsync(
            [new FileEdits("a.txt", null, [
                new Edit.Replace("one", "1"),
                new Edit.DeleteLines(3, 3),
            ])], dryRun: false);

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.Equal("1\ntwo\n", box.ReadText("a.txt"));
    }

    // ---- R10.5 and R10.33: staleness, proved by hash ----

    [Fact]
    public async Task AStaleHashIsRefusedAndACurrentOneApplies()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "original\n");

        Result<IReadOnlyList<ReadSlice>> read = box.Bench.Read(new ReadRequest(["a.txt"]));
        Assert.True(read.IsOk);
        string hash = read.Value[0].Hash;

        // Somebody else edits the file between the read and the write.
        box.Write("a.txt", "moved on\n");

        Result<EditAnswer> stale = await box.Bench.EditAsync(
            [new FileEdits("a.txt", hash, [new Edit.Replace("moved", "changed")])], dryRun: false);

        Assert.False(stale.IsOk);
        Assert.Equal(Outcome.Stale, stale.Failure!.Outcome);
        Assert.Equal("moved on\n", box.ReadText("a.txt"));

        // With the hash of what is actually there, the same edit applies.
        string current = box.Bench.Read(new ReadRequest(["a.txt"])).Value[0].Hash;
        Result<EditAnswer> fresh = await box.Bench.EditAsync(
            [new FileEdits("a.txt", current, [new Edit.Replace("moved", "changed")])], dryRun: false);

        Assert.True(fresh.IsOk, fresh.Failure?.Message);
        Assert.Equal("changed on\n", box.ReadText("a.txt"));
    }

    [Fact]
    public async Task ADryRunReportsWhatItWouldDoAndWritesNothing()
    {
        using var box = new Sandbox();
        const string original = "alpha\nbeta\n";
        box.Write("a.txt", original);

        Result<EditAnswer> done = await box.Bench.EditAsync(
            [new FileEdits("a.txt", null, [new Edit.Replace("beta", "BETA")])], dryRun: true);

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.True(done.Value.DryRun);
        Assert.Single(done.Value.Plan);
        Assert.Empty(done.Value.Files);
        Assert.Equal(original, box.ReadText("a.txt"));
    }

    /// <summary>R4.2 through an edit: a CRLF file stays CRLF, so a
    /// one-line change does not become a whole-file diff.</summary>
    [Fact]
    public async Task AnEditKeepsTheFilesLineEndings()
    {
        using var box = new Sandbox();
        box.WriteRaw("a.txt", System.Text.Encoding.UTF8.GetBytes("one\r\ntwo\r\nthree\r\n"));

        Result<EditAnswer> done = await box.Bench.EditAsync(
            [new FileEdits("a.txt", null, [new Edit.Replace("two", "TWO")])], dryRun: false);

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.Equal("one\r\nTWO\r\nthree\r\n", box.ReadText("a.txt"));
    }

    [Fact]
    public async Task AnInsertUsesTheFilesDominantEnding()
    {
        using var box = new Sandbox();
        box.WriteRaw("a.txt", System.Text.Encoding.UTF8.GetBytes("one\r\ntwo\r\n"));

        Result<EditAnswer> done = await box.Bench.EditAsync(
            [new FileEdits("a.txt", null, [new Edit.InsertAfter(1, "inserted")])], dryRun: false);

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.Equal("one\r\ninserted\r\ntwo\r\n", box.ReadText("a.txt"));
    }

    // ---- R10.36: edit text from a file or a stream (R5.11) ----

    /// <summary>
    /// The clause exists because the tool was awkward on its own
    /// documentation: supplying a paragraph meant hand-escaping every
    /// quote and newline into JSON, which in practice meant writing a
    /// program to generate the edit script. Needing a second language to
    /// drive the editing tool is a defect in the editing tool.
    /// </summary>
    [Fact]
    public async Task ReplacementTextCanComeFromAFile()
    {
        using var box = new Sandbox();
        box.Write("page.html", "<p>OLD</p>\n");

        // Exactly the shape that was painful: quotes and newlines.
        const string awkward = "<div class=\"takeaway\">\n  <b>New</b> text\n</div>";
        box.Write("replacement.txt", awkward);

        CliResult done = await Command.RunAsync(
            ["edit", "page.html", "--replace", "OLD", "--with-file", box.Full("replacement.txt")],
            new StringReader(string.Empty), box.Bench);

        Assert.Equal(0, done.ExitCode);
        Assert.Equal($"<p>{awkward}</p>\n", box.ReadText("page.html"));
    }

    [Fact]
    public async Task ReplacementTextCanComeFromStdin()
    {
        using var box = new Sandbox();
        box.Write("page.html", "<p>OLD</p>\n");

        CliResult done = await Command.RunAsync(
            ["edit", "page.html", "--replace", "OLD", "--with-stdin"],
            new StringReader("piped \"text\""), box.Bench);

        Assert.Equal(0, done.ExitCode);
        Assert.Equal("<p>piped \"text\"</p>\n", box.ReadText("page.html"));
    }

    [Fact]
    public async Task TwoFlagsCannotBothClaimStdin()
    {
        using var box = new Sandbox();
        box.Write("page.html", "<p>OLD</p>\n");

        CliResult refused = await Command.RunAsync(
            ["edit", "page.html", "--replace-stdin", "--with-stdin"],
            new StringReader("OLD"), box.Bench);

        // Stdin is one stream and can be spent once. The second flag
        // would silently get nothing, which is the quiet wrong answer
        // this tool exists to avoid.
        Assert.Equal(ExitCodes.Invalid, refused.ExitCode);
        Assert.Equal("<p>OLD</p>\n", box.ReadText("page.html"));
    }

    [Fact]
    public async Task InsertAndDeleteAreReachableFromTheCommandLine()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "one\ntwo\nthree\n");

        CliResult inserted = await Command.RunAsync(
            ["edit", "a.txt", "--insert-after", "1", "--text-stdin"],
            new StringReader("inserted"), box.Bench);
        Assert.Equal(0, inserted.ExitCode);
        Assert.Equal("one\ninserted\ntwo\nthree\n", box.ReadText("a.txt"));

        CliResult deleted = await Command.RunAsync(
            ["edit", "a.txt", "--delete", "2-3"], new StringReader(string.Empty), box.Bench);
        Assert.Equal(0, deleted.ExitCode);
        Assert.Equal("one\nthree\n", box.ReadText("a.txt"));
    }

    [Fact]
    public async Task AScriptFieldCanNameAFileHoldingItsText()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "before MARK after\n");
        box.Write("body.txt", "a \"quoted\"\nmulti-line body");

        box.Write("edits.json",
            $$"""
              {"edits": [{"replace": "MARK", "withFile": {{System.Text.Json.JsonSerializer.Serialize(box.Full("body.txt"))}}}]}
              """);

        CliResult done = await Command.RunAsync(
            ["edit", "a.txt", "--script", box.Full("edits.json")],
            new StringReader(string.Empty), box.Bench);

        Assert.Equal(0, done.ExitCode);
        Assert.Equal("before a \"quoted\"\nmulti-line body after\n", box.ReadText("a.txt"));
    }

    // ---- R10.34: replace ----

    [Fact]
    public async Task ReplaceReportsInDryRunExactlyWhatItThenChanges()
    {
        using var box = new Sandbox();
        box.Write("a.cs", "IsMatch(x) && IsMatch(y)\n");
        box.Write("b.cs", "IsMatch(z)\n");
        box.Write("c.txt", "IsMatch(ignored)\n");

        Result<ReplaceAnswer> planned = await box.Bench.ReplaceAsync(
            "IsMatch", "Matches", "**/*.cs", null, dryRun: true, includeGenerated: false);

        Assert.True(planned.IsOk, planned.Failure?.Message);
        Assert.Equal(2, planned.Value.Files.Count);
        Assert.Equal(3, planned.Value.TotalOccurrences);
        Assert.Contains("IsMatch", box.ReadText("a.cs"));

        Result<ReplaceAnswer> applied = await box.Bench.ReplaceAsync(
            "IsMatch", "Matches", "**/*.cs", null, dryRun: false, includeGenerated: false);

        Assert.True(applied.IsOk, applied.Failure?.Message);
        Assert.Equal(planned.Value.TotalOccurrences, applied.Value.TotalOccurrences);
        Assert.Equal("Matches(x) && Matches(y)\n", box.ReadText("a.cs"));
        Assert.Equal("Matches(z)\n", box.ReadText("b.cs"));
        Assert.Equal("IsMatch(ignored)\n", box.ReadText("c.txt"));
    }
}
