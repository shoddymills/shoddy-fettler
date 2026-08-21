using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// The files that say what the ASSISTANT may do, not just what this tool
/// may do.
///
/// <para>Fettler's own declaration was protected while
/// <c>.claude/settings.json</c> - which names the tools a model may call
/// at all - sat inside the tree as an ordinary writable file. The
/// boundary was never breached to reach it: the project's Claude
/// configuration lives in the project, so "the file governing the
/// assistant" and "a file the assistant may write" were the same
/// file.</para>
/// </summary>
public sealed class ClientRuleFileTests
{
    [Theory]
    [InlineData(".fettler.json")]
    [InlineData(".fettler.local.json")]
    [InlineData(".mcp.json")]
    [InlineData("nested/deep/.mcp.json")]
    [InlineData(".claude/settings.json")]
    [InlineData(".claude/settings.local.json")]
    [InlineData(".vscode/mcp.json")]
    [InlineData("c:/work/tree/.claude/settings.json")]
    public void FilesThatDecideWhatMayRunAreGoverned(string path)
    {
        Assert.True(RuleFiles.Governs(path), $"{path} must be refused");
    }

    /// <summary>Windows spells the same path with backslashes, and a rule
    /// that held on one separator only would be a rule that quietly did
    /// not apply on the other platform.</summary>
    [Fact]
    public void SeparatorsDoNotDecide()
    {
        Assert.True(RuleFiles.Governs(@"c:\work\tree\.claude\settings.json"));
        Assert.True(RuleFiles.Governs(@".vscode\mcp.json"));
    }

    /// <summary>
    /// The other half, and the one that keeps this usable. A bare
    /// <c>settings.json</c> is an ordinary file, and
    /// <c>.vscode/settings.json</c> is a person's editor configuration
    /// with no say in what an assistant may do. <c>CLAUDE.md</c>
    /// persuades rather than permits, and maintaining a project's
    /// instructions is ordinary work.
    /// </summary>
    [Theory]
    [InlineData("settings.json")]
    [InlineData(".vscode/settings.json")]
    [InlineData("CLAUDE.md")]
    [InlineData(".claude/notes.md")]
    [InlineData(".fettler.json.bak")]
    [InlineData("src/mcp.json")]
    public void OrdinaryFilesStayOrdinary(string path)
    {
        Assert.False(RuleFiles.Governs(path), $"{path} must remain writable");
    }

    [Fact]
    public async Task TheWritePathRefusesAClientConfiguration()
    {
        using var box = new Sandbox();

        Result<Saved> wrote = await box.Bench.WriteAsync(
            ".mcp.json", """{"mcpServers":{}}""", overwrite: true);

        Assert.False(wrote.IsOk, "a model must not rewrite which servers launch");
        Assert.Equal(Outcome.Governed, wrote.Failure!.Outcome);
        Assert.False(File.Exists(box.Full(".mcp.json")), "and nothing may be left behind");
    }

    /// <summary>The refusal names every file it covers, so a caller told
    /// "no" can see the whole rule rather than discovering it one path at
    /// a time.</summary>
    [Fact]
    public void TheRefusalNamesTheWholeSet()
    {
        Result<string> refused = RuleFiles.Refuse<string>(".claude/settings.json");

        Assert.Equal(Outcome.Governed, refused.Failure!.Outcome);
        Assert.Contains(".claude/settings.json", refused.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains(".fettler.json", refused.Failure!.Message, StringComparison.Ordinal);
    }
}
