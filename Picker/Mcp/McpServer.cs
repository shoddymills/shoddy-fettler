using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Picker.Cli;
using Picker.Core;

namespace Picker.Mcp;

/// <summary>
/// The MCP front end: the same operations as the CLI, over stdio
/// JSON-RPC, with <b>stdout carrying the protocol and nothing else</b>.
///
/// <para><b>The reader never blocks.</b> A message is read, dispatched,
/// and the reader returns to reading before the dispatched operation
/// completes - a query against a slow server must not stall a catalogs
/// call behind it, and a cancellation must be readable while the thing
/// it cancels is still running. Fettler's R3.8-R3.10, kept whole.</para>
///
/// <para><b>Stdout has exactly one writer.</b> Concurrent dispatch means
/// concurrent completion, and two JSON-RPC messages interleaved on one
/// stream is a corrupted protocol rather than a slow one.</para>
/// </summary>
public sealed class McpServer : IDisposable
{
    static readonly string[] Protocols = ["2025-06-18", "2025-03-26", "2024-11-05"];

    readonly Bench bench;
    readonly TextReader input;
    readonly TextWriter output;
    readonly Func<Result<Boundary>>? reload;
    readonly SemaphoreSlim writing = new(1, 1);
    readonly ConcurrentDictionary<string, CancellationTokenSource> inFlight = new();

    /// <summary>
    /// <paramref name="reload"/> is the per-request re-read of the
    /// boundary: called before every tools/call, and its answer is the
    /// boundary that call runs against, so an edit to the configuration
    /// binds on the very next request. A re-read that fails refuses the
    /// call it was read for, never falls back to the previous reading.
    /// </summary>
    public McpServer(Bench bench, TextReader input, TextWriter output,
        Func<Result<Boundary>>? reload = null)
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

            // A byte-order mark is the producer's habit, not the
            // caller's content. Windows PowerShell prefixes one to
            // whatever it pipes into a native program, and it would cost
            // the first message of every session launched from a .ps1.
            if (first)
            {
                first = false;
                if (line.Length > 0 && line[0] == '﻿') line = line[1..];
            }

            if (line.Trim().Length == 0) continue;

            // Dispatched and forgotten, deliberately: awaiting here is
            // precisely the stall the class summary forbids.
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

        Bench boundary = bench;
        Bench? snapshot = null;

        try
        {
            if (reload is not null)
            {
                Result<Boundary> current = reload();
                if (!current.IsOk) return Ok(id, Refusal(current.Failure!));

                snapshot = new Bench(current.Value);
                boundary = snapshot;
            }

            bool failed = false;
            Result<string> answer = await ToolCatalogue
                .InvokeAsync(boundary, name, arguments, source.Token, e => failed = e)
                .ConfigureAwait(false);

            // A failed tool is a successful protocol message carrying
            // isError, not a JSON-RPC error: the model is meant to read
            // the reason and try something else.
            return Ok(id, w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("content");
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", answer.IsOk ? answer.Value : Describe(answer.Failure!));
                w.WriteEndObject();
                w.WriteEndArray();
                if (!answer.IsOk || failed) w.WriteBoolean("isError", true);
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
    /// has said anything - the highest-leverage grounding there is, read
    /// at the moment a tool is chosen, shipping with the tool.
    /// </summary>
    public const string Instructions =
        "SELECT and DESCRIBE over declared SQL Server databases. "
        + "There is no default database and no USE: names resolve against the declared "
        + "databases only, and an object outside them does not exist to any caller. "
        + "Call catalogs first. It lists the servers and databases, what each grants, "
        + "and what is screened. "
        + "The verbs are select (one SELECT statement, tables and views only), "
        + "describe (columns, keys and indexes; never a view's definition text) and "
        + "objects (what is here). A column can be hidden; a hidden column does not "
        + "exist, and SELECT * expands to the columns that do. "
        + $"{ConfigFile.FileName} and {ConfigFile.LocalFileName} say what this tool may do. "
        + "The tool does not write them; a person edits them, and an edit applies on the next call.";

    static string Describe(Failure failure) =>
        $"{ExitCodes.NameOf(failure.Outcome)}: {failure.Message}"
        + (failure.Subject is { Length: > 0 } at ? $" ({at})" : "");

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
            w.WriteString("name", "picker");
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
        using (var writer = new Utf8JsonWriter(buffer, Command.Writing))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
