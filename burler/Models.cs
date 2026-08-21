using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Burler;

/// <summary>
/// A refusal with something a person can do about it.
///
/// <para>Every one of these becomes a denied disclosure on the other
/// side of the pipe, so the message is read by somebody trying to work
/// out why their file will not open - not by somebody reading a stack
/// trace.</para>
/// </summary>
public sealed class Refusal(string why) : Exception(why);

/// <summary>What a model directory says about itself.</summary>
public sealed record Manifest(
    string Checkpoint, string Revision, string Licence, string Provenance, string Version);

/// <summary>
/// The models on this machine, loaded when first wanted and held.
///
/// <para><b>Nothing here ships with burler.</b> The quantised models
/// run hundreds of MB against a download measured in single-digit MB, so a
/// person puts them somewhere and names that directory in the
/// configuration. A category whose model is not there is a REFUSAL and
/// never a quiet pass: by the time a directory has been named, somebody
/// has said they expect the model to be in it.</para>
///
/// <para><b>Lazily, one category at a time.</b> A tree screening only
/// <c>clinical</c> loads one model rather than all of them, which is
/// the difference between a hundred megabytes and several hundred for
/// the commonest arrangement there is.</para>
/// </summary>
public sealed class Models : IDisposable
{
    /// <summary>How many verdicts are remembered. Bounded because this
    /// process is held warm for hours and an unbounded cache of every
    /// payload ever screened is a memory leak wearing an optimisation's
    /// hat - and one holding the very text nobody wants held.</summary>
    public const int CacheSize = 256;

    readonly string root;
    readonly Dictionary<string, List<Loaded>> byCategory = new(StringComparer.Ordinal);
    readonly Dictionary<string, IReadOnlyList<Span>> verdicts = new(StringComparer.Ordinal);
    readonly Queue<string> ages = new();

    public Models(string root)
    {
        this.root = root;

        if (!Directory.Exists(root))
            throw new Refusal($"the models directory named in the configuration is not there: {root}");
    }

    /// <summary>
    /// Every entity the models for one category find in a payload.
    ///
    /// <para><b>A category may hold more than one model, and their
    /// findings union.</b> That is what lets <c>legal</c> run a
    /// permissively licensed default alongside a LegalBERT add-on rather
    /// than choosing between them - any detection by any of them denies,
    /// so more models can only ever make the screen stricter.</para>
    /// </summary>
    public IReadOnlyList<Span> Screen(string category, string payload)
    {
        List<Loaded> loaded = For(category);

        // R6.1: keyed by the payload AND the manifest versions of the
        // models that judged it, so swapping a model in silently
        // invalidates every verdict it produced rather than serving an
        // answer the current models never gave.
        string key = Key(payload, loaded);
        if (verdicts.TryGetValue(key, out IReadOnlyList<Span>? remembered)) return remembered;

        var all = new List<Span>();
        foreach (Loaded one in loaded) all.AddRange(one.Run(payload));

        IReadOnlyList<Span> merged = Decode.Merge(all);

        verdicts[key] = merged;
        ages.Enqueue(key);
        while (ages.Count > CacheSize) verdicts.Remove(ages.Dequeue());

        return merged;
    }

    public void Dispose()
    {
        foreach (List<Loaded> loaded in byCategory.Values)
            foreach (Loaded one in loaded) one.Dispose();

        byCategory.Clear();
    }

    static string Key(string payload, List<Loaded> loaded)
    {
        var identity = new StringBuilder();
        foreach (Loaded one in loaded) identity.Append(one.Facts.Version).Append('\n');
        identity.Append(payload);

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
    }

    List<Loaded> For(string category)
    {
        if (byCategory.TryGetValue(category, out List<Loaded>? already)) return already;

        var loaded = new List<Loaded>();
        foreach (string directory in Directories(category)) loaded.Add(Loaded.From(directory));

        byCategory[category] = loaded;
        return loaded;
    }

    /// <summary>
    /// The model directories for a category: either the category
    /// directory itself, or every subdirectory of it that holds a model.
    ///
    /// <para>Both shapes because R4.7 allows several models per category.
    /// One model is the ordinary case and should not need a pointless
    /// nesting level; several need somewhere to live.</para>
    /// </summary>
    IReadOnlyList<string> Directories(string category)
    {
        string here = Path.Combine(root, category);

        if (!Directory.Exists(here))
            throw new Refusal(
                $"'{category}' is screened and there is no model for it: {here} is not there. "
                + $"Put the model in that directory, or take '{category}' out of the screen for "
                + "this scope");

        if (File.Exists(Path.Combine(here, Loaded.ModelFile))) return [here];

        var found = new List<string>();
        foreach (string child in Directory.GetDirectories(here))
            if (File.Exists(Path.Combine(child, Loaded.ModelFile))) found.Add(child);

        if (found.Count == 0)
            throw new Refusal(
                $"'{category}' is screened and {here} holds no model: it needs "
                + $"{Loaded.ModelFile} either directly in it or in a subdirectory");

        found.Sort(StringComparer.Ordinal);
        return found;
    }
}

/// <summary>One model, ready to run: its session, its tokenizer, its
/// labels and what it says about itself.</summary>
public sealed class Loaded : IDisposable
{
    public const string ModelFile = "model.onnx";
    public const string VocabFile = "vocab.txt";
    public const string LabelsFile = "labels.json";
    public const string ManifestFile = "manifest.json";

    readonly InferenceSession session;
    readonly BertTokenizer tokenizer;
    readonly string[] labels;

    Loaded(InferenceSession session, BertTokenizer tokenizer, string[] labels, Manifest facts)
    {
        this.session = session;
        this.tokenizer = tokenizer;
        this.labels = labels;
        Facts = facts;
    }

    public Manifest Facts { get; }

    public static Loaded From(string directory)
    {
        string model = Path.Combine(directory, ModelFile);
        string vocab = Path.Combine(directory, VocabFile);
        string labelling = Path.Combine(directory, LabelsFile);

        foreach (string needed in new[] { model, vocab, labelling })
            if (!File.Exists(needed))
                throw new Refusal($"the model at {directory} is incomplete: {needed} is not there");

        string[] labels = ReadLabels(labelling);
        Manifest facts = ReadManifest(directory);

        InferenceSession session;
        try
        {
            session = new InferenceSession(model);
        }
        catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException
                                      or BadImageFormatException or IOException)
        {
            throw new Refusal($"the model at {model} would not load: {e.Message}");
        }

        BertTokenizer tokenizer;
        try
        {
            tokenizer = BertTokenizer.Create(vocab);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or FormatException)
        {
            session.Dispose();
            throw new Refusal($"the vocabulary at {vocab} would not load: {e.Message}");
        }

        return new Loaded(session, tokenizer, labels, facts);
    }

    /// <summary>
    /// Every entity this model finds in a payload, as character ranges.
    ///
    /// <para><b>Each window is tokenized from its own slice of text.</b>
    /// The alternative - encoding once and re-attaching the classifier's
    /// two special tokens by hand - needs their ids, which means guessing
    /// at a tokenizer's surface for no gain. Re-encoding a substring is a
    /// few microseconds and cannot be wrong about what the model will
    /// actually be handed.</para>
    /// </summary>
    public IReadOnlyList<Span> Run(string payload)
    {
        IReadOnlyList<EncodedToken> whole = Encode(payload);
        var content = new List<EncodedToken>();

        // The tokenizer's own bracketing tokens carry no offsets worth
        // anything and must not be windowed over.
        foreach (EncodedToken one in whole)
            if (!IsSpecial(one.Value)) content.Add(one);

        if (content.Count == 0) return [];

        var found = new List<Span>();

        foreach (Window window in Windows.Over(content.Count))
        {
            int from = content[window.Start].Offset.Start.Value;
            int to = content[window.Start + window.Length - 1].Offset.End.Value;
            if (to <= from) continue;

            found.AddRange(Judge(payload[from..to], from));
        }

        return Decode.Merge(found);
    }

    public void Dispose() => session.Dispose();

    IReadOnlyList<Span> Judge(string slice, int baseOffset)
    {
        IReadOnlyList<EncodedToken> tokens = Encode(slice);
        if (tokens.Count == 0) return [];

        // A re-encoded slice can land a token or two over the limit. The
        // tail is dropped rather than the whole window refused, because
        // the next window's overlap covers exactly that region.
        int count = Math.Min(tokens.Count, Windows.Size);

        var ids = new long[count];
        var mask = new long[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = tokens[i].Id;
            mask[i] = 1;
        }

        float[] logits = Infer(ids, mask, out int classes);
        if (classes <= 0) return [];

        var said = new List<string>(count);
        var offsets = new List<Span>(count);

        for (int i = 0; i < count; i++)
        {
            if (IsSpecial(tokens[i].Value)) continue;

            int best = 0;
            for (int c = 1; c < classes; c++)
                if (logits[(i * classes) + c] > logits[(i * classes) + best]) best = c;

            said.Add(best < labels.Length ? labels[best] : Decode.Outside);
            offsets.Add(new Span(
                baseOffset + tokens[i].Offset.Start.Value,
                baseOffset + tokens[i].Offset.End.Value));
        }

        return Decode.Entities(said, offsets);
    }

    float[] Infer(long[] ids, long[] mask, out int classes)
    {
        int length = ids.Length;
        var inputs = new List<NamedOnnxValue>();

        // Only what this particular export actually asks for. Some carry
        // token_type_ids and some do not, and handing a session an input
        // it did not declare is an error rather than something ignored.
        foreach (string name in session.InputMetadata.Keys)
        {
            long[] values = name switch
            {
                "attention_mask" => mask,
                "token_type_ids" => new long[length],
                _ => ids,
            };

            inputs.Add(NamedOnnxValue.CreateFromTensor(
                name, new DenseTensor<long>(values, [1, length])));
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(inputs);

        DisposableNamedOnnxValue? first = results.FirstOrDefault();
        if (first is null) { classes = 0; return []; }

        Tensor<float> logits = first.AsTensor<float>();

        // [batch, tokens, classes], and the batch is always one here.
        classes = logits.Dimensions.Length >= 3 ? logits.Dimensions[^1] : 0;
        return classes <= 0 ? [] : logits.ToArray();
    }

    IReadOnlyList<EncodedToken> Encode(string text)
    {
        try
        {
            return tokenizer.EncodeToTokens(text, out _);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            throw new Refusal($"the payload could not be tokenized: {e.Message}");
        }
    }

    static bool IsSpecial(string token) =>
        token is "[CLS]" or "[SEP]" or "[PAD]" or "[MASK]";

    static string[] ReadLabels(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new Refusal($"the labels at {path} could not be read: {e.Message}");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);

            // An array, where the position IS the class id. An object of
            // id to name would need a rule for a gap in the numbering, and
            // a gap in a classifier's labels is a broken export rather
            // than something to paper over.
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new Refusal(
                    $"the labels at {path} must be a JSON array, one label per class in order");

            var labels = new List<string>();
            foreach (JsonElement one in doc.RootElement.EnumerateArray())
            {
                if (one.ValueKind != JsonValueKind.String)
                    throw new Refusal($"the labels at {path} contain something that is not a word");
                labels.Add(one.GetString()!);
            }

            if (labels.Count == 0)
                throw new Refusal($"the labels at {path} are empty");

            return [.. labels];
        }
        catch (JsonException e)
        {
            throw new Refusal($"the labels at {path} are not valid JSON: {e.Message}");
        }
    }

    /// <summary>
    /// What the model says about its own provenance.
    ///
    /// <para><b>Required, and the version is a hash of the whole
    /// file.</b> R4.5 makes licence verification a gate rather than a
    /// footnote - a checkpoint whose terms forbid redistribution cannot
    /// be shipped however good its scores - and a manifest that may be
    /// absent is one that will be. Hashing the file means editing any
    /// field invalidates every cached verdict the model produced, which
    /// is the honest behaviour when the thing that judged has
    /// changed.</para>
    /// </summary>
    static Manifest ReadManifest(string directory)
    {
        string path = Path.Combine(directory, ManifestFile);

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or FileNotFoundException or DirectoryNotFoundException)
        {
            throw new Refusal(
                $"the model at {directory} has no readable {ManifestFile}, which has to record "
                + "the checkpoint, its revision, its licence and where it came from");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new Refusal($"{path} must be a JSON object");

            return new Manifest(
                Field(doc.RootElement, "checkpoint", path),
                Field(doc.RootElement, "revision", path),
                Field(doc.RootElement, "licence", path),
                Field(doc.RootElement, "provenance", path),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        }
        catch (JsonException e)
        {
            throw new Refusal($"{path} is not valid JSON: {e.Message}");
        }
    }

    static string Field(JsonElement root, string name, string path)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: > 0 } said)
            throw new Refusal($"{path} needs a \"{name}\" written as a non-empty string");

        return said;
    }
}
