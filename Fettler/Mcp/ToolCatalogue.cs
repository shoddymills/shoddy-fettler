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
        new("find", "Paths matching a glob, with size and modified time. Ordered by path, or by recency with sort=mtime. The glob is relative to a declared tree, not to any working directory.", """
            {"type":"object","properties":{
              "pattern":{"type":"string","description":"glob; * stays within a segment, ** crosses segments, matching is case-insensitive"},
              "in":{"type":"string","description":"which root to search when more than one is open"},
              "sort":{"type":"string","enum":["path","mtime"]},
              "since":{"type":"string","description":"ISO-8601; only files modified after it"},
              "limit":{"type":"integer"},
              "include_generated":{"type":"boolean","description":"do not skip bin, obj, .git, artifacts, node_modules"},
              "exclude":{"type":"array","items":{"type":"string"}}
            }}
            """),

        new("search", "Matching lines as records: path, line, column, text, optional context. Takes several patterns at once. Searches only inside declared trees; there is nothing outside them to search. A document that has to be rendered before it can be read - a PDF - is rendered and searched with everything else, and a hit inside one carries the page it sits on.", """
            {"type":"object","required":["patterns"],"properties":{
              "patterns":{"type":"array","items":{"type":"string"},"description":".NET regular expressions unless literal is true"},
              "glob":{"type":"string","description":"restrict to a subset of files"},
              "in":{"type":"string"},
              "literal":{"type":"boolean"},
              "case_sensitive":{"type":"boolean"},
              "context":{"type":"integer"},
              "limit":{"type":"integer"},
              "count":{"type":"boolean","description":"how many, without the hits"},
              "files_only":{"type":"boolean"},
              "include_generated":{"type":"boolean"},
              "no_documents":{"type":"boolean","description":"leave documents shut instead of rendering them to search inside. They ARE searched by default. Either way the answer says how many were not looked inside, because a file that was never opened is not a file with nothing in it."}
            }}
            """),

        new("read", "Content, encoding, line ending, trailing-newline state and a content hash. Takes several paths in one call. Reads notebooks as cells, PDFs as text per page, spreadsheets as rows of cells with their formulas, Word documents as paragraphs and tables under their headings, and images as facts plus a native image part - so this is the reader for every file type, not only for source. Those are rendered, never written back: a document is refused by write, edit and replace, because the text is a fraction of the file. Stops at 2000 lines unless `to` says otherwise, and says how many were left. For a log or a build transcript ask for `tail` instead - the end of the file, without needing to know its length.", """
            {"type":"object","required":["paths"],"properties":{
              "paths":{"type":"array","items":{"type":"string"}},
              "from":{"type":"integer"},
              "to":{"type":"integer","description":"lifts the default 2000-line cap; say what you need"},
              "tail":{"type":"integer","description":"the LAST n lines, counted from the end. Use this for logs and build output rather than reading the file to find out how long it is. Not combinable with from/to."},
              "member":{"type":"string","description":"read ONE entry out of a .zip/.tar/.tar.gz instead of its manifest, decoded like any other file. Nothing is unpacked to disk."}
            }}
            """),

        new("extract", "Unpack a .zip, .tar, .tar.gz or .tgz into a declared tree. Refused WHOLE - nothing written at all - if any member would land outside a tree, needs a permission this boundary has not granted, or is a link, so an archive never lands half-unpacked. Carries the executable bit. Read the archive first to see what is in it.", """
            {"type":"object","required":["archive","into"],"properties":{
              "archive":{"type":"string"},
              "into":{"type":"string","description":"the directory the members land in; there is no working directory to default to"},
              "overwrite":{"type":"boolean","description":"replace files already there; without it an existing file is a refusal"}
            }}
            """),

        new("write", "Create or replace a file, keeping its encoding and line endings unless told otherwise. Needs create where nothing is there and update where something is; a read-only tree grants neither.", """
            {"type":"object","required":["path","text"],"properties":{
              "path":{"type":"string"},
              "text":{"type":"string"},
              "overwrite":{"type":"boolean","description":"required to replace an existing file"},
              "encoding":{"type":"string"},
              "eol":{"type":"string","enum":["lf","crlf","cr"]}
            }}
            """),

        new("edit", "A batch of edits, resolved against the files as read and applied all or not at all. May span files.", """
            {"type":"object","required":["script"],"properties":{
              "path":{"type":"string","description":"the file, when the script does not carry a files array"},
              "expect":{"type":"string","description":"the hash read returned; the edit is refused if the file has moved"},
              "dry_run":{"type":"boolean"},
              "script":{"type":"object","description":"either {edits:[...]} or {files:[{path,expect,edits:[...]}]}; an edit is {replace,with,from,to,all} or {insertAfter,text} or {deleteFrom,deleteTo}"}
            }}
            """),

        new("replace", "One substitution everywhere it appears across a glob, all or nothing. Dry run first.", """
            {"type":"object","required":["find","with"],"properties":{
              "find":{"type":"string"},
              "with":{"type":"string"},
              "glob":{"type":"string"},
              "in":{"type":"string"},
              "dry_run":{"type":"boolean"},
              "include_generated":{"type":"boolean"}
            }}
            """),

        new("new", "An empty file, refusing rather than truncating one already there.", """
            {"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}
            """),

        new("mkdir", "A directory and the parents it needs.", """
            {"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}
            """),

        new("move", "Rename or relocate a file or a directory, reporting whether it was atomic.", """
            {"type":"object","required":["from","to"],"properties":{
              "from":{"type":"string"},"to":{"type":"string"},"overwrite":{"type":"boolean"}
            }}
            """),

        new("copy", "Copy a file, or a tree with recursive. Carries the executable bit; does not follow links.", """
            {"type":"object","required":["from","to"],"properties":{
              "from":{"type":"string"},"to":{"type":"string"},
              "recursive":{"type":"boolean"},"overwrite":{"type":"boolean"},"preserve_times":{"type":"boolean"}
            }}
            """),

        new("delete", "Delete one named path. Never a pattern; a non-empty directory needs recursive.", """
            {"type":"object","required":["path"],"properties":{
              "path":{"type":"string"},"recursive":{"type":"boolean"},
              "force":{"type":"boolean","description":"clear a read-only attribute first"}
            }}
            """),

        new("exec", "Set or clear the executable bit. Reports unsupported on Windows rather than pretending.", """
            {"type":"object","required":["path","executable"],"properties":{
              "path":{"type":"string"},"executable":{"type":"boolean"}
            }}
            """),

        new("roots", "The declared trees, their paths, WHAT MAY BE DONE in each and in any scope inside it, and which one an unqualified path lands in. Ask this first, and before guessing why something was refused.", """
            {"type":"object","properties":{}}
            """),

        new("tasks", "The declared tasks and what each runs: \"run\" is the line as declared, and \"command\" is the argument list that would actually be launched, with any declared values already filled in.", """

            {"type":"object","properties":{}}
            """),

        new("run", "Run a declared task, capturing its streams and exit code. Never composes a shell command. A task is declared whole and TAKES NO ARGUMENTS: name and timeout are the only inputs, and anything else is refused rather than ignored. If you need a variant, ask for a second task to be declared. A command line MAY carry values the configuration declares under \"replacements\", written {like-this} - those come from the file, never from you, and a person edits that file. The result reports the argument list that was actually launched. Needs the tree it runs in to grant execute, which is never granted by default.", """
            {"type":"object","required":["name"],"additionalProperties":false,"properties":{
              "name":{"type":"string"},"timeout":{"type":"integer","description":"seconds; 0 means no timeout"}
            }}
            """),

        new("doctor", "Whether this tool is wired into each assistant client, at both levels, and what on this machine still lets the boundary be gone round. Diagnoses; never changes anything.", """
            {"type":"object","properties":{
              "client":{"type":"string","enum":["claude-code","claude-desktop","vscode-copilot","github-copilot"],
                        "description":"narrows the REPORT; every client is scanned either way"},
              "quiet":{"type":"boolean","description":"leave out what is healthy or does not apply"}
            }}
            """),

        new("batch", "Several operations in one call, in order, stopping at the first failure. Not transactional.", """
            {"type":"object","required":["script"],"properties":{
              "script":{"type":"object","description":"{operations:[{op,path,...}]}; op is mkdir, new, write, move, copy, delete or exec"}
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
                Flag("include-generated", "include_generated"); Each("exclude", "--exclude");
                break;

            case "search":
                Each("patterns", "-e");
                Flag("glob", "glob"); Flag("in", "in"); Flag("literal", "literal");
                Flag("case-sensitive", "case_sensitive"); Flag("context", "context"); Flag("limit", "limit");
                Flag("count", "count"); Flag("files-only", "files_only");
                Flag("include-generated", "include_generated");
                Flag("no-documents", "no_documents");
                break;

            case "read":
                if (arguments.ValueKind == JsonValueKind.Object
                    && arguments.TryGetProperty("paths", out JsonElement paths)
                    && paths.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement path in paths.EnumerateArray())
                        if (path.ValueKind == JsonValueKind.String) argv.Add(path.GetString()!);
                Flag("from", "from"); Flag("to", "to"); Flag("tail", "tail");
                Flag("member", "member");
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
                break;

            case "replace":
                Positional("find"); Positional("with");
                Flag("glob", "glob"); Flag("in", "in"); Flag("dry-run", "dry_run");
                Flag("include-generated", "include_generated");
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
                Inline("script");
                break;
        }

        CliResult result = await Command
            .RunAsync(argv, new StringReader(stdin), bench, cancel).ConfigureAwait(false);

        // R3.7 already put the complete machine-readable answer, failures
        // included, on stdout - so the tool result is that text VERBATIM
        // whichever way it went, and the server marks isError from the
        // exit code. Describing it again would put a word and a colon in
        // front of a JSON document and hand the caller something that no
        // longer parses - and the word would be the same one every time,
        // where the payload's own "outcome" is the true one.
        ToolAnswer answered = Lift(result.Stdout.TrimEnd());

        return Result<ToolAnswer>.Ok(result.ExitCode == ExitCodes.Ok
            ? answered
            : answered with { IsError = true, Images = [] });
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

            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
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
