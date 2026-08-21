using System.Text;
using System.Text.Json;

namespace Burler;

/// <summary>One thing found, as it crosses the wire: a category, how many
/// of them, and where they sat.</summary>
/// <param name="Category">One of the model-backed category words.</param>
/// <param name="Spans">Half-open character ranges into the payload, in the
/// order they were found.</param>
public sealed record Finding(string Category, IReadOnlyList<(int Start, int End)> Spans)
{
    public int Count => Spans.Count;
}

/// <summary>One request, as it arrives.</summary>
public sealed record Request(string Op, IReadOnlyList<string> Categories, string Payload);

/// <summary>
/// The wire, which is the WHOLE contract between Fettler and burler.
///
/// <para><b>Nothing is shared but this shape.</b> There is no assembly
/// in common and no project reference in either direction, so the two
/// sides are kept in step by a test on each that asserts the same wire -
/// the verify-twins argument applied to a process boundary. A shared DTO
/// assembly would be the easy answer and the wrong one: it would make
/// burler a thing Fettler references, which is the exact coupling the
/// package allowlist forbids.</para>
///
/// <para><b>Line-delimited JSON, and the payload is a JSON string.</b>
/// A document full of newlines is escaped into one line, so the framing
/// needs neither a length prefix nor a sentinel nobody can guarantee is
/// absent from clinical text.</para>
///
/// <para><b>Spans cross the wire and stop at the other end.</b> burler
/// knows where it found each entity, and Fettler reads that into
/// internal state no message path can reach. An offset plus the payload
/// IS the entity, so a refusal quoting one would leak exactly what the
/// screen exists to keep in.</para>
/// </summary>
public static class Protocol
{
    /// <summary>The categories this speaks about. Duplicated from
    /// Fettler's own list deliberately - see the class summary - and
    /// asserted equal by a test on each side. 'identifiers' is not here
    /// and never will be: it is Fettler's in-process pattern tier, and a
    /// request naming it is a caller confused about which side of the
    /// pipe that tier runs on.</summary>
    public static readonly string[] Categories = ["clinical", "legal", "scientific"];

    /// <summary>
    /// Read one request line, or say what was wrong with it.
    ///
    /// <para>A malformed request is answered rather than ignored: silence
    /// on this pipe reads to the other end as a hung sidecar, which it
    /// would then kill and blame for a timeout.</para>
    /// </summary>
    public static Request? Parse(string line, out string error)
    {
        error = "";

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException e)
        {
            error = $"the request is not JSON: {e.Message}";
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "the request is not a JSON object";
                return null;
            }

            if (!doc.RootElement.TryGetProperty("op", out JsonElement op)
                || op.ValueKind != JsonValueKind.String)
            {
                error = "the request has no \"op\"";
                return null;
            }

            if (!doc.RootElement.TryGetProperty("payload", out JsonElement payload)
                || payload.ValueKind != JsonValueKind.String)
            {
                error = "the request has no \"payload\" string";
                return null;
            }

            var categories = new List<string>();
            if (doc.RootElement.TryGetProperty("categories", out JsonElement asked)
                && asked.ValueKind == JsonValueKind.Array)
                foreach (JsonElement one in asked.EnumerateArray())
                {
                    if (one.ValueKind != JsonValueKind.String)
                    {
                        error = "a category in the request is not a word";
                        return null;
                    }

                    string word = one.GetString()!;
                    if (Array.IndexOf(Categories, word) < 0)
                    {
                        error = $"'{word}' is not a category this knows";
                        return null;
                    }

                    categories.Add(word);
                }

            return new Request(op.GetString()!, categories, payload.GetString()!);
        }
    }

    /// <summary>A successful answer. An empty findings array is how this
    /// says it found nothing, and the other side treats a MISSING array as
    /// a fault rather than as the same thing.</summary>
    public static string Found(IReadOnlyList<Finding> findings)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteStartArray("findings");

            foreach (Finding one in findings)
            {
                writer.WriteStartObject();
                writer.WriteString("category", one.Category);
                writer.WriteNumber("count", one.Count);

                writer.WriteStartArray("spans");
                foreach ((int start, int end) in one.Spans)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(start);
                    writer.WriteNumberValue(end);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>A refusal. Whatever it says, the other side turns it into
    /// a denied disclosure - so it has to say something a person can act
    /// on rather than a stack frame.</summary>
    public static string Failed(string why)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", false);
            writer.WriteString("error", why);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
