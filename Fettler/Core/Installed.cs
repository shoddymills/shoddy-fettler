using System.Text.Json;

namespace Fettler.Core;

/// <summary>
/// What is actually installed in the models directory, read so that
/// <c>roots</c> can NAME the model doing the judging.
///
/// <para><b>The model is never hidden.</b> A screen that says
/// <c>clinical</c> and nothing else leaves a caller unable to tell a
/// category judged by a checkpoint from one judged by nothing at all,
/// and those are opposite states wearing the same word. Naming the
/// checkpoint and its revision is the difference between a claim and a
/// fact somebody can check.</para>
///
/// <para><b>Read-only and tolerant, on purpose.</b> Nothing here loads a
/// model, and nothing here throws. <c>roots</c> is the call people make
/// to find out where they stand, so a malformed manifest must not be
/// able to take the whole answer away - it reports as no model, which is
/// what a caller needs to know either way. The refusal with the real
/// diagnosis belongs at the disclosure, where <see cref="Sidecar"/> can
/// say which category wanted the model and why it would not load.</para>
///
/// <para>The layout mirrors burler's R4.7: a category directory holding
/// <c>model.onnx</c> directly, or holding subdirectories that each do.
/// Read rather than shared, because fettle takes no dependency on
/// burler - see <see cref="Sidecar"/> for why the two only ever meet
/// across a pipe.</para>
/// </summary>
public static class Installed
{
    const string ModelFile = "model.onnx";
    const string ManifestFile = "manifest.json";

    /// <summary>One installed model, as it says it should be named.</summary>
    public sealed record Model(string Checkpoint, string Revision)
    {
        /// <summary>Both facts in one phrase, which is how <c>roots</c>
        /// prints it: a checkpoint alone does not say which revision of
        /// it is on this disk.</summary>
        public override string ToString() => $"{Checkpoint} @ {Revision}";
    }

    /// <summary>
    /// Every model installed for one category, in a fixed order.
    ///
    /// <para>Empty means "no model a caller can rely on", whether the
    /// directory is absent, holds no <c>model.onnx</c>, or holds one
    /// whose manifest will not read. A category may legitimately hold
    /// several - they union at screening time, so more of them only ever
    /// makes the screen stricter - and all of them are named.</para>
    /// </summary>
    public static IReadOnlyList<Model> For(string models, string category)
    {
        var found = new List<Model>();

        foreach (string directory in Directories(models, category))
            if (Read(directory) is { } one) found.Add(one);

        return found;
    }

    static IReadOnlyList<string> Directories(string models, string category)
    {
        string here = Path.Combine(models, category);

        try
        {
            if (!Directory.Exists(here)) return [];
            if (File.Exists(Path.Combine(here, ModelFile))) return [here];

            var found = new List<string>();
            foreach (string child in Directory.GetDirectories(here))
                if (File.Exists(Path.Combine(child, ModelFile))) found.Add(child);

            found.Sort(StringComparer.Ordinal);
            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static Model? Read(string directory)
    {
        string path = Path.Combine(directory, ManifestFile);

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            return Word(doc.RootElement, "checkpoint") is { } checkpoint
                && Word(doc.RootElement, "revision") is { } revision
                ? new Model(checkpoint, revision)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Word(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } said
            ? said
            : null;
}
