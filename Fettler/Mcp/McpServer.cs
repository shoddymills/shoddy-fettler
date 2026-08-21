using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;

namespace Fettler.Mcp;

/// <summary>
/// The MCP front end of R3.2: the same operations as the CLI, over stdio
/// JSON-RPC, with <b>stdout carrying the protocol and nothing else</b>.
///
/// <para><b>The reader never blocks (R3.8).</b> A message is read,
/// dispatched, and the reader returns to reading before the dispatched
/// operation completes. The obvious shape - read a line, answer it,
/// repeat - is what this repository's other MCP host does, and correctly,
/// because the engine behind it is serialized anyway. Fettler's is not:
/// R7.5's timeout is measured in minutes, and a loop that awaits a tool
/// call before its next read leaves stdin unread for that whole period.
/// Every other operation then queues behind the slowest one, and the
/// caller cannot even ask what is happening.</para>
///
/// <para><b>Stdout has exactly one writer (R3.9).</b> Concurrent dispatch
/// means concurrent completion, and two JSON-RPC messages interleaved on
/// one stream is a corrupted protocol rather than a slow one.</para>
///
/// <para><b>A request in flight can be cancelled (R3.10).</b> That is
/// what R3.8 exists to make possible: on a blocking loop the
/// cancellation sits unread in the pipe until the operation it cancels
/// has already finished, which is not an implementation shortcoming but
/// an impossibility.</para>
/// </summary>
public sealed class McpServer : IDisposable
{
    static readonly string[] Protocols = ["2025-06-18", "2025-03-26", "2024-11-05"];

    readonly Bench bench;
    readonly TextReader input;
    readonly TextWriter output;
    readonly Func<Result<Roots>>? reload;
    readonly SemaphoreSlim writing = new(1, 1);
    readonly ConcurrentDictionary<string, CancellationTokenSource> inFlight = new();

    /// <summary>
    /// <paramref name="reload"/> is the per-request re-read of the
    /// boundary: called before every tools/call, and its answer is the
    /// boundary that call runs against. Null means the boundary is fixed
    /// for the life of the server - the --root case, and every caller
    /// that opened its trees directly. A re-read that fails refuses the
    /// call it was read for, never falls back to the previous reading:
    /// serving a boundary the file no longer states is how a person's
    /// revocation quietly fails to bind.
    /// </summary>
    public McpServer(Bench bench, TextReader input, TextWriter output, Func<Result<Roots>>? reload = null)
    {
        this.bench = bench;
        this.input = input;
        this.output = output;
        this.reload = reload;
    }

    public void Dispose()
    {
        writing.Dispose();
        foreach (CancellationTokenSource source in inFlight.Values) source.Dispose();
    }

    public async Task RunAsync(CancellationToken cancel = default)
    {
        var running = new ConcurrentDictionary<Task, byte>();
        bool first = true;

        while (!cancel.IsCancellationRequested)
        {
            string? line;
            try { line = await input.ReadLineAsync(cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            if (line is null) break;                    // stdin closed: the way out

            // A byte-order mark is the producer's habit, not the caller's
            // content, and every other stdin path here already takes one
            // off. Windows PowerShell prefixes one to whatever it pipes
            // into a native program, so a client launched from a .ps1 got
            // -32700 to its very first message and never opened a session
            // at all. Only the front of the first line: a mark later in
            // the stream is text somebody may have meant to send.
            if (first) { first = false; line = TextIo.WithoutMark(line); }

            if (line.Trim().Length == 0) continue;

            // Dispatched and forgotten, deliberately: awaiting here is
            // precisely the stall R3.8 forbids.
            Task work = Handle(line, cancel);
            running.TryAdd(work, 0);
            _ = work.ContinueWith(t => running.TryRemove(t, out _), TaskScheduler.Default);
        }

        // Let whatever is still running finish and answer, so a client
        // that closed stdin still receives the replies it is owed.
        try { await Task.WhenAll(running.Keys).ConfigureAwait(false); }
        catch (Exception e) when (e is OperationCanceledException or IOException) { }
    }

    async Task Handle(string line, CancellationToken cancel)
    {
        string? reply;
        try { reply = await AnswerAsync(line, cancel).ConfigureAwait(false); }
        catch (Exception e) { reply = Error(null, -32603, e.Message); }

        if (reply is null) return;                      // a notification earns no answer
        await WriteLineAsync(reply).ConfigureAwait(false);
    }

    /// <summary>R3.9: every reply passes through here, one at a time.</summary>
    async Task WriteLineAsync(string message)
    {
        await writing.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(message).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        finally { writing.Release(); }
    }

    /// <summary>One message in, at most one message out. Public so the
    /// tests can drive the protocol without a process.</summary>
    public async Task<string?> AnswerAsync(string message, CancellationToken cancel = default)
    {
        JsonElement request;
        try { request = JsonDocument.Parse(message).RootElement; }
        catch (JsonException e) { return Error(null, -32700, "the message is not JSON: " + e.Message); }

        string method = request.TryGetProperty("method", out JsonElement m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()! : "";
        JsonElement parameters = request.TryGetProperty("params", out JsonElement p) ? p : default;
        bool hasId = request.TryGetProperty("id", out JsonElement id);

        if (!hasId)
        {
            // R3.10: the one notification that has to do something.
            if (method == "notifications/cancelled") Cancel(parameters);
            return null;
        }

        string key = id.ToString();

        switch (method)
        {
            case "initialize": return Ok(id, Initialize(parameters));
            case "ping": return Ok(id, w => { w.WriteStartObject(); w.WriteEndObject(); });
            case "tools/list": return Ok(id, ToolCatalogue.Write);
            case "tools/call": return await Call(id, key, parameters, cancel).ConfigureAwait(false);
            default: return Error(id, -32601, "no method called " + method);
        }
    }

    void Cancel(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return;
        if (!parameters.TryGetProperty("requestId", out JsonElement id)) return;

        if (inFlight.TryGetValue(id.ToString(), out CancellationTokenSource? source))
        {
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    async Task<string> Call(JsonElement id, string key, JsonElement parameters, CancellationToken cancel)
    {
        string name = parameters.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()! : "";
        JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement a) ? a : default;

        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        inFlight[key] = source;

        // The boundary THIS call runs against. Re-read when there is a
        // file to re-read - unconditionally, not gated on a modification
        // time, so the behaviour is deterministic: every call answers
        // against the file as it is now, and an edit binds on the very
        // next one. The snapshot shares the launch Bench's mutation
        // gate, so R3.11's turn-taking spans boundary readings.
        Bench boundary = bench;
        Bench? snapshot = null;

        try
        {
            if (reload is not null)
            {
                Result<Roots> current = reload();
                if (!current.IsOk) return Ok(id, Refusal(current.Failure!));

                snapshot = new Bench(current.Value, bench);
                boundary = snapshot;
            }

            Result<ToolCatalogue.ToolAnswer> answer = await ToolCatalogue
                .InvokeAsync(boundary, name, arguments, source.Token).ConfigureAwait(false);

            // A failed tool is a successful protocol message carrying
            // isError, not a JSON-RPC error: the model is meant to read
            // the reason and try something else.
            return Ok(id, w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("content");

                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", answer.IsOk ? answer.Value.Text : Describe(answer.Failure!));
                w.WriteEndObject();

                // An image goes as an image. The content model carries
                // one natively and nothing here has to understand the
                // pixels - it is one more content type in one place.
                if (answer.IsOk)
                    foreach (ToolCatalogue.ImagePart image in answer.Value.Images)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "image");
                        w.WriteString("data", image.Base64);
                        w.WriteString("mimeType", image.MimeType);
                        w.WriteEndObject();
                    }

                w.WriteEndArray();

                // A refused operation and an unknown tool are both errors
                // here. Only the second needs describing: the first is
                // already a complete machine-readable answer.
                if (!answer.IsOk || answer.Value.IsError) w.WriteBoolean("isError", true);
                w.WriteEndObject();
            });
        }
        catch (OperationCanceledException)
        {
            return Error(id, -32800, "the request was cancelled");
        }
        finally
        {
            snapshot?.Dispose();
            inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// What every client is told at <c>initialize</c>, before the model
    /// has said anything.
    ///
    /// <para><b>B.16: this is the highest-leverage grounding there is.</b>
    /// It is read at the moment a tool is chosen, it ships with the tool,
    /// it needs no setup by anybody, and it reaches every client at once.
    /// It is also the only one that needs nobody's cooperation - a rule
    /// in a CLAUDE.md has to be written by the user, kept up to date by
    /// the user, and read by a model that has a great deal else in front
    /// of it.</para>
    ///
    /// <para><b>The working-directory clause is here because instruction
    /// alone demonstrably failed.</b> In the session that produced this
    /// design the assistant had extensive grounding, reached for
    /// <c>Set-Location</c> on nearly every command anyway, and needed
    /// three corrections. Saying it here is worth doing and is not
    /// sufficient; what makes it stick is that Fettler needs no working
    /// directory at all, so there is nothing for the habit to be for.</para>
    /// </summary>
    public const string Instructions =
        "File, search and edit operations over a bounded set of declared trees. "
        + "Paths resolve against those trees, never against a working directory: "
        + "there is no current directory here, changing directory does nothing, and "
        + "reaching for one is a sign the wrong tool is being used. "
        + "Paths are written name:path when more than one tree is open. "
        + "Call roots FIRST to see the trees, what each grants, and which one an "
        + "unqualified path lands in - rather than discovering the boundary by being refused. "
        + "A tree may be read-only, and a scope inside it may grant more or less than the tree does. "
        + "Read returns a hash; pass it back as expect on an edit to prove the file has not moved. "
        + $"Declared tasks come from the \"tasks\" object in the {RootsFile.FileName} that declared "
        + "the trees, and running one needs that tree to grant execute, which is never granted by default. "
        + $"{RootsFile.FileName} and {RootsFile.LocalFileName} are the files that say "
        + "what this tool may do, and this tool does not write them - a person edits those. "
        + "An edit to them binds on the next request: serve re-reads both before every call.";

    static string Describe(Failure failure) =>
        $"{ExitCodes.NameOf(failure.Outcome)}: {failure.Message}"
        + (failure.Path is { Length: > 0 } at ? $" ({at})" : "");

    /// <summary>A tools/call answer that is all refusal: the failure's
    /// description as the one content part, marked isError. Used when
    /// the boundary itself cannot be read - there is no operation to
    /// answer with, and a JSON-RPC error would read as a broken server
    /// rather than a stated refusal the model can act on.</summary>
    static Action<Utf8JsonWriter> Refusal(Failure failure) => w =>
    {
        w.WriteStartObject();
        w.WriteStartArray("content");
        w.WriteStartObject();
        w.WriteString("type", "text");
        w.WriteString("text", Describe(failure));
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteBoolean("isError", true);
        w.WriteEndObject();
    };

    Action<Utf8JsonWriter> Initialize(JsonElement parameters)
    {
        string asked = parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("protocolVersion", out JsonElement v)
            && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

        string speak = Array.IndexOf(Protocols, asked) >= 0 ? asked : Protocols[0];

        return w =>
        {
            w.WriteStartObject();
            w.WriteString("protocolVersion", speak);
            w.WriteStartObject("capabilities");
            w.WriteStartObject("tools");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("serverInfo");
            w.WriteString("name", "fettler");
            w.WriteString("version", Command.Version);
            w.WriteEndObject();
            w.WriteString("instructions", Instructions);
            w.WriteEndObject();
        };
    }

    // ---- JSON-RPC envelopes ----

    static string Ok(JsonElement id, Action<Utf8JsonWriter> result) => Envelope(w =>
    {
        w.WritePropertyName("id");
        id.WriteTo(w);
        w.WritePropertyName("result");
        result(w);
    });

    static string Error(JsonElement? id, int code, string message) => Envelope(w =>
    {
        w.WritePropertyName("id");
        if (id is { } present) present.WriteTo(w); else w.WriteNullValue();
        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
    });

    static string Envelope(Action<Utf8JsonWriter> body)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
