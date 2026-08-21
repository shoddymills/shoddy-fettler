using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Fettler.Core;

/// <summary>
/// Fettler's end of the pipe to burler: the second tier, in another
/// process.
///
/// <para><b>Another process because of a package allowlist, not because
/// of taste.</b> BERT inference needs ONNX Runtime, which ships native
/// per-RID binaries, and Fettler's csproj takes only pure managed
/// packages so that fettle can publish self-contained and single-file.
/// That constraint is asserted from outside by a test. So the models
/// live behind a pipe, and the reference set never changes.</para>
///
/// <para><b>No shared assembly, in either direction.</b> The wire format
/// is the entire contract, and each side carries its own copy of the
/// shapes that cross it - proven equivalent by a protocol test on each
/// side rather than by a reference. It is the verify-twins argument
/// applied to a process boundary: twins proven equivalent by test, not
/// by sharing.</para>
///
/// <para><b>Lazy, warm and bounded.</b> A model load is seconds and a
/// warm answer is tens of milliseconds, so spawning per call would put
/// the load cost on every disclosure. The child starts on the first
/// screened payload, is held, and is killed once it has been idle: a
/// tool that sits in an editor all day should not hold 400 MB for a
/// document somebody read at breakfast.</para>
///
/// <para><b>Everything that can go wrong is a refusal.</b> A child that
/// will not start, dies, answers something unparseable, or outruns the
/// clock all come back as a failure, and
/// <see cref="Disclosure.Check"/> turns every one of them into
/// <see cref="Outcome.Screened"/>. There is no path through this class
/// that serves an unscreened payload.</para>
/// </summary>
public sealed class Sidecar : IScreener, IDisposable
{
    /// <summary>How long a cold start may take. A quantised BERT model
    /// loads in seconds, and four of them load lazily one at a time, so
    /// this is generous on purpose - a timeout here denies a disclosure
    /// that would have been served.</summary>
    public static readonly TimeSpan StartBound = TimeSpan.FromSeconds(90);

    /// <summary>How long one warm inference may take. Tens of
    /// milliseconds is the budget; this is the point at which something
    /// is wrong rather than slow.</summary>
    public static readonly TimeSpan InspectBound = TimeSpan.FromSeconds(30);

    /// <summary>How long the child is held with nothing to do before it
    /// is killed.</summary>
    public static readonly TimeSpan IdleBound = TimeSpan.FromMinutes(5);

    readonly string executable;
    readonly string models;
    readonly Lock gate = new();

    Process? child;
    StreamWriter? sending;
    StreamReader? receiving;
    string lastComplaint = "";
    DateTime lastUsed = DateTime.UtcNow;
    Timer? reaper;
    bool disposed;

    Sidecar(string executable, string models)
    {
        this.executable = executable;
        this.models = models;
    }

    /// <summary>The models directory this child was started against.
    /// Read by <see cref="Bench"/> to decide whether a warm sidecar
    /// still matches a freshly re-read configuration.</summary>
    public string Models => models;

    /// <summary>
    /// A sidecar for these trees, or null when no models directory was
    /// declared.
    ///
    /// <para><b>Null is the answer that keeps tier one usable.</b> With
    /// no directory named, nobody has claimed a model is installed, so a
    /// screened tree is screened by the structural detectors alone and a
    /// clean payload is served. Naming a directory is the act that makes
    /// the models load-bearing, and from then on a category whose model
    /// is missing refuses rather than quietly falling back.</para>
    /// </summary>
    public static Sidecar? For(Roots roots) =>
        roots.Models is { } models ? new Sidecar(Executable(), models) : null;

    /// <summary>Beside the running program, which is where the publish
    /// step puts it. Not searched for on PATH: a screening model host
    /// found by a path lookup is one an environment variable can
    /// substitute.</summary>
    public static string Executable() =>
        Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "burler.exe" : "burler");

    public Result<IReadOnlyList<ScreenFinding>> Inspect(string payload, Screened categories)
    {
        lock (gate)
        {
            if (disposed)
                return Result<IReadOnlyList<ScreenFinding>>.Fail(Outcome.Screened,
                    "the screening sidecar has been shut down");

            Result<bool> running = Ensure();
            if (!running.IsOk) return running.Carry<IReadOnlyList<ScreenFinding>>();

            lastUsed = DateTime.UtcNow;

            Result<string> answered = Exchange(Request(payload, categories));
            if (!answered.IsOk)
            {
                // A child that failed mid-exchange is in an unknown
                // protocol state - it may be about to write the rest of an
                // answer nobody asked for - so it is killed rather than
                // reused. The next disclosure pays a cold start, which is
                // the right price for certainty about what is on the pipe.
                Stop();
                return answered.Carry<IReadOnlyList<ScreenFinding>>();
            }

            return Read(answered.Value);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Stop();
        }
    }

    // ---- the wire ----

    /// <summary>One request, as one line. The payload is JSON-escaped, so
    /// a document full of newlines still crosses as a single line and the
    /// framing needs no length prefix and no sentinel.</summary>
    static string Request(string payload, Screened categories)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("op", "screen");

            writer.WriteStartArray("categories");
            // Only the model tiers cross the wire. Identifiers is judged
            // in process by Screen.Scan, and burler has no such model to
            // ask about - naming it there would only earn a refusal for
            // a model that is not supposed to exist.
            foreach (Screened one in Screens.All)
                if (categories.HasFlag(one) && Screens.ModelBacked.HasFlag(one))
                    writer.WriteStringValue(Screens.NameOf(one));
            writer.WriteEndArray();

            writer.WriteString("payload", payload);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The answer, turned into findings.
    ///
    /// <para><b>The spans stop here.</b> burler reports where it found
    /// each entity, and that is useful for nothing a message may say -
    /// an offset plus the payload IS the entity. They are read into
    /// internal state and no path from here to a refusal touches
    /// them.</para>
    ///
    /// <para><b>Public, and on its own, so the wire can be asserted
    /// without a child process.</b> R3.3 asks for a protocol test on each
    /// side of the pipe rather than a shared assembly, and a reader that
    /// could only be reached by starting burler and installing a model
    /// would make this side's half of that test impossible to write.</para>
    /// </summary>
    public static Result<IReadOnlyList<ScreenFinding>> Read(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException e)
        {
            return Result<IReadOnlyList<ScreenFinding>>.Fail(Outcome.Screened,
                $"the screening sidecar answered something that is not JSON: {e.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Result<IReadOnlyList<ScreenFinding>>.Fail(Outcome.Screened,
                    "the screening sidecar answered JSON that is not an object");

            if (!doc.RootElement.TryGetProperty("ok", out JsonElement ok)
                || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Result<IReadOnlyList<ScreenFinding>>.Fail(Outcome.Screened,
                    "the screening sidecar answered without saying whether it succeeded");

            if (ok.ValueKind == JsonValueKind.False)
            {
                string why = doc.RootElement.TryGetProperty("error", out JsonElement e)
                    && e.ValueKind == JsonValueKind.String
                        ? e.GetString()! : "it did not say why";
                return Result<IReadOnlyList<ScreenFinding>>.Fail(Outcome.Screened,
                    $"the screening sidecar refused: {why}");
            }

            if (!doc.RootElement.TryGetProperty("findings", out JsonElement findings)
                || findings.ValueKind != JsonValueKind.Array)
                return Result<IReadOnlyList<ScreenFinding>>.Fail(Outcome.Screened,
                    "the screening sidecar answered success with no findings array; an empty "
                    + "array is how it says it found nothing, and a missing one is a fault");

            var found = new List<ScreenFinding>();

            foreach (JsonElement one in findings.EnumerateArray())
            {
                if (one.ValueKind != JsonValueKind.Object
                    || !one.TryGetProperty("category", out JsonElement category)
                    || category.ValueKind != JsonValueKind.String
                    || !one.TryGetProperty("count", out JsonElement count)
                    || count.ValueKind != JsonValueKind.Number
                    || !count.TryGetInt32(out int n))
                    return Result<IReadOnlyList<ScreenFinding>>.Fail(Outcome.Screened,
                        "the screening sidecar answered a finding without a category and a count");

                Result<Screened> named = Screens.Parse([category.GetString()!]);
                if (!named.IsOk)
                    return Result<IReadOnlyList<ScreenFinding>>.Fail(Outcome.Screened,
                        $"the screening sidecar answered a category this does not know: "
                        + $"{named.Failure!.Message}");

                // A count of N becomes N findings, so Describe counts the
                // same way for both tiers and a refusal reads identically
                // whichever tier produced it.
                for (int i = 0; i < n; i++)
                    found.Add(new ScreenFinding(named.Value, "model"));
            }

            return Result<IReadOnlyList<ScreenFinding>>.Ok(found);
        }
    }

    Result<string> Exchange(string request)
    {
        StreamWriter to = sending!;
        StreamReader from = receiving!;

        try
        {
            to.WriteLine(request);
            to.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return Result<string>.Fail(Outcome.Screened, Died($"it could not be written to: {e.Message}"));
        }

        // Read on a worker and wait with a bound, rather than an async
        // read: a stream read that has already blocked cannot be
        // cancelled on every platform, so the honest way to enforce a
        // deadline is to stop waiting and kill the child.
        Task<string?> reading = Task.Run(from.ReadLine);

        if (!reading.Wait(InspectBound))
            return Result<string>.Fail(Outcome.Screened,
                $"the screening sidecar did not answer within {InspectBound.TotalSeconds:0} seconds");

        string? line;
        try
        {
            line = reading.Result;
        }
        catch (AggregateException e)
        {
            return Result<string>.Fail(Outcome.Screened,
                Died($"it could not be read from: {e.InnerException?.Message ?? e.Message}"));
        }

        return line is null
            ? Result<string>.Fail(Outcome.Screened, Died("it closed the pipe without answering"))
            : Result<string>.Ok(line);
    }

    /// <summary>What the child said on its way out, folded into the
    /// refusal. A sidecar that explained itself on stderr and had it
    /// discarded is one nobody can diagnose.</summary>
    string Died(string what)
    {
        string complaint = lastComplaint.Trim();
        return complaint.Length == 0
            ? $"the screening sidecar failed: {what}"
            : $"the screening sidecar failed: {what} (it said: {complaint})";
    }

    // ---- the child ----

    Result<bool> Ensure()
    {
        if (child is { HasExited: false }) return Result<bool>.Ok(true);

        Stop();

        if (!File.Exists(executable))
            return Result<bool>.Fail(Outcome.Screened,
                $"this scope is screened and the screening sidecar is not installed - "
                + $"burler was expected beside this program, at {executable}. Install it, "
                + $"or take \"screen\" off the scope");

        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // Explicit UTF-8 on every stream. Console.In decodes with the
            // console's input code page on Windows - 437 or 850 on a
            // default install - and a payload of clinical text full of
            // non-ASCII would arrive mangled, which for a screen means
            // entities that quietly stop matching.
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        info.ArgumentList.Add("--models");
        info.ArgumentList.Add(models);

        Process started;
        try
        {
            started = Process.Start(info)
                ?? throw new InvalidOperationException("no process was started");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception
                                      or InvalidOperationException or IOException)
        {
            return Result<bool>.Fail(Outcome.Screened,
                $"the screening sidecar would not start: {e.Message}");
        }

        child = started;
        sending = started.StandardInput;
        receiving = started.StandardOutput;
        lastComplaint = "";

        // Drained continuously, and not merely at the end: a child whose
        // stderr pipe fills up blocks forever writing to it, which would
        // show up here as an inference timeout and send somebody looking
        // at the wrong thing entirely.
        started.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } said) lastComplaint = said;
        };
        started.BeginErrorReadLine();

        lastUsed = DateTime.UtcNow;
        reaper ??= new Timer(_ => Reap(), null, IdleBound, IdleBound);

        return Result<bool>.Ok(true);
    }

    void Reap()
    {
        lock (gate)
        {
            if (child is null || DateTime.UtcNow - lastUsed < IdleBound) return;
            Stop();
        }
    }

    void Stop()
    {
        Process? going = child;
        child = null;
        sending = null;
        receiving = null;

        if (disposed)
        {
            reaper?.Dispose();
            reaper = null;
        }

        if (going is null) return;

        try
        {
            if (!going.HasExited) going.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // Already gone, which is the state being asked for.
        }

        going.Dispose();
    }
}
