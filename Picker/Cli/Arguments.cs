namespace Picker.Cli;

/// <summary>
/// The argument vector, read once: a verb, its positionals, and flags.
///
/// <para><b>A flag no verb knows is refused before anything runs</b>,
/// the Fettler rule, because ignoring one does not produce a failure -
/// it produces a WRONG ANSWER: a flag taking a value eats the argument
/// after it, so a misspelt flag swallows a positional and the command
/// runs against something else with complete confidence.</para>
/// </summary>
public sealed class Arguments
{
    /// <summary>The flags that take a value; everything else written as
    /// <c>--flag</c> is a switch. Declared in one place so parsing never
    /// guesses from what happens to follow.</summary>
    static readonly HashSet<string> Valued = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "limit", "in",
    };

    readonly List<string> positionals = [];
    readonly Dictionary<string, string?> flags = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> missingValue = [];

    public string Verb { get; private set; } = "";

    public static Arguments Parse(IReadOnlyList<string> argv)
    {
        var args = new Arguments();

        for (int i = 0; i < argv.Count; i++)
        {
            string token = argv[i];

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                if (args.Verb.Length == 0) args.Verb = token;
                else args.positionals.Add(token);
                continue;
            }

            string name = token[2..];

            // --name=value spells a deliberately attached value.
            int eq = name.IndexOf('=');
            if (eq >= 0)
            {
                args.flags[name[..eq]] = name[(eq + 1)..];
                continue;
            }

            if (Valued.Contains(name))
            {
                if (i + 1 < argv.Count)
                {
                    args.flags[name] = argv[++i];
                }
                else
                {
                    args.flags[name] = null;
                    args.missingValue.Add("--" + name);
                }
                continue;
            }

            args.flags[name] = null;
        }

        return args;
    }

    public bool Has(string flag) => flags.ContainsKey(flag);

    public string? Value(string flag) =>
        flags.TryGetValue(flag, out string? value) ? value : null;

    public int Int(string flag, int fallback)
    {
        string? value = Value(flag);
        return value is not null && int.TryParse(value, out int parsed) ? parsed : fallback;
    }

    public string? At(int index) =>
        index < positionals.Count ? positionals[index] : null;

    public IReadOnlyList<string> Positionals => positionals;

    /// <summary>Flags declared valued that reached the end of the line
    /// with nothing to take.</summary>
    public IReadOnlyList<string> FlagsMissingAValue => missingValue;

    /// <summary>Everything the caller wrote that this verb does not
    /// take. The universal flags are always known.</summary>
    public IReadOnlyList<string> UnknownFlags(params string[] known)
    {
        var set = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase)
        {
            "json", "help", "version", "config",
        };

        var unknown = new List<string>();
        foreach (string flag in flags.Keys)
            if (!set.Contains(flag)) unknown.Add("--" + flag);
        return unknown;
    }
}
