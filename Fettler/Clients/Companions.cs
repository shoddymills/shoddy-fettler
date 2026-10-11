using System.Text.Json;
using Fettler.Core;

namespace Fettler.Clients;

/// <summary>One other MCP server registered beside fettle.</summary>
/// <param name="Name">The name it is registered under.</param>
/// <param name="Root">The absolute path its <c>--root</c> argument names,
/// or null when it passes none.</param>
/// <param name="Registration">The file the registration is in.</param>
public sealed record Companion(string Name, string? Root, string Registration);

/// <summary>
/// The other servers registered beside fettle, and the folder each one
/// reads and writes (R12).
///
/// <para><b>A companion is a server whose registration passes
/// <c>--root PATH</c>.</b> That is a convention fettle reads, not
/// knowledge of any one program. A server such as sparky reads and
/// writes every file under that folder with none of fettle's rules, and
/// reaches nothing outside it. So where the folder is decides whether the
/// server goes round the trees, and fettle reports it.</para>
///
/// <para><b>The folder is opened as a tree named for the server</b>,
/// read and write. The model already reaches it through the companion,
/// so the tree grants nothing new. It lets fettle write a file there
/// that the companion loads in the same turn.</para>
///
/// <para><b>Read only from the closed list.</b> The registrations are
/// read from <see cref="Places"/> and nowhere else, the same files
/// <see cref="Doctor"/> reads.</para>
/// </summary>
public static class Companions
{
    /// <summary>Every other server registered for this project, in the
    /// order the files are listed, each name once.</summary>
    public static IReadOnlyList<Companion> Find(Machine machine)
    {
        var found = new List<Companion>();

        foreach (Place place in Places.All(machine))
        {
            if (place.Purpose != Purpose.Servers || !File.Exists(place.Path)) continue;
            if (!Places.Allows(machine, place.Path)) continue;

            JsonDocument? doc = Load(place.Path);
            if (doc is null) continue;

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                foreach (string holder in new[] { "mcpServers", "servers" })
                    if (root.TryGetProperty(holder, out JsonElement servers))
                        Take(servers, place.Path, found);

                // Claude Code keeps a per-project copy keyed by the project
                // folder. Only this project's entry applies here.
                if (root.TryGetProperty("projects", out JsonElement projects)
                    && projects.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty project in projects.EnumerateObject())
                        if (SameFolder(project.Name, machine.Tree)
                            && project.Value.ValueKind == JsonValueKind.Object
                            && project.Value.TryGetProperty("mcpServers", out JsonElement inProject))
                            Take(inProject, place.Path, found);
            }
        }

        return found;
    }

    /// <summary>The companions with a root, as trees for the boundary.
    /// A root that does not exist is not a tree.</summary>
    public static IReadOnlyList<HarnessTree> Trees(IReadOnlyList<Companion> companions)
    {
        var trees = new List<HarnessTree>();
        foreach (Companion c in companions)
        {
            if (c.Root is null || !Roots.IsValidName(c.Name)) continue;
            if (Harness.Names.Contains(c.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (trees.Any(t => t.Name.Equals(c.Name, Roots.PathComparison))) continue;

            bool exists;
            try { exists = Directory.Exists(c.Root); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { exists = false; }
            if (!exists) continue;

            trees.Add(new HarnessTree(c.Name, c.Root, Permissions.Full,
                $"the --root of the {c.Name} server registered in {c.Registration}"));
        }
        return trees;
    }

    static void Take(JsonElement servers, string file, List<Companion> into)
    {
        if (servers.ValueKind != JsonValueKind.Object) return;

        foreach (JsonProperty server in servers.EnumerateObject())
        {
            if (server.Name.Equals(Places.ServerName, StringComparison.Ordinal)) continue;
            if (into.Any(c => c.Name.Equals(server.Name, StringComparison.Ordinal))) continue;

            into.Add(new Companion(server.Name, RootOf(server.Value, file), file));
        }
    }

    /// <summary>
    /// The folder a registration's <c>--root</c> names, as
    /// <c>--root PATH</c> or <c>--root=PATH</c>.
    ///
    /// <para>A relative path is resolved against the folder holding the
    /// registration, which is where a project-level registration's server
    /// is launched from.</para>
    /// </summary>
    static string? RootOf(JsonElement entry, string file)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("args", out JsonElement args)
            || args.ValueKind != JsonValueKind.Array)
            return null;

        var words = new List<string>();
        foreach (JsonElement word in args.EnumerateArray())
            if (word.ValueKind == JsonValueKind.String) words.Add(word.GetString()!);

        string? named = null;
        for (int i = 0; i < words.Count; i++)
        {
            if (words[i] == "--root" && i + 1 < words.Count) { named = words[i + 1]; break; }
            if (words[i].StartsWith("--root=", StringComparison.Ordinal)) { named = words[i]["--root=".Length..]; break; }
        }

        if (named is not { Length: > 0 }) return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, named)));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    static bool SameFolder(string a, string b)
    {
        try
        {
            string x = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
            string y = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
            return x.Equals(y, Roots.PathComparison);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The last read of each file, kept while its size and time
    /// are unchanged. serve asks before every call, and a client's own
    /// file can run to megabytes.</summary>
    static readonly Dictionary<string, (long Length, DateTime Written, string Text)> Cache = new(StringComparer.Ordinal);

    static readonly object Gate = new();

    static JsonDocument? Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            string text;

            lock (Gate)
            {
                if (Cache.TryGetValue(path, out var held) && held.Length == info.Length && held.Written == info.LastWriteTimeUtc)
                {
                    text = held.Text;
                }
                else
                {
                    text = TextIo.WithoutMark(File.ReadAllText(path));
                    Cache[path] = (info.Length, info.LastWriteTimeUtc, text);
                }
            }

            return JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
