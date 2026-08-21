using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// The disclosure screen: what leaves a screened scope, and what does
/// not.
///
/// <para><b>Every fixture here is synthetic, and that is a requirement
/// rather than a convenience (R7.2).</b> The invented numbers are shaped
/// like the real thing - the card even passes Luhn, because otherwise it
/// would prove nothing - but no real person's data enters this tree, for
/// the same reason a refusal never quotes what it found.</para>
///
/// <para><b>No model runs in any of this.</b> The first tier needs none,
/// and the second is reached through <see cref="IScreener"/>, which a
/// fake implements - so the fail-closed behaviour is proven without 400
/// MB of weights. A gate that could only be exercised by installing
/// those is a gate nobody would exercise.</para>
/// </summary>
public sealed class ScreenTests
{
    // Invented, and shaped like the real thing so the detectors are
    // actually exercised.
    const string Ssn = "123-45-6789";
    const string Card = "4111 1111 1111 1111";        // passes Luhn
    const string BadCard = "4111 1111 1111 1112";     // does not
    const string Email = "renee@example.com";
    const string Phone = "(555) 123-4567";

    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    static Result<IReadOnlyList<ReadSlice>> ReadOf(Sandbox box, string path, int? from = null, int? to = null) =>
        box.Bench.Read(new ReadRequest([path], from, to));

    /// <summary>A stand-in for the model host, so the gate can be driven
    /// into every state the real one can reach.</summary>
    sealed class Fake(Func<string, Screened, Result<IReadOnlyList<ScreenFinding>>> answer) : IScreener
    {
        public string? Saw { get; private set; }

        public Screened Asked { get; private set; }

        public Result<IReadOnlyList<ScreenFinding>> Inspect(string payload, Screened categories)
        {
            Saw = payload;
            Asked = categories;
            return answer(payload, categories);
        }

        public static Fake Clean() =>
            new((_, _) => Result<IReadOnlyList<ScreenFinding>>.Ok([]));

        public static Fake Finds(Screened category, int many) =>
            new((_, _) =>
            {
                var found = new List<ScreenFinding>();
                for (int i = 0; i < many; i++) found.Add(new ScreenFinding(category, "model"));
                return Result<IReadOnlyList<ScreenFinding>>.Ok(found);
            });

        public static Fake Fails(string why) =>
            new((_, _) => Result<IReadOnlyList<ScreenFinding>>.Fail(Outcome.Screened, why));
    }

    // ================= the category grammar (R1.4, AC7) =================

    [Fact]
    public void ABareListIncludesOnlyWhatItNames() =>
        Assert.Equal(Screened.Clinical | Screened.Legal, Screens.Parse(["clinical", "legal"]).Value);

    [Fact]
    public void AMinusListExcludesFromEverything()
    {
        Result<Screened> parsed = Screens.Parse(["-scientific"]);

        Assert.True(parsed.IsOk);
        Assert.Equal(Screens.Everything & ~Screened.Scientific, parsed.Value);
        Assert.True(parsed.Value.HasFlag(Screened.Identifiers));
        Assert.False(parsed.Value.HasFlag(Screened.Scientific));
    }

    /// <summary>
    /// AC7: mixing the two forms is refused, and the refusal names the
    /// ambiguity rather than picking one.
    ///
    /// <para><c>["identifiers", "-scientific"]</c> reads either as
    /// "identifiers only" or as "everything but scientific", and those
    /// differ by two whole categories. A rule choosing one would silently
    /// withhold a screen from somebody who believed they had asked for
    /// it.</para>
    /// </summary>
    [Fact]
    public void MixingIncludedAndExcludedIsRefused()
    {
        Result<Screened> parsed = Screens.Parse(["identifiers", "-scientific"]);

        Assert.False(parsed.IsOk);
        Assert.Contains("cannot be combined", parsed.Failure!.Message, StringComparison.Ordinal);
    }

    /// <summary>A misspelt category is refused rather than ignored: it
    /// would otherwise quietly SUBTRACT a screen, and the failure would
    /// arrive as data leaving a tree everybody believed was covered.</summary>
    [Fact]
    public void AnUnknownCategoryIsRefusedAndNamed()
    {
        Result<Screened> parsed = Screens.Parse(["lgal"]);

        Assert.False(parsed.IsOk);
        Assert.Contains("lgal", parsed.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("legal", parsed.Failure!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The words v2.5.0 shipped are refused with directions, not
    /// quietly translated to their nearest new equivalent.
    ///
    /// <para>Translating them would keep them alive in configurations
    /// indefinitely, still promising the coverage they always
    /// over-promised - and the refusal is the one moment anybody is
    /// going to read what the new words actually mean.</para>
    /// </summary>
    [Theory]
    [InlineData("phi", "identifiers")]
    [InlineData("pii", "identifiers")]
    [InlineData("sci", "scientific")]
    public void AWordThisVersionRenamedIsRefusedWithDirections(string was, string now)
    {
        Result<Screened> parsed = Screens.Parse([was]);

        Assert.False(parsed.IsOk, $"'{was}' was still accepted");
        Assert.Equal(Outcome.Invalid, parsed.Failure!.Outcome);
        Assert.Contains(was, parsed.Failure.Message, StringComparison.Ordinal);
        Assert.Contains("no longer a screening category", parsed.Failure.Message,
            StringComparison.Ordinal);
        Assert.Contains(now, parsed.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreensAreWrittenBackInAFixedOrder()
    {
        Assert.Equal("identifiers clinical legal scientific", Screens.Write(Screens.Everything));
        Assert.Equal("nothing", Screens.Write(Screened.None));
    }

    // ---- read out of a configuration file ----

    static string Configured(string json)
    {
        string dir = Directory.CreateTempSubdirectory("fettle-screen-cfg-").FullName;
        File.WriteAllText(Path.Combine(dir, RootsFile.FileName), json);
        return Path.Combine(dir, RootsFile.FileName);
    }

    [Fact]
    public void ScreenTrueMeansEveryCategory()
    {
        Result<RootsFile.Found> found = RootsFile.Read(Configured(
            """{"trees":{"work":{"path":".","screen":true}}}"""));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(Screens.Everything, found.Value.Trees[0].Screen);
    }

    [Fact]
    public void ATreeThatSaysNothingScreensNothing()
    {
        Result<RootsFile.Found> found = RootsFile.Read(Configured(
            """{"trees":{"work":{"path":"."}}}"""));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(Screened.None, found.Value.Trees[0].Screen);
    }

    [Fact]
    public void AMixedListIsRefusedWhenTheConfigurationIsREAD()
    {
        Result<RootsFile.Found> found = RootsFile.Read(Configured(
            """{"trees":{"work":{"path":".","screen":["identifiers","-scientific"]}}}"""));

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
        Assert.Contains("cannot be combined", found.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AScreenThatIsNotTrueFalseOrAListIsRefused()
    {
        Result<RootsFile.Found> found = RootsFile.Read(Configured(
            """{"trees":{"work":{"path":".","screen":"identifiers"}}}"""));

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
    }

    /// <summary>A scope's screen REPLACES the tree's, which is what lets
    /// a scope take screening away as well as tighten it.</summary>
    [Fact]
    public void AScopeReplacesTheTreesScreen()
    {
        Result<RootsFile.Found> found = RootsFile.Read(Configured(
            """
            {"trees":{"work":{"path":".","screen":true,
              "scopes":{"public":{"can":["list","read"],"screen":false}}}}}
            """));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(Screened.None, found.Value.Trees[0].Scopes![0].Screen);
    }

    /// <summary>
    /// A scope that says nothing about screening INHERITS the tree's.
    ///
    /// <para>Reading silence as "screen nothing here" would punch a hole
    /// in the first tree anybody switched the screen on for, because
    /// every scope written before the feature existed says nothing.</para>
    /// </summary>
    [Fact]
    public void AScopeThatSaysNothingInheritsTheTrees()
    {
        Result<RootsFile.Found> found = RootsFile.Read(Configured(
            """
            {"trees":{"work":{"path":".","screen":["clinical"],
              "scopes":{"inner":{"can":["list","read"]}}}}}
            """));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Null(found.Value.Trees[0].Scopes![0].Screen);

        var grant = new Grant(Permissions.Full,
            [new Scope("/t/inner", Permissions.ReadOnly)], Screened.Clinical);

        Assert.Equal(Screened.Clinical, grant.At("/t", "/t/inner/x").Screen);
    }

    [Fact]
    public void AScopeCanTakeScreeningAway()
    {
        var grant = new Grant(Permissions.Full,
            [new Scope("/t/open", Permissions.ReadOnly, Screened.None)], Screens.Everything);

        Assert.Equal(Screens.Everything, grant.At("/t", "/t/x").Screen);
        Assert.Equal(Screened.None, grant.At("/t", "/t/open/x").Screen);
    }

    // ================= tier one, with no sidecar at all =================

    /// <summary>AC6: tier one refuses a synthetic SSN with no sidecar
    /// installed. This is the whole of the screen on a machine where the
    /// models were never downloaded, and it still works.</summary>
    [Fact]
    public void TierOneRefusesAnSsnWithNoSidecar()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"reference {Ssn} was filed\n");
        box.Screen(Screened.Identifiers);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "notes.txt");

        Assert.False(read.IsOk);
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
    }

    /// <summary>
    /// A pattern that runs out of time checked NOTHING, and a check that
    /// did not happen must never be reported as a check that found
    /// nothing.
    ///
    /// <para><b>Reached by lowering the ceiling rather than by finding a
    /// pathological payload.</b> Every detector is deliberately free of
    /// the nested quantifiers that make a pattern blow up, so no ordinary
    /// input gets near the one-second bound - which is a good property of
    /// the detectors and an awkward one for this test. One tick times out
    /// on anything, and it is the same code path either way.</para>
    ///
    /// <para><b><see cref="Searcher"/> answers the same exception the
    /// opposite way</b>, by skipping the line and carrying on, because
    /// there the cost of giving up is a missing search hit. Here it is a
    /// disclosure. The two look alike and must not be handled
    /// alike.</para>
    /// </summary>
    [Fact]
    public void APatternThatRunsOutOfTimeRefusesRatherThanServing()
    {
        Failure? refused = Disclosure.Check(
            $"reference {Ssn} was filed", Screened.Identifiers, null, null, TimeSpan.FromTicks(1));

        Assert.NotNull(refused);
        Assert.Equal(Outcome.Screened, refused.Outcome);
        Assert.Contains("ran out of time", refused.Message, StringComparison.Ordinal);

        // And the payload is one tier one WOULD have refused, so the
        // refusal cannot be mistaken for the ordinary detection: it says
        // the check did not happen, not that it found something.
        Assert.DoesNotContain("would disclose", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Ssn, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Luhn check is what makes the card detector worth having.
    ///
    /// <para>Without it the pattern is "sixteen digits in groups of
    /// four", which every document full of reference numbers matches -
    /// and a check that cries wolf is a check somebody switches off.</para>
    /// </summary>
    [Fact]
    public void ACardIsJudgedByLuhnAndNotByShape()
    {
        using var box = new Sandbox();
        box.Write("good.txt", $"card {Card}\n");
        box.Write("bad.txt", $"order {BadCard}\n");
        box.Screen(Screened.Identifiers);

        Assert.False(ReadOf(box, "good.txt").IsOk);
        Assert.True(ReadOf(box, "bad.txt").IsOk, "a number that fails Luhn is not a card");
    }

    [Theory]
    [InlineData("contact renee@example.com about it", Screened.Identifiers)]
    [InlineData("call (555) 123-4567 tomorrow", Screened.Identifiers)]
    [InlineData("MRN: A1234567", Screened.Identifiers)]
    [InlineData("DOB: 3/4/1980", Screened.Identifiers)]
    [InlineData("date of birth 12-01-1975", Screened.Identifiers)]
    public void EachStructuralDetectorRefuses(string text, Screened category)
    {
        using var box = new Sandbox();
        box.Write("f.txt", text + "\n");
        box.Screen(category);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "f.txt");

        Assert.False(read.IsOk, $"'{text}' was served");
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
    }

    /// <summary>
    /// The things that must NOT be refused.
    ///
    /// <para>A screen that denies ordinary numbers is one people turn
    /// off, and a screen that is off protects nothing. Nine bare digits
    /// are an order number far more often than a social security number,
    /// and nothing about them says which.</para>
    /// </summary>
    [Theory]
    [InlineData("order 123456789 shipped")]
    [InlineData("version 1.2.3 released")]
    [InlineData("the total was 1200 units")]
    [InlineData("commit 4111111111111111 in the log")]
    public void OrdinaryTextIsServed(string text)
    {
        using var box = new Sandbox();
        box.Write("f.txt", text + "\n");
        box.Screen(Screens.Everything);

        Assert.True(ReadOf(box, "f.txt").IsOk, $"'{text}' was refused");
    }

    /// <summary>A category that is not screened does not deny. The
    /// configuration says what is being looked for, and excluding one
    /// takes a single word.</summary>
    [Fact]
    public void ADetectorForAnUnscreenedCategoryDoesNotFire()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"reference {Ssn}\n");
        box.Screen(Screened.Clinical);

        Assert.True(ReadOf(box, "notes.txt").IsOk,
            "an SSN is an identifier, and this scope screens only clinical - "
            + "a model tier, which no tier one detector belongs to");
    }

    [Fact]
    public void AnUnscreenedTreeIsUntouched()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"{Ssn} {Card} {Email}\n");

        Assert.True(ReadOf(box, "notes.txt").IsOk);
    }

    // ================= payload semantics (AC1, AC2) =================

    const string ClinicalNote = """
        Ward round notes
        The patient was seen today and is comfortable.
        No concerns were raised on the round.
        MRN: A1234567
        DOB: 3/4/1980
        """;

    /// <summary>
    /// AC1: a read of a synthetic clinical note is refused; the refusal
    /// names the category and a count and quotes nothing.
    ///
    /// <para><b>The category is <c>identifiers</c>, not
    /// <c>clinical</c>.</b> What tier one finds here is the labelled MRN
    /// and the labelled date of birth. The clinical prose around them -
    /// the ward round, the patient being comfortable - is found by a
    /// model or by nothing at all, and no model runs in this test.</para>
    /// </summary>
    [Fact]
    public void AClinicalNoteIsRefusedNamingTheCategoryAndACount()
    {
        using var box = new Sandbox();
        box.Write("note.txt", ClinicalNote);
        box.Screen(Screened.Identifiers);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "note.txt");

        Assert.False(read.IsOk);
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
        Assert.Contains("identifiers", read.Failure.Message, StringComparison.Ordinal);
        Assert.Contains("2 in identifiers", read.Failure.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("A1234567", read.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("3/4/1980", read.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC2: the SAME file's clean range is served.
    ///
    /// <para>This is the whole of R1.1 demonstrated. The object under
    /// judgement is the payload, not the file, so a document carrying an
    /// identifier on one line stays readable everywhere else - and the
    /// screen does not lock the very trees people most need help in.</para>
    /// </summary>
    [Fact]
    public void TheCleanRangeOfTheSameFileIsServed()
    {
        using var box = new Sandbox();
        box.Write("note.txt", ClinicalNote);
        box.Screen(Screened.Identifiers);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "note.txt", from: 1, to: 3);

        Assert.True(read.IsOk, read.Failure?.Message);
        Assert.Contains("comfortable", read.Value[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("A1234567", read.Value[0].Text, StringComparison.Ordinal);
    }

    // ================= search (AC3) =================

    [Fact]
    public void ACleanSearchOverAScreenedTreeIsServed()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");
        box.Write("notes.txt", $"reference {Ssn} was filed\n");
        box.Screen(Screened.Identifiers);

        Result<SearchAnswer> found = box.Bench.Search(new SearchRequest(["quarterly"]));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Single(found.Value.Hits);
    }

    /// <summary>AC3: the response whose hit records contain a detected
    /// entity is refused - the hit line is the disclosure, and it is the
    /// hit line that is judged.</summary>
    [Fact]
    public void ASearchWhoseHitsCarryAnEntityIsRefused()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");
        box.Write("notes.txt", $"reference {Ssn} was filed\n");
        box.Screen(Screened.Identifiers);

        Result<SearchAnswer> found = box.Bench.Search(new SearchRequest(["reference"]));

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Screened, found.Failure!.Outcome);
        Assert.DoesNotContain(Ssn, found.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The CONTEXT lines are part of the disclosure and are judged with
    /// the hit.
    ///
    /// <para>A hit whose own line is clean still discloses everything
    /// <c>--context</c> brings with it, and screening only the matched
    /// line would hand the neighbours over unread.</para>
    /// </summary>
    [Fact]
    public void ContextLinesAreScreenedWithTheHit()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"the marker line\n{Ssn}\n");
        box.Screen(Screened.Identifiers);

        Assert.False(box.Bench.Search(new SearchRequest(["marker"], Context: 2)).IsOk);
        Assert.True(box.Bench.Search(new SearchRequest(["marker"])).IsOk,
            "with no context the hit line alone is clean and is served");
    }

    // ================= documents (AC4) =================

    static byte[] Package(params (string Part, string Xml)[] parts)
    {
        using var bytes = new MemoryStream();

        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            foreach ((string part, string xml) in parts)
            {
                ZipArchiveEntry entry = zip.CreateEntry(part);
                using Stream stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(xml);
            }

        return bytes.ToArray();
    }

    const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="xml" ContentType="application/xml"/>
        </Types>
        """;

    static byte[] AWorkbookHolding(string text) => Package(
        ("[Content_Types].xml", ContentTypes),
        ("xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Patients" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """),
        ("xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Target="worksheets/sheet1.xml"
                Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"/>
            </Relationships>
            """),
        ("xl/worksheets/sheet1.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>{text}</t></is></c></row>
              </sheetData>
            </worksheet>
            """));

    static byte[] ADocumentHolding(string text) => Package(
        ("[Content_Types].xml", ContentTypes),
        ("word/document.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:r><w:t>{text}</w:t></w:r></w:p>
              </w:body>
            </w:document>
            """));

    /// <summary>
    /// AC4: the same fixtures disclosed through extraction screen
    /// identically to their plain-text twins.
    ///
    /// <para>That equivalence is the point of putting the gate at the
    /// seam rather than in each reader: a workbook, a document and a text
    /// file are all just text leaving the tool, and nothing about the
    /// screen should know which produced it.</para>
    /// </summary>
    [Fact]
    public void AWorkbookAndADocumentScreenLikeTheirPlainTextTwins()
    {
        using var box = new Sandbox();
        Documents.Forget();

        box.Write("twin.txt", $"reference {Ssn} was filed\n");
        box.WriteRaw("book.xlsx", AWorkbookHolding($"reference {Ssn} was filed"));
        box.WriteRaw("terms.docx", ADocumentHolding($"reference {Ssn} was filed"));

        box.Screen(Screened.Identifiers);

        foreach (string path in new[] { "twin.txt", "book.xlsx", "terms.docx" })
        {
            Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, path);

            Assert.False(read.IsOk, $"{path} was served");
            Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
            Assert.DoesNotContain(Ssn, read.Failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AWorkbookWithNothingInItIsStillServed()
    {
        using var box = new Sandbox();
        Documents.Forget();

        box.WriteRaw("book.xlsx", AWorkbookHolding("the quarterly total is fine"));
        box.Screen(Screens.Everything);

        Assert.True(ReadOf(box, "book.xlsx").IsOk);
    }

    /// <summary>A screened scope does not hand over an image, because
    /// nothing here can read one - and a photographed discharge summary
    /// is exactly the disclosure the screen exists to stop.</summary>
    [Fact]
    public void AnImageIsRefusedFromAScreenedScope()
    {
        using var box = new Sandbox();

        var png = new byte[24];
        png[0] = 0x89; png[1] = 0x50; png[2] = 0x4E; png[3] = 0x47;
        png[19] = 1; png[23] = 1;
        box.WriteRaw("scan.png", png);

        box.Screen(Screens.Everything);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "scan.png");

        Assert.False(read.IsOk);
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
        Assert.Contains("image", read.Failure.Message, StringComparison.Ordinal);
    }

    // ================= the sidecar (AC5, R7.1) =================

    /// <summary>
    /// AC5: with burler absent, a screened disclosure is refused with a
    /// reason naming the sidecar.
    ///
    /// <para>The real <see cref="Sidecar"/> runs here, not a fake -
    /// "burler is not installed" is exactly the state a machine is in
    /// before anybody installs it, and it deserves to be tested as
    /// itself.</para>
    /// </summary>
    [Fact]
    public void WithBurlerAbsentAScreenedDisclosureIsRefusedNamingTheSidecar()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");

        // Naming a models directory is the act that makes the second tier
        // load-bearing: from here on, its absence is a refusal.
        box.Screen(Screened.Clinical, models: box.Root);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "clean.txt");

        Assert.False(read.IsOk, "tier one found nothing, but the second tier could not run");
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
        Assert.Contains("burler", read.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>AC5, the other half: an unscreened tree is untouched. A
    /// missing sidecar is only a problem for a scope that asked to be
    /// screened.</summary>
    [Fact]
    public void WithBurlerAbsentAnUnscreenedTreeIsUntouched()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"reference {Ssn}\n");
        box.Screen(Screened.None, models: box.Root);

        Assert.True(ReadOf(box, "notes.txt").IsOk);
    }

    /// <summary>
    /// With NO models directory declared, tier one is the whole screen
    /// and a clean payload is served.
    ///
    /// <para>Nobody has claimed a model is installed, so there is no
    /// second tier to have failed. This is what keeps the screen usable
    /// before anybody downloads 400 MB - and it is why naming a directory
    /// is a deliberate act with consequences.</para>
    /// </summary>
    [Fact]
    public void WithNoModelsDirectoryTierOneIsTheWholeScreen()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");
        box.Write("notes.txt", $"reference {Ssn}\n");
        box.Screen(Screens.Everything);

        Assert.True(ReadOf(box, "clean.txt").IsOk);
        Assert.False(ReadOf(box, "notes.txt").IsOk);
    }

    // ---- R7.1: the gate, driven by a fake ----

    [Fact]
    public void ADetectionByTheModelDeniesTheResponse()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");
        box.Screen(Screened.Clinical, Fake.Finds(Screened.Clinical, 3));

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "clean.txt");

        Assert.False(read.IsOk);
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
        Assert.Contains("3 in clinical", read.Failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("the screening sidecar closed the pipe without answering")]
    [InlineData("the screening sidecar did not answer within 30 seconds")]
    [InlineData("the screening sidecar answered something that is not JSON")]
    [InlineData("the screening sidecar refused: no model for 'clinical'")]
    public void EveryWayTheSidecarCanFailIsARefusal(string why)
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");
        box.Screen(Screened.Clinical, Fake.Fails(why));

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "clean.txt");

        Assert.False(read.IsOk, "a screen that could not run must never serve");
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
        Assert.Contains(why, read.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanVerdictFromTheModelServesTheResponse()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");
        box.Screen(Screened.Clinical, Fake.Clean());

        Assert.True(ReadOf(box, "clean.txt").IsOk);
    }

    /// <summary>
    /// The model is handed the payload, and only the categories the scope
    /// screens.
    ///
    /// <para>Running a category the configuration excluded would burn the
    /// inference and tempt somebody to report the answer.</para>
    /// </summary>
    [Fact]
    public void TheModelSeesThePayloadAndOnlyTheScreenedCategories()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");

        Fake fake = Fake.Clean();
        box.Screen(Screened.Clinical | Screened.Scientific, fake);

        Assert.True(ReadOf(box, "clean.txt").IsOk);
        Assert.Equal(Screened.Clinical | Screened.Scientific, fake.Asked);
        Assert.Contains("quarterly", fake.Saw!, StringComparison.Ordinal);
    }

    /// <summary>Tier one runs FIRST, so a payload it refuses never costs
    /// an inference - and the screen still works when the second tier is
    /// the thing that is broken.</summary>
    [Fact]
    public void TierOneRefusesBeforeTheModelIsEverAsked()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"reference {Ssn}\n");

        Fake fake = Fake.Fails("this should never be reached");
        box.Screen(Screened.Identifiers, fake);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "notes.txt");

        Assert.False(read.IsOk);
        Assert.Null(fake.Saw);
        Assert.Contains("1 in identifiers", read.Failure!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An identifiers-only screen never reaches the screener at all,
    /// even when tier one finds nothing to refuse.
    ///
    /// <para><see cref="Screened.Identifiers"/> is the tier that runs in
    /// process, so a scope screening nothing else has no question to put
    /// to a model. Asking anyway would spend an inference on a category
    /// the configuration never requested, and would turn a missing
    /// sidecar into a refusal for scopes that never needed one.</para>
    ///
    /// <para>Distinct from <see cref="TierOneRefusesBeforeTheModelIsEverAsked"/>:
    /// there the payload is dirty and tier one short-circuits, so the
    /// screener going unasked proves only the ordering. Here the payload
    /// is clean and there is nothing to short-circuit - the screener is
    /// unasked because the category set says so.</para>
    /// </summary>
    [Fact]
    public void AnIdentifiersOnlyScreenNeverAsksTheScreener()
    {
        using var box = new Sandbox();
        box.Write("clean.txt", "the quarterly total is fine\n");

        Fake fake = Fake.Fails("the screener must not be reached");
        box.Screen(Screened.Identifiers, fake);

        Assert.True(ReadOf(box, "clean.txt").IsOk,
            "a clean payload under an identifiers-only screen is served");
        Assert.Null(fake.Saw);
        Assert.Equal(Screened.None, fake.Asked);
    }

    // ================= the wire, this side of it (R3.3) =================

    [Fact]
    public void AnAnswerWithFindingsIsRead()
    {
        Result<IReadOnlyList<ScreenFinding>> read = Sidecar.Read(
            """{"ok":true,"findings":[{"category":"clinical","count":2,"spans":[[3,9],[20,28]]}]}""");

        Assert.True(read.IsOk, read.Failure?.Message);
        Assert.Equal(2, read.Value.Count);
        Assert.All(read.Value, one => Assert.Equal(Screened.Clinical, one.Category));
    }

    [Fact]
    public void AnEmptyFindingsArrayIsACleanVerdict()
    {
        Result<IReadOnlyList<ScreenFinding>> read = Sidecar.Read("""{"ok":true,"findings":[]}""");

        Assert.True(read.IsOk, read.Failure?.Message);
        Assert.Empty(read.Value);
    }

    /// <summary>
    /// A MISSING findings array is a fault, not a clean verdict.
    ///
    /// <para>"I checked and it is clean" and "I did not answer properly"
    /// must not be spelled the same way, or a broken sidecar reads as a
    /// tree with nothing in it.</para>
    /// </summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"findings":[]}""")]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":true,"findings":{}}""")]
    [InlineData("""{"ok":true,"findings":[{"category":"clinical"}]}""")]
    [InlineData("""{"ok":true,"findings":[{"count":1}]}""")]
    [InlineData("""{"ok":true,"findings":[{"category":"lgal","count":1}]}""")]
    public void AMalformedAnswerIsARefusalAndNeverAPass(string line)
    {
        Result<IReadOnlyList<ScreenFinding>> read = Sidecar.Read(line);

        Assert.False(read.IsOk, $"'{line}' was read as an answer");
        Assert.Equal(Outcome.Screened, read.Failure!.Outcome);
    }

    [Fact]
    public void ARefusalFromTheSidecarCarriesItsReason()
    {
        Result<IReadOnlyList<ScreenFinding>> read = Sidecar.Read(
            """{"ok":false,"error":"no model for 'clinical'"}""");

        Assert.False(read.IsOk);
        Assert.Contains("no model for 'clinical'", read.Failure!.Message, StringComparison.Ordinal);
    }

    // ================= the refusal itself (R5) =================

    /// <summary>
    /// R5.1: nothing the screen finds is ever repeated back.
    ///
    /// <para>A refusal naming what it found would write the identifier
    /// into a log, a transcript and whatever ships those onward - the
    /// exact harm the screen exists to prevent, delivered by the screen
    /// itself.</para>
    /// </summary>
    [Theory]
    [InlineData(Ssn, false)]
    [InlineData(Card, false)]
    [InlineData(Email, false)]
    [InlineData(Phone, false)]
    [InlineData("A1234567", true)]
    public void TheRefusalNeverRepeatsWhatItFound(string secret, bool labelled)
    {
        using var box = new Sandbox();
        box.Write("f.txt", labelled ? $"MRN: {secret}\n" : $"value {secret}\n");
        box.Screen(Screened.Identifiers);

        Result<IReadOnlyList<ReadSlice>> read = ReadOf(box, "f.txt");

        Assert.False(read.IsOk);
        Assert.DoesNotContain(secret, read.Failure!.Message, StringComparison.Ordinal);

        // Not even a fragment of it: the first run of digits on its own
        // would be enough to reconstruct most of these.
        Assert.DoesNotContain(secret.Split(' ', '-', '@')[0], read.Failure.Message,
            StringComparison.Ordinal);
    }

    /// <summary>Nothing found leaks through the CLI either, which is the
    /// path that actually reaches a terminal and a transcript.</summary>
    [Fact]
    public async Task NothingLeaksThroughTheCommandLine()
    {
        using var box = new Sandbox();
        box.Write("notes.txt", $"reference {Ssn} was filed\n");
        box.Screen(Screened.Identifiers);

        CliResult said = await Run(box, "read", "notes.txt");

        Assert.Equal(ExitCodes.Screened, said.ExitCode);
        Assert.DoesNotContain(Ssn, said.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(Ssn, said.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("123-45", said.Stdout + said.Stderr, StringComparison.Ordinal);
    }

    /// <summary>R5.2: its own outcome and its own exit code, so a script
    /// can tell a screened refusal from every other kind without parsing
    /// text.</summary>
    [Fact]
    public void TheOutcomeHasItsOwnExitCodeAndName()
    {
        Assert.Equal(13, ExitCodes.Of(Outcome.Screened));
        Assert.Equal("screened", ExitCodes.NameOf(Outcome.Screened));

        Assert.NotEqual(ExitCodes.Of(Outcome.Refused), ExitCodes.Of(Outcome.Screened));
        Assert.NotEqual(ExitCodes.Of(Outcome.Credential), ExitCodes.Of(Outcome.Screened));
        Assert.NotEqual(ExitCodes.Of(Outcome.Governed), ExitCodes.Of(Outcome.Screened));
    }

    /// <summary>
    /// A screened refusal is not a credential refusal.
    ///
    /// <para>They are mirror images - one is a write going in, the other
    /// a payload coming out - and a script branching on "a secret was
    /// involved" would take entirely the wrong action for each.</para>
    /// </summary>
    [Fact]
    public void ScreeningDoesNotDisturbTheCredentialNet()
    {
        Assert.NotEqual(Outcome.Credential, Outcome.Screened);
        Assert.Equal(12, (int)Outcome.Screened);
        Assert.Equal(11, (int)Outcome.Credential);
    }

    // ================= the honesty clause is not decoration =================

    /// <summary>
    /// The documented limit, asserted so nobody can quietly come to
    /// believe otherwise: this is a safety net and NOT a boundary.
    ///
    /// <para>Split the number across two lines and every rule here is
    /// defeated - by accident as easily as on purpose. The test exists to
    /// make that concrete rather than to defend it, because a caller who
    /// believes a screened tree CANNOT leak has been misled, and that
    /// belief is the actual hazard.</para>
    /// </summary>
    [Fact]
    public void TheScreenIsASafetyNetAndNotABoundary()
    {
        using var box = new Sandbox();
        box.Write("split.txt", "reference 123-45-\n6789 was filed\n");
        box.Screen(Screens.Everything);

        Assert.True(ReadOf(box, "split.txt").IsOk,
            "a number split across a line break is not found, which is what "
            + "'a safety net and not a boundary' means in practice");
    }

    // ========= roots names the model, and never hides it =========

    /// <summary>
    /// A models directory laid out the way R4.7 says, so what
    /// <c>roots</c> reports is read off the same shape burler loads
    /// from rather than off a shape invented for the test.
    /// </summary>
    static string ModelsHolding(Sandbox box, string category, string manifest)
    {
        string models = box.Full("models");
        string here = Path.Combine(models, category);
        Directory.CreateDirectory(here);

        // Never loaded by anything here - roots reads the manifest and
        // nothing else. Its presence is what says "a model is installed".
        File.WriteAllText(Path.Combine(here, "model.onnx"), "not a real model");
        File.WriteAllText(Path.Combine(here, "manifest.json"), manifest);

        return models;
    }

    static string Manifest(string checkpoint, string revision) => $$"""
        {"checkpoint":"{{checkpoint}}","revision":"{{revision}}",
         "licence":"apache-2.0","provenance":"written by a test"}
        """;

    /// <summary>
    /// Where a category is judged by a model, <c>roots</c> names the
    /// checkpoint and the revision sitting on this disk.
    ///
    /// <para><b>Naming it is the whole point.</b> <c>screen: clinical</c>
    /// says a category is covered; it does not say by what, or whether
    /// anything is there at all. Somebody deciding whether to trust the
    /// screen needs the name of the thing doing the judging, and a
    /// revision precise enough to tell two builds of it apart.</para>
    /// </summary>
    [Fact]
    public async Task RootsNamesTheCheckpointJudgingAModelBackedCategory()
    {
        using var box = new Sandbox();
        string models = ModelsHolding(box, "clinical", Manifest("obi/deid-roberta-i2b2", "a1b2c3d"));

        box.Screen(Screened.Identifiers | Screened.Clinical, Fake.Clean(), models: models);

        CliResult listed = await Run(box, "roots");

        Assert.Equal(ExitCodes.Ok, listed.ExitCode);
        Assert.Contains("clinical: obi/deid-roberta-i2b2 @ a1b2c3d", listed.Stdout,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A named directory with no model in it says so, and says what it
    /// means: reads from here will refuse.
    ///
    /// <para>This is the state that most needs reporting. The screen is
    /// configured, the category is listed, and every read is about to
    /// fail - and without this line the only way to discover that is to
    /// be refused by it.</para>
    /// </summary>
    [Fact]
    public async Task RootsSaysWhenAScreenedCategoryHasNoModelInstalled()
    {
        using var box = new Sandbox();

        string models = box.Full("models");
        Directory.CreateDirectory(models);

        box.Screen(Screened.Clinical, Fake.Clean(), models: models);

        CliResult listed = await Run(box, "roots");

        Assert.Equal(ExitCodes.Ok, listed.ExitCode);
        Assert.Contains("clinical: no model installed - reads here will refuse", listed.Stdout,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An unreadable manifest reports as no model rather than taking the
    /// whole answer away.
    ///
    /// <para><c>roots</c> is the call somebody makes to find out where
    /// they stand, very often BECAUSE something is already wrong. A
    /// malformed file somewhere under the models directory must not be
    /// able to stop them being told what the trees are.</para>
    /// </summary>
    [Fact]
    public async Task AnUnreadableManifestReportsAsNoModelAndNeverThrows()
    {
        using var box = new Sandbox();
        string models = ModelsHolding(box, "clinical", "this is not JSON at all");

        box.Screen(Screened.Clinical, Fake.Clean(), models: models);

        CliResult listed = await Run(box, "roots");

        Assert.Equal(ExitCodes.Ok, listed.ExitCode);
        Assert.Contains("clinical: no model installed", listed.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// A manifest missing the two fields that name the model is no more
    /// use than an absent one, and is reported the same way.
    /// </summary>
    [Fact]
    public async Task AManifestThatNamesNoCheckpointReportsAsNoModel()
    {
        using var box = new Sandbox();
        string models = ModelsHolding(box, "clinical", """{"licence":"apache-2.0"}""");

        box.Screen(Screened.Clinical, Fake.Clean(), models: models);

        CliResult listed = await Run(box, "roots");

        Assert.Contains("clinical: no model installed", listed.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no models directory declared at all, the model tiers are
    /// said to be inactive - once, and in as many words.
    ///
    /// <para>A screen reading <c>identifiers clinical legal
    /// scientific</c> with nothing installed looks like four categories
    /// of protection and is one. That gap between what the line says and
    /// what runs is the thing this release exists to close.</para>
    /// </summary>
    [Fact]
    public async Task WithNoModelsDirectoryRootsSaysTheModelTiersAreInactive()
    {
        using var box = new Sandbox();
        box.Screen(Screens.Everything);

        CliResult listed = await Run(box, "roots");

        Assert.Contains("no \"models\" directory is declared", listed.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("only the identifier patterns run", listed.Stdout, StringComparison.Ordinal);

        // Every model-backed category named, so the sentence cannot
        // quietly cover three of four.
        foreach (Screened one in Screens.All)
            if (Screens.ModelBacked.HasFlag(one))
                Assert.Contains(Screens.NameOf(one), listed.Stdout, StringComparison.Ordinal);
    }

    /// <summary>Somebody screening only <c>identifiers</c> asked for no
    /// model, and is told nothing about models. The first tier needs
    /// none, so a note about a missing directory would be an answer to a
    /// question they did not ask.</summary>
    [Fact]
    public async Task AnIdentifiersOnlyScreenIsToldNothingAboutModels()
    {
        using var box = new Sandbox();
        box.Screen(Screened.Identifiers);

        CliResult listed = await Run(box, "roots");

        Assert.Contains("screen: identifiers", listed.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("models", listed.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("no model installed", listed.Stdout, StringComparison.Ordinal);
    }

    /// <summary>The JSON answer carries the same fact, because the MCP
    /// front end never sees the text one - and that is the front end
    /// where a model is the thing reading the answer.</summary>
    [Fact]
    public async Task TheJsonRootsCarriesTheCheckpointAndTheModelsDirectory()
    {
        using var box = new Sandbox();
        string models = ModelsHolding(box, "legal", Manifest("nlpaueb/legal-bert-base", "9f8e7d6"));

        box.Screen(Screened.Legal | Screened.Clinical, Fake.Clean(), models: models);

        CliResult listed = await Run(box, "roots", "--json");
        using JsonDocument answer = JsonDocument.Parse(listed.Stdout);

        Assert.Equal(models, answer.RootElement.GetProperty("models").GetString());

        JsonElement screening = answer.RootElement
            .GetProperty("roots")[0].GetProperty("screening");

        Assert.Equal("nlpaueb/legal-bert-base @ 9f8e7d6",
            screening.GetProperty("legal").GetString());
        Assert.Equal("no model installed - reads here will refuse",
            screening.GetProperty("clinical").GetString());
    }

    /// <summary>
    /// R4.7's other shape: several models under one category, each
    /// named.
    ///
    /// <para>They union at screening time - any detection by any of them
    /// denies - so reporting only the first would name one of the things
    /// judging and hide the rest.</para>
    /// </summary>
    [Fact]
    public async Task EveryModelUnderACategoryIsNamedAndNotJustTheFirst()
    {
        using var box = new Sandbox();

        string models = box.Full("models");
        foreach ((string under, string checkpoint) in
                 new[] { ("a-default", "permissive/base"), ("b-addon", "nlpaueb/legal-bert") })
        {
            string here = Path.Combine(models, "legal", under);
            Directory.CreateDirectory(here);
            File.WriteAllText(Path.Combine(here, "model.onnx"), "not a real model");
            File.WriteAllText(Path.Combine(here, "manifest.json"), Manifest(checkpoint, "r1"));
        }

        box.Screen(Screened.Legal, Fake.Clean(), models: models);

        CliResult listed = await Run(box, "roots");

        Assert.Contains("permissive/base @ r1", listed.Stdout, StringComparison.Ordinal);
        Assert.Contains("nlpaueb/legal-bert @ r1", listed.Stdout, StringComparison.Ordinal);
    }
}
