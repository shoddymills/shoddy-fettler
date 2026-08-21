using System.Text;
using System.Text.RegularExpressions;

namespace Fettler.Core;

/// <summary>One credential a write would have introduced.</summary>
public sealed record SecretFinding(int Line, string Detector)
{
    /// <summary>
    /// The text that matched. INTERNAL, and it must stay that way: a
    /// refusal naming the secret writes it into a log, a transcript and
    /// whatever ships those onward, which is a worse outcome than the
    /// write it was refusing. <see cref="Secrets.Describe"/> is the only
    /// thing that turns findings into a message, and it emits a line
    /// number and a detector name and nothing else.
    /// </summary>
    internal string Match { get; init; } = "";
}

/// <summary>
/// Refuses a write that INTRODUCES a credential.
///
/// <para><b>It judges the diff, not the file.</b> A config that already
/// holds a key stays editable - otherwise the rule would lock the very
/// files people most need help with, and the way round it would be to
/// turn the rule off. Only a secret absent from the previous content
/// stops a write, so reformatting, reordering and editing the lines
/// around one all still work.</para>
///
/// <para><b>Two tiers, for two different confidences.</b> The structural
/// patterns match issued credentials whose shape is fixed by whoever
/// issues them - an AWS key id, a GitHub token, a PEM private key header
/// - and those are near enough to zero false positives to refuse on
/// sight. The second tier is a guess: a secret-shaped NAME assigned a
/// secret-shaped VALUE, gated hard on length, entropy, and not being a
/// reference, so <c>password = ""</c> and <c>token: ${GITHUB_TOKEN}</c>
/// pass without an argument.</para>
///
/// <para><b>This is a safety net and NOT a boundary, and the difference
/// is not a quibble.</b> Base64 the value, split the string in two, or
/// build it from parts, and every rule here is defeated - by accident as
/// easily as on purpose. It exists to stop the ordinary accident of
/// pasting a real key into a config and writing it to disk. Anything
/// that reasons about a determined adversary is reasoning about the
/// wrong thing, and a caller who believes this scanned their content for
/// secrets has been misled.</para>
/// </summary>
public static class Secrets
{
    /// <summary>Every pattern runs under a bound, like every other
    /// regular expression this tool runs.</summary>
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    /// <summary>The shortest tier-two value worth suspecting. Real
    /// issued secrets are long; short assignments are overwhelmingly
    /// placeholders, flags and test fixtures.</summary>
    const int MinLength = 20;

    /// <summary>Shannon entropy per character, above which a value stops
    /// looking like words. English prose sits near 3.0-3.5; base64 and
    /// hex key material sit well above 4.</summary>
    const double MinEntropy = 3.8;

    sealed record Detector(string Name, Regex Pattern);

    /// <summary>
    /// Tier one: credentials whose shape their issuer fixed. Each of
    /// these is specific enough that a match is a credential rather than
    /// a resemblance, which is what earns them a refusal with no further
    /// test applied.
    /// </summary>
    static readonly Detector[] Structural =
    [
        new("aws-access-key-id", New(@"\bAKIA[0-9A-Z]{16}\b")),
        new("github-token", New(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b")),
        new("slack-token", New(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b")),
        new("stripe-live-key", New(@"\bsk_live_[A-Za-z0-9]{16,}\b")),
        new("google-api-key", New(@"\bAIza[0-9A-Za-z\-_]{35}\b")),
        new("private-key-header",
            New(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY-----")),
    ];

    /// <summary>
    /// Tier two: a secret-shaped name assigned a value. The name list is
    /// deliberately short - every entry is a word that means "this is a
    /// credential" in every configuration format there is, and adding
    /// vaguer ones buys coverage with false positives that teach people
    /// to switch the check off.
    /// </summary>
    static readonly Regex Assignment = New(
        @"(?i)\b(?<name>password|passwd|pwd|secret|api[-_]?key|apikey|"
        + @"access[-_]?key|auth[-_]?token|token)\b[""']?\s*[:=]\s*"
        + @"(?:""(?<val>[^""\r\n]*)""|'(?<val>[^'\r\n]*)'|(?<val>[^\s""',;)}\]]+))");

    /// <summary>
    /// Shapes that are a POINTER to a secret rather than one: an
    /// environment reference, a template hole, a shell substitution, an
    /// obvious placeholder. These are how a config is supposed to be
    /// written, so matching one is a reason to say nothing at all.
    /// </summary>
    static readonly Regex Reference = New(
        @"^(?:\$\{|\$\(|\{\{|%[A-Za-z_]|<|\$[A-Za-z_])"
        + @"|^(?i:x{3,}|changeme|placeholder|redacted|your[-_].*|example.*|"
        + @"test|none|null|true|false|secret|password)$");

    static Regex New(string pattern) => new(pattern, RegexOptions.None, Bound);

    /// <summary>Every credential in a piece of text.</summary>
    public static IReadOnlyList<SecretFinding> Scan(string text)
    {
        var found = new List<SecretFinding>();
        if (string.IsNullOrEmpty(text)) return found;

        // One secret, one finding. `token = ghp_...` matches the GitHub
        // pattern AND the assignment pattern, and reporting it twice
        // would make a refusal look like two problems and inflate the
        // count in the message. The structural pass runs first and is the
        // more specific answer, so it is the one that survives.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Detector detector in Structural)
            foreach (Match m in detector.Pattern.Matches(text))
            {
                if (!seen.Add(m.Value)) continue;
                found.Add(new SecretFinding(LineOf(text, m.Index), detector.Name)
                {
                    Match = m.Value,
                });
            }

        foreach (Match m in Assignment.Matches(text))
        {
            string value = m.Groups["val"].Value;
            if (!Suspicious(value)) continue;
            if (!seen.Add(value)) continue;
            found.Add(new SecretFinding(LineOf(text, m.Index),
                    "assigned-" + m.Groups["name"].Value.ToLowerInvariant())
            {
                Match = value,
            });
        }

        return found;
    }

    /// <summary>
    /// The credentials <paramref name="updated"/> would introduce that
    /// <paramref name="existing"/> did not already carry.
    ///
    /// <para>Compared by the matched text rather than by line, so moving
    /// a line, re-indenting it or editing its neighbours does not make an
    /// old secret look new. Pass null for a file that is being
    /// created.</para>
    /// </summary>
    public static IReadOnlyList<SecretFinding> Introduced(string? existing, string updated)
    {
        IReadOnlyList<SecretFinding> now = Scan(updated);
        if (now.Count == 0 || string.IsNullOrEmpty(existing)) return now;

        var already = new HashSet<string>(StringComparer.Ordinal);
        foreach (SecretFinding f in Scan(existing)) already.Add(f.Match);

        var fresh = new List<SecretFinding>();
        foreach (SecretFinding f in now)
            if (!already.Contains(f.Match)) fresh.Add(f);
        return fresh;
    }

    /// <summary>
    /// The refusal's wording. Line numbers and detector names only -
    /// never the match, and never a fragment of it.
    /// </summary>
    public static string Describe(IReadOnlyList<SecretFinding> findings)
    {
        var where = new StringBuilder();
        int shown = 0;
        foreach (SecretFinding f in findings)
        {
            if (shown == 3) { where.Append(", and more"); break; }
            if (shown > 0) where.Append(", ");
            where.Append($"line {f.Line} ({f.Detector})");
            shown++;
        }

        return $"this write would add a credential to the file: {where}. "
            + "The secret itself is deliberately not quoted back, because a "
            + "refusal that named it would put it in the log and the transcript. "
            + "Use a reference such as ${ENV_VAR} instead, or pass the override "
            + "if this is not a credential.";
    }

    /// <summary>A value worth refusing: long, high-entropy, no
    /// whitespace, and not a reference to a secret held elsewhere.</summary>
    static bool Suspicious(string value)
    {
        if (value.Length < MinLength) return false;
        if (Reference.IsMatch(value)) return false;

        // Real key material has no spaces in it. This single test removes
        // most of what tier two would otherwise get wrong - a sentence
        // assigned to something called `token` reads as prose and is.
        foreach (char c in value)
            if (char.IsWhiteSpace(c)) return false;

        // Some mixture of character classes: a long run of one class is a
        // hash of something public, a path, or an identifier far more
        // often than it is a credential.
        bool digit = false, letter = false;
        foreach (char c in value)
        {
            if (char.IsAsciiDigit(c)) digit = true;
            else if (char.IsAsciiLetter(c)) letter = true;
        }
        if (!digit || !letter) return false;

        return Entropy(value) >= MinEntropy;
    }

    static double Entropy(string s)
    {
        var counts = new Dictionary<char, int>();
        foreach (char c in s) counts[c] = counts.GetValueOrDefault(c) + 1;

        double bits = 0;
        foreach (int n in counts.Values)
        {
            double p = (double)n / s.Length;
            bits -= p * Math.Log2(p);
        }
        return bits;
    }

    static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }
}
