using System.Buffers;
using System.Text;
using System.Text.Json;
using Fettler.Core;

namespace Fettler.Cli;

/// <summary>
/// The size of something once written, measured by writing it.
///
/// <para><b>Measured, never estimated.</b> How many bytes a string takes
/// in a JSON answer depends on the encoder: a quote is two bytes, a
/// control character six, a character outside the basic plane twelve. A
/// rule written out by hand would have to agree with the encoder in every
/// case, and would drift the first time it did not. Writing the value
/// with the same writer and the same options as the answer, and counting
/// the bytes, agrees by construction.</para>
/// </summary>
static class Size
{
    [ThreadStatic] static ArrayBufferWriter<byte>? scratch;

    /// <summary>The bytes <paramref name="write"/> produces, written the
    /// way every answer is written. Not reentrant: a writer passed here
    /// must not measure anything itself.</summary>
    public static int Json(Action<Utf8JsonWriter> write)
    {
        ArrayBufferWriter<byte> buffer = scratch ??= new ArrayBufferWriter<byte>(4096);
        buffer.ResetWrittenCount();
        using (var w = new Utf8JsonWriter(buffer, Command.Writing)) write(w);
        return buffer.WrittenCount;
    }

    /// <summary>The bytes <paramref name="text"/> takes inside a JSON
    /// string, quotes not counted.</summary>
    public static int Escaped(string text) => Json(w => w.WriteStringValue(text)) - 2;

    /// <summary>The bytes <paramref name="text"/> takes as plain
    /// UTF-8, as the terminal answer writes it.</summary>
    public static int Text(string text) => Encoding.UTF8.GetByteCount(text);

    public static readonly int NewLine = Environment.NewLine.Length;
}

/// <summary>
/// The meter the command line hands the core: each part of a read or a
/// search, measured in both output modes, and the larger of the two.
///
/// <para><b>The larger of the two</b> is what makes one cut serve both.
/// The terminal numbers every line and the JSON escapes every quote, so
/// either can be the bigger; a cut that fits the bigger fits both, and
/// the two answers stop at the same line.</para>
/// </summary>
sealed class Meter(Bench bench, int room) : IMeter
{
    public int Room { get; } = room;

    public int Slice(ContainedPath path, int lines)
    {
        FileFacts facts = Tree.Facts(path.Full, isDirectory: false);
        int worst = 0;

        foreach (ReadSlice s in Command.WorstSlices(path, facts))
            worst = Math.Max(worst, Math.Max(
                Size.Json(w => Command.WriteSlice(w, bench, s)) + 1,
                Size.Text(Command.HumanSlice(new StringBuilder(), bench, s, numbered: false).ToString())));

        return worst;
    }

    public int Line(int number, string text, string ending, bool numbered)
    {
        string prefix = number.ToString().PadLeft(Bench.NumberWidth) + " | ";
        int json = (numbered ? prefix.Length : 0) + Size.Escaped(text) + Size.Escaped(ending);
        int human = Size.Text(prefix) + Size.Text(text) + Size.NewLine;
        return Math.Max(json, human);
    }

    public int Hit(Hit hit) => Math.Max(
        Size.Json(w => Command.WriteHit(w, bench, hit)) + 1,
        Size.Text(Command.HumanHit(new StringBuilder(), bench, hit).ToString()));

    public int After(Hit hit) => Next(hit.Position);

    public int Matched(ContainedPath path, int hits)
    {
        var file = new MatchedFile(path, hits);
        return Math.Max(
            Size.Json(w => Command.WriteMatched(w, bench, file)) + 1,
            Size.Text(Command.HumanMatched(new StringBuilder(), bench, file).ToString()));
    }

    public int After(ContainedPath path) => Next(path.Display);

    /// <summary>What a <c>next</c> adds to an answer: the field in the
    /// JSON, or the sentence naming it in the terminal.</summary>
    public static int Next(string position) => Math.Max(
        Size.Json(w => { w.WriteStartObject(); w.WriteString("next", position); w.WriteEndObject(); }) - 1,
        Size.Text(Command.MoreSentence(position)));
}

public static partial class Command
{
    // ---- the ceiling ----

    /// <summary>
    /// The last check every answer passes on its way out.
    ///
    /// <para><b>An answer over the ceiling is never sent.</b> Every verb
    /// cuts to fit before it writes, so reaching the replacement here is
    /// a defect, and it says so with the size, because a defect that is
    /// said is one somebody can fix. The alternative, sending it, is the
    /// failure the ceiling exists for: the client saves it to a file and
    /// the model gets a pointer instead of an answer.</para>
    ///
    /// <para><b>A refusal over its ceiling keeps what it is.</b> Its
    /// outcome and path are kept, its message is shortened, and the cut
    /// is said. Dropping it would trade a long refusal for none.</para>
    ///
    /// <para>An image's bytes are not text, and travel beside the answer
    /// as an image, so the measure leaves them out.</para>
    /// </summary>
    public static CliResult Held(CliResult answer, bool json)
    {
        int ceiling = answer.Failed ? Budget.ErrorCeiling : Budget.Ceiling;
        string shown = json ? answer.Stdout : answer.Stdout + answer.Stderr;
        int size = Size.Text(json ? WithoutImages(shown) : shown);

        if (size <= ceiling) return answer;

        if (answer.Failed && json && Refusal(answer.Stdout) is { } refusal)
            return answer with { Stdout = Shortened(refusal, size) };

        if (answer.Failed && !json)
            return answer with { Stderr = Clipped(answer.Stderr, Budget.ErrorCeiling - Size.NewLine) + Environment.NewLine };

        return Failed(new Failure(Outcome.Refused,
            $"the command ran, and its answer was {size:N0} bytes, over the {ceiling:N0}-byte ceiling, "
            + "so the answer was not sent. Any change it was asked to make has been made. This is a fettle defect: "
            + "ask for less in one call (a range, a narrower glob, a lower limit), and report it"), json);
    }

    /// <summary>The outcome, message and path of a refusal written as
    /// JSON, or null when the text is not one.</summary>
    static (string Outcome, string Message, string? Path)? Refusal(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("outcome", out JsonElement outcome)) return null;

            string message = root.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()! : string.Empty;
            string? path = root.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;

            return (outcome.GetString() ?? "refused", message, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A refusal rewritten to fit the error ceiling: the same
    /// outcome and path, and as much of the message as fits, with the cut
    /// said.</summary>
    static string Shortened((string Outcome, string Message, string? Path) refusal, int size)
    {
        string said = $" [{size:N0} bytes; the rest was left out to stay under the "
                      + $"{Budget.ErrorCeiling:N0}-byte limit for a refusal]";

        string Write(string message) => JsonBody(w =>
        {
            w.WriteBoolean("ok", false);
            w.WriteString("outcome", refusal.Outcome);
            w.WriteString("message", message);
            if (refusal.Path is { } path) w.WriteString("path", path);
        });

        int keep = Budget.Prefix(refusal.Message, Budget.ErrorCeiling,
            m => Size.Text(Write(m + said)));
        return Write(refusal.Message[..keep] + said);
    }

    static string Clipped(string text, int room) => text[..Budget.Prefix(text, room, Size.Text)];

    /// <summary>A JSON answer with every <c>base64</c> value emptied, for
    /// measuring the text of an answer that carries images.</summary>
    static string WithoutImages(string text)
    {
        if (!text.Contains("\"base64\"", StringComparison.Ordinal)) return text;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            return JsonBody(w =>
            {
                foreach (JsonProperty top in doc.RootElement.EnumerateObject())
                {
                    if (top.Name != "files" || top.Value.ValueKind != JsonValueKind.Array)
                    {
                        top.WriteTo(w);
                        continue;
                    }

                    w.WriteStartArray("files");
                    foreach (JsonElement file in top.Value.EnumerateArray())
                    {
                        w.WriteStartObject();
                        foreach (JsonProperty field in file.EnumerateObject())
                            if (field.Name != "base64") field.WriteTo(w);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
            });
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>The bytes an answer's envelope takes in whichever mode
    /// is larger, so the room left is the room both modes have.</summary>
    static int Envelope(Action<Utf8JsonWriter> json, string human) =>
        Math.Max(Size.Text(Json(json)), Size.Text(human));

    /// <summary>The sentence the terminal ends a cut answer with.</summary>
    internal static string MoreSentence(string position) =>
        $"... more; pass --after {position} to carry on{Environment.NewLine}";

    // ---- read ----

    static CliResult Read(Bench bench, Arguments args, bool json)
    {
        var paths = new List<string>(args.Positional);
        if (paths.Count == 0)
            return Failed(new Failure(Outcome.Invalid, "read needs at least one path"), json);

        int? from = args.Has("from") ? args.Int("from", 1) : null;
        int? to = args.Has("to") ? args.Int("to", int.MaxValue) : null;
        int? tail = args.Has("tail") ? args.Int("tail", 0) : null;
        int? column = args.Has("column") ? args.Int("column", 0) : null;
        string? member = args.Value("member");

        // Two different questions, so the contradiction is named rather
        // than resolved by precedence. Answering one of them silently is
        // R3.14's failure in another costume: not an error, a wrong answer.
        if (tail is not null && (from is not null || to is not null))
            return Failed(new Failure(Outcome.Invalid,
                "--tail cannot be combined with --from or --to"), json);

        if (tail is < 1)
            return Failed(new Failure(Outcome.Invalid,
                "--tail needs a count of 1 or more"), json);

        if (member is not null && paths.Count != 1)
            return Failed(new Failure(Outcome.Invalid,
                "--member reads one entry from one archive; give one archive path"), json);

        int envelope = Envelope(w =>
        {
            w.WriteStartArray("files");
            w.WriteEndArray();
            w.WriteNumber("count", int.MaxValue);
        }, string.Empty);

        Result<IReadOnlyList<ReadSlice>> result = bench.Read(new ReadRequest(
            paths, from, to, tail, member, args.Has("numbered"), column,
            new Meter(bench, Budget.Ceiling - envelope)));
        if (!result.IsOk) return Failed(result.Failure!, json);

        if (json)
            return Ok(Json(w =>
            {
                w.WriteStartArray("files");
                foreach (ReadSlice s in result.Value) WriteSlice(w, bench, s);
                w.WriteEndArray();
                w.WriteNumber("count", result.Value.Count);
            }));

        var text = new StringBuilder();
        foreach (ReadSlice s in result.Value) HumanSlice(text, bench, s, args.Has("numbered"));
        return Ok(text.ToString());
    }

    /// <summary>One file of a read, as JSON.</summary>
    internal static void WriteSlice(Utf8JsonWriter w, Bench bench, ReadSlice s)
    {
        w.WriteStartObject();
        w.WriteString("path", Show(bench, s.Path));
        w.WriteString("kind", Typed.NameOf(s.Kind));

        if (s.Image is { } image)
        {
            w.WriteString("mime_type", image.MimeType);
            w.WriteNumber("width", image.Width);
            w.WriteNumber("height", image.Height);
            w.WriteNumber("bytes", image.Bytes);
            w.WriteString("hash", s.Hash);

            // The bytes travel base64 in the machine-readable answer. The
            // MCP front end lifts them out into a native image part rather
            // than leaving a caller to read base64 as prose. An image held
            // back says why instead.
            if (s.Withheld is { } why) w.WriteString("withheld", why);
            else w.WriteString("base64", Convert.ToBase64String(image.Content));

            WriteFacts(w, s.Facts);
            w.WriteEndObject();
            return;
        }

        w.WriteString("text", s.Text);
        w.WriteNumber("from", s.From);
        w.WriteNumber("to", s.To);
        w.WriteNumber("lines", s.TotalLines);
        w.WriteBoolean("truncated", s.Truncated);
        if (s.Truncated) w.WriteNumber("lines_remaining", s.Remaining);
        if (s.Column != 1) w.WriteNumber("column", s.Column);

        if (s.Next is { } next)
        {
            w.WriteStartObject("next");
            w.WriteNumber("from", next.From);
            if (next.Column is { } c) w.WriteNumber("column", c);
            w.WriteEndObject();
        }

        if (s.Budgeted) w.WriteBoolean("budgeted", true);
        w.WriteString("encoding", s.EncodingName);
        w.WriteString("line_ending", s.LineEnding);
        w.WriteBoolean("line_ending_mixed", s.MixedEndings);
        w.WriteBoolean("final_newline", s.FinalNewline);
        w.WriteString("hash", s.Hash);
        w.WriteNumber("bytes", s.Bytes);
        WriteFacts(w, s.Facts);
        w.WriteEndObject();
    }

    /// <summary>One file of a read, as the terminal shows it.</summary>
    internal static StringBuilder HumanSlice(StringBuilder text, Bench bench, ReadSlice s, bool numbered)
    {
        if (s.Image is { } image)
        {
            // A terminal cannot show a picture, so it is told what there
            // is instead of being sent a screenful of base64.
            text.Append(Show(bench, s.Path)).Append("  ").Append(image.MimeType)
                .Append("  ").Append(image.Width).Append('x').Append(image.Height)
                .Append("  ").Append(image.Bytes).Append(" bytes");
            if (s.Withheld is { } why) text.Append("  (not sent: ").Append(why).Append(')');
            return text.AppendLine();
        }

        text.Append(Show(bench, s.Path)).Append("  ").Append(Typed.NameOf(s.Kind))
            .Append("  ").Append(s.EncodingName)
            .Append("  ").Append(s.LineEnding).Append(s.MixedEndings ? " (MIXED)" : "")
            .Append("  final-newline: ").Append(s.FinalNewline ? "yes" : "no")
            .Append("  lines ").Append(s.From).Append('-').Append(s.To)
            .Append(" of ").Append(s.TotalLines);
        if (s.Column != 1) text.Append(", line ").Append(s.From).Append(" from column ").Append(s.Column);
        text.AppendLine();

        // The terminal always numbers. With --numbered the text is
        // numbered already, in the same spelling, so it is not numbered
        // twice.
        int number = s.From;
        foreach (Line line in TextIo.SplitLines(s.Text))
            if (numbered) text.AppendLine(line.Text);
            else text.Append((number++).ToString().PadLeft(Bench.NumberWidth)).Append(" | ").AppendLine(line.Text);

        // 9b.8: a truncated answer must never read as a complete one, and
        // must say the way to see the rest.
        if (s.Next is { } next)
        {
            if (next.Column is { } column)
                text.Append("... line ").Append(next.From).Append(" goes on; pass --from ").Append(next.From)
                    .Append(" --column ").Append(column).AppendLine(" for the rest of it");
            else
                text.Append("... ").Append(s.Remaining)
                    .Append(" more line(s); pass --from ").Append(next.From)
                    .AppendLine(" to continue, or --to N for a range");
        }
        else if (s.Budgeted && s.From > 1)
        {
            text.Append("... ").Append(s.From - 1)
                .Append(" earlier line(s); pass --to ").Append(s.From - 1)
                .AppendLine(" to go back");
        }

        // Which cap stopped it, because "pass --to for more" is bad advice
        // when the answer was bounded by size rather than by the range
        // asked for.
        if (s.Budgeted)
            text.Append("      (stopped at the ").Append($"{Budget.Ceiling:N0}")
                .AppendLine("-byte limit for one call)");

        return text;
    }

    /// <summary>The largest a slice of this path could be with no text in
    /// it, in each of its shapes: cut part way along a line, stopped
    /// between lines, and an image held back.</summary>
    internal static IEnumerable<ReadSlice> WorstSlices(ContainedPath path, FileFacts facts)
    {
        const int big = int.MaxValue;
        string hash = new('f', 64);
        string encoding = new('e', 24);

        yield return new ReadSlice(path, string.Empty, big, big, big, encoding, "crlf", true, true,
            hash, long.MaxValue, facts, FileKind.Spreadsheet, null, true, big, big);

        yield return new ReadSlice(path, string.Empty, big, 1, big, encoding, "crlf", true, true,
            hash, long.MaxValue, facts, FileKind.Spreadsheet, null, true, big, null);

        yield return new ReadSlice(path, string.Empty, 0, 0, 0, "binary", "none", false, false,
            hash, long.MaxValue, facts, FileKind.Image,
            new ImageFacts("image/x-portable-graymap", big, big, long.MaxValue, []),
            Withheld: $"larger than 5 MB ({long.MaxValue:N0} bytes), the most one image may be when "
                      + "it is sent to the model");
    }

    // ---- search ----

    static CliResult Search(Bench bench, Arguments args, bool json, TextReader stdin)
    {
        var patterns = new List<string>(args.Values("pattern"));
        if (args.At(0) is { } first) patterns.Insert(0, first);
        for (int i = 1; i < args.Positional.Count; i++) patterns.Add(args.Positional[i]);

        // R4.20: the pattern is the argument a shell is likeliest to
        // mangle before Fettler ever sees it - quotes, backslashes and a
        // leading -- all mean something to somebody first - so it may
        // arrive as bytes instead. This is R5.11's argument about edit
        // text, applied where it turns out to matter just as much: an
        // eaten quote does not fail, it silently searches for something
        // else.
        if (args.Value("pattern-file") is { } file)
        {
            try { AddLines(patterns, File.ReadAllText(file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Failed(new Failure(Outcome.NotFound,
                    $"cannot read --pattern-file: {e.Message}", file), json);
            }
        }

        if (args.Has("pattern-stdin")) AddLines(patterns, TextIo.WithoutMark(stdin.ReadToEnd()));

        if (patterns.Count == 0)
            return Failed(new Failure(Outcome.Invalid,
                "search needs a pattern: PATTERN, -e PATTERN, "
                + "--pattern-file PATH or --pattern-stdin"), json);

        PatternKind kind = args.Has("literal") ? PatternKind.Literal : PatternKind.Regex;
        SearchMode mode = args.Has("count") ? SearchMode.Count
            : args.Has("files-only") ? SearchMode.Files
            : SearchMode.Hits;
        bool glob = args.Value("glob") is not null;

        // Everything in the answer but the hits, at its largest.
        var worst = new SearchAnswer([], int.MaxValue, int.MaxValue, true, kind, patterns,
            int.MaxValue, int.MaxValue, int.MaxValue, mode, [], int.MaxValue);
        int envelope = Envelope(w => WriteSearch(w, bench, worst),
            HumanSearch(bench, worst, glob) + GlobSentence(int.MaxValue));

        if (envelope >= Budget.Ceiling)
            return Failed(new Failure(Outcome.Invalid,
                "the patterns alone fill an answer; search for fewer at a time"), json);

        Result<SearchAnswer> result = bench.Search(new SearchRequest(
            patterns,
            Root: args.Value("in"),
            Glob: args.Value("glob"),
            Kind: kind,
            CaseSensitive: args.Has("case-sensitive"),
            Context: args.Int("context", 0),
            Limit: args.Int("limit", 200),
            IncludeGenerated: args.Has("include-generated"),
            Exclude: args.Values("exclude"),
            Documents: !args.Has("no-documents"),
            Mode: mode,
            After: args.Value("after"),
            Meter: new Meter(bench, Budget.Ceiling - envelope)));

        if (!result.IsOk) return Failed(result.Failure!, json);

        return Ok(json
            ? Json(w => WriteSearch(w, bench, result.Value))
            : HumanSearch(bench, result.Value, glob));
    }

    /// <summary>A search answer as JSON.</summary>
    static void WriteSearch(Utf8JsonWriter w, Bench bench, SearchAnswer answer)
    {
        w.WriteString("kind", answer.Kind == PatternKind.Literal ? "literal" : "regex");
        w.WriteStartArray("patterns");
        foreach (string p in answer.Patterns) w.WriteStringValue(p);
        w.WriteEndArray();

        if (answer.Mode == SearchMode.Hits)
        {
            w.WriteStartArray("hits");
            foreach (Hit h in answer.Hits) WriteHit(w, bench, h);
            w.WriteEndArray();
        }

        if (answer.Mode == SearchMode.Files)
        {
            w.WriteStartArray("matched_files");
            foreach (MatchedFile f in answer.Matched ?? []) WriteMatched(w, bench, f);
            w.WriteEndArray();
        }

        w.WriteNumber("hits_count", HitsCount(answer));
        w.WriteNumber("files", answer.Files);
        w.WriteNumber("files_matched", answer.FilesMatched);
        w.WriteNumber("files_searched", answer.FilesSearched);
        w.WriteBoolean("truncated", answer.Truncated);
        w.WriteNumber("excluded", answer.Excluded);
        if (answer.DocumentsSkipped > 0)
            w.WriteNumber("documents_skipped", answer.DocumentsSkipped);
        if (answer.Next is { } next) w.WriteString("next", next);
    }

    /// <summary>How many matching lines the answer accounts for: every one
    /// for a count, those in the files named for a list of files, those
    /// served for hits.</summary>
    static int HitsCount(SearchAnswer answer) => answer.Mode switch
    {
        SearchMode.Count => answer.Total,
        SearchMode.Files => (answer.Matched ?? []).Sum(f => f.Hits),
        _ => answer.Hits.Count,
    };

    /// <summary>One search hit, as JSON.</summary>
    internal static void WriteHit(Utf8JsonWriter w, Bench bench, Hit h)
    {
        w.WriteStartObject();
        w.WriteString("path", Show(bench, h.Path));
        w.WriteNumber("line", h.Line);
        w.WriteNumber("column", h.Column);
        if (h.Cite is { } cite) w.WriteString("cite", cite);
        w.WriteString("text", h.Text);
        WriteClip(w, h.Clip);

        if (h.Context.Count > 0)
        {
            w.WriteStartArray("context");
            foreach (ContextLine c in h.Context)
            {
                w.WriteStartObject();
                w.WriteNumber("line", c.Number);
                w.WriteString("text", c.Text);
                WriteClip(w, c.Clip);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    /// <summary>Where a cut-down line sits in the whole one, so the text
    /// can still be found in the file and edited.</summary>
    static void WriteClip(Utf8JsonWriter w, Clip? clip)
    {
        if (clip is null) return;
        w.WriteBoolean("clipped", true);
        w.WriteNumber("text_from_column", clip.FromColumn);
        w.WriteNumber("line_length", clip.LineLength);
    }

    /// <summary>One search hit, as the terminal shows it.</summary>
    internal static StringBuilder HumanHit(StringBuilder text, Bench bench, Hit h)
    {
        text.Append(Show(bench, h.Path)).Append(':').Append(h.Line).Append(':').Append(h.Column);
        if (h.Cite is { } cite) text.Append("  (").Append(cite).Append(')');
        if (h.Clip is { } clip)
            text.Append("  (columns ").Append(clip.FromColumn).Append('-')
                .Append(clip.FromColumn + h.Text.Length - 1).Append(" of ").Append(clip.LineLength).Append(')');
        text.AppendLine();

        if (h.Context.Count > 0)
            foreach (ContextLine c in h.Context)
                text.Append(c.Number.ToString().PadLeft(5)).Append(" | ").AppendLine(c.Text);
        else
            text.Append(h.Line.ToString().PadLeft(5)).Append(" | ").AppendLine(h.Text);

        return text;
    }

    /// <summary>One file of a search that names files, as JSON.</summary>
    internal static void WriteMatched(Utf8JsonWriter w, Bench bench, MatchedFile f)
    {
        w.WriteStartObject();
        w.WriteString("path", Show(bench, f.Path));
        w.WriteNumber("hits", f.Hits);
        w.WriteEndObject();
    }

    /// <summary>One file of a search that names files, as the terminal
    /// shows it.</summary>
    internal static StringBuilder HumanMatched(StringBuilder text, Bench bench, MatchedFile f) =>
        text.Append(Show(bench, f.Path)).Append("  ").Append(f.Hits).AppendLine();

    /// <summary>A search answer as the terminal shows it.</summary>
    static string HumanSearch(Bench bench, SearchAnswer answer, bool glob)
    {
        var text = new StringBuilder();
        int hits = HitsCount(answer);

        if (answer.Mode == SearchMode.Files)
            foreach (MatchedFile f in answer.Matched ?? []) HumanMatched(text, bench, f);
        else if (answer.Mode == SearchMode.Hits)
            foreach (Hit h in answer.Hits) HumanHit(text, bench, h);

        text.Append(hits).Append(hits == 1 ? " hit in " : " hits in ")
            .Append(answer.Files).Append(answer.Files == 1 ? " file" : " files");

        // Said out loud rather than left to be inferred from a count that
        // looks complete. A search that skipped a document found nothing
        // in it, which is not the same as there being nothing in it.
        if (answer.DocumentsSkipped > 0)
            text.Append("; ").Append(answer.DocumentsSkipped)
                .Append(answer.DocumentsSkipped == 1 ? " document was" : " documents were")
                .Append(" not searched");

        text.AppendLine();
        if (answer.Next is { } next) text.Append(MoreSentence(next));

        // R3.3: a glob that matched nothing, or matched only files with
        // no text in them, is said out loud. Otherwise it reads as a
        // search that looked and found nothing.
        if (glob) text.Append(GlobSentence(answer.FilesMatched, answer.FilesSearched));

        return text.ToString();
    }

    /// <summary>What the terminal says about a glob that matched nothing,
    /// or nothing that holds text.</summary>
    static string GlobSentence(int matched, int searched = 1) =>
        matched == 0 ? NothingMatched + Environment.NewLine
        : searched == 0 ? $"{matched} {(matched == 1 ? "file" : "files")} matched the glob; "
                          + $"none could be searched as text{Environment.NewLine}"
        : string.Empty;

    /// <summary>The longest <see cref="GlobSentence"/> can be, for the
    /// room an answer keeps for it.</summary>
    static string GlobSentence(int largest) =>
        $"{largest} files matched the glob; none could be searched as text{Environment.NewLine}";

    const string NothingMatched = "0 files matched the glob; nothing was searched";

    // ---- find ----

    static CliResult Find(Bench bench, Arguments args, bool json)
    {
        DateTimeOffset? since = null;
        if (args.Value("since") is { } s)
        {
            if (!DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
                return Failed(new Failure(Outcome.Invalid, $"--since needs an ISO-8601 time; got '{s}'"), json);
            since = parsed;
        }

        bool byModified = args.Value("sort") == "mtime" || args.Has("sort-modified");

        var request = new FindRequest(
            Root: args.Value("in"),
            Pattern: args.At(0),
            IncludeGenerated: args.Has("include-generated"),
            Exclude: args.Values("exclude"),
            Since: since,
            Limit: args.Int("limit", 1000),
            ByModified: byModified,
            After: args.Value("after"));

        Result<Bounded<FoundFile>> found = bench.FindChecked(request);
        if (!found.IsOk) return Failed(found.Failure!, json);

        Bounded<FoundFile> answer = found.Value;

        int room = Budget.Ceiling - Envelope(
            w => WriteFind(w, bench, [], int.MaxValue),
            HumanFind(bench, [], int.MaxValue, null));

        int fit = Budget.Fit(answer.Items, room,
            f => Math.Max(Size.Json(w => WriteFound(w, bench, f)) + 1, Size.Text(HumanFound(bench, f))),
            f => Meter.Next(Tree.Position(f, byModified)),
            more: answer.Truncated);

        // An answer always moves forward: one file is a path and a few
        // numbers, far inside any room.
        fit = Math.Max(fit, Math.Min(1, answer.Count));

        IReadOnlyList<FoundFile> shown = [.. answer.Items.Take(fit)];
        string? next = fit < answer.Count || answer.Truncated
            ? Tree.Position(shown[^1], byModified)
            : null;

        return Ok(json
            ? Json(w => WriteFind(w, bench, shown, answer.Excluded, next))
            : HumanFind(bench, shown, answer.Excluded, next));
    }

    static void WriteFind(Utf8JsonWriter w, Bench bench, IReadOnlyList<FoundFile> files, int excluded,
                          string? next = null)
    {
        w.WriteStartArray("files");
        foreach (FoundFile f in files) WriteFound(w, bench, f);
        w.WriteEndArray();
        w.WriteNumber("count", files.Count);
        w.WriteBoolean("truncated", next is not null);
        w.WriteNumber("excluded", excluded);
        if (next is not null) w.WriteString("next", next);
    }

    static void WriteFound(Utf8JsonWriter w, Bench bench, FoundFile f)
    {
        w.WriteStartObject();
        w.WriteString("path", Show(bench, f.Path));
        w.WriteNumber("bytes", f.Bytes);
        w.WriteString("modified", f.Modified.UtcDateTime.ToString("o"));
        WriteFacts(w, f.Facts);
        w.WriteEndObject();
    }

    static string HumanFound(Bench bench, FoundFile f) =>
        Show(bench, f.Path).PadRight(48) + f.Bytes.ToString().PadLeft(9) + "  "
        + f.Modified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ") + Environment.NewLine;

    static string HumanFind(Bench bench, IReadOnlyList<FoundFile> files, int excluded, string? next)
    {
        var text = new StringBuilder();
        foreach (FoundFile f in files) text.Append(HumanFound(bench, f));

        text.Append(files.Count).Append(files.Count == 1 ? " file" : " files");
        if (excluded > 0) text.Append($" ({excluded} skipped in bin, obj, .git and the like; pass --include-generated to see them)");
        text.AppendLine();
        if (next is not null) text.Append(MoreSentence(next));
        return text.ToString();
    }

    // ---- edit ----

    static async Task<CliResult> Edit(Bench bench, Arguments args, bool json, TextReader stdin, CancellationToken cancel)
    {
        Result<IReadOnlyList<FileEdits>> batch = EditScript.Build(args, stdin, bench);
        if (!batch.IsOk) return Failed(batch.Failure!, json);

        Result<EditAnswer> applied = await bench.EditAsync(batch.Value, args.Has("dry-run"), cancel,
                args.Has("allow-credential"))
            .ConfigureAwait(false);

        if (!applied.IsOk) return Failed(applied.Failure!, json);
        EditAnswer answer = applied.Value;

        // The edit is done, or planned, by now. Its report is cut to fit
        // like any list, files first because each carries the hash the
        // next edit needs, then the plan, and says how much it left out.
        int room = Budget.Ceiling - Envelope(
            w => WriteEdit(w, bench, answer, [], [], int.MaxValue, int.MaxValue),
            HumanEdit(bench, answer, [], [], int.MaxValue, int.MaxValue));

        int Filed(EditedFile f) => Math.Max(Size.Json(w => WriteEdited(w, bench, f)) + 1, Size.Text(HumanEdited(bench, f)));
        int Planned(PlannedEdit p) => Math.Max(Size.Json(w => WritePlanned(w, p)) + 1, Size.Text(HumanPlanned(p)));

        int files = Budget.Fit(answer.Files, room, Filed);
        room -= answer.Files.Take(files).Sum(Filed);
        int plan = Budget.Fit(answer.Plan, room, Planned);

        IReadOnlyList<EditedFile> shownFiles = [.. answer.Files.Take(files)];
        IReadOnlyList<PlannedEdit> shownPlan = [.. answer.Plan.Take(plan)];
        int filesOmitted = answer.Files.Count - files;
        int planOmitted = answer.Plan.Count - plan;

        return Ok(json
            ? Json(w => WriteEdit(w, bench, answer, shownPlan, shownFiles, planOmitted, filesOmitted))
            : HumanEdit(bench, answer, shownPlan, shownFiles, planOmitted, filesOmitted));
    }

    static void WriteEdit(Utf8JsonWriter w, Bench bench, EditAnswer answer, IReadOnlyList<PlannedEdit> plan,
                          IReadOnlyList<EditedFile> files, int planOmitted, int filesOmitted)
    {
        w.WriteBoolean("dry_run", answer.DryRun);
        w.WriteStartArray("plan");
        foreach (PlannedEdit p in plan) WritePlanned(w, p);
        w.WriteEndArray();
        if (planOmitted > 0) w.WriteNumber("plan_omitted", planOmitted);

        w.WriteStartArray("files");
        foreach (EditedFile f in files) WriteEdited(w, bench, f);
        w.WriteEndArray();
        if (filesOmitted > 0) w.WriteNumber("files_omitted", filesOmitted);
    }

    static void WritePlanned(Utf8JsonWriter w, PlannedEdit p)
    {
        w.WriteStartObject();
        w.WriteString("path", p.Path);
        w.WriteNumber("edit", p.Index);
        w.WriteNumber("line", p.Line);
        w.WriteString("description", p.Description);
        w.WriteEndObject();
    }

    static void WriteEdited(Utf8JsonWriter w, Bench bench, EditedFile f)
    {
        w.WriteStartObject();
        w.WriteString("path", Show(bench, f.Path));
        w.WriteNumber("edits", f.EditsApplied);
        w.WriteString("hash", f.Hash);
        w.WriteBoolean("line_ending_mixed", f.MixedEndings);
        WriteLost(w, f.MetadataLost);
        w.WriteEndObject();
    }

    static string HumanPlanned(PlannedEdit p) =>
        $"   edit {p.Index}  {p.Path} line {p.Line}  {p.Description}{Environment.NewLine}";

    static string HumanEdited(Bench bench, EditedFile f)
    {
        var human = new StringBuilder()
            .Append("   ").Append(Show(bench, f.Path)).Append("  ").Append(f.Hash[..12])
            .AppendLine(f.MixedEndings ? "  (MIXED line endings)" : "");
        AppendLost(human, f.MetadataLost);
        return human.ToString();
    }

    static string HumanEdit(Bench bench, EditAnswer answer, IReadOnlyList<PlannedEdit> plan,
                            IReadOnlyList<EditedFile> files, int planOmitted, int filesOmitted)
    {
        var human = new StringBuilder();
        if (answer.DryRun)
        {
            human.Append("would apply ").Append(answer.Plan.Count)
                 .Append(answer.Plan.Count == 1 ? " edit" : " edits").AppendLine();
            foreach (PlannedEdit p in plan) human.Append(HumanPlanned(p));
            if (planOmitted > 0) human.Append("   and ").Append(planOmitted).AppendLine(" more, not listed");
        }
        else
        {
            int edits = 0;
            foreach (EditedFile f in answer.Files) edits += f.EditsApplied;
            human.Append("applied ").Append(edits).Append(edits == 1 ? " edit to " : " edits to ")
                 .Append(answer.Files.Count).AppendLine(answer.Files.Count == 1 ? " file" : " files");
            foreach (EditedFile f in files) human.Append(HumanEdited(bench, f));
            if (filesOmitted > 0) human.Append("   and ").Append(filesOmitted).AppendLine(" more files, not listed");
        }

        return human.ToString();
    }

    // ---- replace ----

    static async Task<CliResult> Replace(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        string? find = args.At(0);
        string? with = args.At(1);
        if (find is null || with is null)
            return Failed(new Failure(Outcome.Invalid, "replace needs FIND and WITH"), json);

        // after pages the list a dry run prints. A replace that writes
        // changes every file whatever the list shows, so after there would
        // read as narrowing what it changes, and it does not.
        string? after = args.Value("after");
        if (after is not null && !args.Has("dry-run"))
            return Failed(new Failure(Outcome.Invalid,
                "after pages the list a dry run prints. A replace that writes changes every file, "
                + "so after is only for --dry-run"), json);

        Result<ReplaceAnswer> result = await bench.ReplaceAsync(
            find, with, args.Value("glob"), args.Value("in"), args.Has("dry-run"),
            args.Has("include-generated"), cancel).ConfigureAwait(false);

        if (!result.IsOk) return Failed(result.Failure!, json);
        ReplaceAnswer answer = result.Value;
        bool glob = args.Value("glob") is not null;

        IReadOnlyList<ReplacePlan> listed = after is null
            ? answer.Files
            : [.. answer.Files.Where(p => string.CompareOrdinal(p.Path, after) > 0)];

        int room = Budget.Ceiling - Envelope(
            w => WriteReplace(w, answer with { TotalOccurrences = int.MaxValue }, [], null),
            HumanReplace(answer with { TotalOccurrences = int.MaxValue }, [], null, glob));

        int fit = Budget.Fit(listed, room,
            p => Math.Max(Size.Json(w => WritePlan(w, p)) + 1, Size.Text(HumanPlan(p))),
            p => Meter.Next(p.Path));

        fit = Math.Max(fit, Math.Min(1, listed.Count));
        IReadOnlyList<ReplacePlan> shown = [.. listed.Take(fit)];
        string? next = fit < listed.Count ? shown[^1].Path : null;

        return Ok(json
            ? Json(w => WriteReplace(w, answer, shown, next))
            : HumanReplace(answer, shown, next, glob));
    }

    static void WriteReplace(Utf8JsonWriter w, ReplaceAnswer answer, IReadOnlyList<ReplacePlan> shown, string? next)
    {
        w.WriteBoolean("dry_run", answer.DryRun);
        w.WriteNumber("occurrences", answer.TotalOccurrences);
        w.WriteStartArray("files");
        foreach (ReplacePlan p in shown) WritePlan(w, p);
        w.WriteEndArray();
        w.WriteNumber("count", answer.Files.Count);
        if (next is not null) w.WriteString("next", next);
    }

    static void WritePlan(Utf8JsonWriter w, ReplacePlan p)
    {
        w.WriteStartObject();
        w.WriteString("path", p.Path);
        w.WriteNumber("occurrences", p.Occurrences);
        w.WriteEndObject();
    }

    static string HumanPlan(ReplacePlan p) =>
        "   " + p.Path.PadRight(44) + p.Occurrences + Environment.NewLine;

    static string HumanReplace(ReplaceAnswer answer, IReadOnlyList<ReplacePlan> shown, string? next, bool glob)
    {
        var human = new StringBuilder()
            .Append(answer.DryRun ? "would change " : "changed ")
            .Append(answer.Files.Count).Append(answer.Files.Count == 1 ? " file, " : " files, ")
            .Append(answer.TotalOccurrences)
            .AppendLine(answer.TotalOccurrences == 1 ? " occurrence" : " occurrences");

        foreach (ReplacePlan p in shown) human.Append(HumanPlan(p));
        if (next is not null) human.Append(MoreSentence(next));

        if (glob && answer.FilesMatched == 0)
            human.AppendLine(NothingMatched);

        return human.ToString();
    }

    // ---- copy ----

    static async Task<CliResult> Copy(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (args.At(0) is not { } from || args.At(1) is not { } to)
            return Failed(new Failure(Outcome.Invalid, "copy needs a source and a destination"), json);

        Result<CopyReport> copied = await bench.CopyAsync(
            from, to, args.Has("recursive"), args.Has("overwrite"), args.Has("preserve-times"), cancel)
            .ConfigureAwait(false);

        if (!copied.IsOk) return Failed(copied.Failure!, json);
        CopyReport c = copied.Value;

        int room = Budget.Ceiling - Envelope(
            w => WriteCopy(w, bench, c with { Files = int.MaxValue, Directories = int.MaxValue, Bytes = long.MaxValue }, [], int.MaxValue),
            HumanCopy(bench, c with { Files = int.MaxValue, Directories = int.MaxValue }, [], int.MaxValue));

        int fit = Budget.Fit(c.LinksSkipped, room,
            s => Math.Max(Size.Escaped(s) + 3, Size.Text(HumanSkipped(s))));
        IReadOnlyList<string> shown = [.. c.LinksSkipped.Take(fit)];
        int omitted = c.LinksSkipped.Count - fit;

        return Ok(json
            ? Json(w => WriteCopy(w, bench, c, shown, omitted))
            : HumanCopy(bench, c, shown, omitted));
    }

    static void WriteCopy(Utf8JsonWriter w, Bench bench, CopyReport c, IReadOnlyList<string> shown, int omitted)
    {
        w.WriteString("from", Show(bench, c.From));
        w.WriteString("to", Show(bench, c.To));
        w.WriteNumber("files", c.Files);
        w.WriteNumber("directories", c.Directories);
        w.WriteNumber("bytes", c.Bytes);
        w.WriteBoolean("crossed_root", c.CrossedRoot);
        w.WriteStartArray("links_skipped");
        foreach (string s in shown) w.WriteStringValue(s);
        w.WriteEndArray();
        if (omitted > 0) w.WriteNumber("links_skipped_omitted", omitted);
    }

    static string HumanSkipped(string skipped) => "   not copied (a link): " + skipped + Environment.NewLine;

    static string HumanCopy(Bench bench, CopyReport c, IReadOnlyList<string> shown, int omitted)
    {
        var human = new StringBuilder()
            .Append("copied ").Append(Show(bench, c.From)).Append(" -> ").Append(Show(bench, c.To))
            .Append("  ").Append(c.Files).Append(c.Files == 1 ? " file" : " files");
        if (c.Directories > 0) human.Append("  ").Append(c.Directories).Append(c.Directories == 1 ? " directory" : " directories");
        if (c.CrossedRoot) human.Append("  crossed roots");
        human.AppendLine();

        foreach (string skipped in shown) human.Append(HumanSkipped(skipped));
        if (omitted > 0) human.Append("   and ").Append(omitted).AppendLine(" more links, not copied and not listed");

        return human.ToString();
    }

    // ---- tasks ----

    static CliResult TaskList(Bench bench, bool json)
    {
        Result<IReadOnlyList<TaskDecl>> tasks = bench.TaskList();
        if (!tasks.IsOk) return Failed(tasks.Failure!, json);

        if (tasks.Value.Count == 0 && !json)
            return Ok(bench.Roots.Levels.GuardrailsOn
                ? $"no tasks declared (no \"tasks\" under \"guardrails\" in {RootsFile.FileName}){Environment.NewLine}"
                : $"no tasks declared. Tasks belong to the guardrails level, which is off{Environment.NewLine}");

        int column = NameColumn(tasks.Value.Select(t => t.Name));

        int room = Budget.Ceiling - Envelope(
            w => WriteTasks(w, [], int.MaxValue, int.MaxValue),
            HumanTasks([], int.MaxValue, column));

        int fit = Budget.Fit(tasks.Value, room,
            t => Math.Max(Size.Json(w => WriteTask(w, t)) + 1, Size.Text(HumanTask(t, column))));
        IReadOnlyList<TaskDecl> shown = [.. tasks.Value.Take(fit)];
        int omitted = tasks.Value.Count - fit;

        return Ok(json
            ? Json(w => WriteTasks(w, shown, tasks.Value.Count, omitted))
            : HumanTasks(shown, omitted, column));
    }

    static void WriteTasks(Utf8JsonWriter w, IReadOnlyList<TaskDecl> shown, int count, int omitted)
    {
        w.WriteStartArray("tasks");
        foreach (TaskDecl t in shown) WriteTask(w, t);
        w.WriteEndArray();
        w.WriteNumber("count", count);
        if (omitted > 0) w.WriteNumber("tasks_omitted", omitted);
    }

    static void WriteTask(Utf8JsonWriter w, TaskDecl t)
    {
        w.WriteStartObject();
        w.WriteString("name", t.Name);
        // Both spellings, deliberately. "run" is the line as DECLARED,
        // placeholders and all, so the listing can be compared with the
        // file; "command" is what would actually be launched. Showing only
        // one of them leaves the caller trusting a listing that is wrong
        // in one direction or the other.
        w.WriteString("run", t.Display);
        w.WriteStartArray("command");
        foreach (string part in t.Command) w.WriteStringValue(part);
        w.WriteEndArray();
        if (t.WorkingDirectory is { } cwd) w.WriteString("cwd", cwd);
        w.WriteEndObject();
    }

    static string HumanTask(TaskDecl t, int column) => t.Name.PadRight(column) + t.Display + Environment.NewLine;

    static string HumanTasks(IReadOnlyList<TaskDecl> shown, int omitted, int column)
    {
        var text = new StringBuilder();
        foreach (TaskDecl t in shown) text.Append(HumanTask(t, column));
        if (omitted > 0)
            text.Append("... and ").Append(omitted)
                .Append(" more tasks, not listed; every task is in ").AppendLine(RootsFile.FileName);
        return text.ToString();
    }

    // ---- run ----

    static async Task<CliResult> Run(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (args.At(0) is not { } name)
            return Failed(new Failure(Outcome.Invalid, "run needs the name of a declared task"), json);

        // A task is declared whole, so nothing is composed at the call.
        // Dropping an extra word silently would run something other than
        // what the caller asked for and answer 0, which is the wrong-answer
        // shape R3.14 exists to prevent, one argument further in.
        if (args.Positional.Count > 1)
            return Failed(new Failure(Outcome.Invalid,
                $"run takes only a task name; '{args.At(1)}' is extra. "
                + "A task takes no arguments. Declare a second task for a variant"), json);

        var timeout = TimeSpan.FromSeconds(args.Int("timeout", 0));
        Result<TaskRun> ran = await bench.RunAsync(name, timeout, cancel).ConfigureAwait(false);
        if (!ran.IsOk) return Failed(ran.Failure!, json);

        // The core kept as many characters as could ever fit. The cut to
        // the bytes actually written is made here, once, for both modes,
        // and keeps the end of each stream.
        TaskRun worst = ran.Value with
        {
            Stdout = string.Empty, Stderr = string.Empty, ExitCode = int.MinValue,
            Duration = TimeSpan.MaxValue, StdoutOmitted = long.MaxValue, StderrOmitted = long.MaxValue,
        };
        int room = Budget.Ceiling - Envelope(w => WriteRun(w, worst, truncated: true), HumanRun(worst, truncated: true));

        (Captured output, Captured error) = Tasks.Share(
            new Captured(ran.Value.Stdout, ran.Value.StdoutOmitted),
            new Captured(ran.Value.Stderr, ran.Value.StderrOmitted),
            room, text => Math.Max(Size.Escaped(text), Size.Text("[stdout] " + text) + Size.NewLine));

        TaskRun r = ran.Value with
        {
            Stdout = output.Text, Stderr = error.Text,
            StdoutOmitted = output.Omitted, StderrOmitted = error.Omitted,
        };

        return Ok(json
            ? Json(w => WriteRun(w, r, r.Truncated))
            : HumanRun(r, r.Truncated));
    }

    static void WriteRun(Utf8JsonWriter w, TaskRun r, bool truncated)
    {
        w.WriteString("task", r.Name);
        if (r.Command is { } launched)
        {
            w.WriteStartArray("command");
            foreach (string part in launched) w.WriteStringValue(part);
            w.WriteEndArray();
        }
        w.WriteNumber("exit_code", r.ExitCode);
        w.WriteString("stdout", r.Stdout);
        w.WriteString("stderr", r.Stderr);
        w.WriteBoolean("truncated", truncated);
        if (truncated)
        {
            w.WriteNumber("stdout_omitted", r.StdoutOmitted);
            w.WriteNumber("stderr_omitted", r.StderrOmitted);
        }
        w.WriteNumber("seconds", Math.Round(r.Duration.TotalSeconds, 3));
        w.WriteBoolean("timed_out", r.TimedOut);
    }

    static string HumanRun(TaskRun r, bool truncated)
    {
        var human = new StringBuilder()
            .Append(r.Name).Append(": exit ").Append(r.ExitCode)
            .Append(" in ").Append(r.Duration.TotalSeconds.ToString("0.0")).AppendLine("s");

        if (r.Stdout.Length > 0) human.Append("[stdout] ").AppendLine(r.Stdout.TrimEnd());
        if (r.Stderr.Length > 0) human.Append("[stderr] ").AppendLine(r.Stderr.TrimEnd());

        // 9b.8, as read says it: a cut answer must never read as a whole
        // one, and must say the way to the rest.
        if (truncated)
            human.Append("... ").Append($"{r.StdoutOmitted:N0} characters of stdout and ")
                .Append($"{r.StderrOmitted:N0} of stderr were left out from the start; ")
                .Append($"the limit is {Budget.Ceiling:N0} bytes a call. ")
                .AppendLine("For all of it, have the task write a log and read it with --tail");

        // The task's own exit code is not this command's: `run` succeeded
        // in running it. A caller wanting the child's code reads it from
        // the answer, which is why it is in both output modes.
        return human.ToString();
    }

    // ---- batch ----

    static async Task<CliResult> Batch(Bench bench, Arguments args, bool json, CancellationToken cancel)
    {
        if (Scripts.Given(args, bench) is not { } script)
            return Failed(new Failure(Outcome.Invalid, "batch needs --script FILE or --script-path PATH"), json);

        Result<BatchAnswer> ran = await BatchScript.RunAsync(bench, script, cancel).ConfigureAwait(false);

        if (!ran.IsOk) return Failed(ran.Failure!, json);
        BatchAnswer answer = ran.Value;

        // A batch that stopped early did not do what was asked, so it is
        // a refusal, held to the error ceiling. The operation that failed
        // always goes in it, and the operations nearest it fill the rest:
        // they are the ones that say what state the tree was left in.
        bool stopped = answer.StoppedEarly;
        int ceiling = stopped ? Budget.ErrorCeiling : Budget.Ceiling;
        BatchStep? failed = stopped && answer.Steps.Count > 0 ? answer.Steps[^1] : null;
        IReadOnlyList<BatchStep> before = failed is null ? answer.Steps : [.. answer.Steps.Take(answer.Steps.Count - 1)];

        int room = ceiling - Envelope(
            w => WriteBatch(w, answer with { Completed = int.MaxValue }, [], int.MaxValue),
            HumanBatch(answer with { Completed = int.MaxValue }, [], int.MaxValue));

        int Cost(BatchStep s) => Math.Max(Size.Json(w => WriteStep(w, s)) + 1, Size.Text(HumanStep(s)));
        if (failed is not null) room -= Cost(failed);

        List<BatchStep> shown;
        if (stopped)
        {
            IReadOnlyList<BatchStep> nearest = [.. before.Reverse()];
            shown = [.. nearest.Take(Budget.Fit(nearest, room, Cost)).Reverse()];
            if (failed is not null) shown.Add(failed);
        }
        else
        {
            shown = [.. before.Take(Budget.Fit(before, room, Cost))];
        }

        int omitted = answer.Steps.Count - shown.Count;

        string stdout = json
            ? JsonBody(w => WriteBatch(w, answer, shown, omitted))
            : HumanBatch(answer, shown, omitted);

        return new CliResult(stopped ? ExitCodes.Refused : ExitCodes.Ok, stdout, string.Empty) { Failed = stopped };
    }

    /// <summary>A batch answer as JSON. One that stopped early carries the
    /// shape of a refusal, ok false with an outcome and a message, so it
    /// reads as one whichever way it is read.</summary>
    static void WriteBatch(Utf8JsonWriter w, BatchAnswer answer, IReadOnlyList<BatchStep> shown, int omitted)
    {
        w.WriteBoolean("ok", !answer.StoppedEarly);

        if (answer.StoppedEarly && answer.Steps.Count > 0)
        {
            BatchStep failed = answer.Steps[^1];
            w.WriteString("outcome", ExitCodes.NameOf(failed.Outcome));
            w.WriteString("message",
                $"stopped at operation {failed.Index}, which failed. The operations before it were done and stay done");
        }

        w.WriteStartArray("operations");
        foreach (BatchStep s in shown) WriteStep(w, s);
        w.WriteEndArray();
        if (omitted > 0) w.WriteNumber("operations_omitted", omitted);
        w.WriteNumber("completed", answer.Completed);
        w.WriteNumber("count", answer.Steps.Count);
        w.WriteBoolean("stopped_early", answer.StoppedEarly);
    }

    static void WriteStep(Utf8JsonWriter w, BatchStep s)
    {
        w.WriteStartObject();
        w.WriteNumber("index", s.Index);
        w.WriteString("op", s.Op);
        w.WriteString("outcome", ExitCodes.NameOf(s.Outcome));
        w.WriteString("detail", s.Detail);
        w.WriteEndObject();
    }

    static string HumanStep(BatchStep s) =>
        s.Index.ToString().PadLeft(2) + "  " + s.Op.PadRight(8)
        + ExitCodes.NameOf(s.Outcome).PadRight(14) + s.Detail + Environment.NewLine;

    static string HumanBatch(BatchAnswer answer, IReadOnlyList<BatchStep> shown, int omitted)
    {
        var human = new StringBuilder();
        if (omitted > 0 && answer.StoppedEarly)
            human.Append("... ").Append(omitted).AppendLine(" earlier operations, all ok, not listed");

        foreach (BatchStep s in shown) human.Append(HumanStep(s));

        if (omitted > 0 && !answer.StoppedEarly)
            human.Append("... ").Append(omitted).AppendLine(" more operations, not listed");

        human.Append(answer.Steps.Count).Append(answer.Steps.Count == 1 ? " operation, " : " operations, ")
             .Append(answer.Completed).AppendLine(" ok");

        if (answer.StoppedEarly)
            human.AppendLine("stopped at the first failure. The operations before it were done and stay done");

        return human.ToString();
    }
}
