using Picker.Core;
using Xunit;

namespace Picker.Tests;

/// <summary>
/// Picker's half of the burler protocol - the third independent
/// assertion of the one wire, after Fettler's and burler's own. No
/// shared assembly means each side proves the shape it speaks.
/// </summary>
public sealed class WireTests
{
    [Fact]
    public void AFindingsAnswerBecomesFindings()
    {
        Result<IReadOnlyList<ScreenFinding>> read = Sidecar.Read(
            """{"ok":true,"findings":[{"category":"clinical","count":2,"spans":[[0,4],[9,14]]}]}""");

        Assert.True(read.IsOk, read.Failure?.Message);
        Assert.Equal(2, read.Value.Count);
        Assert.All(read.Value, f => Assert.Equal(Screened.Clinical, f.Category));
    }

    [Fact]
    public void AnEmptyFindingsArrayIsACleanVerdict()
    {
        Result<IReadOnlyList<ScreenFinding>> read = Sidecar.Read(
            """{"ok":true,"findings":[]}""");

        Assert.True(read.IsOk);
        Assert.Empty(read.Value);
    }

    [Fact]
    public void AMissingFindingsArrayIsAFaultNotACleanVerdict()
    {
        Result<IReadOnlyList<ScreenFinding>> read = Sidecar.Read("""{"ok":true}""");

        Assert.False(read.IsOk);
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
        Assert.Contains("no findings array", read.Failure.Message);
    }

    [Theory]
    [InlineData("not json at all", "not JSON")]
    [InlineData("[1,2,3]", "not an object")]
    [InlineData("""{"findings":[]}""", "whether it succeeded")]
    [InlineData("""{"ok":false,"error":"model missing"}""", "model missing")]
    [InlineData("""{"ok":false}""", "did not say why")]
    [InlineData("""{"ok":true,"findings":[{"category":"clinical"}]}""", "without a category and a count")]
    [InlineData("""{"ok":true,"findings":[{"category":"astrology","count":1}]}""", "does not know")]
    public void EveryMalformedAnswerIsARefusalNeverAPass(string line, string expected)
    {
        Result<IReadOnlyList<ScreenFinding>> read = Sidecar.Read(line);

        Assert.False(read.IsOk);
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
        Assert.Contains(expected, read.Failure.Message);
    }

    [Fact]
    public void NoModelsDirectoryMeansNoSidecarAtAll()
    {
        Assert.Null(Sidecar.For(null));
        Assert.NotNull(Sidecar.For("somewhere"));
        Sidecar.For("somewhere")!.Dispose();
    }
}
