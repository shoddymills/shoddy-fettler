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
/// <item><b>Braces are alternation, one level deep.</b>
/// <c>**/*.{cs,md}</c> matches what <c>**/*.cs</c> or <c>**/*.md</c>
/// matches, and each alternative is an ordinary glob, so
/// <c>{src/**,tests/*.cs}</c> works. Assistants write braces by habit:
/// refusing them, as this once did, cost a refused call and a retry in
/// thirteen of seventeen sessions surveyed.</item>
/// <item><b>Brackets, a leading <c>!</c>, a brace inside a brace and a
/// brace left open or closed alone are refused.</b> Other dialects give
/// each of those a meaning, and here they would mean the characters
/// themselves, so a pattern using one would match nothing and say
/// nothing. An unknown flag is refused for the same reason: ignoring it
/// produces a wrong answer, not a failure.</item>
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

        if (Unsupported(pattern) is { } refused) return Result<Glob>.Fail(refused);

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

    /// <summary>
    /// The refusal for a glob form other dialects have and this grammar
    /// does not, or null when the pattern uses none.
    ///
    /// <para>Its own entry point because <c>--exclude</c> needs it too:
    /// an exclude that fails to compile for any other reason is dropped,
    /// and dropping one of these would search the files the caller asked
    /// to leave out.</para>
    /// </summary>
    public static Failure? Unsupported(string pattern)
    {
        string? form = pattern.StartsWith('!') ? "a leading '!'" : null;
        int depth = 0;

        if (form is null)
            foreach (char c in pattern)
            {
                if (c is '[' or ']') { form = $"'{c}'"; break; }
                if (c == '{')
                {
                    if (depth > 0) { form = "a '{' inside braces"; break; }
                    depth++;
                }
                else if (c == '}')
                {
                    if (depth == 0) { form = "a '}' with no '{' before it"; break; }
                    depth--;
                }
            }

        if (form is null && depth > 0) form = "a '{' that is never closed";
        if (form is null) return null;

        return new Failure(Outcome.Invalid,
            $"the glob {pattern} uses {form}, which fettle globs do not support. "
            + "A glob has * within a segment, ** across segments, ? for one character and "
            + "{a,b} for either of two, one level deep; every other character is literal. "
            + "For a bracket, put ? or * in its place");
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
                if (p[i] is '*' or '?' or '{') break;
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
    /// Braces become a group of alternatives; <see cref="Unsupported"/>
    /// has already refused any that are nested or unbalanced, and a comma
    /// outside braces is an ordinary character.
    /// </summary>
    static string Translate(string pattern)
    {
        string p = Normalize(pattern.Replace('\\', '/'));
        var sb = new StringBuilder("^");
        bool braced = false;

        for (int i = 0; i < p.Length;)
        {
            char c = p[i];

            if (c == '{') { sb.Append("(?:"); braced = true; i++; continue; }
            if (c == '}' && braced) { sb.Append(')'); braced = false; i++; continue; }
            if (c == ',' && braced) { sb.Append('|'); i++; continue; }

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
