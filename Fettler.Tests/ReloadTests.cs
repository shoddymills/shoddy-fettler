using System.Text.Json;
using Fettler.Core;
using Fettler.Mcp;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// The serve boundary is re-read before every request: a grant appears
/// on the next call, a revocation binds on the next call, a malformed
/// edit refuses until it is fixed, and a configuration that has gone
/// missing refuses rather than widening. The CLI has these properties by
/// construction - every command is a fresh process reading the file anew
/// - and serve used to hold its launch reading for the life of the
/// process, which left the boundary wider than the file after a person
/// narrowed it: the one direction it must never fail in.
/// </summary>
public sealed class ReloadTests : IDisposable
{
    readonly string dir;
    readonly string file;

    public ReloadTests()
    {
        dir = Directory.CreateTempSubdirectory("fettle-reload-").FullName;
        file = Path.Combine(dir, RootsFile.FileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Write the declaration the way a person does: with an
    /// editor, from outside Fettler.</summary>
    void Declare(string json) => File.WriteAllText(file, json);

    /// <summary>A server on the sandbox's declaration, wired the way
    /// serve wires it: the launch reading held, the same file re-read
    /// before every request.</summary>
    McpServer Server(out Bench bench)
    {
        Result<Core.Roots> opened = RootsFile.Reopen(file);
        Assert.True(opened.IsOk, opened.Failure?.Message);
        bench = new Bench(opened.Value);
        return new McpServer(bench, TextReader.Null, TextWriter.Null, () => RootsFile.Reopen(file));
    }

    static string Call(int id, string tool, string arguments = "{}") =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\","
        + "\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}";

    /// <summary>The one content part of a tools/call reply, and whether
    /// the reply was marked as an error.</summary>
    static (string Text, bool IsError) Answer(string? reply)
    {
        Assert.NotNull(reply);
        JsonElement result = JsonDocument.Parse(reply!).RootElement.GetProperty("result");
        string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        bool isError = result.TryGetProperty("isError", out JsonElement e) && e.GetBoolean();
        return (text, isError);
    }

    [Fact]
    public async Task ATaskDeclaredAfterLaunchIsListedOnTheNextRequest()
    {
        Declare("""{"trees":{"work":{"path":"."}}}""");
        using McpServer server = Server(out Bench bench);
        using Bench _ = bench;

        (string before, bool beforeError) = Answer(await server.AnswerAsync(Call(1, "tasks")));
        Assert.False(beforeError, before);
        Assert.DoesNotContain("probe", before);

        Declare("""{"trees":{"work":{"path":"."}},"tasks":{"probe":{"run":"echo hello"}}}""");

        (string after, bool afterError) = Answer(await server.AnswerAsync(Call(2, "tasks")));
        Assert.False(afterError, after);
        Assert.Contains("probe", after);
    }

    [Fact]
    public async Task ARevocationBindsOnTheNextRequest()
    {
        Declare("""{"trees":{"work":{"path":"."}}}""");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "alpha\n");
        using McpServer server = Server(out Bench bench);
        using Bench _ = bench;

        (string granted, bool grantedError) = Answer(
            await server.AnswerAsync(Call(1, "read", """{"paths":["a.txt"]}""")));
        Assert.False(grantedError, granted);
        Assert.Contains("alpha", granted);

        // The person narrows the tree to list alone. No restart follows,
        // because none is needed - and none must be needed, or the wider
        // boundary outlives the file that granted it.
        Declare("""{"trees":{"work":{"path":".","can":["list"]}}}""");

        (string revoked, bool revokedError) = Answer(
            await server.AnswerAsync(Call(2, "read", """{"paths":["a.txt"]}""")));
        Assert.True(revokedError, "a revoked permission must refuse on the next request");
        Assert.DoesNotContain("alpha", revoked);
    }

    [Fact]
    public async Task AMalformedEditRefusesEveryRequestUntilItIsFixed()
    {
        Declare("""{"trees":{"work":{"path":"."}}}""");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "alpha\n");
        using McpServer server = Server(out Bench bench);
        using Bench _ = bench;

        Declare("""{"trees":{"work":""");

        // Refusal, never fallback: answering from the last good reading
        // would hide the fault and serve a boundary nobody can read.
        (string broken, bool brokenError) = Answer(
            await server.AnswerAsync(Call(1, "read", """{"paths":["a.txt"]}""")));
        Assert.True(brokenError, "a malformed declaration must refuse the request");
        Assert.Contains("JSON", broken);
        Assert.DoesNotContain("alpha", broken);

        Declare("""{"trees":{"work":{"path":"."}}}""");

        (string fixedUp, bool fixedError) = Answer(
            await server.AnswerAsync(Call(2, "read", """{"paths":["a.txt"]}""")));
        Assert.False(fixedError, fixedUp);
        Assert.Contains("alpha", fixedUp);
    }

    [Fact]
    public async Task AConfigurationThatGoesMissingRefusesRatherThanRediscovering()
    {
        // A broader declaration sits one level up, exactly where a fresh
        // discovery from the tree would find it. The server must refuse
        // instead: re-reading means the file found at launch, and a walk
        // up from where it was is how a boundary widens on its own.
        string child = Directory.CreateDirectory(Path.Combine(dir, "child")).FullName;
        File.WriteAllText(Path.Combine(dir, RootsFile.FileName), """{"trees":{"wide":{"path":"."}}}""");
        string childFile = Path.Combine(child, RootsFile.FileName);
        File.WriteAllText(childFile, """{"trees":{"work":{"path":"."}}}""");

        Result<Core.Roots> opened = RootsFile.Reopen(childFile);
        Assert.True(opened.IsOk, opened.Failure?.Message);
        using var bench = new Bench(opened.Value);
        using var server = new McpServer(bench, TextReader.Null, TextWriter.Null,
            () => RootsFile.Reopen(childFile));

        File.Delete(childFile);

        (string text, bool isError) = Answer(await server.AnswerAsync(Call(1, "tasks")));
        Assert.True(isError, "a missing declaration must refuse, not rediscover");
        Assert.Contains("no configuration", text);
        Assert.DoesNotContain("wide", text);
    }

    [Fact]
    public async Task ABoundaryWithNoFileBehindItStaysAsLaunched()
    {
        // The --root case: trees opened directly, no reload wired. A
        // declaration appearing later changes nothing, because there was
        // no file behind this boundary to re-read.
        Result<Core.Roots> opened = Core.Roots.Open(
            [new TreeDecl("work", dir, Permissions.ReadOnly)]);
        Assert.True(opened.IsOk, opened.Failure?.Message);
        using var bench = new Bench(opened.Value);
        using var server = new McpServer(bench, TextReader.Null, TextWriter.Null);

        Declare("""{"trees":{"work":{"path":"."}},"tasks":{"probe":{"run":"echo hello"}}}""");

        (string text, bool isError) = Answer(await server.AnswerAsync(Call(1, "tasks")));
        Assert.False(isError, text);
        Assert.DoesNotContain("probe", text);
    }

    /// <summary>R3.11 across readings: the snapshot shares its gate with
    /// the Bench it was taken from, so disposing one request's snapshot
    /// must leave the tree's turn-taking - and the next request - intact.
    /// </summary>
    [Fact]
    public async Task DisposingASnapshotLeavesTheSharedGateStanding()
    {
        using var box = new Sandbox();

        var snapshot = new Bench(box.Roots, box.Bench);
        snapshot.Dispose();

        Result<Saved> wrote = await box.Bench.WriteAsync("after.txt", "still standing\n", overwrite: false);
        Assert.True(wrote.IsOk, wrote.Failure?.Message);
    }
}
