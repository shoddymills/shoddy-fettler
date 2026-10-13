using System.Text;
using Fettler.Core;

namespace Fettler.Clients;

/// <summary>
/// The Fettler agent file for VS Code Copilot: what setup writes, and how
/// doctor reads one back.
///
/// <para><b>VS Code has no deny list.</b> Its settings hold no key that
/// turns a built-in tool off, so the permissions block the other clients
/// take would be a key VS Code ignores. What VS Code does have is the
/// custom agent: a Markdown file with a <c>tools:</c> list in its front
/// matter, and the built-ins named there as tool sets. An agent whose list
/// leaves a set out cannot use it. So the Fettler agent lists the Fettler
/// server and leaves out <c>read</c>, <c>edit</c> and <c>search</c>; at
/// the guardrails level it leaves out <c>execute</c> too, the way the
/// shell denies do for Claude.</para>
///
/// <para><b>It binds when chosen.</b> The default agent keeps every tool.
/// A person picks the Fettler agent in the chat, and nothing here can
/// observe that choice; doctor judges the file, which is what it can
/// see.</para>
///
/// <para><b>The list is read from the file's own text</b>, by a reader
/// that knows the two YAML shapes a tools list takes - a flow sequence on
/// one line and a block of dashed lines - and nothing more. A YAML library
/// would read every file VS Code accepts, and would be a dependency carried
/// for one key.</para>
/// </summary>
public static class AgentFile
{
    /// <summary>The entry that stands for the Fettler server's tools: its
    /// registered name, which VS Code expands to every tool the server
    /// offers. Written from VS Code's documentation of agent files and not
    /// verified on this machine, so it is the first thing to re-check when
    /// the agent shows no Fettler tools.</summary>
    public const string ServerEntry = Places.ServerName;

    /// <summary>The built-in sets that reach no file and stay at every
    /// level: VS Code's own commands, subagents, the web and the task
    /// list.</summary>
    public static readonly string[] Kept = ["vscode", "agent", "web", "todo"];

    /// <summary>The tools the agent may use at a level: the Fettler server,
    /// the kept sets, and the terminal at the tools level only, because a
    /// declared task is what replaces a shell.</summary>
    public static IReadOnlyList<string> ToolsAt(string level) =>
        level == Levels.GuardrailsName
            ? [ServerEntry, .. Kept]
            : [ServerEntry, .. Doctor.CopilotShell, .. Kept];

    /// <summary>The guardrails paragraph in Copilot's own terms. The
    /// Claude one names the Claude tools, which this agent never had.</summary>
    public const string GuardrailsParagraph = """
        **Guardrails are on.** This agent has no built-in file tools and no terminal.
        Use the Fettler tools for every file operation, and `run` for a declared task.

        `.fettler.json` and `.fettler.local.json` say what Fettler may do. Fettler
        does not write them. A person edits them.
        """;

    /// <summary>The whole file for a level, with LF line endings.</summary>
    public static string Compose(string level)
    {
        bool guardrails = level == Levels.GuardrailsName;

        var text = new StringBuilder();
        text.Append("---\n");
        text.Append("description: Work on files through Fettler. ");
        text.Append(guardrails
            ? "This agent has no built-in file tools and no terminal.\n"
            : "This agent has no built-in file tools.\n");
        text.Append("tools: [")
            .Append(string.Join(", ", ToolsAt(level).Select(t => $"'{t}'")))
            .Append("]\n");
        text.Append("---\n");
        text.Append(Scaffold.ToolsParagraph);
        if (guardrails) text.Append("\n\n").Append(GuardrailsParagraph);
        text.Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// The <c>tools:</c> list of an agent file, or null when the file has
    /// none, which gives the agent every tool.
    /// </summary>
    public static IReadOnlyList<string>? Tools(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return null;

        int end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0) return null;

        for (int i = 1; i < end; i++)
        {
            if (!lines[i].StartsWith("tools:", StringComparison.Ordinal)) continue;

            string rest = lines[i]["tools:".Length..].Trim();
            var found = new List<string>();

            if (rest.StartsWith('['))
            {
                int close = rest.IndexOf(']');
                string inner = close < 0 ? rest[1..] : rest[1..close];
                foreach (string item in inner.Split(','))
                    if (Unquote(item) is { Length: > 0 } word) found.Add(word);
                return found;
            }

            for (int j = i + 1; j < end; j++)
            {
                string item = lines[j].Trim();
                if (!item.StartsWith('-')) break;
                if (Unquote(item[1..]) is { Length: > 0 } word) found.Add(word);
            }

            return found;
        }

        return null;
    }

    static string Unquote(string item)
    {
        string word = item.Trim();
        if (word.Length >= 2 && (word[0] == '\'' || word[0] == '"') && word[^1] == word[0])
            word = word[1..^1];
        return word.Trim();
    }
}
