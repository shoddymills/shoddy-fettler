namespace Burler;

/// <summary>A half-open character range in the payload.</summary>
public readonly record struct Span(int Start, int End)
{
    public bool Overlaps(Span other) => Start < other.End && other.Start < End;
}

/// <summary>A run of content tokens handed to the model in one go.</summary>
public readonly record struct Window(int Start, int Length);

/// <summary>
/// Cutting a payload into the fixed-length pieces a BERT model can
/// actually take.
///
/// <para><b>512 tokens is the model's limit, not a choice.</b> The
/// position embeddings of a BERT checkpoint are trained to that length
/// and there is nothing sensible past it, so anything longer is cut and
/// each piece run separately.</para>
///
/// <para><b>The windows OVERLAP, and that is the whole point.</b> An
/// entity that straddles a cut - a patient name whose surname lands in
/// the next window - is unrecognisable in both halves. Advancing by less
/// than a full window means every position within <see cref="Stride"/>
/// tokens of a cut also appears comfortably inside its neighbour. The
/// price is that the overlap region is judged twice, which
/// <see cref="Decode.Merge"/> then collapses.</para>
/// </summary>
public static class Windows
{
    /// <summary>Tokens per window, INCLUDING the two the tokenizer adds
    /// at each end.</summary>
    public const int Size = 512;

    /// <summary>How much of each window repeats the one before it.</summary>
    public const int Stride = 128;

    /// <summary>Content tokens per window: the limit less the classifier's
    /// own two.</summary>
    public const int Content = Size - 2;

    /// <summary>
    /// The windows covering a payload of this many content tokens.
    ///
    /// <para>The last window ends exactly at the end of the payload
    /// rather than being padded out to a full one, so no window ever
    /// names a token that is not there.</para>
    /// </summary>
    public static IReadOnlyList<Window> Over(int tokens)
    {
        var windows = new List<Window>();
        if (tokens <= 0) return windows;

        int step = Content - Stride;

        for (int start = 0; ; start += step)
        {
            int length = Math.Min(Content, tokens - start);
            windows.Add(new Window(start, length));
            if (start + length >= tokens) break;
        }

        return windows;
    }
}

/// <summary>
/// Turning a model's per-token labels back into entities.
///
/// <para><b>BIO, decoded forgivingly in the direction that denies.</b>
/// A well-formed sequence opens an entity with <c>B-</c> and continues
/// it with <c>I-</c>. Exports are not always well formed - an
/// <c>I-</c> with no <c>B-</c> before it is common enough that every
/// serious decoder has a rule for it. Here that rule OPENS an entity
/// rather than discarding the token, because the two possible mistakes
/// are not equal: treating a stray <c>I-</c> as an entity denies a
/// disclosure that might have been servable, and discarding it serves
/// one that might have carried a patient name.</para>
/// </summary>
public static class Decode
{
    /// <summary>The label meaning "not part of anything".</summary>
    public const string Outside = "O";

    /// <summary>
    /// Every entity in one window's worth of labelled tokens.
    /// </summary>
    /// <param name="labels">One label per token, in order.</param>
    /// <param name="offsets">Where each token sat in the payload, same
    /// order and same length.</param>
    public static IReadOnlyList<Span> Entities(
        IReadOnlyList<string> labels, IReadOnlyList<Span> offsets)
    {
        var found = new List<Span>();
        int count = Math.Min(labels.Count, offsets.Count);

        int openStart = -1, openEnd = -1;
        string openType = "";

        void Close()
        {
            if (openStart >= 0) found.Add(new Span(openStart, openEnd));
            openStart = -1;
            openEnd = -1;
            openType = "";
        }

        for (int i = 0; i < count; i++)
        {
            string label = labels[i];

            if (label.Length == 0 || label == Outside) { Close(); continue; }

            bool begins = label.StartsWith("B-", StringComparison.Ordinal);
            bool inside = label.StartsWith("I-", StringComparison.Ordinal);

            // A label that is neither O nor BIO-prefixed is a flat scheme
            // - some checkpoints emit only the type. Treated as a
            // continuation of its own type, so a run of them is one entity
            // rather than one per token.
            string type = begins || inside ? label[2..] : label;

            // B- always starts a fresh entity, even directly after one of
            // the same type: that is exactly what B- is FOR, and it is the
            // only way two adjacent people are two names rather than one.
            bool continues = !begins && openStart >= 0 && type == openType;
            if (!continues) Close();

            if (openStart < 0)
            {
                openStart = offsets[i].Start;
                openType = type;
            }

            openEnd = offsets[i].End;
        }

        Close();
        return found;
    }

    /// <summary>
    /// Collapse spans that overlap into one.
    ///
    /// <para><b>This is what makes the overlap between windows free.</b>
    /// An entity inside the repeated region is found twice, once by each
    /// window, and counting it twice would inflate the number a refusal
    /// quotes.</para>
    ///
    /// <para><b>Only OVERLAPPING spans merge, never merely touching
    /// ones.</b> Two entities that sit side by side with nothing between
    /// them are two entities, and joining them would undercount - which
    /// matters, because the count is the only thing about a finding a
    /// person ever sees.</para>
    /// </summary>
    public static IReadOnlyList<Span> Merge(IEnumerable<Span> spans)
    {
        var sorted = new List<Span>(spans);
        sorted.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

        var merged = new List<Span>();
        foreach (Span one in sorted)
        {
            if (merged.Count > 0 && merged[^1].Overlaps(one))
            {
                Span last = merged[^1];
                merged[^1] = new Span(last.Start, Math.Max(last.End, one.End));
                continue;
            }

            merged.Add(one);
        }

        return merged;
    }
}
