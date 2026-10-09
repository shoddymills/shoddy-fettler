using System.Text;
using System.Text.RegularExpressions;

namespace Picker.Core;

/// <summary>
/// The concerns a disclosure may be screened for, as a closed set.
///
/// <para><b>This is Fettler's Screened, carried as a twin.</b> No
/// assembly is shared between the programs, so this file is a copy on
/// purpose, and a verifier asserts the two have not drifted apart -
/// twins proven equivalent by test, not by sharing. The categories are
/// named after WHAT ACTUALLY RUNS - the pattern tier by what it matches,
/// the model tiers by the model that judges them - never after a
/// regulation, which would promise coverage that only arrives once a
/// model is installed.</para>
/// </summary>
[Flags]
public enum Screened
{
    /// <summary>Nothing is screened, which is what a database that says
    /// nothing gets.</summary>
    None = 0,

    /// <summary>Structured identifiers the first tier matches with no
    /// model installed: an SSN, a Luhn-valid card number, a phone
    /// number, an email address, and a labelled record number or date
    /// of birth. This is the whole tier-one surface, and the only
    /// category that works with nothing installed.</summary>
    Identifiers = 1 << 0,

    /// <summary>Clinical text, judged by a clinical de-identification
    /// model you install - and by nothing until you do.</summary>
    Clinical = 1 << 1,

    /// <summary>Contract and consumer-report content, judged by a
    /// contract-clause model you install - and by nothing until you
    /// do.</summary>
    Legal = 1 << 2,

    /// <summary>Scientific text, judged by a scientific named-entity
    /// model you install - and by nothing until you do.</summary>
    Scientific = 1 << 3,
}

/// <summary>One regulated entity a disclosure would have carried.</summary>
public sealed record ScreenFinding(Screened Category, string Detector)
{
    /// <summary>
    /// The text that matched. INTERNAL, for the reason Fettler's twin
    /// states: a refusal naming what it found would write that
    /// identifier into a log, a transcript and whatever ships those
    /// onward, which is the exact harm the screen exists to prevent,
    /// delivered by the screen itself. <see cref="Screen.Describe"/> is
    /// the only thing that turns findings into a message, and it emits
    /// a category and a count.
    /// </summary>
    internal string Match { get; init; } = "";

    /// <summary>Where it sat in the payload. Internal for the same
    /// reason the text is: an offset plus the payload is the text. The
    /// sidecar reports spans and they stop here.</summary>
    internal int Offset { get; init; }
}

/// <summary>
/// Reading and writing the set of screened categories, and the grammar a
/// configuration writes it in. A twin of Fettler's Screens, kept in step
/// by a verifier.
/// </summary>
public static class Screens
{
    /// <summary>Every category, in the order they are written and read,
    /// so a listing never depends on enum ordering by accident.</summary>
    public static readonly Screened[] All =
    [
        Screened.Identifiers, Screened.Clinical, Screened.Legal, Screened.Scientific,
    ];

    /// <summary>What <c>"screen": true</c> means. Switching the screen on
    /// without naming categories screens all of them: excluding one takes
    /// a single word, so the failure mode of forgetting to name a
    /// category simply does not exist.</summary>
    public const Screened Everything =
        Screened.Identifiers | Screened.Clinical | Screened.Legal | Screened.Scientific;

    /// <summary>The categories a model judges.
    /// <see cref="Screened.Identifiers"/> is deliberately absent: it is
    /// the tier that runs in process with no model, so it never crosses
    /// the sidecar's pipe and never asks burler for a model it could
    /// not have.</summary>
    public const Screened ModelBacked =
        Screened.Clinical | Screened.Legal | Screened.Scientific;

    /// <summary>The words Fettler v2.5.0 shipped and later renamed, each
    /// mapped to what to write instead. Refused with directions rather
    /// than silently translated, so the old words cannot live on in
    /// configurations, still promising what they always over-promised.</summary>
    static readonly IReadOnlyDictionary<string, string> Renamed =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["phi"] = "the labelled identifier patterns are 'identifiers', and "
                + "clinical-model screening is 'clinical'",
            ["pii"] = "the identifier patterns are 'identifiers'",
            ["sci"] = "it is 'scientific'",
        };

    public static string NameOf(Screened one) => one switch
    {
        Screened.Identifiers => "identifiers",
        Screened.Clinical => "clinical",
        Screened.Legal => "legal",
        Screened.Scientific => "scientific",
        _ => "nothing",
    };

    /// <summary>A screened set as words, in the fixed order above. An
    /// empty set reads as <c>nothing</c> rather than as an empty string,
    /// so a report never has a blank where an answer goes.</summary>
    public static string Write(Screened screen)
    {
        var names = new List<string>();
        foreach (Screened one in All)
            if (screen.HasFlag(one)) names.Add(NameOf(one));
        return names.Count == 0 ? "nothing" : string.Join(' ', names);
    }

    /// <summary>
    /// The list forms: a bare list INCLUDES, and a list of <c>-</c>
    /// words EXCLUDES from the full set.
    ///
    /// <para><b>Mixing the two is refused rather than resolved.</b>
    /// <c>["identifiers", "-scientific"]</c> can be read as "identifiers
    /// only" or as "everything but scientific", and those differ by two
    /// whole categories. A rule picking one would silently withhold a
    /// screen from somebody who believed they had asked for it.</para>
    ///
    /// <para><b>An unknown word is refused rather than ignored</b>: a
    /// misspelt <c>"lgal"</c> quietly SUBTRACTS a screen, so the failure
    /// arrives much later as data that left a database everybody
    /// believed was covered.</para>
    /// </summary>
    public static Result<Screened> Parse(IReadOnlyList<string> words)
    {
        bool anyBare = false, anyMinus = false;

        foreach (string raw in words)
        {
            if (raw.StartsWith('-')) anyMinus = true;
            else anyBare = true;
        }

        if (anyBare && anyMinus)
            return Result<Screened>.Fail(Outcome.Invalid,
                "mixes included and excluded categories in one list. Write "
                + "[\"identifiers\", \"clinical\"] to screen only those, or "
                + "[\"-scientific\"] to screen everything except that");

        Screened chosen = anyMinus ? Everything : Screened.None;

        foreach (string raw in words)
        {
            bool excluding = raw.StartsWith('-');
            string word = excluding ? raw[1..] : raw;

            Screened? known = null;
            foreach (Screened one in All)
                if (word.Equals(NameOf(one), StringComparison.OrdinalIgnoreCase)) known = one;

            if (known is null && Renamed.TryGetValue(word, out string? became))
                return Result<Screened>.Fail(Outcome.Invalid,
                    $"'{word}' is no longer a screening category; {became}");

            if (known is null)
                return Result<Screened>.Fail(Outcome.Invalid,
                    $"'{raw}' is not a screening category; they are: "
                    + string.Join(", ", All.Select(NameOf)));

            if (excluding) chosen &= ~known.Value;
            else chosen |= known.Value;
        }

        return Result<Screened>.Ok(chosen);
    }
}

/// <summary>
/// Refuses a DISCLOSURE that would carry regulated personal data out of
/// a declared database.
///
/// <para><b>It judges the PAYLOAD, not the table.</b> A table may hold
/// clinical notes in row 40,000; a SELECT that discloses none of them is
/// served. Judging the whole table would lock the very databases people
/// most need help in, and the way round it would be to turn the screen
/// off - the same argument that makes Fettler's screen judge the payload
/// and its Secrets judge the diff.</para>
///
/// <para><b>Any detection is a deny, and there is no threshold.</b> One
/// entity refuses the whole response, and no rows are trimmed: a result
/// set with the offending rows quietly removed is a fabricated answer
/// wearing a clean verdict.</para>
///
/// <para><b>The detectors are a twin of Fettler's</b>, byte for byte
/// where it matters - the patterns - and a verifier asserts the two
/// programs' pattern sets have not drifted.</para>
///
/// <para><b>This is a safety net and NOT a boundary, and the difference
/// is not a quibble.</b> A named-entity model misses entities; these
/// patterns miss anything written a way they do not expect. A clean
/// verdict is evidence of absence and never a certificate of it. The
/// boundary is the column scope, the grant, and the least-privilege
/// login; the screen is the net under them, for what nobody predicted -
/// an identifier pasted into a notes column, an email in a free-text
/// field.</para>
/// </summary>
public static class Screen
{
    /// <summary>Every pattern runs under a bound, like every other
    /// regular expression this tool runs.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    sealed record Detector(string Name, Screened Category, Regex Pattern, bool Luhn = false);

    /// <summary>
    /// Tier one: identifiers whose shape whoever issues them fixed, and
    /// identifiers that arrive under their own label. A TWIN of
    /// Fettler's detectors - the patterns must not drift, and the
    /// verifier holds them equal.
    /// </summary>
    static readonly Detector[] Structural =
    [
        // The Social Security Administration's own invalid ranges are
        // excluded: no area 000, 666 or 900-999, no group 00, no serial
        // 0000. That is what separates a real number from a nine-digit
        // string with hyphens in it, and it costs nothing to check.
        new("ssn", Screened.Identifiers,
            New(@"\b(?!000|666|9\d\d)\d{3}-(?!00)\d{2}-(?!0000)\d{4}\b")),

        // 13 to 19 digits in the groupings cards are actually written in,
        // then Luhn. Bare runs of digits are NOT matched: a column of
        // order numbers would otherwise deny every page of the table,
        // and roughly one in ten random digit strings passes Luhn.
        new("credit-card", Screened.Identifiers,
            New(@"\b(?:\d{4}[ -]){3}\d{1,4}\b|\b\d{4}[ -]\d{6}[ -]\d{4,5}\b"), Luhn: true),

        // A leading + or a separated grouping. Ten bare digits are not a
        // phone number as far as this is concerned, for the same reason
        // the card pattern refuses them.
        new("phone", Screened.Identifiers,
            New(@"\+\d{1,3}[ .-]?\(?\d{1,4}\)?[ .-]?\d{2,4}[ .-]?\d{2,4}(?:[ .-]?\d{2,4})?"
                + @"|\(\d{3}\)\s?\d{3}[ .-]\d{4}\b|\b\d{3}[.-]\d{3}[.-]\d{4}\b")),

        new("email", Screened.Identifiers,
            New(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?"
                + @"(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,}\b")),

        // Labelled, because a medical record number has no fixed shape -
        // every issuer invents one - so the only honest detector is the
        // label that introduces it.
        new("medical-record-number", Screened.Identifiers,
            New(@"(?i)\b(?:mrn|m\.r\.n\.|medical\s+record\s+(?:number|no\.?|#)"
                + @"|patient\s+(?:id|identifier|number|no\.?|#))\b\s*[:=#-]?\s*[A-Za-z0-9][A-Za-z0-9-]{3,}")),

        new("date-of-birth", Screened.Identifiers,
            New(@"(?i)\b(?:dob|d\.o\.b\.?|date\s+of\s+birth|birth\s?date|born(?:\s+on)?)\b"
                + @"\s*[:=-]?\s*(?:\d{1,4}[/-]\d{1,2}[/-]\d{1,4}"
                + @"|(?:\d{1,2}\s+)?(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*"
                + @"\.?\s+\d{1,2}(?:st|nd|rd|th)?,?\s+\d{2,4})")),
    ];

    static Regex New(string pattern) => new(pattern, RegexOptions.None, Bound);

    /// <summary>
    /// Every regulated entity tier one can find in a payload, restricted
    /// to the categories actually being screened - restricted rather
    /// than filtered afterwards, because a category the configuration
    /// deliberately excluded should cost nothing.
    /// </summary>
    public static IReadOnlyList<ScreenFinding> Scan(string text, Screened screened) =>
        Scan(text, screened, Structural);

    /// <summary>
    /// The same scan with a different ceiling on each pattern. Public so
    /// the timeout branch of <see cref="Disclosure.Check"/> can be
    /// proven: the detectors above are deliberately free of the nested
    /// quantifiers that make a pattern blow up, so no ordinary input
    /// reaches the ordinary ceiling - which would leave the handling for
    /// that case both untested and untestable.
    /// </summary>
    public static IReadOnlyList<ScreenFinding> Scan(string text, Screened screened, TimeSpan bound)
    {
        var under = new Detector[Structural.Length];
        for (int i = 0; i < Structural.Length; i++)
            under[i] = Structural[i] with
            {
                Pattern = new Regex(
                    Structural[i].Pattern.ToString(), Structural[i].Pattern.Options, bound),
            };

        return Scan(text, screened, under);
    }

    static IReadOnlyList<ScreenFinding> Scan(
        string text, Screened screened, IReadOnlyList<Detector> detectors)
    {
        var found = new List<ScreenFinding>();
        if (string.IsNullOrEmpty(text) || screened == Screened.None) return found;

        // One entity, one finding: an identifier written inside a longer
        // labelled string can match two detectors, and reporting it twice
        // would inflate the count a refusal quotes.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Detector detector in detectors)
        {
            if (!screened.HasFlag(detector.Category)) continue;

            foreach (Match m in detector.Pattern.Matches(text))
            {
                if (detector.Luhn && !PassesLuhn(m.Value)) continue;
                if (!seen.Add(m.Value)) continue;

                found.Add(new ScreenFinding(detector.Category, detector.Name)
                {
                    Match = m.Value,
                    Offset = m.Index,
                });
            }
        }

        return found;
    }

    /// <summary>
    /// The refusal's wording: categories and counts, never the entity
    /// and never a fragment of one. A refusal that quoted the identifier
    /// it found would put it in the log and the transcript - the exact
    /// disclosure being refused, made by the refusal.
    /// </summary>
    public static string Describe(IReadOnlyList<ScreenFinding> findings)
    {
        var counts = new Dictionary<Screened, int>();
        foreach (ScreenFinding f in findings)
            counts[f.Category] = counts.GetValueOrDefault(f.Category) + 1;

        var parts = new StringBuilder();
        foreach (Screened one in Screens.All)
        {
            if (!counts.TryGetValue(one, out int n)) continue;
            if (parts.Length > 0) parts.Append(", ");
            parts.Append(n).Append(" in ").Append(Screens.NameOf(one));
        }

        return $"this response would disclose regulated data: {parts}. "
            + "The matches are not quoted, because the refusal would then disclose them. "
            + "Select fewer columns or rows, or take the screen off this scope if the "
            + "content is not regulated.";
    }

    /// <summary>
    /// The Luhn check digit, over a string that may carry spaces and
    /// hyphens. It is what makes the card detector worth having: without
    /// it the pattern is "sixteen digits in groups of four", which every
    /// table full of reference numbers matches.
    /// </summary>
    static bool PassesLuhn(string candidate)
    {
        int sum = 0, digits = 0;
        bool alternate = false;

        for (int i = candidate.Length - 1; i >= 0; i--)
        {
            char c = candidate[i];
            if (c is ' ' or '-') continue;
            if (!char.IsAsciiDigit(c)) return false;

            int d = c - '0';
            digits++;

            if (alternate)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }

            sum += d;
            alternate = !alternate;
        }

        return digits is >= 13 and <= 19 && sum % 10 == 0;
    }
}
