using System.Text;
using System.Text.RegularExpressions;

namespace Fettler.Core;

/// <summary>
/// A path pattern, with the semantics of R4.7 stated rather than
/// inherited from whatever the platform happened to do.
///
/// <list type="bullet">
/// <item><c>*</c> matches any run of characters <b>within one path
/// segment</b>. It does not cross a separator.</item>
/// <item><c>**</c> matches any number of whole segments, including
/// none, so <c>src/**/*.cs</c> matches <c>src/A.cs</c> as well as
/// <c>src/deep/B.cs</c>.</item>
/// <item><c>?</c> matches exactly one character, and likewise does not
/// cross a separator.</item>
/// <item><b>Matching is case-insensitive on every platform.</b> This is
/// the choice R4.7 demands be made rather than deferred: both required
/// platforms have case-insensitive filesystems, so deferring would give
/// case-insensitive matching on Windows and macOS anyway - and fixing it
/// here means a pattern behaves identically everywhere instead of
/// quietly differing on the one platform R9.1 does not require.</item>
/// <item>Both <c>/</c> and <c>\</c> are accepted in a pattern and in a
/// path, and results are written with <c>/</c> (R9.2).</item>
/// </list>
///
/// <para>Names are compared in composed Unicode form, so a pattern typed
/// in one normalization matches a filename macOS stored in the other
/// (R9.2). Without it, an accented filename created on Windows is
/// invisible to its own pattern on macOS.</para>
/// </summary>
public sealed class Glob
{
    readonly Regex regex;

    /// <summary>The pattern as given, for reporting it back.</summary>
    public string Pattern { get; }

    Glob(string pattern, Regex regex)
    {
        Pattern = pattern;
        this.regex = regex;
    }

    public static Result<Glob> Compile(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return Result<Glob>.Fail(Outcome.Invalid, "an empty pattern matches nothing; use ** for everything");

        string translated = Translate(pattern);
        try
        {
            var regex = new Regex(translated,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            return Result<Glob>.Ok(new Glob(pattern, regex));
        }
        catch (ArgumentException e)
        {
            return Result<Glob>.Fail(Outcome.Invalid, $"the pattern will not compile: {e.Message}");
        }
    }

    public bool Matches(string relativePath) =>
        regex.IsMatch(Normalize(relativePath.Replace('\\', '/')));

    /// <summary>
    /// The longest run of whole directory segments at the front of the
    /// pattern that contain no wildcard - so <c>src/main/**/*.cs</c>
    /// answers <c>src/main</c>, and <c>**/*.cs</c> answers nothing.
    ///
    /// <para>Defect 1.3: without this a search walks the entire tree and
    /// then discards nearly all of it. Rooted at a home directory that
    /// made a one-file search exceed two minutes. The prefix is a
    /// property of the pattern alone, so deriving it here keeps the walk
    /// and the match agreeing about what the pattern means.</para>
    ///
    /// <para><b>Only whole segments, and only before the first
    /// wildcard.</b> A partial segment like <c>src/ma*</c> yields
    /// <c>src</c> and not <c>src/ma</c>, because the prefix is used to
    /// pick a directory to start from and half a directory name is not
    /// one.</para>
    /// </summary>
    public string FixedPrefix
    {
        get
        {
            string p = Pattern.Replace('\\', '/');
            int lastSeparator = -1;

            for (int i = 0; i < p.Length; i++)
            {
                if (p[i] is '*' or '?') break;
                if (p[i] == '/') lastSeparator = i;
            }

            return lastSeparator <= 0 ? string.Empty : p[..lastSeparator];
        }
    }

    static string Normalize(string s) =>
        s.IsNormalized(NormalizationForm.FormC) ? s : s.Normalize(NormalizationForm.FormC);

    /// <summary>
    /// Glob to regular expression, one construct at a time. Everything
    /// that is not a wildcard is escaped, so a pattern carrying a dot or
    /// a bracket means that character and not a regular-expression class.
    /// </summary>
    static string Translate(string pattern)
    {
        string p = Normalize(pattern.Replace('\\', '/'));
        var sb = new StringBuilder("^");

        for (int i = 0; i < p.Length;)
        {
            char c = p[i];

            if (c == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                i += 2;
                if (i < p.Length && p[i] == '/')
                {
                    // '**/' - any number of leading segments, including
                    // none, which is what makes src/**/*.cs match a file
                    // sitting directly in src.
                    i++;
                    sb.Append("(?:[^/]+/)*");
                }
                else
                {
                    sb.Append(".*");
                }
                continue;
            }

            switch (c)
            {
                case '*': sb.Append("[^/]*"); break;
                case '?': sb.Append("[^/]"); break;
                case '/': sb.Append('/'); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }

            i++;
        }

        return sb.Append('$').ToString();
    }
}
