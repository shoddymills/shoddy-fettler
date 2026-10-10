using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;
using Fettler.Mcp;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R1 of the tools-first enhancement: <c>roots</c> names the branch of a
/// tree that is a checkout, says the fact is local, and reads it at the
/// call rather than at launch.
/// </summary>
public sealed class CheckoutTests
{
    static Task<CliResult> Run(Bench bench, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), bench);

    static Bench BenchOn(string tree)
    {
        Result<Roots> opened = Roots.Open([new TreeDecl("work", tree, Permissions.Full)]);
        Assert.True(opened.IsOk, opened.Failure?.Message);
        return new Bench(opened.Value);
    }

    static JsonElement FirstTree(CliResult listed) =>
        JsonDocument.Parse(listed.Stdout).RootElement.GetProperty("roots")[0].Clone();

    [Fact]
    public async Task ACheckedOutBranchIsNamedAndSaidToBeLocal()
    {
        using var box = new Sandbox();
        box.Write(".git/HEAD", "ref: refs/heads/feature/x\n");

        CliResult human = await Run(box.Bench, "roots");
        Assert.Contains("branch: feature/x (local)", human.Stdout);

        JsonElement tree = FirstTree(await Run(box.Bench, "roots", "--json"));
        Assert.Equal("feature/x", tree.GetProperty("branch").GetString());
        Assert.False(tree.GetProperty("detached").GetBoolean());
        Assert.Equal(box.Root, tree.GetProperty("repository").GetString());
    }

    [Fact]
    public async Task ADetachedHeadIsNamedByItsShortHash()
    {
        using var box = new Sandbox();
        box.Write(".git/HEAD", "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678\n");

        CliResult human = await Run(box.Bench, "roots");
        Assert.Contains("detached at a1b2c3d (local)", human.Stdout);

        JsonElement tree = FirstTree(await Run(box.Bench, "roots", "--json"));
        Assert.Equal("a1b2c3d", tree.GetProperty("branch").GetString());
        Assert.True(tree.GetProperty("detached").GetBoolean());
    }

    /// <summary>A worktree or a submodule has a <c>.git</c> FILE naming
    /// the folder that holds its <c>HEAD</c>.</summary>
    [Fact]
    public async Task AGitFileIsFollowedToTheFolderItNames()
    {
        using var box = new Sandbox();
        box.Write(".git", "gitdir: elsewhere/worktrees/w\n");
        box.Write("elsewhere/worktrees/w/HEAD", "ref: refs/heads/wt-branch\n");

        CliResult human = await Run(box.Bench, "roots");
        Assert.Contains("branch: wt-branch (local)", human.Stdout);
    }

    [Fact]
    public async Task ATreeWithNoGitHasNoBranchLine()
    {
        using var box = new Sandbox();
        box.Write("a.txt", "plain folder\n");

        CliResult human = await Run(box.Bench, "roots");
        Assert.Equal(ExitCodes.Ok, human.ExitCode);
        Assert.DoesNotContain("branch:", human.Stdout);
        Assert.DoesNotContain("detached at", human.Stdout);

        JsonElement tree = FirstTree(await Run(box.Bench, "roots", "--json"));
        Assert.False(tree.TryGetProperty("branch", out _));
        Assert.False(tree.TryGetProperty("repository", out _));
    }

    /// <summary>The planning tree's shape: the tree is a folder inside a
    /// repository, and the <c>.git</c> sits outside the tree.</summary>
    [Fact]
    public async Task AGitTwoFoldersAboveTheTreeIsFoundAndItsFolderNamed()
    {
        using var box = new Sandbox();
        box.Write("repo/.git/HEAD", "ref: refs/heads/main\n");
        Directory.CreateDirectory(box.Full("repo/a/b"));

        using Bench bench = BenchOn(box.Full("repo/a/b"));

        CliResult human = await Run(bench, "roots");
        Assert.Contains("branch: main (local)", human.Stdout);
        Assert.Contains("repository: " + box.Full("repo"), human.Stdout);

        JsonElement tree = FirstTree(await Run(bench, "roots", "--json"));
        Assert.Equal(box.Full("repo"), tree.GetProperty("repository").GetString());
    }

    /// <summary>R1.3: a broken <c>.git</c> answers as no <c>.git</c>, and
    /// the rest of the answer is intact.</summary>
    [Fact]
    public async Task AHeadThatCannotBeReadAnswersNoBranchAndTheRestIsIntact()
    {
        using var box = new Sandbox();
        string head = box.Write(".git/HEAD", "ref: refs/heads/main\n");

        CliResult human;
        using (new FileStream(head, FileMode.Open, FileAccess.Read, FileShare.None))
            human = await Run(box.Bench, "roots");

        Assert.Equal(ExitCodes.Ok, human.ExitCode);
        Assert.DoesNotContain("branch:", human.Stdout);
        Assert.Contains("can: ", human.Stdout);
        Assert.Contains("declared by", human.Stdout);
    }

    /// <summary>R1.6: a branch switched in a terminal shows on the next
    /// call over MCP, with no restart.</summary>
    [Fact]
    public async Task ASwitchShowsOnTheNextCallWithoutARestart()
    {
        using var box = new Sandbox();
        box.Write(".git/HEAD", "ref: refs/heads/main\n");

        using var server = new McpServer(box.Bench, TextReader.Null, TextWriter.Null);
        const string call = """
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"roots","arguments":{}}}
            """;

        Assert.Contains("\"branch\":\"main\"", Text(await server.AnswerAsync(call)));

        box.Write(".git/HEAD", "ref: refs/heads/feature/y\n");

        Assert.Contains("\"branch\":\"feature/y\"", Text(await server.AnswerAsync(call)));
    }

    static string Text(string? reply)
    {
        Assert.NotNull(reply);
        return JsonDocument.Parse(reply!).RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!;
    }
}
