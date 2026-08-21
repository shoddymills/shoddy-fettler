using Microsoft.ML.Tokenizers;
using Xunit;

namespace Burler.Tests;

/// <summary>
/// R7.3: the tokenizer round trip.
///
/// <para><b>What is actually under test is the OFFSETS.</b> The ids
/// matter to the model and nothing else; the offsets are what turns a
/// labelled token back into a range of the payload, and a decoder whose
/// offsets are wrong reports findings in the wrong place - or, far worse
/// and far quieter, reports none because every span came out empty.</para>
///
/// <para>A tiny synthetic vocabulary, because a real checkpoint's is 30k
/// lines and none of them would make this assert anything more.</para>
/// </summary>
public sealed class TokenizerTests : IDisposable
{
    readonly string directory;
    readonly BertTokenizer tokenizer;

    public TokenizerTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "burler-vocab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string vocab = Path.Combine(directory, "vocab.txt");
        File.WriteAllLines(vocab,
        [
            "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]",
            "the", "patient", "was", "seen", "today",
            "renee", "##s", "born", "on",
        ]);

        tokenizer = BertTokenizer.Create(vocab);
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is not a failure */ }
    }

    /// <summary>
    /// Every token's offset names the text that token came from.
    ///
    /// <para>This is the assertion the whole decode rests on: if slicing
    /// the payload by a token's offset does not give that token back,
    /// then every span burler reports is pointing somewhere else.</para>
    /// </summary>
    [Fact]
    public void EveryOffsetNamesTheTextItCameFrom()
    {
        const string text = "the patient was seen today";

        IReadOnlyList<EncodedToken> tokens = tokenizer.EncodeToTokens(text, out _);

        foreach (EncodedToken one in tokens)
        {
            if (one.Value is "[CLS]" or "[SEP]") continue;

            int from = one.Offset.Start.Value;
            int to = one.Offset.End.Value;

            Assert.True(from >= 0 && to <= text.Length && to >= from,
                $"'{one.Value}' has an offset outside the text: {from}..{to}");

            Assert.Equal(one.Value, text[from..to]);
        }
    }

    /// <summary>The offsets march forwards. A decoder builds an entity by
    /// taking the first token's start and the last one's end, which is
    /// only the right answer if they are in order.</summary>
    [Fact]
    public void OffsetsRunForwards()
    {
        IReadOnlyList<EncodedToken> tokens =
            tokenizer.EncodeToTokens("the patient was seen today", out _);

        int previous = -1;
        foreach (EncodedToken one in tokens)
        {
            if (one.Value is "[CLS]" or "[SEP]") continue;

            Assert.True(one.Offset.Start.Value >= previous,
                "a token's offset went backwards, so a span built from a run of them would invert");
            previous = one.Offset.Start.Value;
        }
    }

    /// <summary>
    /// A word the vocabulary does not have still produces a token with a
    /// usable offset, rather than vanishing.
    ///
    /// <para>It matters here more than in an ordinary tokenizer: an
    /// unknown word in a screened payload is very often exactly the
    /// proper noun the screen is looking for, and a token that vanished
    /// would take its entity with it.</para>
    /// </summary>
    [Fact]
    public void AnUnknownWordStillCarriesAnOffset()
    {
        const string text = "the zzzqqq was seen";

        IReadOnlyList<EncodedToken> tokens = tokenizer.EncodeToTokens(text, out _);

        bool covered = false;
        foreach (EncodedToken one in tokens)
        {
            if (one.Value is "[CLS]" or "[SEP]") continue;

            int from = one.Offset.Start.Value;
            int to = one.Offset.End.Value;
            if (from <= 4 && to >= 10) covered = true;

            Assert.True(to <= text.Length, "an offset ran past the end of the text");
        }

        Assert.True(covered, "the unknown word is in no token's offset, so nothing could ever find it");
    }

    /// <summary>A slice of the payload re-encodes on its own, which is
    /// how each window is fed to the model - the alternative needs the
    /// special tokens' ids and a guess at a tokenizer's surface.</summary>
    [Fact]
    public void ASliceEncodesOnItsOwn()
    {
        const string text = "the patient was seen today";

        IReadOnlyList<EncodedToken> whole = tokenizer.EncodeToTokens(text, out _);
        IReadOnlyList<EncodedToken> slice = tokenizer.EncodeToTokens(text[4..11], out _);

        Assert.NotEmpty(whole);
        Assert.NotEmpty(slice);

        foreach (EncodedToken one in slice)
            if (one.Value is not ("[CLS]" or "[SEP]"))
                Assert.Equal(one.Value, text[4..11][one.Offset.Start.Value..one.Offset.End.Value]);
    }
}
