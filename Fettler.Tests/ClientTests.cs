using System.Text.Json;
using Fettler.Clients;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// Phase 6 for the doctor and the scaffolder, which read and write other
/// programs' configuration and are therefore the highest-consequence code
/// in the tool.
///
/// <para><b>6.1 governs every test here: no test may read the real
/// machine's configuration.</b> A suite that does is a suite that passes
/// only on the machine it was written on, and this is exactly the code
/// where that would matter most. Every path comes from a fixture
/// <see cref="Machine"/> - a synthetic home, a synthetic app-data and a
/// synthetic tree - which is why <c>Machine</c> takes its three
/// directories as arguments rather than reading the environment.</para>
/// </summary>
public sealed class ClientTests
{
    /// <summary>A whole machine in a temporary directory: a home, an
    /// application-data folder and a tree, none of them real.</summary>
    sealed class Fixture : IDisposable
    {
        readonly string root = Directory.CreateTempSubdirectory("fettle-machine-").FullName;

        public Fixture()
        {
            Machine = new Machine(Dir("home"), Dir("appdata"), Dir("tree"));
        }

        public Machine Machine { get; }

        public string Dir(string relative)
        {
            string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(full);
            return full;
        }

        /// <summary>Put a file at one of the places, so a test names the
        /// PURPOSE rather than repeating a path the code already
        /// knows.</summary>
        public string Put(string client, Reach reach, Purpose purpose, string content)
        {
            Place place = Places.All(Machine).First(p =>
                p.Client == client && p.Reach == reach && p.Purpose == purpose);

            Directory.CreateDirectory(Path.GetDirectoryName(place.Path)!);
            File.WriteAllText(place.Path, content);
            return place.Path;
        }

        public string PathOf(string client, Reach reach, Purpose purpose) =>
            Places.All(Machine).First(p => p.Client == client && p.Reach == reach && p.Purpose == purpose).Path;

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The levels of a project with guardrails on, which is the
    /// project most of these checks are about.</summary>
    static readonly Levels Guarded = new([], ["test"], []);

    /// <summary>A boundary on the fixture's tree with guardrails on.</summary>
    static Core.Roots GuardedRoots(Fixture box, params TaskDecl[] tasks)
    {
        Result<Core.Roots> opened = Core.Roots.Open(
            [new TreeDecl("work", box.Machine.Tree, Permissions.Full)], null, tasks, null, Guarded);
        Assert.True(opened.IsOk, opened.Failure?.Message);
        return opened.Value;
    }

    static Diagnosis Examine(Fixture box) => Doctor.Examine(box.Machine, GuardedRoots(box), "test");

    static ClientReport Report(Diagnosis d, string client, Reach reach) =>
        d.Clients.First(c => c.Client == client && c.Reach == reach);

    // ---- 6.2: all four verdicts, per client ----

    /// <summary>
    /// The distinction the verdicts exist to make. Claude Desktop has no
    /// project scope at all - reporting that as a failure would train a
    /// reader to ignore the whole report, which is how a diagnostic stops
    /// being read.
    /// </summary>
    [Fact]
    public void AClientWithNothingAtALevelIsNotApplicableAndNeverAFailure()
    {
        using var box = new Fixture();

        Diagnosis d = Examine(box);

        Assert.Equal(Verdict.NotApplicable, Report(d, Places.ClaudeDesktop, Reach.Repo).Verdict);
        Assert.Contains("no project registration", Report(d, Places.ClaudeDesktop, Reach.Repo).Detail);
        Assert.False(d.AnyBroken);
    }

    [Fact]
    public void AClientThatIsHereWithoutFettlerRegisteredIsAbsent()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Servers, """{"mcpServers":{"other":{"command":"x"}}}""");

        Assert.Equal(Verdict.Absent, Report(Examine(box), Places.ClaudeCode, Reach.Repo).Verdict);
    }

    /// <summary>
    /// Live on this machine when the works order was written: every
    /// documented registration launched a bare <c>fettle</c>, and nothing
    /// resolved it. A server that cannot start reports as registered from
    /// every direction but the one that matters.
    /// </summary>
    [Fact]
    public void ARegistrationWhoseCommandCannotBeLaunchedIsBroken()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Servers, """
            {"mcpServers":{"fettler":{"command":"a-binary-that-is-not-anywhere","args":["serve"]}}}
            """);

        ClientReport report = Report(Examine(box), Places.ClaudeCode, Reach.Repo);

        Assert.Equal(Verdict.Broken, report.Verdict);
        Assert.Contains("has that name", report.Detail);
        Assert.True(Examine(box).AnyBroken);
    }

    [Fact]
    public void ARegistrationNamingARealBinaryIsHealthy()
    {
        using var box = new Fixture();
        string binary = Environment.ProcessPath!;
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Servers,
            "{\"mcpServers\":{\"fettler\":{\"command\":" + JsonSerializer.Serialize(binary) + ",\"args\":[\"serve\"]}}}");

        Assert.Equal(Verdict.Healthy, Report(Examine(box), Places.ClaudeCode, Reach.Repo).Verdict);
    }

    [Fact]
    public void VsCodeKeepsItsServersUnderADifferentKeyAndIsFoundThere()
    {
        using var box = new Fixture();
        box.Put(Places.VsCodeCopilot, Reach.Repo, Purpose.Servers,
            "{\"servers\":{\"fettler\":{\"command\":" + JsonSerializer.Serialize(Environment.ProcessPath!) + "}}}");

        Assert.Equal(Verdict.Healthy, Report(Examine(box), Places.VsCodeCopilot, Reach.Repo).Verdict);
    }

    [Fact]
    public void AConfigurationThatWillNotParseIsBrokenAndNeverSilentlySkipped()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Servers, "{ this is not json");

        ClientReport report = Report(Examine(box), Places.ClaudeCode, Reach.Global);

        Assert.Equal(Verdict.Broken, report.Verdict);
        Assert.Contains("is not valid JSON", report.Detail);
    }

    // ---- 6.3: every override check, positive and clean ----

    [Fact]
    public void AShellCommandThatWritesFilesIsReportedAsAPreApprovedRouteRound()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Policy, """
            {"permissions":{"allow":["Bash(sed -i:*)","Bash(cp:*)","PowerShell(Set-Content:*)","Bash(git status)"]}}
            """);

        Finding f = Examine(box).Findings.First(x => x.Check == "2.6");

        Assert.True(f.Serious);
        Assert.Contains("writes files", f.Message);
        Assert.Contains("sed", f.Message);

        // The one that reads rather than writes is not named.
        Assert.DoesNotContain("git status", f.Message);
    }

    /// <summary>
    /// Windows has TWO shell tools, not one. The machine that produced
    /// this design named Bash twenty-two times and PowerShell zero -
    /// while PowerShell was the shell used for essentially every command.
    /// A Bash-only check would have caught none of it.
    /// </summary>
    [Fact]
    public void PowerShellIsCheckedAsWellAsBash()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Policy, """
            {"permissions":{"allow":["PowerShell(Remove-Item:*)"]}}
            """);

        Assert.Contains(Examine(box).Findings, f => f.Check == "2.6" && f.Message.Contains("Remove-Item"));
    }

    /// <summary>
    /// Read was the one first waved through, on the reasoning that
    /// reading is not the risk. It is: a blanket Read defeats
    /// containment, the read permission and hidden scopes at once.
    /// </summary>
    [Fact]
    public void AnAllowOnTheBuiltInReaderOrEditorsIsSerious()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Policy, """
            {"permissions":{"allow":["Read(//c/somewhere/**)","Edit"]}}
            """);

        Finding f = Examine(box).Findings.First(x => x.Check == "2.7");

        Assert.True(f.Serious);
        Assert.Contains("Read", f.Message);
        Assert.Contains("Fettler replaces", f.Message);
    }

    [Fact]
    public void NothingDenyingTheBuiltInEditorsIsNotedAtTheRepoLevel()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, """{"permissions":{"allow":[]}}""");

        Finding f = Examine(box).Findings.First(x => x.Check == "2.8");

        Assert.False(f.Serious);
        Assert.Equal(Levels.ToolsName, f.Level);
        Assert.Contains("deny does not list", f.Message);
        Assert.Contains("fettle setup tools claude-code --local", f.Message);
    }

    /// <summary>The six replaced tools alone are not the whole list: a
    /// shell left open is a route round the boundary, and
    /// <c>node x.js</c> takes it without being a writing command. The
    /// shells are guardrails' own finding, 2.20, because a declared task
    /// is what replaces a shell.</summary>
    [Fact]
    public void AnOpenShellIsReportedEvenWhenEveryReplacedToolIsDenied()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, """
            {"permissions":{"deny":["Read","Write","Edit","NotebookEdit","Grep","Glob","BashOutput","TaskOutput"]}}
            """);

        Diagnosis d = Examine(box);
        Finding f = d.Findings.First(x => x.Check == Doctor.ShellCheck);

        Assert.Equal(Levels.GuardrailsName, f.Level);
        Assert.Contains("Bash", f.Message);
        Assert.Contains("PowerShell", f.Message);
        Assert.Contains("fettle setup guardrails claude-code --local", f.Message);
        Assert.DoesNotContain(d.Findings, x => x.Check == "2.8");
    }

    /// <summary>Built FROM <see cref="Doctor.Denied"/> rather than from a
    /// list typed out again here, so a route added to that list cannot be
    /// left untested by a fixture nobody remembered to update.</summary>
    [Fact]
    public void DenyingThemAllLeavesNothingToReport()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy,
            "{\"permissions\":{\"deny\":" + JsonSerializer.Serialize(Doctor.Denied) + "}}");

        Assert.DoesNotContain(Examine(box).Findings, f => f.Check is "2.7" or "2.8" or Doctor.ShellCheck or "2.18");
    }

    /// <summary>
    /// Monitor runs the command it is handed "in the same shell
    /// environment as Bash" by its own description, so denying Bash and
    /// leaving Monitor open denies the word and not the thing. It was
    /// found wide open on a machine where every other route was shut.
    /// </summary>
    [Fact]
    public void AShellRegisteredUnderAnotherNameIsReportedToo()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, """
            {"permissions":{"deny":["Read","Write","Edit","NotebookEdit","Grep","Glob","Bash","PowerShell"]}}
            """);

        Assert.Contains("Monitor", Examine(box).Findings.First(x => x.Check == Doctor.ShellCheck).Message);
    }

    /// <summary>
    /// A tool that hands back work already done reaches the model without
    /// passing the tree boundary, the secret scan or the disclosure
    /// screen. Neither an editor nor a shell, which is why they were
    /// missed - and TaskOutput settles it by naming Read as its own
    /// alternative, a tool that is already denied.
    /// </summary>
    [Fact]
    public void ToolsCarryingAnotherToolsOutputBackAreReportedToo()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, """
            {"permissions":{"deny":["Read","Write","Edit","NotebookEdit","Grep","Glob","Bash","PowerShell"]}}
            """);

        Finding f = Examine(box).Findings.First(x => x.Check == "2.8");

        Assert.Equal(Levels.ToolsName, f.Level);
        Assert.Contains("BashOutput", f.Message);
        Assert.Contains("TaskOutput", f.Message);
    }

    /// <summary>B.17 must not fire on the configuration this tool now
    /// writes. Denying the shell and allowing back a named command is the
    /// intended shape; calling it a conflict would name "remove the
    /// allow" as the fix, which is the one change that breaks it.</summary>
    [Fact]
    public void ANarrowedShellAllowBesideTheShellDenyIsNotAConflict()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, """
            {"permissions":{"allow":["Bash(git status:*)"],"deny":["Read","Write","Edit","NotebookEdit","Grep","Glob","Bash","PowerShell"]}}
            """);

        Assert.DoesNotContain(Examine(box).Findings, f => f.Check == "B.17");
    }

    /// <summary>The exemption is for the shell and stops there: a
    /// narrowed allow on Read is still a hole, because reading is what
    /// Fettler does.</summary>
    [Fact]
    public void ANarrowedAllowOnAReplacedToolIsStillAConflict()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, """
            {"permissions":{"allow":["Read(//c/x/**)"],"deny":["Read"]}}
            """);

        Assert.Contains(Examine(box).Findings, f => f.Check == "B.17");
    }

    /// <summary>
    /// B.17. Which of allow and deny wins is the client's to decide and
    /// cannot be observed from here, so the conflict is reported and the
    /// fix named is the one that is right under either precedence.
    /// </summary>
    [Fact]
    public void ARuleInBothAllowAndDenyIsReportedWithoutGuessingWhichWins()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Policy, """
            {"permissions":{"allow":["Read(//c/x/**)"],"deny":["Read"]}}
            """);

        Finding f = Examine(box).Findings.First(x => x.Check == "B.17");

        Assert.True(f.Serious);
        Assert.Contains("Remove the allow", f.Message);
    }

    /// <summary>
    /// Two boundaries that disagree means the narrower one is decorative,
    /// and the wider one is the one that applies.
    /// </summary>
    [Fact]
    public void AdditionalDirectoriesWiderThanTheDeclaredTreesIsReported()
    {
        using var box = new Fixture();
        string elsewhere = box.Dir("elsewhere");
        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Policy,
            $$$"""{"permissions":{"additionalDirectories":[{{{JsonSerializer.Serialize(elsewhere)}}}]}}""");

        Diagnosis d = Doctor.Examine(box.Machine, GuardedRoots(box), "test");

        Assert.Contains(d.Findings, f => f.Check == "2.9" && f.Message.Contains("outside"));
    }

    /// <summary>
    /// Live on this machine: two project entries differing only in a
    /// drive letter's case. Approvals attach to whichever spelling was
    /// resolved that day and silently do not apply to the other.
    /// </summary>
    [Fact]
    public void TwoProjectKeysNamingOneDirectoryAreReported()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Servers, """
            {"projects":{"c:/github/shoddy":{},"C:/github/shoddy":{}}}
            """);

        Assert.Contains(Examine(box).Findings, f => f.Check == "2.11");
    }

    [Fact]
    public void OneProjectKeySpelledOnceIsNotReported()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Servers, """
            {"projects":{"c:/github/shoddy":{},"c:/github/other":{}}}
            """);

        Assert.DoesNotContain(Examine(box).Findings, f => f.Check == "2.11");
    }

    [Fact]
    public void InstructionsThatNeverMentionFettlerAreNoted()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Instructions, "# Working here\n\nBe careful.\n");

        Assert.Contains(Examine(box).Findings, f => f.Check == "2.10");
    }

    [Fact]
    public void APersonalSettingsFileIsNotedBecauseNobodyElseCanSeeIt()
    {
        using var box = new Fixture();
        string local = box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Policy)
            .Replace("settings.json", "settings.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, "{}");

        Assert.Contains(Examine(box).Findings, f => f.Check == "2.13");
    }

    [Fact]
    public void AutoApprovingArbitraryToolsIsSerious()
    {
        using var box = new Fixture();
        box.Put(Places.VsCodeCopilot, Reach.Global, Purpose.Policy, """{"chat.tools.autoApprove":true}""");

        Assert.Contains(Examine(box).Findings, f => f.Check == "2.14" && f.Serious);
    }

    // ---- what a closed list of tool names cannot see ----

    /// <summary>
    /// 2.18. Check 2.8 can only report on the tools this fettle has heard
    /// of, and the client keeps growing new ones. Silence about an
    /// unknown tool reads exactly like a clean bill of health, so the
    /// gap in the inventory is reported instead of being kept in the
    /// source.
    /// </summary>
    [Fact]
    public void AToolTheInventoryDoesNotKnowIsReportedRatherThanPassedOver()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, """
            {"permissions":{"allow":["Telepathy(read:*)"]}}
            """);

        Finding f = Examine(box).Findings.First(x => x.Check == "2.18");

        Assert.False(f.Serious);
        Assert.Contains("Telepathy", f.Message);
        Assert.Contains(Doctor.InventoryOf, f.Message);
    }

    /// <summary>A rule naming another server's tools is that server's
    /// business and 2.17's, not this list's. Reporting them here would
    /// make 2.18 fire on every machine with a second MCP server on
    /// it.</summary>
    [Fact]
    public void RulesOnAnotherServersToolsAreNotReportedAsUnknown()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, """
            {"permissions":{"deny":["mcp__something__write"]}}
            """);

        Assert.DoesNotContain(Examine(box).Findings, f => f.Check == "2.18");
    }

    /// <summary>Every name the scaffolder writes must be one the
    /// inventory knows, or setup would produce a configuration its own
    /// doctor then reports as unrecognised.</summary>
    [Fact]
    public void EverythingSetupDeniesIsInTheInventory()
    {
        foreach (string tool in Doctor.Denied) Assert.Contains(tool, Doctor.Known);
    }

    /// <summary>
    /// 2.17. The deny list closes NAMES. Another MCP server arrives with
    /// tools that are not on it and never will be, reaching whatever its
    /// own process reaches - so the fact is reported, the servers are not
    /// judged, and unlike 2.8 this reads the machine in front of it and
    /// so cannot fall behind.
    /// </summary>
    [Fact]
    public void OtherServersRegisteredBesideFettlerAreNamedAsOutsideTheBoundary()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Servers, """
            {"mcpServers":{"fettler":{"command":"fettle"},"sparky":{"command":"sparky"}}}
            """);

        Finding f = Examine(box).Findings.First(x => x.Check == "2.17");

        Assert.False(f.Serious);
        Assert.Contains("sparky", f.Message);
        Assert.DoesNotContain(Places.ServerName, f.Message);
    }

    /// <summary>A server declared only in the per-project copy is still a
    /// route, and Registration already reads that shape.</summary>
    [Fact]
    public void AServerDeclaredOnlyUnderAProjectIsFoundToo()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Servers, """
            {"projects":{"c:/x":{"mcpServers":{"elsewhere":{"command":"e"}}}}}
            """);

        Assert.Contains("elsewhere", Examine(box).Findings.First(x => x.Check == "2.17").Message);
    }

    [Fact]
    public void FettlerIsNotReportedAsASiblingOfItself()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Servers, """
            {"mcpServers":{"fettler":{"command":"fettle"}}}
            """);

        Assert.DoesNotContain(Examine(box).Findings, f => f.Check == "2.17");
    }

    // ---- 6.7: no secret is ever printed ----

    /// <summary>
    /// <c>~/.claude/.credentials.json</c> sits in the same directory as
    /// files the doctor reads. It names files and keys, never values -
    /// and this asserts it by putting a distinctive secret on the machine
    /// and checking that nothing anywhere in the answer carries it.
    /// </summary>
    [Fact]
    public void NothingFromACredentialsFileReachesTheAnswer()
    {
        using var box = new Fixture();
        const string secret = "sk-THIS-MUST-NEVER-BE-PRINTED-0123456789";

        Directory.CreateDirectory(Path.Combine(box.Machine.Home, ".claude"));
        File.WriteAllText(Path.Combine(box.Machine.Home, ".claude", ".credentials.json"),
            $$"""{"token":"{{secret}}"}""");

        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Policy, """
            {"permissions":{"allow":["Bash(rm:*)","Read"]}}
            """);

        Diagnosis d = Examine(box);
        string everything = string.Join("\n",
            d.Clients.Select(c => c.Detail + c.Where).Concat(d.Findings.Select(f => f.Message + f.Where)));

        Assert.DoesNotContain(secret, everything);
        Assert.DoesNotContain("sk-", everything);
    }

    // ---- 6.8: containment holds for the closed list ----

    /// <summary>
    /// The doctor reads files no tree contains, which is the one
    /// exception to R8. It is kept honest by being a fixed list rather
    /// than a permission: never a caller-supplied path, never a glob,
    /// never a directory walk.
    /// </summary>
    [Fact]
    public void OnlyTheCompiledInListMayBeReadOrWritten()
    {
        using var box = new Fixture();

        Assert.True(Places.Allows(box.Machine, box.PathOf(Places.ClaudeCode, Reach.Global, Purpose.Policy)));
        Assert.True(Places.Allows(box.Machine, box.PathOf(Places.ClaudeCode, Reach.Global, Purpose.Policy)
            + Places.BackupSuffix));

        foreach (string forbidden in new[]
        {
            Path.Combine(box.Machine.Home, ".claude", ".credentials.json"),
            Path.Combine(box.Machine.Home, ".ssh", "id_rsa"),
            Path.Combine(box.Machine.Tree, "anything.json"),
            Path.Combine(box.Machine.Home, ".claude", "settings.json.other"),
        })
            Assert.False(Places.Allows(box.Machine, forbidden), $"{forbidden} must not be readable");
    }

    // ---- setup ----

    static Result<Scaffolding> Setup(Fixture box, Scaffold.Options options) =>
        Scaffold.Apply(box.Machine, options, "test");

    const string G = Levels.GuardrailsName;

    /// <summary>
    /// 6.4, and the whole risk of the feature. These files hold other
    /// people's servers. A merge bug here does visible damage to a
    /// machine outside this repository.
    /// </summary>
    [Fact]
    public void MergingKeepsEveryOtherServerExactlyAsItWas()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Servers, """
            {
              "mcpServers": {
                "one":   { "command": "one-binary",   "args": ["a", "b"] },
                "two":   { "command": "two-binary",   "env": { "K": "V" } },
                "three": { "command": "three-binary" }
              },
              "somethingElse": { "kept": true }
            }
            """);

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo));
        Assert.True(done.IsOk, done.Failure?.Message);

        JsonElement after = JsonDocument.Parse(
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Servers))).RootElement;

        JsonElement servers = after.GetProperty("mcpServers");
        Assert.Equal("one-binary", servers.GetProperty("one").GetProperty("command").GetString());
        Assert.Equal(["a", "b"], servers.GetProperty("one").GetProperty("args")
            .EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("V", servers.GetProperty("two").GetProperty("env").GetProperty("K").GetString());
        Assert.Equal("three-binary", servers.GetProperty("three").GetProperty("command").GetString());
        Assert.True(after.GetProperty("somethingElse").GetProperty("kept").GetBoolean());

        Assert.Equal("serve", servers.GetProperty("fettler").GetProperty("args")[0].GetString());
    }

    /// <summary>3.3: back up before the first write, beside the original,
    /// and say where it went.</summary>
    [Fact]
    public void TheFirstWriteLeavesABackupAndSaysWhereItIs()
    {
        using var box = new Fixture();
        string path = box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Servers, """{"mcpServers":{}}""");

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo));

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.Contains(done.Value.Changes, c => c.Backup == path + Places.BackupSuffix);
        Assert.True(File.Exists(path + Places.BackupSuffix));
        Assert.Equal("""{"mcpServers":{}}""", File.ReadAllText(path + Places.BackupSuffix));
    }

    /// <summary>6.5: running it twice changes nothing the second time -
    /// for JSON and for the marker blocks alike.</summary>
    [Fact]
    public void SetupIsIdempotent()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo)).IsOk);
        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Level: G)).IsOk);

        string servers = File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Servers));
        string instructions = File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Instructions));
        string policy = File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Policy));

        Result<Scaffolding> tools = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo));
        Result<Scaffolding> again = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Level: G));

        Assert.True(tools.IsOk, tools.Failure?.Message);
        Assert.True(again.IsOk, again.Failure?.Message);
        Assert.Empty(tools.Value.Changes);
        Assert.Empty(again.Value.Changes);
        Assert.Equal(servers, File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Servers)));
        Assert.Equal(instructions, File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Instructions)));
        Assert.Equal(policy, File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Policy)));
    }

    /// <summary>An unmarked instruction block always ends up appended
    /// twice; a marked one updates in place.</summary>
    [Fact]
    public void TheInstructionBlockUpdatesInPlaceAndKeepsWhatWasAlreadyWritten()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Instructions,
            "# House rules\n\nSomething the user wrote themselves.\n");

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Level: G)).IsOk);
        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Level: G)).IsOk);

        string text = File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Instructions));

        Assert.Contains("Something the user wrote themselves.", text);
        Assert.Equal(1, text.Split(Scaffold.BlockOpen).Length - 1);
    }

    /// <summary>--local writes the deny list by default: it lands in the
    /// project's own settings, is reviewable in a diff, and affects only
    /// this tree.</summary>
    [Fact]
    public void SetupLocalWritesTheDenyList()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Level: G)).IsOk);

        JsonElement deny = JsonDocument.Parse(
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Policy)))
            .RootElement.GetProperty("permissions").GetProperty("deny");

        string[] denied = [.. deny.EnumerateArray().Select(d => d.GetString()!)];
        foreach (string tool in Doctor.Denied) Assert.Contains(tool, denied);
    }

    /// <summary>The deny list a settings file holds, read back the way
    /// the client would.</summary>
    static string[] DeniedIn(Fixture box, Reach reach)
    {
        JsonElement deny = JsonDocument.Parse(
            File.ReadAllText(box.PathOf(Places.ClaudeCode, reach, Purpose.Policy)))
            .RootElement.GetProperty("permissions").GetProperty("deny");
        return [.. deny.EnumerateArray().Select(d => d.GetString()!)];
    }

    /// <summary>setup tools writes the denies that make Fettler the route:
    /// the six it replaces and the two output readers. The shells stay
    /// open at this level, because a declared task is what replaces a
    /// shell and the tools level declares none.</summary>
    [Fact]
    public void SetupToolsWritesTheToolsDeniesAndLeavesTheShellOpen()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo)).IsOk);

        string[] denied = DeniedIn(box, Reach.Repo);
        foreach (string tool in Doctor.ToolsDenied) Assert.Contains(tool, denied);
        foreach (string tool in Doctor.Shell) Assert.DoesNotContain(tool, denied);
    }

    /// <summary>The documented order: tools, then guardrails. The second
    /// run adds the shells and changes nothing tools wrote.</summary>
    [Fact]
    public void SetupGuardrailsAfterSetupToolsAddsOnlyTheShells()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo)).IsOk);
        Result<Scaffolding> guarded = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Level: G));
        Assert.True(guarded.IsOk, guarded.Failure?.Message);

        Change policy = Assert.Single(guarded.Value.Changes,
            c => c.Path == box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Policy));
        foreach (string tool in Doctor.Shell) Assert.Contains(tool, policy.What);
        foreach (string tool in Doctor.ToolsDenied) Assert.DoesNotContain(tool, policy.What);

        string[] denied = DeniedIn(box, Reach.Repo);
        foreach (string tool in Doctor.Denied) Assert.Contains(tool, denied);
    }

    /// <summary>--global only OFFERS: changing how the assistant may work
    /// in every project is not a side effect of configuring one.</summary>
    [Fact]
    public void SetupGlobalOnlyOffersTheDenyListUntilItIsAskedFor()
    {
        using var box = new Fixture();

        Result<Scaffolding> offered = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Global, Level: G));
        Assert.True(offered.IsOk, offered.Failure?.Message);
        Assert.Contains(offered.Value.Notes, n => n.Contains("not written"));
        Assert.False(File.Exists(box.PathOf(Places.ClaudeCode, Reach.Global, Purpose.Policy)));

        Result<Scaffolding> asked = Setup(box,
            new Scaffold.Options(Places.ClaudeCode, Reach.Global, Deny: true, Level: G));
        Assert.True(asked.IsOk, asked.Failure?.Message);
        Assert.Contains("Read", File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Global, Purpose.Policy)));
    }

    /// <summary>The same at the tools level, which is the run that was
    /// meant to deny and did not: setup tools --global offers, and --deny
    /// writes the tools-level denies and the tools paragraph.</summary>
    [Fact]
    public void SetupToolsGlobalOffersTheDenyListAndDenyWritesIt()
    {
        using var box = new Fixture();

        Result<Scaffolding> offered = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Global));
        Assert.True(offered.IsOk, offered.Failure?.Message);
        Assert.Contains(offered.Value.Notes, n => n.Contains("not written") && n.Contains("--deny"));
        Assert.False(File.Exists(box.PathOf(Places.ClaudeCode, Reach.Global, Purpose.Policy)));

        Result<Scaffolding> asked = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Global, Deny: true));
        Assert.True(asked.IsOk, asked.Failure?.Message);

        string[] denied = DeniedIn(box, Reach.Global);
        foreach (string tool in Doctor.ToolsDenied) Assert.Contains(tool, denied);
        foreach (string tool in Doctor.Shell) Assert.DoesNotContain(tool, denied);
        Assert.Contains("Call `roots` first",
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Global, Purpose.Instructions)));
    }

    /// <summary>An existing allow entry is the user's own configuration.
    /// A tool that quietly removes it has done exactly what this design
    /// objects to.</summary>
    [Fact]
    public void ExistingAllowEntriesAreNamedAndNeverDeleted()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, """
            {"permissions":{"allow":["Read(//c/x/**)","Bash(git status)"]}}
            """);

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Level: G));

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.Contains(done.Value.Notes, n => n.Contains("remove them yourself") && n.Contains("Read(//c/x/**)"));
        Assert.Contains("Read(//c/x/**)",
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Policy)));
    }

    [Fact]
    public void ADryRunTouchesNothingAtAllAndSaysWhatItWould()
    {
        using var box = new Fixture();

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, DryRun: true, Level: G));

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.NotEmpty(done.Value.Changes);
        Assert.All(done.Value.Changes, c => Assert.False(c.Applied));
        Assert.False(File.Exists(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Servers)));
        Assert.False(File.Exists(Path.Combine(box.Machine.Tree, RootsFile.FileName)));
    }

    /// <summary>3.x: re-running never silently reshapes somebody's own
    /// registration.</summary>
    [Fact]
    public void ARegistrationThatSaysSomethingElseNeedsForceToBeReplaced()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Servers, """
            {"mcpServers":{"fettler":{"command":"somebody-elses-choice","args":["--special"]}}}
            """);

        Result<Scaffolding> refused = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo));
        Assert.False(refused.IsOk);
        Assert.Equal(Outcome.TargetExists, refused.Failure!.Outcome);
        Assert.Contains("somebody-elses-choice",
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Servers)));

        Result<Scaffolding> forced = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Force: true));
        Assert.True(forced.IsOk, forced.Failure?.Message);
        Assert.DoesNotContain("somebody-elses-choice",
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Servers)));
    }

    /// <summary>6.6: a file that will not parse is one setup must not
    /// overwrite, because overwriting throws away whatever it holds.</summary>
    [Fact]
    public void SetupRefusesToOverwriteAFileItCannotRead()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Servers, "{ half a file");

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo));

        Assert.False(done.IsOk);
        Assert.Equal(Outcome.Invalid, done.Failure!.Outcome);
        Assert.Equal("{ half a file", File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Servers)));
    }

    [Fact]
    public void SetupWritesATreeConfigurationWhereThereIsNone()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Level: G)).IsOk);

        Result<RootsFile.Found> read = RootsFile.Read(Path.Combine(box.Machine.Tree, RootsFile.FileName));
        Assert.True(read.IsOk, read.Failure?.Message);
        Assert.Equal("work", read.Value.Trees[0].Name);
        Assert.Equal(Permissions.Full, read.Value.Trees[0].Can);
    }

    /// <summary>
    /// B.14: creating a configuration where none exists is scaffolding;
    /// REWRITING one is the escape B.13 closes, and belongs to a person
    /// with an editor.
    /// </summary>
    [Fact]
    public void SetupNeverChangesATreeConfigurationThatIsAlreadyThere()
    {
        using var box = new Fixture();
        string path = Path.Combine(box.Machine.Tree, RootsFile.FileName);
        const string mine = """{"guardrails":{"trees":{"mine":{"path":".","can":["list","read"]}}}}""";
        File.WriteAllText(path, mine);

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Level: G)).IsOk);

        Assert.Equal(mine, File.ReadAllText(path));
    }

    // ---- 13: the hook ----

    /// <summary>
    /// There is a working SessionStart hook on the machine this was
    /// written for, running an unrelated script. Replacing that array
    /// would silently remove somebody's working setup.
    /// </summary>
    [Fact]
    public void TheHookIsAppendedToWhateverIsAlreadyThere()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, """
            {"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"somebody-elses-script.ps1"}]}]}}
            """);

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Hooks: true, Level: G)).IsOk);

        string text = File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Policy));
        JsonElement sessions = JsonDocument.Parse(text).RootElement
            .GetProperty("hooks").GetProperty("SessionStart");

        Assert.Equal(2, sessions.GetArrayLength());
        Assert.Contains("somebody-elses-script.ps1", text);
        Assert.Contains("doctor --hook", text);
    }

    [Fact]
    public void TheHookIsNotInstalledTwice()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Hooks: true, Level: G)).IsOk);
        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Hooks: true, Level: G)).IsOk);

        JsonElement sessions = JsonDocument.Parse(
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Policy)))
            .RootElement.GetProperty("hooks").GetProperty("SessionStart");

        Assert.Equal(1, sessions.GetArrayLength());
    }

    [Fact]
    public void TheHookIsBoundedByATimeout()
    {
        using var box = new Fixture();
        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Hooks: true, Level: G)).IsOk);

        Assert.Contains("\"timeout\"",
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Policy)));
    }

    /// <summary>
    /// A dry run must describe the one interesting case rather than
    /// refuse it. It first did the opposite - refused, and advised the
    /// very flag the caller had already passed - which was found by
    /// running the documented commands exactly as written.
    /// </summary>
    [Fact]
    public void ADryRunDescribesAReplacementInsteadOfRefusingIt()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Servers, """
            {"mcpServers":{"fettler":{"command":"something-else"}}}
            """);

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, DryRun: true));

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.Contains(done.Value.Changes, c => c.What.Contains("replace") && c.What.Contains("--force"));
        Assert.All(done.Value.Changes, c => Assert.False(c.Applied));
        Assert.Contains("something-else",
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Servers)));
    }

    [Fact]
    public void AnUnknownClientIsRefusedNamingTheOnesThereAre()
    {
        using var box = new Fixture();

        Result<Scaffolding> done = Setup(box, new Scaffold.Options("emacs", Reach.Repo));

        Assert.False(done.IsOk);
        Assert.Contains("claude-code", done.Failure!.Message);
    }

    // ---- 2.16: a declared value handed to a shell to parse ----

    /// <summary>A boundary with tasks on it, the way a configuration
    /// produces one: the line as declared, and the command already
    /// filled.</summary>
    static Core.Roots WithTasks(Fixture box, params TaskDecl[] tasks) => GuardedRoots(box, tasks);

    /// <summary>
    /// Filling a value into a WORD makes it one argument whatever is in
    /// it, and that guarantee stops at the process boundary. A task that
    /// hands a shell a STRING is on the far side: the argument is itself
    /// a script the shell parses, so a quote or a semicolon in the value
    /// changes what runs.
    /// </summary>
    [Fact]
    public void AValueFilledIntoAStringAShellWillParseIsReported()
    {
        using var box = new Fixture();

        Core.Roots roots = WithTasks(box, new TaskDecl("commit",
            ["pwsh", "-Command", "git add -A; git commit -m 'a note'"], null,
            "pwsh -Command \"git add -A; git commit -m '{commit-note}'\""));

        Finding f = Doctor.Examine(box.Machine, roots, "test")
            .Findings.First(x => x.Check == "2.16");

        Assert.False(f.Serious);
        Assert.Contains("commit", f.Message);
        Assert.Contains("-Command", f.Message);
        Assert.Contains("separate tasks", f.Message);
    }

    /// <summary>
    /// The shapes that must NOT warn, because a check that fires on
    /// correct configurations is one people learn to scroll past.
    /// </summary>
    [Fact]
    public void AValueThatStaysAnArgumentIsNotReported()
    {
        using var box = new Fixture();

        Core.Roots roots = WithTasks(box,
            // An argument list the whole way down: the value never meets a
            // parser, which is the rewrite 2.16 asks for.
            new TaskDecl("commit", ["git", "commit", "-m", "a note"], null,
                "git commit -m {commit-note}"),
            // A shell string, but nothing filled into it.
            new TaskDecl("clean", ["pwsh", "-Command", "rm -rf obj; rm -rf bin"], null,
                "pwsh -Command \"rm -rf obj; rm -rf bin\""),
            // A value, and a shell - but handed a FILE to run, not a
            // string to interpret.
            new TaskDecl("feature", ["pwsh", "-File", "new.ps1", "hebdenbridge"], null,
                "pwsh -File new.ps1 {feature-branch}"));

        Assert.DoesNotContain(Doctor.Examine(box.Machine, roots, "test").Findings,
            f => f.Check == "2.16");
    }

    // ---- VS Code Copilot: the agent file is the deny list ----

    static string AgentText(Fixture box, Reach reach) =>
        File.ReadAllText(box.PathOf(Places.VsCodeCopilot, reach, Purpose.Agent));

    /// <summary>VS Code has no deny list, so setup writes none into its
    /// settings. What it writes is the Fettler agent file, whose tools list
    /// names the Fettler server and leaves out the built-in read, edit and
    /// search. At the tools level the terminal stays in.</summary>
    [Fact]
    public void SetupToolsForVsCodeWritesTheAgentFileAndNoDenyList()
    {
        using var box = new Fixture();
        box.Put(Places.VsCodeCopilot, Reach.Repo, Purpose.Policy, "{}");

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.VsCodeCopilot, Reach.Repo));
        Assert.True(done.IsOk, done.Failure?.Message);

        IReadOnlyList<string> tools = AgentFile.Tools(AgentText(box, Reach.Repo))!;
        Assert.Contains(Places.ServerName, tools);
        Assert.Contains("execute", tools);
        foreach (string set in Doctor.CopilotReplaced) Assert.DoesNotContain(set, tools);

        Assert.DoesNotContain("permissions",
            File.ReadAllText(box.PathOf(Places.VsCodeCopilot, Reach.Repo, Purpose.Policy)));
        Assert.Contains(done.Value.Notes, n => n.Contains("no deny list"));
    }

    /// <summary>The documented order for this client: guardrails takes the
    /// terminal out, and tools run again afterwards does not put it back.</summary>
    [Fact]
    public void SetupGuardrailsForVsCodeDropsTheTerminalAndToolsAfterwardsLeavesItOut()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.VsCodeCopilot, Reach.Repo)).IsOk);
        Assert.True(Setup(box, new Scaffold.Options(Places.VsCodeCopilot, Reach.Repo, Level: G)).IsOk);

        IReadOnlyList<string> guarded = AgentFile.Tools(AgentText(box, Reach.Repo))!;
        Assert.DoesNotContain("execute", guarded);
        Assert.Contains(Places.ServerName, guarded);
        Assert.Contains("Guardrails are on", AgentText(box, Reach.Repo));

        Result<Scaffolding> again = Setup(box, new Scaffold.Options(Places.VsCodeCopilot, Reach.Repo));
        Assert.True(again.IsOk, again.Failure?.Message);
        Assert.DoesNotContain(again.Value.Changes,
            c => c.Path == box.PathOf(Places.VsCodeCopilot, Reach.Repo, Purpose.Agent));
        Assert.DoesNotContain("execute", AgentFile.Tools(AgentText(box, Reach.Repo))!);
    }

    /// <summary>An agent file adds a choice and changes nothing until a
    /// person picks it, so the global one is written without --deny, and
    /// VS Code's own settings are not touched.</summary>
    [Fact]
    public void SetupToolsForVsCodeGlobalWritesTheAgentFileWithoutDeny()
    {
        using var box = new Fixture();

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.VsCodeCopilot, Reach.Global));

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.True(File.Exists(box.PathOf(Places.VsCodeCopilot, Reach.Global, Purpose.Agent)));
        Assert.False(File.Exists(box.PathOf(Places.VsCodeCopilot, Reach.Global, Purpose.Policy)));
    }

    /// <summary>Written twice, nothing changes the second time.</summary>
    [Fact]
    public void TheAgentFileIsIdempotent()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.VsCodeCopilot, Reach.Repo)).IsOk);
        Result<Scaffolding> again = Setup(box, new Scaffold.Options(Places.VsCodeCopilot, Reach.Repo));

        Assert.True(again.IsOk, again.Failure?.Message);
        Assert.Empty(again.Value.Changes);
    }

    /// <summary>VS Code is installed and no Fettler agent file exists: 2.8,
    /// at the tools level, naming the project agent file and the remedy.</summary>
    [Fact]
    public void VsCodeWithoutAnAgentFileIsTold28()
    {
        using var box = new Fixture();
        box.Put(Places.VsCodeCopilot, Reach.Global, Purpose.Policy, "{}");

        Finding f = Examine(box).Findings.First(x => x.Check == "2.8"
            && x.Where == box.PathOf(Places.VsCodeCopilot, Reach.Repo, Purpose.Agent));

        Assert.Equal(Levels.ToolsName, f.Level);
        Assert.Contains("agent file", f.Message);
        Assert.Contains("fettle setup tools vscode-copilot --local", f.Message);
    }

    /// <summary>A permissions block in VS Code's settings is a key VS Code
    /// ignores, so it clears nothing.</summary>
    [Fact]
    public void ADenyListInVsCodeSettingsCountsForNothing()
    {
        using var box = new Fixture();
        box.Put(Places.VsCodeCopilot, Reach.Global, Purpose.Policy,
            "{\"permissions\":{\"deny\":" + JsonSerializer.Serialize(Doctor.Denied) + "}}");

        Assert.Contains(Examine(box).Findings, f => f.Check == "2.8"
            && f.Where == box.PathOf(Places.VsCodeCopilot, Reach.Repo, Purpose.Agent));
    }

    /// <summary>An agent file that keeps read is told 2.8; one that keeps
    /// the terminal is told 2.20 with guardrails on; one that leaves both
    /// out clears both.</summary>
    [Fact]
    public void TheAgentFileIsJudgedByWhatItsToolsListKeeps()
    {
        using var box = new Fixture();

        box.Put(Places.VsCodeCopilot, Reach.Global, Purpose.Agent, "---\ntools: ['fettler', 'read', 'execute']\n---\n");
        Diagnosis keeps = Examine(box);
        Assert.Contains(keeps.Findings, f => f.Check == "2.8" && f.Message.Contains("read"));
        Assert.Contains(keeps.Findings, f => f.Check == Doctor.ShellCheck && f.Message.Contains("execute"));

        box.Put(Places.VsCodeCopilot, Reach.Global, Purpose.Agent, AgentFile.Compose(G));
        Diagnosis clean = Examine(box);
        Assert.DoesNotContain(clean.Findings, f => f.Check is "2.8" or Doctor.ShellCheck);
    }

    /// <summary>A flow list and a block list are the two shapes a tools
    /// list takes, and a file with no list gives the agent everything.</summary>
    [Fact]
    public void TheToolsListIsReadInBothShapes()
    {
        Assert.Equal(["fettler", "web"], AgentFile.Tools("---\ndescription: x\ntools: ['fettler', \"web\"]\n---\nbody\n")!);
        Assert.Equal(["fettler", "web"], AgentFile.Tools("---\ntools:\n  - fettler\n  - 'web'\n---\n")!);
        Assert.Null(AgentFile.Tools("---\ndescription: x\n---\n"));
        Assert.Null(AgentFile.Tools("no front matter\n"));
    }

    // ---- R7.4: setup is per level ----

    /// <summary>Acceptance criterion 7, as corrected: setup tools proposes
    /// the registration, the tools-level denies and the tools paragraph,
    /// and writes no project file. It used to propose the registration
    /// alone, which reported success and left the built-ins open, so the
    /// tools it registered went unused.</summary>
    [Fact]
    public void SetupToolsProposesTheRegistrationTheDeniesAndTheBlockAndNoProjectFile()
    {
        using var box = new Fixture();

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, DryRun: true));

        Assert.True(done.IsOk, done.Failure?.Message);
        string[] paths = [.. done.Value.Changes.Select(c => c.Path)];
        Assert.Equal(3, paths.Length);
        Assert.Contains(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Servers), paths);
        Assert.Contains(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Policy), paths);
        Assert.Contains(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Instructions), paths);
        Assert.False(File.Exists(Path.Combine(box.Machine.Tree, RootsFile.FileName)));
    }

    /// <summary>Acceptance criterion 7: setup guardrails proposes the deny
    /// list, the instruction block and a starter section, and never the
    /// registration.</summary>
    [Fact]
    public void SetupGuardrailsProposesTheDenyListTheBlockAndAStarterSection()
    {
        using var box = new Fixture();

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, DryRun: true, Level: G));

        Assert.True(done.IsOk, done.Failure?.Message);
        string[] paths = [.. done.Value.Changes.Select(c => c.Path)];
        Assert.Contains(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Policy), paths);
        Assert.Contains(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Instructions), paths);
        Assert.Contains(Path.Combine(box.Machine.Tree, RootsFile.FileName), paths);
        Assert.DoesNotContain(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Servers), paths);
    }

    /// <summary>R9.6: the guardrails paragraph is written only by setup
    /// guardrails, and the tools paragraph is in every block.</summary>
    [Fact]
    public void TheInstructionBlockHoldsTheToolsAndTheGuardrailsParagraphs()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Level: G)).IsOk);

        string text = File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Instructions));
        Assert.Contains("Call `roots` first", text);
        Assert.Contains("Guardrails are on", text);
    }

    /// <summary>R9.6, the other half: the block setup tools writes holds
    /// the tools paragraph and not the guardrails one, which would claim
    /// guardrails that are not on.</summary>
    [Fact]
    public void TheToolsBlockHoldsTheToolsParagraphAlone()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo)).IsOk);

        string text = File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Instructions));
        Assert.Contains("Call `roots` first", text);
        Assert.DoesNotContain("Guardrails are on", text);
    }

    /// <summary>Re-running setup tools on a guarded project leaves the
    /// block alone. The tools level asks only that its paragraph be there,
    /// so it never strips the guardrails paragraph a higher level wrote.</summary>
    [Fact]
    public void SetupToolsOnAGuardedProjectKeepsTheGuardrailsParagraph()
    {
        using var box = new Fixture();

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Level: G)).IsOk);
        Result<Scaffolding> again = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo));
        Assert.True(again.IsOk, again.Failure?.Message);

        Assert.DoesNotContain(again.Value.Changes,
            c => c.Path == box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Instructions));
        Assert.Contains("Guardrails are on",
            File.ReadAllText(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Instructions)));
    }

    /// <summary>R7.4: a project file with no guardrails section gets one,
    /// and keeps what it already said.</summary>
    [Fact]
    public void SetupGuardrailsAddsASectionToAFileThatHasNone()
    {
        using var box = new Fixture();
        string path = Path.Combine(box.Machine.Tree, RootsFile.FileName);
        File.WriteAllText(path, """{"tools":{"generated":["out"]}}""");

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Level: G)).IsOk);

        Result<RootsFile.Found> read = RootsFile.Read(path);
        Assert.True(read.IsOk, read.Failure?.Message);
        Assert.True(read.Value.Levels.GuardrailsOn);
        Assert.Equal(["out"], read.Value.Generated);
        Assert.Equal("work", read.Value.Trees[0].Name);
    }

    /// <summary>R7.4: setup screenings writes a starter screenings section
    /// and says whether burler and the models are there.</summary>
    [Fact]
    public void SetupScreeningsWritesAStarterSectionAndChecksTheModels()
    {
        using var box = new Fixture();

        Result<Scaffolding> done = Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo,
            Level: Levels.ScreeningsName, Models: Path.Combine(box.Machine.Tree, "no-such-models")));

        Assert.True(done.IsOk, done.Failure?.Message);
        Assert.Contains(done.Value.Notes, n => n.Contains("burler"));
        Assert.Contains(done.Value.Notes, n => n.Contains("does not exist"));

        Result<RootsFile.Found> read = RootsFile.Read(Path.Combine(box.Machine.Tree, RootsFile.FileName));
        Assert.True(read.IsOk, read.Failure?.Message);
        Assert.True(read.Value.Levels.ScreeningsOn);
        Assert.Equal(Screens.Everything, read.Value.Trees[0].Screen);
    }

    // ---- R5: guardrails findings only for a project with guardrails on ----

    /// <summary>R11.5, as corrected: a tools-level project with no deny
    /// list reports 2.8, because the tools level writes a deny list and
    /// the tools go unused without it, and reports no guardrails finding.
    /// The same fixture with guardrails on reports 2.20 for the open
    /// shell as well.</summary>
    [Fact]
    public void AToolsLevelProjectReportsTwoEightAndNoGuardrailsFinding()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, """{"permissions":{}}""");

        Result<Core.Roots> tools = Core.Roots.Open([new TreeDecl("work", box.Machine.Tree, Permissions.Full)]);
        Assert.True(tools.IsOk, tools.Failure?.Message);

        Diagnosis plain = Doctor.Examine(box.Machine, tools.Value, "test");
        Assert.Contains(plain.Findings, f => f.Check == "2.8" && f.Level == Levels.ToolsName);
        Assert.DoesNotContain(plain.Findings, f => f.Level == Levels.GuardrailsName);

        Diagnosis guarded = Examine(box);
        Assert.Contains(guarded.Findings, f => f.Check == "2.8");
        Assert.Contains(guarded.Findings, f => f.Check == Doctor.ShellCheck && f.Level == Levels.GuardrailsName);
    }

    /// <summary>R11.5a, first case: denies only in settings.json bind
    /// whatever settings.local.json says, so there is no 2.8.</summary>
    [Fact]
    public void DeniesInSettingsJsonClearTwoEightWhateverTheLocalFileSays()
    {
        using var box = new Fixture();
        string denies = JsonSerializer.Serialize(Doctor.Denied);
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, $$$"""{"permissions":{"deny":{{{denies}}}}}""");
        File.WriteAllText(Path.Combine(box.Machine.Tree, ".claude", "settings.local.json"), """{"permissions":{}}""");

        Assert.DoesNotContain(Examine(box).Findings, f => f.Check is "2.8" or Doctor.ShellCheck);
    }

    /// <summary>R11.5a, second case: denies in neither file is one 2.8
    /// and one 2.20, each naming the file setup writes and its own
    /// level's remedy.</summary>
    [Fact]
    public void DeniesInNeitherFileIsOneTwoEightAndOneTwoTwentyNamingSettingsJson()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, """{"permissions":{}}""");
        File.WriteAllText(Path.Combine(box.Machine.Tree, ".claude", "settings.local.json"), """{"permissions":{}}""");

        Diagnosis d = Examine(box);

        Finding tools = Assert.Single(d.Findings, f => f.Check == "2.8");
        Assert.Equal(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Policy), tools.Where);
        Assert.Contains("fettle setup tools claude-code --local", tools.Message);

        Finding shells = Assert.Single(d.Findings, f => f.Check == Doctor.ShellCheck);
        Assert.Equal(box.PathOf(Places.ClaudeCode, Reach.Repo, Purpose.Policy), shells.Where);
        Assert.Contains("fettle setup guardrails claude-code --local", shells.Message);
    }

    /// <summary>A deny in the global settings binds every project, the way
    /// the client merges its files, so a project with no denies of its own
    /// is not told to write what the machine already has.</summary>
    [Fact]
    public void DeniesInTheGlobalFileClearTwoEightForEveryProject()
    {
        using var box = new Fixture();
        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Policy,
            "{\"permissions\":{\"deny\":" + JsonSerializer.Serialize(Doctor.ToolsDenied) + "}}");
        box.Put(Places.ClaudeCode, Reach.Repo, Purpose.Policy, """{"permissions":{}}""");

        Diagnosis d = Examine(box);

        Assert.DoesNotContain(d.Findings, f => f.Check == "2.8");
        Assert.Contains(d.Findings, f => f.Check == Doctor.ShellCheck);
    }

    /// <summary>R11.5a, third case, and acceptance criterion 14: following
    /// the remedy clears the finding.</summary>
    [Fact]
    public void SetupGuardrailsThenDoctorReportsNoTwoEight()
    {
        using var box = new Fixture();
        Directory.CreateDirectory(Path.Combine(box.Machine.Tree, ".claude"));
        File.WriteAllText(Path.Combine(box.Machine.Tree, ".claude", "settings.local.json"), """{"permissions":{}}""");

        Assert.True(Setup(box, new Scaffold.Options(Places.ClaudeCode, Reach.Repo, Level: G)).IsOk);

        Assert.DoesNotContain(Examine(box).Findings, f => f.Check is "2.8" or Doctor.ShellCheck);
    }

    /// <summary>R10.2: 2.15 is retired and never reported.</summary>
    [Fact]
    public void TheRetiredScratchFindingIsNeverReported()
    {
        using var box = new Fixture();
        Assert.DoesNotContain(Examine(box).Findings, f => f.Check == "2.15");
    }

    // ---- R12: companion servers ----

    static void RegisterCompanion(Fixture box, string root) =>
        box.Put(Places.ClaudeCode, Reach.Global, Purpose.Servers,
            """{"mcpServers":{"sparky":{"command":"sparky","args":["--root",""" + JsonSerializer.Serialize(root) + "]}}}");

    /// <summary>R11.9, second test: a companion's root is opened as a tree
    /// named for the server, read and write, and the trees say which
    /// registration it came from.</summary>
    [Fact]
    public void ACompanionsRootIsATreeNamedForTheServer()
    {
        using var box = new Fixture();
        string root = box.Dir("sparky-files");
        RegisterCompanion(box, root);

        HarnessTree tree = Assert.Single(Companions.Trees(Companions.Find(box.Machine)));

        Assert.Equal("sparky", tree.Name);
        Assert.Equal(Permissions.Full, tree.Can);
        Assert.Contains(box.PathOf(Places.ClaudeCode, Reach.Global, Purpose.Servers), tree.How);
    }

    /// <summary>R11.9, first test: a root inside a declared tree, and one
    /// inside a screened scope, are serious; one outside every tree is a
    /// fact and no finding.</summary>
    [Fact]
    public void ACompanionRootInsideAGuardedTreeIsSerious()
    {
        using var box = new Fixture();
        RegisterCompanion(box, box.Dir("tree/inside"));

        Finding f = Assert.Single(Examine(box).Findings, x => x.Check == Doctor.CompanionCheck);
        Assert.True(f.Serious);
        Assert.Contains("'work'", f.Message);
        Assert.Contains("Move the --root", f.Message);
        Assert.True(Examine(box).AnyBroken, "a companion root inside a guarded tree is exit 2");
    }

    [Fact]
    public void ACompanionRootInsideAScreenedScopeNamesTheCategories()
    {
        using var box = new Fixture();
        string records = box.Dir("tree/records");
        RegisterCompanion(box, records);

        Result<Core.Roots> opened = Core.Roots.Open(
            [new TreeDecl("work", box.Machine.Tree, Permissions.Full,
                [new ScopeDecl("records", Permissions.ReadOnly, Screened.Identifiers)])],
            null, null, null, Guarded);
        Assert.True(opened.IsOk, opened.Failure?.Message);

        Finding f = Assert.Single(Doctor.Examine(box.Machine, opened.Value, "test").Findings,
            x => x.Check == Doctor.CompanionCheck);
        Assert.Contains(Screens.Write(Screened.Identifiers), f.Message);
    }

    [Fact]
    public void ACompanionRootOutsideEveryTreeIsAFactAndNoFinding()
    {
        using var box = new Fixture();
        RegisterCompanion(box, box.Dir("elsewhere"));

        Diagnosis d = Examine(box);

        Assert.DoesNotContain(d.Findings, f => f.Check == Doctor.CompanionCheck);
        Assert.Contains(d.Facts, f => f.Level == Levels.GuardrailsName && f.Text.Contains("outside every declared tree"));
    }

    /// <summary>R12.4: a tools-level project sees the one-line fact and no
    /// finding, wherever the root is.</summary>
    [Fact]
    public void AToolsLevelProjectSeesACompanionAsAFactOnly()
    {
        using var box = new Fixture();
        RegisterCompanion(box, box.Dir("tree/inside"));

        Result<Core.Roots> tools = Core.Roots.Open([new TreeDecl("work", box.Machine.Tree, Permissions.Full)]);
        Diagnosis d = Doctor.Examine(box.Machine, tools.Value, "test");

        Assert.DoesNotContain(d.Findings, f => f.Check == Doctor.CompanionCheck);
        Assert.Contains(d.Facts, f => f.Level == Levels.ToolsName && f.Text.Contains("sparky"));
    }

    // ---- a fact is a path, not an explanation ----

    /// <summary>A doctor fact names where a folder is and nothing else.
    /// How it was found belongs to <c>roots</c>. "found as the newest
    /// session folder, because no environment variable names one" read
    /// as a warning in doctor's output, and it is not one.</summary>
    [Fact]
    public void AFactSaysWhereAFolderIsAndNotHowItWasFound()
    {
        using var box = new Fixture();

        foreach (Fact fact in Examine(box).Facts)
        {
            Assert.DoesNotContain("found as", fact.Text);
            Assert.DoesNotContain("because", fact.Text);
        }
    }
}

