using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fettler.Core;

namespace Fettler.Mcp;

/// <summary>
/// Hints: one short line the server adds beside an answer when the calls
/// so far show a missed batch or a missed hand-off.
///
/// <para><b>Why the server says it.</b> The instructions say to do several
/// things in one call and to feed each answer into the next. They are
/// read once, at the start of a session. A hint arrives at the moment of
/// the miss, about the call just made, which is when it can change the
/// next one.</para>
///
/// <para><b>Every rule was measured before it was written.</b> A survey of
/// 17 sessions found 78% of reads naming one path, back-to-back
/// single-target calls mostly in pairs, 56 reads of lines already read,
/// 25 edits without the hash a read had given, and the same refusal twice
/// in one session across 10 kinds of refusal. Rules with no evidence
/// were left out.</para>
///
/// <para><b>Only what the server can see.</b> A run continues only when a
/// call arrived after the previous answer had gone out, and only when its
/// target was not named in that answer: a read of a path the last answer
/// gave had to wait for it. At most one hint per answer, in this order:
/// refusals, hand-offs, runs.</para>
///
/// <para>MCP only. The command line answers one call in a fresh process,
/// so there is nothing before it to judge.</para>
/// </summary>
public sealed class Hints
{
    static readonly HashSet<string> FileOps = ["write", "new", "mkdir", "move", "copy", "delete", "exec"];

    readonly object gate = new();

    string? runKind;
    int runLength;
    readonly List<string> runTools = [];
    string previousAnswer = string.Empty;
    List<(string Path, int Line)> previousHits = [];

    readonly HashSet<string> read = new(StringComparer.Ordinal);
    readonly HashSet<string> seen = new(StringComparer.Ordinal);
    readonly HashSet<string> refusals = new(StringComparer.Ordinal);

    /// <summary>
    /// The hint for a call just answered, or null.
    /// </summary>
    /// <param name="sequential">Whether the call arrived after the
    /// previous answer had gone out, by more than a turn's gap.</param>
    public string? After(string tool, JsonElement arguments, string answer, bool isError, bool sequential)
    {
        lock (gate)
        {
            Call call = new(tool, arguments);
            JsonElement root = Parse(answer);

            string? hint = Refused(call, root, isError)
                ?? (isError ? null : Unexpected(call))
                ?? Run(call, isError, sequential)
                ?? (isError ? null : Again(call, root))
                ?? (isError ? null : Around(call));

            Remember(call, root, answer, isError);
            return hint is null ? null : Fit(hint);
        }
    }

    // ---- 8 and 9: refusals ----

    string? Refused(Call call, JsonElement root, bool isError)
    {
        if (!isError) return null;

        string outcome = Text(root, "outcome") ?? "";
        if (outcome == "outside-root")
            return "hint: no tree covers this path. Retrying will not help. A person has to add a tree "
                   + "to .fettler.local.json.";

        string message = Text(root, "message") ?? string.Empty;
        string key = $"{call.Tool} {outcome} {Class(message)}";
        return refusals.Add(key) ? null : "hint: this call was refused the same way earlier in this session.";
    }

    /// <summary>A refusal's message with the parts that differ between two
    /// calls of the same kind taken out: quoted text, paths and numbers.</summary>
    static string Class(string message)
    {
        string c = Regex.Replace(message, "'[^']*'|\"[^\"]*\"|`[^`]*`", "X");
        c = Regex.Replace(c, @"[/\w.-]*[/\\][/\w.\\-]*", "P");
        return Regex.Replace(c, @"\d[\d,]*", "N");
    }

    // ---- 5: an edit that did not hand on the hash ----

    string? Unexpected(Call call)
    {
        if (call.Tool != "edit") return null;
        foreach ((string path, string? expect) in call.Edits)
            if (expect is null && read.Contains(Norm(path)))
                return $"hint: this edit had no expect. {path} was read earlier in this session. "
                       + "Pass the hash that read returned as expect.";
        return null;
    }

    // ---- 1 to 4: runs ----

    string? Run(Call call, bool isError, bool sequential)
    {
        string? kind = isError ? null : call.Kind;

        bool waited = kind is not null && !kind.StartsWith("edit:", StringComparison.Ordinal)
                      && call.Targets.Any(t => previousAnswer.Contains(Last(t), StringComparison.Ordinal));

        if (kind is not null && kind == runKind && sequential && !waited)
        {
            runLength++;
            runTools.Add(call.Tool);
        }
        else
        {
            runKind = kind;
            runLength = kind is null ? 0 : 1;
            runTools.Clear();
            if (kind is not null) runTools.Add(call.Tool);
            return null;
        }

        return kind switch
        {
            "read" => $"hint: the last {runLength} read calls each named one path, sent one after another. "
                      + "read takes several paths in one call.",
            "search" => $"hint: the last {runLength} search calls each had one pattern, sent one after another. "
                        + "search takes several patterns in one call.",
            "fileop" => $"hint: the last {runLength} calls were single file operations "
                        + $"({string.Join(", ", runTools.Distinct())}), sent one after another. "
                        + "batch runs a list of them in one call.",
            _ => $"hint: the last {runLength} edit calls changed the same file, one after another. "
                 + "One edit call takes every change to a file.",
        };
    }

    // ---- 6: the same lines again ----

    string? Again(Call call, JsonElement root)
    {
        if (call.Tool != "read") return null;
        foreach ((string path, string key) in Slices(root))
            if (seen.Contains(key))
                return $"hint: this read returned the same lines of {path} as an earlier read in this session. "
                       + "The file had not changed.";
        return null;
    }

    // ---- 7: the lines around a hit ----

    string? Around(Call call)
    {
        if (call.Tool != "read" || call.Paths.Count != 1 || call.From is not { } from || call.To is not { } to)
            return null;
        if (to - from + 1 > 21) return null;

        string path = Norm(call.Paths[0]);
        return previousHits.Any(h => Norm(h.Path) == path && h.Line >= from && h.Line <= to)
            ? "hint: this read covered the lines around a hit from the search before it. "
              + "search takes context N to return them with the hit."
            : null;
    }

    // ---- what is kept between calls ----

    void Remember(Call call, JsonElement root, string answer, bool isError)
    {
        previousAnswer = answer;
        previousHits = [];

        if (isError) return;

        if (call.Tool == "read")
            foreach ((string path, string key) in Slices(root))
            {
                read.Add(Norm(path));
                seen.Add(key);
            }

        if (call.Tool == "search" && root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("hits", out JsonElement hits) && hits.ValueKind == JsonValueKind.Array)
            foreach (JsonElement h in hits.EnumerateArray())
                if (Text(h, "path") is { } p && h.TryGetProperty("line", out JsonElement l) && l.TryGetInt32(out int line))
                    previousHits.Add((p, line));
    }

    /// <summary>Each text file a read answered, with a key naming the
    /// exact lines and the file's hash: two reads with the same key read
    /// the same lines of an unchanged file.</summary>
    static IEnumerable<(string Path, string Key)> Slices(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out JsonElement files)
            || files.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (JsonElement f in files.EnumerateArray())
        {
            if (Text(f, "path") is not { } path || Text(f, "hash") is not { } hash) continue;
            if (!f.TryGetProperty("text", out _)) continue;
            string column = f.TryGetProperty("column", out JsonElement c) ? c.ToString() : "1";
            string key = $"{Norm(path)}|{Number(f, "from")}|{Number(f, "to")}|{column}|{hash}";
            yield return (path, key);
        }
    }

    // ---- reading ----

    /// <summary>One call's arguments, as far as the rules need them.</summary>
    sealed class Call(string tool, JsonElement arguments)
    {
        public string Tool { get; } = tool;

        string? Str(string key) =>
            arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(key, out JsonElement v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        int? Int(string key) =>
            arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(key, out JsonElement v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;

        List<string> List(string key)
        {
            var list = new List<string>();
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(key, out JsonElement v)
                && v.ValueKind == JsonValueKind.Array)
                foreach (JsonElement s in v.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.String) list.Add(s.GetString()!);
            return list;
        }

        /// <summary>The paths, each without the range it may carry.</summary>
        public List<string> Paths =>
            [.. List("paths").Select(p => ReadRange.Split(p) is { IsOk: true } r ? r.Value.Path : p)];

        /// <summary>The range the one path of a read carries, when there is
        /// one path and it carries one.</summary>
        RangedPath? Own =>
            List("paths") is [string only] && ReadRange.Split(only) is { IsOk: true } r ? r.Value : null;

        public int? From => Int("from") ?? Own?.From;
        public int? To => Int("to") ?? Own?.To;

        /// <summary>The files an edit names, each with its expect.</summary>
        public List<(string Path, string? Expect)> Edits
        {
            get
            {
                var list = new List<(string, string?)>();
                if (arguments.ValueKind != JsonValueKind.Object) return list;
                if (arguments.TryGetProperty("script", out JsonElement s) && s.ValueKind == JsonValueKind.Object
                    && s.TryGetProperty("files", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement f in files.EnumerateArray())
                        if (Text(f, "path") is { } p) list.Add((p, Text(f, "expect")));
                    return list;
                }

                if (Str("path") is { } path)
                {
                    string? expect = Str("expect")
                        ?? (arguments.TryGetProperty("script", out JsonElement sc) ? Text(sc, "expect") : null);
                    list.Add((path, expect));
                }
                return list;
            }
        }

        /// <summary>The kind of run this call can belong to, or null.</summary>
        public string? Kind => Tool switch
        {
            "read" when Paths.Count == 1 && Int("column") is null => "read",
            "search" when List("patterns").Count == 1 => "search",
            "edit" when Edits.Count == 1 => "edit:" + Norm(Edits[0].Path),
            _ when FileOps.Contains(Tool) => "fileop",
            _ => null,
        };

        /// <summary>What the call names: paths, patterns, places.</summary>
        public List<string> Targets
        {
            get
            {
                var t = new List<string>();
                t.AddRange(Paths);
                t.AddRange(List("patterns"));
                foreach (string k in new[] { "path", "from", "to" }) if (Str(k) is { } v) t.Add(v);
                return t;
            }
        }
    }

    static JsonElement Parse(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    static string? Text(JsonElement owner, string key) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(key, out JsonElement v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string Number(JsonElement owner, string key) =>
        owner.TryGetProperty(key, out JsonElement v) ? v.ToString() : "";

    /// <summary>A path with any tree name in front taken off, so
    /// <c>work:a.txt</c> and <c>a.txt</c> compare equal.</summary>
    static string Norm(string path)
    {
        int colon = path.IndexOf(':');
        if (colon > 1 && path[..colon].IndexOfAny(['/', '\\']) < 0) path = path[(colon + 1)..];
        return path.Replace('\\', '/');
    }

    /// <summary>The last part of a path, or the whole of a pattern.</summary>
    static string Last(string target)
    {
        string t = Norm(target);
        int slash = t.LastIndexOf('/');
        return slash >= 0 && slash < t.Length - 1 ? t[(slash + 1)..] : t;
    }

    /// <summary>A hint cut to the room kept for it. Only a path makes one
    /// long, so the middle of the longest word goes first.</summary>
    static string Fit(string hint)
    {
        if (Encoding.UTF8.GetByteCount(hint) <= Budget.HintRoom) return hint;

        string longest = hint.Split(' ').MaxBy(w => w.Length)!;
        int over = Encoding.UTF8.GetByteCount(hint) - Budget.HintRoom + 3;
        int keep = Math.Max(8, longest.Length - over);
        string shorter = "..." + longest[^Math.Min(longest.Length, keep)..];
        string fitted = hint.Replace(longest, shorter);

        return Encoding.UTF8.GetByteCount(fitted) <= Budget.HintRoom
            ? fitted
            : fitted[..Budget.Prefix(fitted, Budget.HintRoom, s => Encoding.UTF8.GetByteCount(s))];
    }
}
