using System.Text;
using System.Text.RegularExpressions;

namespace Fettler.Core;

/// <summary>
/// The concerns a disclosure may be screened for, as a closed set.
///
/// <para><b>Closed for the same reason <see cref="Permission"/> is.</b> A
/// set that grows a member per detector is one nobody can hold in mind,
/// and one where a configuration written today means something different
/// tomorrow. These four are named after WHAT ACTUALLY RUNS - the
/// pattern tier by what it matches, the model tiers by the model that
/// judges them. The words that stood here first, 'phi' and 'pii', named
/// the regulation instead, and so promised coverage that only arrives
/// once a model is installed while reading as if it were there from the
/// start.</para>
///
/// <para><b>FCRA and FERPA are deliberately not members.</b> Consumer
/// report and education record data is largely structured identifiers,
/// which <see cref="Screen"/>'s first tier catches under
/// <see cref="Identifiers"/>. Giving them a category of their own would promise
/// a coverage nothing here delivers - see the honesty clause on
/// <see cref="Screen"/>.</para>
/// </summary>
[Flags]
public enum Screened
{
    /// <summary>Nothing is screened, which is what a tree that says
    /// nothing gets.</summary>
    None = 0,

    /// <summary>Structured identifiers the first tier matches with no
    /// model installed: an SSN, a Luhn-valid card number, a phone
    /// number, an email address, and a labelled record number or date
    /// of birth. This is the whole tier-one surface, and the only
    /// category that works with nothing installed.</summary>
    Identifiers = 1 << 0,

    /// <summary>Clinical text, judged by a clinical de-identification
    /// model you install - and by nothing until you do. The manifest
    /// beside the model names the exact checkpoint doing the judging,
    /// and <c>roots</c> reports it.</summary>
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
    /// The text that matched. INTERNAL, and for a sharper reason than
    /// <see cref="SecretFinding.Match"/>: a refusal naming what it found
    /// would write that identifier into a log, a transcript and whatever
    /// ships those onward, which is the exact harm the screen exists to
    /// prevent, delivered by the screen itself.
    /// <see cref="Screen.Describe"/> is the only thing that turns
    /// findings into a message, and it emits a category and a count.
    /// </summary>
    internal string Match { get; init; } = "";

    /// <summary>Where it sat in the payload. Internal for the same reason
    /// the text is: an offset plus the payload is the text. The sidecar
    /// reports spans and they stop here.</summary>
    internal int Offset { get; init; }
}

/// <summary>
/// Reading and writing the set of screened categories, and the grammar a
/// configuration writes it in.
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

    /// <summary>The words v2.5.0 shipped and this version renamed, each
    /// mapped to what to write instead. Refused with directions rather
    /// than silently translated: a translation would keep the old words
    /// alive in configurations indefinitely, still promising what they
    /// always over-promised.</summary>
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
    /// The list forms of R1.4: a bare list INCLUDES, and a list of
    /// <c>-</c> words EXCLUDES from the full set.
    ///
    /// <para><b>Mixing the two is refused rather than resolved.</b>
    /// <c>["identifiers", "-scientific"]</c> can be read as "identifiers
    /// only" or as "everything but scientific, and identifiers is
    /// redundant", and those differ by two whole categories. A rule
    /// picking one would silently withhold a screen from somebody who
    /// believed they had asked for it.</para>
    ///
    /// <para><b>An unknown word is refused rather than ignored</b>, on
    /// the <see cref="Permissions.Parse"/> precedent and for the sharper
    /// version of its reason: a misspelt <c>"lgal"</c> quietly SUBTRACTS
    /// a screen, so the failure arrives much later as data that left a
    /// tree everybody believed was covered.</para>
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
                "mixes included and excluded categories in one list, and the two cannot be "
                + "combined: write [\"identifiers\", \"clinical\"] to screen only those, or "
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
/// a tree.
///
/// <para><b>It is <see cref="Secrets"/> pointed the other way.</b> That
/// one judges a write going in and refuses an introduced credential;
/// this one judges a payload coming out and refuses a detected entity.
/// The shapes are deliberately the same, down to the two tiers and the
/// rule that a refusal never repeats what it found.</para>
///
/// <para><b>It judges the PAYLOAD, not the file.</b> A file may carry a
/// record number on page 40; a read of page 2 that discloses none is
/// served. Judging
/// the whole file would lock the very trees people most need help in,
/// and the way round it would be to turn the screen off - which is the
/// same argument that makes <see cref="Secrets"/> judge the diff.</para>
///
/// <para><b>Any detection is a deny, and there is no threshold.</b> One
/// entity refuses the whole response. A confidence dial is a dial that
/// gets turned until the check is off, which is the reasoning that kept
/// <see cref="Secrets"/>' second tier short.</para>
///
/// <para><b>Two tiers, for two different confidences.</b> The structural
/// detectors here match identifiers whose shape their issuer fixed - a
/// social security number, a card that passes Luhn, a record number
/// under its own label - and they run in process with no model, no
/// sidecar and no new dependency. The second tier is the model host,
/// which lives in another executable entirely because BERT inference
/// needs native binaries that Fettler's package allowlist forbids.</para>
///
/// <para><b>This is a safety net and NOT a boundary, and the difference
/// is not a quibble.</b> A named-entity model misses entities; these
/// patterns miss anything written a way they do not expect. A clean
/// verdict is evidence of absence and never a certificate of it, and a
/// caller who believes a screened tree cannot leak has been misled. It
/// exists to stop the ordinary accident of a regulated document being
/// read into a transcript, not to withstand anybody trying.</para>
/// </summary>
public static class Screen
{
    /// <summary>Every pattern runs under a bound, like every other
    /// regular expression this tool runs.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    sealed record Detector(string Name, Screened Category, Regex Pattern, bool Luhn = false);

    /// <summary>
    /// Tier one: identifiers whose shape whoever issues them fixed, and
    /// identifiers that arrive under their own label.
    ///
    /// <para><b>The labelled ones are labelled on purpose.</b> A medical
    /// record number has no fixed shape - every issuer invents one - so
    /// the only honest detector is the label that introduces it. Matching
    /// bare identifier-shaped strings would refuse every part number in
    /// every spreadsheet, and a check that cries wolf is a check somebody
    /// switches off.</para>
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
        // then Luhn. Bare runs of digits are NOT matched: a spreadsheet
        // column of order numbers would otherwise deny every read of the
        // sheet, and roughly one in ten random digit strings passes Luhn.
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

        // Labelled, as argued above. The identifier itself may be
        // anything an issuer chose, so the label is what is trusted.
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
    /// to the categories actually being screened.
    ///
    /// <para>Restricted rather than filtered afterwards, because a
    /// category that is not screened is one the configuration
    /// deliberately excluded - running its detectors and discarding the
    /// answer would burn the time and tempt somebody to report it.</para>
    /// </summary>
    public static IReadOnlyList<ScreenFinding> Scan(string text, Screened screened) =>
        Scan(text, screened, Structural);

    /// <summary>
    /// The same scan with a different ceiling on each pattern.
    ///
    /// <para><b>Public so that the timeout branch of
    /// <see cref="Disclosure.Check"/> can be proven</b>, on the
    /// <see cref="Sidecar.Read"/> precedent. The detectors above are
    /// deliberately free of the nested quantifiers that make a pattern
    /// blow up, so no ordinary input reaches the ordinary ceiling - which
    /// would leave the handling for that case both untested and
    /// untestable. A ceiling of one tick reaches it on any input at all,
    /// and a detector added later that CAN blow up finds the handling
    /// already there and already proven.</para>
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

        // One entity, one finding: a phone number written inside a longer
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
    /// The refusal's wording: categories and counts, never the entity and
    /// never a fragment of one.
    ///
    /// <para><see cref="Secrets.Describe"/> is the precedent, and here
    /// the reason is stronger. A refusal that quoted the identifier it
    /// found would put it in the log and the transcript - the exact
    /// disclosure being refused, made by the refusal.</para>
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
            + "What was found is deliberately not quoted back, because a refusal that "
            + "named it would put it in the log and the transcript, which is the disclosure "
            + "being refused. Read a narrower range, or take the screen off this scope if "
            + "the content is not what it looks like.";
    }

    /// <summary>
    /// The Luhn check digit, over a string that may carry spaces and
    /// hyphens.
    ///
    /// <para>It is what makes the card detector worth having: without it
    /// the pattern is "sixteen digits in groups of four", which every
    /// document full of reference numbers matches.</para>
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
