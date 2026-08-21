using Burler;
using Xunit;

namespace Burler.Tests;

/// <summary>
/// R7.3: the window arithmetic and the BIO decode, which are the two
/// places a model's output is turned back into an answer - and the two
/// places a quiet mistake shows up as "found nothing" rather than as a
/// failure.
/// </summary>
public sealed class TextTests
{
    // ---- windows ----

    [Fact]
    public void NothingToWindowIsNoWindows()
    {
        Assert.Empty(Windows.Over(0));
        Assert.Empty(Windows.Over(-1));
    }

    [Fact]
    public void APayloadThatFitsIsOneWindow()
    {
        IReadOnlyList<Window> windows = Windows.Over(Windows.Content);

        Assert.Single(windows);
        Assert.Equal(0, windows[0].Start);
        Assert.Equal(Windows.Content, windows[0].Length);
    }

    /// <summary>One token past a window is what proves the cut happens at
    /// all, and that the second window is not a whole empty one.</summary>
    [Fact]
    public void OneTokenPastAWindowIsTwo()
    {
        IReadOnlyList<Window> windows = Windows.Over(Windows.Content + 1);

        Assert.Equal(2, windows.Count);
        Assert.Equal(Windows.Content + 1, windows[1].Start + windows[1].Length);
    }

    /// <summary>
    /// The property that matters: every token is inside some window, and
    /// no window names a token that is not there.
    ///
    /// <para>An entity is only findable if the tokens carrying it are
    /// somewhere the model actually looks, so a gap here is a silent hole
    /// in the screen rather than an error anybody would see.</para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(509)]
    [InlineData(510)]
    [InlineData(511)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void EveryTokenIsCoveredAndNoneIsInvented(int tokens)
    {
        IReadOnlyList<Window> windows = Windows.Over(tokens);

        var covered = new bool[tokens];
        foreach (Window window in windows)
        {
            Assert.True(window.Length > 0, "an empty window is a wasted inference");
            Assert.True(window.Length <= Windows.Content, "a window past the model's limit");
            Assert.True(window.Start >= 0);
            Assert.True(window.Start + window.Length <= tokens, "a window naming a token that is not there");

            for (int i = window.Start; i < window.Start + window.Length; i++) covered[i] = true;
        }

        for (int i = 0; i < tokens; i++)
            Assert.True(covered[i], $"token {i} of {tokens} is in no window");
    }

    /// <summary>
    /// Consecutive windows overlap, which is the whole reason for the
    /// stride: an entity straddling a cut has to be wholly inside at
    /// least one window or it cannot be recognised in either.
    /// </summary>
    [Fact]
    public void ConsecutiveWindowsOverlapByTheStride()
    {
        IReadOnlyList<Window> windows = Windows.Over(5000);

        Assert.True(windows.Count > 2);

        for (int i = 1; i < windows.Count; i++)
        {
            int previousEnd = windows[i - 1].Start + windows[i - 1].Length;
            int overlap = previousEnd - windows[i].Start;

            Assert.True(overlap >= Windows.Stride || windows[i].Start + windows[i].Length == 5000,
                $"windows {i - 1} and {i} overlap by {overlap}, under the {Windows.Stride} stride");
        }
    }

    // ---- BIO decode ----

    static IReadOnlyList<Span> At(params int[] positions)
    {
        var spans = new List<Span>();
        foreach (int p in positions) spans.Add(new Span(p, p + 1));
        return spans;
    }

    [Fact]
    public void NothingLabelledIsNothingFound() =>
        Assert.Empty(Decode.Entities(["O", "O", "O"], At(0, 1, 2)));

    [Fact]
    public void ABeginningAndItsInsidesAreOneEntity()
    {
        IReadOnlyList<Span> found = Decode.Entities(["O", "B-PER", "I-PER", "O"], At(0, 1, 2, 3));

        Assert.Single(found);
        Assert.Equal(new Span(1, 3), found[0]);
    }

    /// <summary>Two beginnings are two entities even with no O between
    /// them. That is what B- is for, and collapsing them would report two
    /// patients as one.</summary>
    [Fact]
    public void TwoBeginningsAreTwoEntities()
    {
        IReadOnlyList<Span> found = Decode.Entities(["B-PER", "B-PER"], At(0, 1));

        Assert.Equal(2, found.Count);
    }

    /// <summary>
    /// A stray I- with no B- before it opens an entity rather than being
    /// discarded.
    ///
    /// <para>Ragged exports do this, and the two possible mistakes are
    /// not equal: opening one denies a disclosure that might have been
    /// servable, and discarding it serves one that might have carried a
    /// patient name.</para>
    /// </summary>
    [Fact]
    public void AStrayInsideStillOpensAnEntity()
    {
        IReadOnlyList<Span> found = Decode.Entities(["O", "I-PER", "O"], At(0, 1, 2));

        Assert.Single(found);
        Assert.Equal(new Span(1, 2), found[0]);
    }

    /// <summary>Some checkpoints emit a bare type with no BIO prefix at
    /// all. A run of them is one entity, not one per token.</summary>
    [Fact]
    public void AFlatSchemeRunsTogether()
    {
        IReadOnlyList<Span> found = Decode.Entities(["PER", "PER", "O", "LOC"], At(0, 1, 2, 3));

        Assert.Equal(2, found.Count);
        Assert.Equal(new Span(0, 2), found[0]);
    }

    [Fact]
    public void AChangeOfTypeEndsTheEntity()
    {
        IReadOnlyList<Span> found = Decode.Entities(["B-PER", "I-LOC"], At(0, 1));

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void AnEntityRunningToTheEndIsStillClosed()
    {
        IReadOnlyList<Span> found = Decode.Entities(["O", "B-PER", "I-PER"], At(0, 1, 2));

        Assert.Single(found);
        Assert.Equal(new Span(1, 3), found[0]);
    }

    // ---- merge ----

    /// <summary>The same entity found by two overlapping windows is one
    /// entity. Counting it twice would inflate the only number a person
    /// ever sees about a finding.</summary>
    [Fact]
    public void TheSameSpanFoundTwiceIsOne()
    {
        IReadOnlyList<Span> merged = Decode.Merge([new Span(4, 9), new Span(4, 9)]);

        Assert.Single(merged);
        Assert.Equal(new Span(4, 9), merged[0]);
    }

    [Fact]
    public void OverlappingSpansBecomeOne()
    {
        IReadOnlyList<Span> merged = Decode.Merge([new Span(4, 9), new Span(6, 12)]);

        Assert.Single(merged);
        Assert.Equal(new Span(4, 12), merged[0]);
    }

    /// <summary>Touching is not overlapping. Two entities written side by
    /// side are two, and joining them would UNDERCOUNT - the direction
    /// that matters least for the deny decision and most for the
    /// number.</summary>
    [Fact]
    public void TouchingSpansStayTwo()
    {
        IReadOnlyList<Span> merged = Decode.Merge([new Span(4, 9), new Span(9, 12)]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void MergeSortsWhateverOrderItIsGiven()
    {
        IReadOnlyList<Span> merged = Decode.Merge([new Span(20, 25), new Span(1, 3), new Span(10, 12)]);

        Assert.Equal(3, merged.Count);
        Assert.Equal(new Span(1, 3), merged[0]);
        Assert.Equal(new Span(20, 25), merged[2]);
    }
}
