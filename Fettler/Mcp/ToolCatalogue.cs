using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;

namespace Fettler.Mcp;

/// <summary>
/// The tools a model sees, and the one place they are turned into work.
///
/// <para><b>R3.4 requires that the two front ends share every operation,
/// and this is how that is kept true rather than promised.</b> A tool
/// call is marshalled into the same argument vector the command line
/// takes and handed to the same dispatcher, in machine-readable mode.
/// The MCP answer is therefore byte-identical to what
/// <c>fettle VERB --json</c> prints, which is exactly what R3.6 asks for
/// - and a capability available to a model and not to a script becomes
/// impossible to write rather than merely forbidden.</para>
///
/// <para>The tool names are the CLI verbs, unchanged, because two
/// vocabularies for one surface would break parity on the first day.</para>
/// </summary>
public static class ToolCatalogue
{
    sealed record Tool(string Name, string Summary, string Schema);

    static readonly Tool[] Tools =
    [
        new("find", "List files matching a glob, with size and modified time. Sorted by path, or by modified time with sort=mtime. The glob is relative to a declared tree. An answer that stops short gives next; pass it back as after for the rest.", """
            {"type":"object","properties":{
              "pattern":{"type":"string","description":"glob; * stays within a segment, ** crosses segments, matching is case-insensitive"},
              "in":{"type":"string","description":"which tree to search when more than one is open"},
              "sort":{"type":"string","enum":["path","mtime"]},
              "since":{"type":"string","description":"ISO-8601; only files modified after it"},
              "limit":{"type":"integer"},
              "after":{"type":"string","description":"the next the last answer gave; the listing starts after it"},
              "include_generated":{"type":"boolean","description":"include bin, obj, .git, artifacts and node_modules"},
              "exclude":{"type":"array","items":{"type":"string"}}
            }}
            """),

        new("search", "Find lines matching one or more patterns. Ask several questions in one call by giving several patterns. Each hit gives path, line, column and text, with optional context. Searches only inside the declared trees. PDFs are rendered to text and searched; a hit in one names its page. A long line is cut to the match and 200 characters either side; clipped, text_from_column and line_length say where. Stops at `limit` hits or at 40,000 bytes; `truncated` says so, and next is the after to pass for the rest. count is exact. files_only names each file once with its hits.", """
            {"type":"object","required":["patterns"],"properties":{
              "patterns":{"type":"array","items":{"type":"string"},"description":".NET regular expressions unless literal is true"},
              "glob":{"type":"string","description":"restrict to a subset of files"},
              "in":{"type":"string"},
              "literal":{"type":"boolean"},
              "case_sensitive":{"type":"boolean"},
              "context":{"type":"integer"},
              "limit":{"type":"integer"},
              "count":{"type":"boolean","description":"return counts only, no hits"},
              "files_only":{"type":"boolean"},
              "after":{"type":"string","description":"the next the last answer gave: PATH:LINE, or PATH with files_only"},
              "include_generated":{"type":"boolean"},
              "no_documents":{"type":"boolean","description":"do not render PDFs to search inside them. The answer says how many documents were skipped."}
            }}
            """),

        new("read", "Read files. Returns content, encoding, line ending, trailing-newline state and a content hash. Takes several paths in one call: read every file you need at once. Reads notebooks as cells, PDFs as text per page, spreadsheets as rows with their formulas, Word documents as paragraphs and tables, and images as facts plus the image itself. Rendered documents cannot be written back. Stops at 2000 lines unless `to` says otherwise, and at 40,000 bytes per call. An answer that stops short gives next: pass its from, and its column when it has one, for the rest. A line too long for one answer comes back in pieces. One image per call, and none over 5 MB. Use `tail` for the end of a log. With `numbered`, each line starts with its number and a bar, which is a rendering and not the file's content, so leave that prefix out of an edit. When screenings are on, a read that would disclose a screened category is refused.", """
            {"type":"object","required":["paths"],"properties":{
              "paths":{"type":"array","items":{"type":"string"}},
              "numbered":{"type":"boolean","description":"start each line with its number and a bar"},
              "from":{"type":"integer"},
              "to":{"type":"integer","description":"last line to read; lifts the 2000-line default. The 40,000-byte limit per call still applies."},
              "column":{"type":"integer","description":"the column the first line starts at, from next.column. Not combinable with tail."},
              "tail":{"type":"integer","description":"read the last n lines. Not combinable with from or to."},
              "member":{"type":"string","description":"read one entry of an archive instead of its member list. Nothing is unpacked to disk."}
            }}
            """),

        new("extract", "Unpack a .zip, .tar, .tar.gz or .tgz into a declared tree. Refused whole, with nothing written, if any member would land outside a tree, needs a permission the tree does not grant, or is a link. Keeps the executable bit. Read the archive first to see its members.", """
            {"type":"object","required":["archive","into"],"properties":{
              "archive":{"type":"string"},
              "into":{"type":"string","description":"the directory to unpack into"},
              "overwrite":{"type":"boolean","description":"replace files that already exist"}
            }}
            """),

        new("write", "Create or replace a file. Keeps its encoding and line endings unless told otherwise. Needs create for a new file and update for an existing one.", """
            {"type":"object","required":["path","text"],"properties":{
              "path":{"type":"string"},
              "text":{"type":"string"},
              "overwrite":{"type":"boolean","description":"required to replace an existing file"},
              "encoding":{"type":"string"},
              "eol":{"type":"string","enum":["lf","crlf","cr"]}
            }}
            """),

        new("edit", "Apply a batch of edits to one or more files. All of them apply, or none do. Make every change a task needs in one call, with the hash read gave as expect for each file. A large script goes in a file in the scratch tree and is named by script_path.", """
            {"type":"object","properties":{
              "path":{"type":"string","description":"the file, when the script does not carry a files array"},
              "expect":{"type":"string","description":"the hash read returned; the edit is refused if the file changed since"},
              "dry_run":{"type":"boolean"},
              "script":{"type":"object","description":"either {edits:[...]} or {files:[{path,expect,edits:[...]}]}; an edit is {replace,with,from,to,all} or {insertAfter,text} or {deleteFrom,deleteTo}"},
              "script_path":{"type":"string","description":"a file in a tree holding the script, instead of script. Give one or the other"}
            }}
            """),

        new("replace", "Replace one string with another in every file matching a glob. All files change, or none do. Use dry_run first. The totals are exact; a list that stops short gives next.", """
            {"type":"object","required":["find","with"],"properties":{
              "find":{"type":"string"},
              "with":{"type":"string"},
              "glob":{"type":"string"},
              "in":{"type":"string"},
              "dry_run":{"type":"boolean"},
              "after":{"type":"string","description":"the next a dry run gave; lists the files after it. Only with dry_run"},
              "include_generated":{"type":"boolean"}
            }}
            """),

        new("new", "Create an empty file. Refused if the file exists.", """
            {"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}
            """),

        new("mkdir", "Create a directory, with any missing parents.", """
            {"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}
            """),

        new("move", "Rename or move a file or directory. Reports whether the move was atomic.", """
            {"type":"object","required":["from","to"],"properties":{
              "from":{"type":"string"},"to":{"type":"string"},"overwrite":{"type":"boolean"}
            }}
            """),

        new("copy", "Copy a file, or a directory with recursive. Keeps the executable bit. Does not follow links.", """
            {"type":"object","required":["from","to"],"properties":{
              "from":{"type":"string"},"to":{"type":"string"},
              "recursive":{"type":"boolean"},"overwrite":{"type":"boolean"},"preserve_times":{"type":"boolean"}
            }}
            """),

        new("delete", "Delete one path. Not a pattern. A non-empty directory needs recursive.", """
            {"type":"object","required":["path"],"properties":{
              "path":{"type":"string"},"recursive":{"type":"boolean"},
              "force":{"type":"boolean","description":"clear a read-only attribute first"}
            }}
            """),

        new("exec", "Set or clear the executable bit. Reports unsupported on Windows.", """
            {"type":"object","required":["path","executable"],"properties":{
              "path":{"type":"string"},"executable":{"type":"boolean"}
            }}
            """),

        new("roots", "Name the levels that are on, then list the trees: path, what each allows, the scopes inside it, and which tree an unqualified path lands in. The client's scratch, tool-results and memory folders are listed with how each was found. Call this first.", """
            {"type":"object","properties":{}}
            """),

        new("tasks", "List the declared tasks. \"run\" is the line as declared; \"command\" is the argument list that would be launched, with declared values filled in. Tasks belong to the guardrails level, so with guardrails off there are none.", """

            {"type":"object","properties":{}}
            """),

        new("run", "Run a declared task and return its output and exit code. A task takes no arguments: name and timeout are the only inputs, and anything else is refused. Values written {like-this} in a task come from the configuration file, never from the caller. The result names the argument list that was launched. Keeps the end of stdout and stderr that fits in 40,000 bytes, with terminal colour codes removed; `truncated` says so, and `stdout_omitted` and `stderr_omitted` say how many characters were left out. For all of it, have the task write a log and read it with `tail`. Needs the tree to grant execute, which is never a default. Tasks belong to the guardrails level, so with guardrails off nothing runs.", """
            {"type":"object","required":["name"],"additionalProperties":false,"properties":{
              "name":{"type":"string"},"timeout":{"type":"integer","description":"seconds; 0 means no timeout"}
            }}
            """),

        new("doctor", "Report whether fettle is registered with each assistant client, globally and in this project, then report each level in its own section. Findings appear only for a level that is on. Changes nothing.", """
            {"type":"object","properties":{
              "client":{"type":"string","enum":["claude-code","claude-desktop","vscode-copilot","github-copilot"],
                        "description":"report only this client; every client is still scanned"},
              "quiet":{"type":"boolean","description":"leave out what is healthy or does not apply"}
            }}
            """),

        new("batch", "Run several operations in order, stopping at the first failure. Not transactional. A large script goes in a file in the scratch tree and is named by script_path.", """
            {"type":"object","properties":{
              "script":{"type":"object","description":"{operations:[{op,path,...}]}; op is mkdir, new, write, move, copy, delete or exec"},
              "script_path":{"type":"string","description":"a file in a tree holding the script, instead of script. Give one or the other"}
            }}
            """),
    ];

    public static void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("tools");

        foreach (Tool tool in Tools)
        {
            w.WriteStartObject();
            w.WriteString("name", tool.Name);
            w.WriteString("description", tool.Summary);
            w.WritePropertyName("inputSchema");
            using JsonDocument schema = JsonDocument.Parse(tool.Schema);
            schema.RootElement.WriteTo(w);

            // The ceiling, said to the client as well. Claude Code reads
            // this as the size at which it saves an answer to a file, in
            // place of its own character and token limits for text.
            // Every answer is held under it anyway, so saying it costs
            // nothing and spares the client estimating.
            w.WriteStartObject("_meta");
            w.WriteNumber("anthropic/maxResultSizeChars", Budget.Ceiling);
            w.WriteEndObject();

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static async Task<Result<ToolAnswer>> InvokeAsync(
        Bench bench, string name, JsonElement arguments, CancellationToken cancel)
    {
        bool known = false;
        foreach (Tool tool in Tools) if (tool.Name == name) { known = true; break; }
        if (!known)
            return Result<ToolAnswer>.Fail(Outcome.Invalid, $"no tool called '{name}'");

        var argv = new List<string> { name, "--json" };
        string stdin = string.Empty;

        void Flag(string flag, string key)
        {
            if (arguments.ValueKind != JsonValueKind.Object) return;
            if (!arguments.TryGetProperty(key, out JsonElement v)) return;

            switch (v.ValueKind)
            {
                case JsonValueKind.True: argv.Add($"--{flag}"); break;
                case JsonValueKind.False: break;
                case JsonValueKind.String: argv.Add($"--{flag}"); argv.Add(v.GetString()!); break;
                case JsonValueKind.Number: argv.Add($"--{flag}"); argv.Add(v.ToString()); break;
            }
        }

        void Positional(string key)
        {
            if (arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty(key, out JsonElement v)
                && v.ValueKind == JsonValueKind.String)
                argv.Add(v.GetString()!);
        }

        void Each(string key, string flag)
        {
            if (arguments.ValueKind != JsonValueKind.Object) return;
            if (!arguments.TryGetProperty(key, out JsonElement v) || v.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) { argv.Add(flag); argv.Add(item.GetString()!); }
        }

        void Inline(string key)
        {
            if (arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty(key, out JsonElement v)
                && v.ValueKind == JsonValueKind.Object)
            {
                argv.Add("--script-inline");
                argv.Add(v.GetRawText());
            }
        }

        switch (name)
        {
            case "find":
                Positional("pattern");
                Flag("in", "in"); Flag("sort", "sort"); Flag("since", "since"); Flag("limit", "limit");
                Flag("after", "after");
                Flag("include-generated", "include_generated"); Each("exclude", "--exclude");
                break;

            case "search":
                Each("patterns", "-e");
                Flag("glob", "glob"); Flag("in", "in"); Flag("literal", "literal");
                Flag("case-sensitive", "case_sensitive"); Flag("context", "context"); Flag("limit", "limit");
                Flag("count", "count"); Flag("files-only", "files_only");
                Flag("include-generated", "include_generated");
                Flag("no-documents", "no_documents");
                Flag("after", "after");
                break;

            case "read":
                if (arguments.ValueKind == JsonValueKind.Object
                    && arguments.TryGetProperty("paths", out JsonElement paths)
                    && paths.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement path in paths.EnumerateArray())
                        if (path.ValueKind == JsonValueKind.String) argv.Add(path.GetString()!);
                Flag("from", "from"); Flag("to", "to"); Flag("tail", "tail");
                Flag("member", "member"); Flag("numbered", "numbered"); Flag("column", "column");
                break;

            case "extract":
                Positional("archive");
                Flag("into", "into"); Flag("overwrite", "overwrite");
                break;

            case "write":
                Positional("path");
                argv.Add("--stdin");
                Flag("overwrite", "overwrite"); Flag("encoding", "encoding"); Flag("eol", "eol");
                if (arguments.ValueKind == JsonValueKind.Object
                    && arguments.TryGetProperty("text", out JsonElement text)
                    && text.ValueKind == JsonValueKind.String)
                    stdin = text.GetString()!;
                break;

            case "edit":
                Positional("path");
                Flag("expect", "expect"); Flag("dry-run", "dry_run"); Inline("script");
                Flag("script-path", "script_path");
                break;

            case "replace":
                Positional("find"); Positional("with");
                Flag("glob", "glob"); Flag("in", "in"); Flag("dry-run", "dry_run");
                Flag("include-generated", "include_generated"); Flag("after", "after");
                break;

            case "new":
            case "mkdir":
                Positional("path");
                break;

            case "move":
                Positional("from"); Positional("to"); Flag("overwrite", "overwrite");
                break;

            case "copy":
                Positional("from"); Positional("to");
                Flag("recursive", "recursive"); Flag("overwrite", "overwrite");
                Flag("preserve-times", "preserve_times");
                break;

            case "delete":
                Positional("path"); Flag("recursive", "recursive"); Flag("force", "force");
                break;

            case "exec":
                Positional("path");
                argv.Add(arguments.ValueKind == JsonValueKind.Object
                    && arguments.TryGetProperty("executable", out JsonElement on)
                    && on.ValueKind == JsonValueKind.True ? "--on" : "--off");
                break;

            case "run":
                Positional("name"); Flag("timeout", "timeout");

                // Anything else a caller sends is an attempt to compose the
                // command at the call. It is passed on as an extra word so
                // the one refusal in Cli answers it by name, rather than
                // being dropped here where nobody would learn of it.
                if (arguments.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty extra in arguments.EnumerateObject())
                        if (extra.Name is not ("name" or "timeout")) argv.Add(extra.Name);
                break;

            case "doctor":
                Flag("client", "client"); Flag("quiet", "quiet");
                break;

            case "batch":
                Inline("script"); Flag("script-path", "script_path");
                break;
        }

        CliResult result = await Command
            .RunAsync(argv, new StringReader(stdin), bench, cancel).ConfigureAwait(false);

        // R3.7 already put the complete machine-readable answer, failures
        // included, on stdout - so the tool result is that text VERBATIM
        // whichever way it went. Describing it again would put a word and
        // a colon in front of a JSON document and hand the caller
        // something that no longer parses - and the word would be the
        // same one every time, where the payload's own "outcome" is the
        // true one.
        //
        // isError is marked when the operation did not do what was asked,
        // not from the exit code. doctor exits 1 or 2 for a script when it
        // finds something, and a client cuts an error answer over about
        // 11,000 characters to its head and tail - which turned a full
        // doctor report into JSON that did not parse.
        ToolAnswer answered = Lift(result.Stdout.TrimEnd());

        return Result<ToolAnswer>.Ok(result.Failed
            ? answered with { IsError = true, Images = [] }
            : answered);
    }

    /// <summary>
    /// An image the MCP answer carries natively rather than as text.
    /// </summary>
    public sealed record ImagePart(string MimeType, string Base64);

    /// <summary>What a tool call produced: the machine-readable answer,
    /// and any images that go beside it rather than inside it.</summary>
    public sealed record ToolAnswer(string Text, IReadOnlyList<ImagePart> Images, bool IsError = false)
    {
        public static ToolAnswer Of(string text) => new(text, []);
    }

    /// <summary>
    /// Move any base64 image out of the JSON text and into a native image
    /// part.
    ///
    /// <para><b>The one deliberate departure from R3.6's byte-identity,
    /// and the reason is arithmetic.</b> An image left in the text costs
    /// a caller a screenful of base64 they cannot look at; carried
    /// natively it costs the same bytes once and can actually be seen.
    /// Leaving it in BOTH places would cost it twice. Everything else
    /// about the answer is the CLI's own JSON, unchanged - only the
    /// <c>base64</c> field moves, and the mime type, size and hash stay
    /// where they were, so nothing is lost from the text.</para>
    /// </summary>
    static ToolAnswer Lift(string text)
    {
        if (!text.Contains("\"base64\"", StringComparison.Ordinal)) return ToolAnswer.Of(text);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return ToolAnswer.Of(text); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("files", out JsonElement files)
                || files.ValueKind != JsonValueKind.Array)
                return ToolAnswer.Of(text);

            var images = new List<ImagePart>();
            var buffer = new MemoryStream();

            using (var w = new Utf8JsonWriter(buffer, Command.Writing))
            {
                w.WriteStartObject();
                foreach (JsonProperty top in doc.RootElement.EnumerateObject())
                {
                    if (top.Name != "files") { top.WriteTo(w); continue; }

                    w.WriteStartArray("files");
                    foreach (JsonElement file in top.Value.EnumerateArray())
                    {
                        w.WriteStartObject();
                        foreach (JsonProperty field in file.EnumerateObject())
                        {
                            if (field.Name == "base64")
                            {
                                images.Add(new ImagePart(
                                    file.TryGetProperty("mime_type", out JsonElement m) && m.ValueKind == JsonValueKind.String
                                        ? m.GetString()! : "application/octet-stream",
                                    field.Value.GetString() ?? ""));
                                continue;
                            }
                            field.WriteTo(w);
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }

            return new ToolAnswer(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), images);
        }
    }
}
