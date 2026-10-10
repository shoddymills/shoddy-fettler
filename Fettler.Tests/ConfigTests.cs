using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R8.9, B.2, R2, R3 and R4: where the boundary comes from when the
/// command line does not say it, the three sections, and the machine file.
///
/// <para>These tests take the directory to search from as an argument
/// rather than setting the process's current directory, because the
/// suite runs concurrently and a current directory is process-wide.
/// Every boundary opened from a command line here is opened on
/// <see cref="Host.None"/>, so no test reads the real home or temp
/// folders.</para>
/// </summary>
public sealed class ConfigTests
{
    /// <summary>A throwaway tree, resolved the way Roots resolves one,
    /// so a comparison holds on macOS where the temp directory is
    /// itself a link.</summary>
    sealed class Tree : IDisposable
    {
        public Tree() => Root = Real(Directory.CreateTempSubdirectory("fettle-config-").FullName);

        public string Root { get; }

        public string Dir(string relative)
        {
            string full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(full);
            return full;
        }

        public string Config(string at, string json) => Put(at, RootsFile.FileName, json);

        public string Local(string at, string json) => Put(at, RootsFile.LocalFileName, json);

        public string Put(string at, string name, string json)
        {
            string dir = at.Length == 0 ? Root : Dir(at);
            string file = Path.Combine(dir, name);
            File.WriteAllText(file, json);
            return file;
        }

        public static string Real(string path) =>
            Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static string Json(string path) => System.Text.Json.JsonSerializer.Serialize(path);

    [Fact]
    public void DiscoveryWalksUpFromASubdirectoryToTheTreeItIsIn()
    {
        using var tree = new Tree();
        string file = tree.Config("", """{ "guardrails": { "trees": { "work": { "path": "." } } } }""");
        string deep = tree.Dir("src/inner/deeper");

        Result<RootsFile.Found> found = RootsFile.Discover(deep);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(file, found.Value.File);
        Assert.Equal("work", found.Value.Trees[0].Name);
        Assert.True(found.Value.Levels.GuardrailsOn);
    }

    /// <summary>R11.2: a file two folders up is found from the launch
    /// folder, and its trees are the ones it declares.</summary>
    [Fact]
    public void AFileTwoFoldersUpIsFoundAndHonoured()
    {
        using var tree = new Tree();
        string file = tree.Config("", """{ "guardrails": { "trees": { "top": { "path": "." } } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Dir("one/two"));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(file, found.Value.File);
        Assert.Equal(["top"], found.Value.Trees.Select(t => t.Name));
        Assert.Equal(tree.Root, found.Value.Trees[0].Path);
    }

    /// <summary>
    /// The reason the file exists at all. A path read against the
    /// caller's current directory names a different tree from every
    /// directory; read against the file's own directory it names one
    /// tree from everywhere.
    /// </summary>
    [Fact]
    public void PathsAreRelativeToTheFileAndNotToWhoeverIsAsking()
    {
        using var tree = new Tree();
        tree.Config("", """{ "guardrails": { "trees": { "work": { "path": "sub" } } } }""");
        string wanted = tree.Dir("sub");
        string asking = tree.Dir("src/inner");

        Result<RootsFile.Found> found = RootsFile.Discover(asking);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(wanted, found.Value.Trees[0].Path);
    }

    [Fact]
    public void TheNEARESTConfigurationWinsRatherThanTheTopmost()
    {
        using var tree = new Tree();
        tree.Config("", """{ "guardrails": { "trees": { "outer": { "path": "." } } } }""");
        string inner = tree.Config("nested", """{ "guardrails": { "trees": { "inner": { "path": "." } } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Dir("nested/further"));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(inner, found.Value.File);
        Assert.Equal("inner", found.Value.Trees[0].Name);
    }

    /// <summary>
    /// The one that matters. Falling back to the tools level on a broken
    /// configuration would WIDEN the boundary at the moment somebody had
    /// tried to state one. R11.2.
    /// </summary>
    [Fact]
    public void AMalformedConfigurationIsRefusedAndNeverFallenBackFrom()
    {
        using var tree = new Tree();
        tree.Config("", "{ this is not json");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Dir("one/two"));

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
        Assert.Contains("JSON", found.Failure!.Message);
    }

    [Theory]
    [InlineData("""{ "guardrails": { "trees": { } } }""")]
    [InlineData("""{ "guardrails": { } }""")]
    [InlineData("""{ "nothing": true }""")]
    [InlineData("""{ "guardrails": { "trees": { "work": 3 } } }""")]
    [InlineData("""{ "guardrails": { "trees": { "work": { } } } }""")]
    [InlineData("""{ "guardrails": { "trees": { "work": { "path": "" } } } }""")]
    [InlineData("""{ "guardrails": { "trees": { "work": { "path": ".", "can": "read" } } } }""")]
    [InlineData("""{ "guardrails": { "trees": { "work": { "path": ".", "can": ["delte"] } } } }""")]
    [InlineData("""{ "guardrails": { "trees": { "work": { "path": ".", "scopes": [] } } } }""")]
    [InlineData("""{ "guardrails": { "trees": { "work": { "path": ".", "scopes": { "sub": { } } } } } }""")]
    [InlineData("""{ "guardrails": { "trees": { "work": { "path": "." } }, "trees2": { } } }""")]
    [InlineData("""{ "guardrails": [] }""")]
    [InlineData("""[ "guardrails" ]""")]
    public void AConfigurationThatDeclaresNoUsableTreeIsRefused(string json)
    {
        using var tree = new Tree();
        tree.Config("", json);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
    }

    /// <summary>R3.1: an unknown top-level key refuses the load, naming
    /// the three sections.</summary>
    [Fact]
    public void AnUnknownTopLevelKeyNamesTheThreeSections()
    {
        using var tree = new Tree();
        tree.Config("", """{ "guardrail": { } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Contains("tools, guardrails, screenings", found.Failure!.Message);
    }

    /// <summary>
    /// B.2: the oldest shape meant something different - a root was
    /// readable and writable in full - so reading it as the new one would
    /// GRANT more than the file says.
    /// </summary>
    [Fact]
    public void TheOldRootsShapeIsRefusedWithTheMigrationNamed()
    {
        using var tree = new Tree();
        tree.Config("", """{ "roots": { "repo": "." } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
        Assert.Contains("\"guardrails\"", found.Failure.Message);
        Assert.Contains("execute is never a default", found.Failure.Message);
    }

    /// <summary>
    /// R3.4 and acceptance criterion 5: the flat shape of the last release
    /// is refused, and the message holds the file's own entries under the
    /// new sections, screens moved out of their trees.
    /// </summary>
    [Fact]
    public void TheFlatShapeIsRefusedWithItsOwnEntriesRewritten()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "trees": {
                "work": { "path": ".", "screen": ["identifiers"],
                          "scopes": { "records": { "can": ["list", "read"], "screen": true } } }
              },
              "tasks": { "build": { "run": "pwsh -File build.ps1" } },
              "models": "screening-models"
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);

        string message = found.Failure.Message;
        Assert.Contains("\"guardrails\":{\"trees\":{\"work\":{\"path\":\".\"", message);
        Assert.Contains("\"tasks\":{\"build\":{\"run\":\"pwsh -File build.ps1\"}}", message);
        Assert.Contains("\"screenings\":{\"models\":\"screening-models\"", message);
        Assert.Contains("\"work\":[\"identifiers\"]", message);
        Assert.Contains("\"work/records\":true", message);
    }

    /// <summary>A screen inside a tree is the old place for it, and says
    /// where it goes now.</summary>
    [Fact]
    public void AScreenInsideATreeIsRefusedNamingTheScreeningsSection()
    {
        using var tree = new Tree();
        tree.Config("", """{ "guardrails": { "trees": { "work": { "path": ".", "screen": true } } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Contains("\"screenings\"", found.Failure!.Message);
    }

    [Fact]
    public void ATreeWrittenAsABarePathSaysWhatToWriteInstead()
    {
        using var tree = new Tree();
        tree.Config("", """{ "guardrails": { "trees": { "work": "." } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Contains("\"path\"", found.Failure!.Message);
    }

    // ---- R2: the tools level needs no file ----

    /// <summary>R2.1: with no configuration found, the launch folder is the
    /// one tree, called work, granted everything but execute, and only the
    /// tools level is on.</summary>
    [Fact]
    public void NoConfigurationAnywhereIsTheToolsLevelOnTheLaunchFolder()
    {
        using var tree = new Tree();
        string launch = tree.Dir("src/inner");

        Result<RootsFile.Found> found = RootsFile.Discover(launch);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Null(found.Value.File);
        Assert.Equal(["work"], found.Value.Trees.Select(t => t.Name));
        Assert.Equal(launch, found.Value.Trees[0].Path);
        Assert.Equal(Permissions.Full, found.Value.Trees[0].Can);
        Assert.False(found.Value.Trees[0].Can.HasFlag(Permission.Execute));
        Assert.False(found.Value.Levels.GuardrailsOn);
        Assert.False(found.Value.Levels.ScreeningsOn);
        Assert.Empty(found.Value.Tasks);
    }

    /// <summary>R2.7: a file with only a tools section leaves guardrails off
    /// and sets the generated-folder names.</summary>
    [Fact]
    public void AToolsOnlyFileLeavesGuardrailsOffAndSetsTheGeneratedNames()
    {
        using var tree = new Tree();
        string file = tree.Config("", """{ "tools": { "generated": ["out", "dist"] } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(file, found.Value.File);
        Assert.Equal(["work"], found.Value.Trees.Select(t => t.Name));
        Assert.Equal(["out", "dist"], found.Value.Generated);
        Assert.False(found.Value.Levels.GuardrailsOn);
        Assert.Equal([file], found.Value.Levels.Tools);
    }

    /// <summary>R3.3: at the tools level a screen may name work, which is
    /// the launch folder.</summary>
    [Fact]
    public void AScreenOnWorkAtTheToolsLevelLandsOnTheLaunchFolder()
    {
        using var tree = new Tree();
        tree.Config("", """{ "screenings": { "screen": { "work": ["identifiers"] } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.False(found.Value.Levels.GuardrailsOn);
        Assert.True(found.Value.Levels.ScreeningsOn);
        Assert.Equal(Screened.Identifiers, found.Value.Trees[0].Screen);
    }

    /// <summary>R3.3: a screen naming no tree or no scope is refused, because
    /// it would screen nothing while saying it screens something.</summary>
    [Theory]
    [InlineData("""{ "screenings": { "screen": { "elsewhere": true } } }""")]
    [InlineData("""{ "screenings": { "screen": { "work/records": true } } }""")]
    [InlineData("""{ "guardrails": { "trees": { "work": { "path": "." } } }, "screenings": { "screen": { "work/nope": true } } }""")]
    public void AScreenThatNamesNothingIsRefused(string json)
    {
        using var tree = new Tree();
        tree.Config("", json);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
    }

    [Fact]
    public void AScreenOnAScopeLandsOnThatScope()
    {
        using var tree = new Tree();
        tree.Dir("records");
        tree.Config("", """
            {
              "guardrails": { "trees": { "work": { "path": ".", "scopes": { "records": { "can": ["list", "read"] } } } } },
              "screenings": { "screen": { "work/records": ["identifiers"] } }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(Screened.None, found.Value.Trees[0].Screen);
        Assert.Equal(Screened.Identifiers, found.Value.Trees[0].Scopes![0].Screen);
    }

    [Fact]
    public void AnAbsolutePathInTheConfigurationIsLeftAbsolute()
    {
        using var tree = new Tree();
        using var other = new Tree();
        tree.Config("", $$"""
            { "guardrails": { "trees": { "far": { "path": {{Json(other.Root)}} } } } }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(other.Root, found.Value.Trees[0].Path);
    }

    /// <summary>The client's folder names are fettle's, so a declared tree
    /// may not take one.</summary>
    [Theory]
    [InlineData("scratch")]
    [InlineData("tool-results")]
    [InlineData("memory")]
    public void ADeclaredTreeMayNotTakeAClientFolderName(string name)
    {
        using var tree = new Tree();
        tree.Config("", $$"""{ "guardrails": { "trees": { "{{name}}": { "path": "." } } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Contains(name, found.Failure!.Message);
    }

    // ---- the defaults, which are the safe ones ----

    /// <summary>
    /// The tree the configuration sits in gets everything but execute;
    /// every other tree gets list read. Deciding by WHERE the tree is
    /// rather than by how the path was spelled means "./" and an absolute
    /// path to the same folder mean the same thing.
    /// </summary>
    [Fact]
    public void TheTreeTheConfigurationSitsInIsWritableAndEveryOtherOneIsNot()
    {
        using var tree = new Tree();
        using var other = new Tree();
        tree.Config("", $$"""
            {
              "guardrails": {
                "trees": {
                  "work": { "path": "." },
                  "here": { "path": {{Json(tree.Root)}} },
                  "far":  { "path": {{Json(other.Root)}} }
                }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);
        Assert.True(found.IsOk, found.Failure?.Message);

        Assert.Equal(Permissions.Full, found.Value.Trees[0].Can);
        Assert.Equal(Permissions.Full, found.Value.Trees[1].Can);
        Assert.Equal(Permissions.ReadOnly, found.Value.Trees[2].Can);
    }

    [Fact]
    public void ExecuteIsNeverADefaultAnywhere()
    {
        Assert.False(Permissions.Full.HasFlag(Permission.Execute));
        Assert.False(Permissions.ReadOnly.HasFlag(Permission.Execute));
    }

    [Fact]
    public void ScopesAreReadAndMayGrantNothingAtAll()
    {
        using var tree = new Tree();
        tree.Dir("requirements/cancelled");
        tree.Config("", """
            {
              "guardrails": {
                "trees": {
                  "work": {
                    "path": ".",
                    "can": ["list", "read"],
                    "scopes": {
                      "requirements":           { "can": ["list", "read", "create", "update", "rename"] },
                      "requirements/cancelled": { "can": [] }
                    }
                  }
                }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);
        Assert.True(found.IsOk, found.Failure?.Message);

        TreeDecl work = found.Value.Trees[0];
        Assert.Equal(Permissions.ReadOnly, work.Can);
        Assert.Equal(2, work.Scopes!.Count);
        Assert.Equal(Permission.None, work.Scopes[1].Can);
        Assert.True(work.Scopes[0].Can.HasFlag(Permission.Rename));
        Assert.False(work.Scopes[0].Can.HasFlag(Permission.Delete));
    }

    [Fact]
    public void AScopeOutsideItsOwnTreeIsAConfigurationMistake()
    {
        using var tree = new Tree();
        tree.Config("", """
            { "guardrails": { "trees": { "work": { "path": ".", "scopes": { "../elsewhere": { "can": [] } } } } } }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);
        Assert.True(found.IsOk, found.Failure?.Message);

        Result<Roots> opened = Roots.Open(found.Value.Trees);
        Assert.False(opened.IsOk);
        Assert.Contains("not inside", opened.Failure!.Message);
    }

    // ---- the personal overlay (B.12, R3.5) ----

    [Fact]
    public void TheLocalOverlayAddsTreesAndReplacesOnesOfTheSameName()
    {
        using var tree = new Tree();
        using var mine = new Tree();
        tree.Config("", """{ "guardrails": { "trees": { "work": { "path": ".", "can": ["list"] } } } }""");
        tree.Local("", $$"""
            {
              "guardrails": {
                "trees": {
                  "work": { "path": ".", "can": ["list", "read"] },
                  "mine": { "path": {{Json(mine.Root)}} }
                }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["work", "mine"], found.Value.Trees.Select(t => t.Name));
        Assert.Equal(Permissions.ReadOnly, found.Value.Trees[0].Can);
        Assert.Contains(RootsFile.LocalFileName, found.Value.Origin);
    }

    /// <summary>R3.5: the overlay may hold a section the checked-in file
    /// does not, and that section turns its level on.</summary>
    [Fact]
    public void TheOverlayMayTurnALevelOnByItself()
    {
        using var tree = new Tree();
        tree.Config("", """{ "guardrails": { "trees": { "work": { "path": "." } } } }""");
        string local = tree.Local("", """{ "screenings": { "screen": { "work": true } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal([local], found.Value.Levels.Screenings);
        Assert.Equal(Screens.Everything, found.Value.Trees[0].Screen);
    }

    [Fact]
    public void ABrokenOverlayIsRefusedRatherThanIgnored()
    {
        using var tree = new Tree();
        tree.Config("", """{ "guardrails": { "trees": { "work": { "path": "." } } } }""");
        tree.Local("", "{ not json");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
    }

    /// <summary>A lone overlay is read, not ignored. Ignoring it would
    /// leave its guardrails off without a word.</summary>
    [Fact]
    public void ALoneOverlayIsReadAsTheProjectFile()
    {
        using var tree = new Tree();
        string local = tree.Local("", """{ "guardrails": { "trees": { "mine": { "path": "." } } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Dir("src"));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(local, found.Value.File);
        Assert.Equal(["mine"], found.Value.Trees.Select(t => t.Name));
    }

    // ---- R4: the machine file ----

    /// <summary>R11.3: the machine file supplies tools and screenings, and
    /// the project file wins over it.</summary>
    [Fact]
    public void TheMachineFileSuppliesToolsAndScreenings()
    {
        using var tree = new Tree();
        using var machine = new Tree();
        string machineFile = machine.Put("", Host.MachineFileName, """
            { "tools": { "generated": ["bin"] }, "screenings": { "models": "models" } }
            """);
        tree.Config("", """{ "guardrails": { "trees": { "work": { "path": "." } } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root, machineFile);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["bin"], found.Value.Generated);
        Assert.Equal(Path.Combine(machine.Root, "models"), found.Value.Models);
        Assert.Equal(machineFile, found.Value.Machine);
        Assert.Contains(machineFile, found.Value.Levels.Screenings);
        Assert.Contains(machineFile, found.Value.Origin);
    }

    [Fact]
    public void TheProjectFileWinsOverTheMachineFile()
    {
        using var tree = new Tree();
        using var machine = new Tree();
        string machineFile = machine.Put("", Host.MachineFileName, """{ "tools": { "generated": ["bin"] } }""");
        tree.Config("", """{ "tools": { "generated": ["out"] } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root, machineFile);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["out"], found.Value.Generated);
    }

    /// <summary>R4.3 and R11.3: a guardrails section in the machine file
    /// refuses the load, and says why.</summary>
    [Fact]
    public void AGuardrailsSectionInTheMachineFileRefusesTheLoad()
    {
        using var tree = new Tree();
        using var machine = new Tree();
        string machineFile = machine.Put("", Host.MachineFileName,
            """{ "guardrails": { "trees": { "everywhere": { "path": "/" } } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root, machineFile);

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
        Assert.Contains("every project", found.Failure.Message);
    }

    /// <summary>The machine file is a switch between levels, so fettle
    /// refuses to write it like the other governed files.</summary>
    [Fact]
    public void TheMachineFileIsGoverned()
    {
        Assert.True(RuleFiles.Governs(Path.Combine("home", Host.MachineFolder, Host.MachineFileName)));
        Assert.False(RuleFiles.Governs(Path.Combine("home", "other", Host.MachineFileName)));
    }

    // ---- where a permission may come from ----

    /// <summary>
    /// R8.9: --root REPLACES the configuration. Merging would leave a
    /// caller unable to narrow the boundary.
    /// </summary>
    [Fact]
    public void TheCommandLineReplacesTheConfigurationRatherThanAddingToIt()
    {
        using var tree = new Tree();
        string file = tree.Config("", """{ "guardrails": { "trees": { "work": { "path": "." } } } }""");
        string only = tree.Dir("sub");

        Arguments args = Arguments.Parse(["roots", "--config", file, "--root", $"just={only}"]);
        Result<Roots> opened = args.DeclaredRoots(Host.None);

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal(["just"], opened.Value.Names);
    }

    /// <summary>
    /// B.4. A command line is not a deliberate, reviewable act inside the
    /// tree it governs. It may open a tree for reading and it may not do
    /// more, whatever combination of flags is offered.
    /// </summary>
    [Fact]
    public void ARootOnTheCommandLineGrantsListAndReadAndNothingElse()
    {
        using var tree = new Tree();

        foreach (string[] argv in new[]
        {
            new[] { "roots", "--root", tree.Root },
            ["roots", "--root", $"named={tree.Root}"],
            ["roots", "--root", tree.Root, "--root", $"second={tree.Root}"],
            ["roots", "--no-config", "--root", tree.Root],
        })
        {
            Result<Roots> opened = Arguments.Parse(argv).DeclaredRoots(Host.None);
            Assert.True(opened.IsOk, opened.Failure?.Message);

            foreach (string name in opened.Value.Names)
                Assert.Equal(Permissions.ReadOnly, opened.Value.GrantOf(name).Can);
        }
    }

    [Fact]
    public void ConfigNamedByFlagIsRead_AndTheRootsSayWhichFileSaidSo()
    {
        using var tree = new Tree();
        string file = tree.Config("", """
            { "guardrails": { "trees": { "work": { "path": "." }, "sub": { "path": "sub" } } } }
            """);
        tree.Dir("sub");

        Arguments args = Arguments.Parse(["roots", "--config", file]);
        Result<Roots> opened = args.DeclaredRoots(Host.None);

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal(["work", "sub"], opened.Value.Names);
        Assert.Equal(file, opened.Value.Origin);
    }

    [Fact]
    public void AConfigNamedByFlagThatIsNotThereIsRefusedRatherThanIgnored()
    {
        using var tree = new Tree();

        Arguments args = Arguments.Parse(
            ["roots", "--config", Path.Combine(tree.Root, "absent.json")]);
        Result<Roots> opened = args.DeclaredRoots(Host.None);

        Assert.False(opened.IsOk);
        Assert.Equal(Outcome.NotFound, opened.Failure!.Outcome);
    }

    /// <summary>R2.6: --no-config stops the walk, and the launch folder is
    /// the tree, writable, with no execute.</summary>
    [Fact]
    public void NoConfigIsTheToolsLevelOnTheLaunchFolder()
    {
        using var tree = new Tree();
        tree.Config("", """{ "guardrails": { "trees": { "declared": { "path": "." } } } }""");

        Result<Boundary> opened = Arguments.Parse(["roots", "--no-config"]).DeclaredBoundary(Host.None, tree.Root);

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal(["work"], opened.Value.Roots.Names);
        Assert.Equal(tree.Root, opened.Value.Roots.PathOf("work"));
        Assert.Equal(Permissions.Full, opened.Value.Roots.GrantOf("work").Can);
        Assert.False(opened.Value.Roots.Levels.GuardrailsOn);
    }

    /// <summary>R2.2: at the tools level a path that climbs out of the
    /// launch folder is refused as outside, the same as always.</summary>
    [Fact]
    public void AtTheToolsLevelAPathOutOfTheLaunchFolderIsOutside()
    {
        using var tree = new Tree();
        string launch = tree.Dir("inner");

        Result<Boundary> opened = Arguments.Parse(["roots"]).DeclaredBoundary(Host.None, launch);
        Assert.True(opened.IsOk, opened.Failure?.Message);

        Result<ContainedPath> climbed = opened.Value.Roots.Resolve("../x.txt", Permission.Read);
        Assert.False(climbed.IsOk);
        Assert.Equal(Outcome.OutsideRoot, climbed.Failure!.Outcome);
    }

    // ---- R7: tasks, declared under guardrails ----

    /// <summary>
    /// The command line is one string, split once. The awkward argument is
    /// the point: a space survives because quotes group it, and a double
    /// quote survives because a doubled one is a literal.
    /// </summary>
    [Fact]
    public void TasksAreReadFromTheGuardrailsSection()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "guardrails": {
                "trees": { "work": { "path": "." } },
                "tasks": {
                  "build": { "run": "pwsh -File build.ps1" },
                  "gate":  { "run": "node driver.mjs \"a \"\"quoted\"\" thing\"", "cwd": "src" }
                }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(2, found.Value.Tasks.Count);
        Assert.Equal("build", found.Value.Tasks[0].Name);
        Assert.Equal(["pwsh", "-File", "build.ps1"], found.Value.Tasks[0].Command);
        Assert.Null(found.Value.Tasks[0].WorkingDirectory);
        Assert.Equal("""a "quoted" thing""", found.Value.Tasks[1].Command[2]);
        Assert.Equal("src", found.Value.Tasks[1].WorkingDirectory);
    }

    /// <summary>
    /// The whole grammar, stated as assertions: whitespace separates, a
    /// quoted span is one argument, a doubled quote inside one is a literal
    /// quote, and <b>a backslash is an ordinary character</b>.
    /// </summary>
    [Theory]
    [InlineData("node driver.mjs", new[] { "node", "driver.mjs" })]
    [InlineData("  node   driver.mjs  ", new[] { "node", "driver.mjs" })]
    [InlineData(@"pwsh -File C:\tools\build.ps1", new[] { "pwsh", "-File", @"C:\tools\build.ps1" })]
    [InlineData("pwsh -File \"C:\\Program Files\\b.ps1\"", new[] { "pwsh", "-File", @"C:\Program Files\b.ps1" })]
    [InlineData("say \"\"\"quoted\"\"\"", new[] { "say", "\"quoted\"" })]
    [InlineData("a \"\" b", new[] { "a", "", "b" })]
    [InlineData("git commit -m \"one two\"", new[] { "git", "commit", "-m", "one two" })]
    public void TheCommandGrammarIsWhitespaceAndDoubleQuotesAndNothingElse(string line, string[] expected)
    {
        Result<IReadOnlyList<string>> split = Tasks.Split(line);

        Assert.True(split.IsOk, split.Failure?.Message);
        Assert.Equal(expected, split.Value);
    }

    [Fact]
    public void AQuoteThatIsNeverClosedIsRefused()
    {
        Result<IReadOnlyList<string>> split = Tasks.Split("pwsh -File \"build.ps1");

        Assert.False(split.IsOk, "an unclosed quote must be refused, not guessed at");
        Assert.Equal(Outcome.Invalid, split.Failure!.Outcome);
    }

    /// <summary>
    /// A relative declared path travels, so it is held to forward slashes.
    /// </summary>
    [Fact]
    public void ARelativeDeclaredPathMustUseForwardSlashes()
    {
        using var tree = new Tree();
        tree.Config("", """{ "guardrails": { "trees": { "work": { "path": "..\\sibling" } } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk, "a relative path with a backslash does not travel");
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
        Assert.Contains("backslash", found.Failure.Message);
    }

    /// <summary>
    /// The overlay carries tasks by the same rule it carries trees: a name
    /// in both is the overlay's entirely, a name only it has is added.
    /// </summary>
    [Fact]
    public void TheOverlayReplacesATaskByNameAndAddsItsOwn()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "guardrails": {
                "trees": { "work": { "path": "." } },
                "tasks": { "build": { "run": "pwsh -File build.ps1" } }
              }
            }
            """);
        tree.Local("", """
            {
              "guardrails": {
                "tasks": {
                  "build": { "run": "pwsh -File build.ps1 --fast" },
                  "mine":  { "run": "node scratch.mjs" }
                }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(2, found.Value.Tasks.Count);
        Assert.Equal(["pwsh", "-File", "build.ps1", "--fast"], found.Value.Tasks[0].Command);
        Assert.Equal("mine", found.Value.Tasks[1].Name);
    }

    /// <summary>
    /// A command line declares no file, and a file is the only thing that
    /// can say what may run.
    /// </summary>
    [Fact]
    public void ARootOnTheCommandLineDeclaresNoTasks()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "guardrails": {
                "trees": { "work": { "path": "." } },
                "tasks": { "build": { "run": "pwsh" } }
              }
            }
            """);

        Arguments args = Arguments.Parse(["roots", "--root", tree.Root]);
        Result<Roots> opened = args.DeclaredRoots(Host.None);

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Empty(opened.Value.Tasks);
    }

    /// <summary>
    /// A task that is not shaped like a task is refused with the file
    /// named, never skipped.
    /// </summary>
    [Theory]
    [InlineData("""{"guardrails":{"trees":{"w1":{"path":"."}},"tasks":{"build":"pwsh"}}}""")]
    [InlineData("""{"guardrails":{"trees":{"w1":{"path":"."}},"tasks":{"build":{"run":["pwsh"]}}}}""")]
    [InlineData("""{"guardrails":{"trees":{"w1":{"path":"."}},"tasks":{"build":{"run":""}}}}""")]
    [InlineData("""{"guardrails":{"trees":{"w1":{"path":"."}},"tasks":{"build":{"run":7}}}}""")]
    [InlineData("""{"guardrails":{"trees":{"w1":{"path":"."}},"tasks":{"build":{"run":"pwsh \"x"}}}}""")]
    [InlineData("""{"guardrails":{"trees":{"w1":{"path":"."}},"tasks":{"build":{"run":"pwsh","cwd":""}}}}""")]
    [InlineData("""{"guardrails":{"trees":{"w1":{"path":"."}},"tasks":{"build":{"run":"pwsh","cwd":"..\\up"}}}}""")]
    public void AMalformedTaskIsRefusedRatherThanSkipped(string json)
    {
        using var tree = new Tree();
        tree.Config("", json);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk, "a malformed task must be refused, not skipped");
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
        Assert.Contains("build", found.Failure.Message);
    }

    // ---- replacements: a value the FILE supplies, never the caller ----

    /// <summary>
    /// The whole feature in one assertion. <c>run</c> still takes a name
    /// and a timeout and nothing else, so the value did not come from the
    /// caller.
    /// </summary>
    [Fact]
    public void AReplacementFillsAPlaceholderInATaskCommand()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "guardrails": {
                "replacements": { "feature-branch": "hebdenbridge" },
                "trees": { "work": { "path": "." } },
                "tasks": { "feature": { "run": "pwsh -File new.ps1 {feature-branch}" } }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["pwsh", "-File", "new.ps1", "hebdenbridge"], found.Value.Tasks[0].Command);
    }

    /// <summary>
    /// Why filling happens AFTER the split and never before it: filled
    /// into a WORD, every one of these is one argument holding precisely
    /// those characters.
    /// </summary>
    [Theory]
    [InlineData("two words")]
    [InlineData("it's \"fine\"")]
    [InlineData("a; rm -rf / && echo")]
    [InlineData("{another-name}")]
    [InlineData("")]
    public void AFilledValueIsOneArgumentWhateverIsInIt(string value)
    {
        using var tree = new Tree();
        tree.Config("", $$"""
            {
              "guardrails": {
                "replacements": { "note": {{Json(value)}} },
                "trees": { "work": { "path": "." } },
                "tasks": { "commit": { "run": "git commit -m {note}" } }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["git", "commit", "-m", value], found.Value.Tasks[0].Command);
    }

    [Fact]
    public void APlaceholderMayBePartOfAWord()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "guardrails": {
                "replacements": { "version": "1.2.3" },
                "trees": { "work": { "path": "." } },
                "tasks": { "tag": { "run": "git tag v{version} -m rel-{version}" } }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["git", "tag", "v1.2.3", "-m", "rel-1.2.3"], found.Value.Tasks[0].Command);
    }

    /// <summary>
    /// A brace on a command line was ordinary, and the braces people
    /// actually write hold spaces, dollars, pipes, colons or bare digits -
    /// none of which can begin a name.
    /// </summary>
    [Theory]
    [InlineData("pwsh -Command \"gci | % { $_.Name }\"")]
    [InlineData("jq \"{a:.b}\"")]
    [InlineData("log \"{0} of {1}\"")]
    [InlineData("node -e \"console.log({})\"")]
    [InlineData("awk \"{print $1}\"")]
    public void ABraceThatIsNotANameIsOrdinaryText(string run)
    {
        using var tree = new Tree();
        tree.Config("", $$"""
            {
              "guardrails": {
                "trees": { "work": { "path": "." } },
                "tasks": { "t": { "run": {{Json(run)}} } }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(Tasks.Split(run).Value, found.Value.Tasks[0].Command);
    }

    /// <summary>A doubled brace is the escape for a command line that
    /// must carry the text of a placeholder itself.</summary>
    [Fact]
    public void ADoubledBraceIsALiteralBraceAndNotAPlaceholder()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "guardrails": {
                "trees": { "work": { "path": "." } },
                "tasks": { "echo": { "run": "echo {{feature-branch}" } }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["echo", "{feature-branch}"], found.Value.Tasks[0].Command);
    }

    /// <summary>
    /// An unresolved name refuses the read, names itself, and says what
    /// IS declared.
    /// </summary>
    [Fact]
    public void AnUnresolvedPlaceholderRefusesTheReadAndNamesItself()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "guardrails": {
                "replacements": { "release-tag": "v1.0.0" },
                "trees": { "work": { "path": "." } },
                "tasks": { "feature": { "run": "pwsh -File new.ps1 {feature-branch}" } }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk, "an unresolved placeholder must refuse, not pass through");
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
        Assert.Contains("feature-branch", found.Failure.Message);
        Assert.Contains("feature", found.Failure.Message);
        Assert.Contains("release-tag", found.Failure.Message);
    }

    /// <summary>A value is parked between uses rather than deleted and
    /// retyped, so declaring one nothing refers to is ordinary.</summary>
    [Fact]
    public void ADeclaredButUnreferencedReplacementIsNotAnError()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "guardrails": {
                "replacements": { "unused": "parked" },
                "trees": { "work": { "path": "." } },
                "tasks": { "build": { "run": "pwsh -File build.ps1" } }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["pwsh", "-File", "build.ps1"], found.Value.Tasks[0].Command);
    }

    /// <summary>
    /// Why filling waits for the merge: the checked-in file declares the
    /// task and the gitignored overlay beside it carries today's value.
    /// </summary>
    [Fact]
    public void TheOverlaySuppliesTheValueForATaskTheCheckedInFileDeclares()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "guardrails": {
                "trees": { "work": { "path": "." } },
                "tasks": { "feature": { "run": "pwsh -File new.ps1 {feature-branch}" } }
              }
            }
            """);
        tree.Local("", """
            { "guardrails": { "replacements": { "feature-branch": "hebdenbridge" } } }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["pwsh", "-File", "new.ps1", "hebdenbridge"], found.Value.Tasks[0].Command);
    }

    /// <summary>The overlay's replacements over the checked-in ones, by
    /// the same rule the trees and the tasks already use.</summary>
    [Fact]
    public void TheOverlayReplacesAReplacementByNameAndAddsItsOwn()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "guardrails": {
                "replacements": { "branch": "placeholder", "shared": "kept" },
                "trees": { "work": { "path": "." } },
                "tasks": { "t": { "run": "x {branch} {shared} {mine}" } }
              }
            }
            """);
        tree.Local("", """
            { "guardrails": { "replacements": { "branch": "hebdenbridge", "mine": "extra" } } }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["x", "hebdenbridge", "kept", "extra"], found.Value.Tasks[0].Command);
    }

    /// <summary>
    /// Both spellings survive. <c>Command</c> is what would be launched;
    /// <c>Display</c> is the line as declared, braces and all.
    /// </summary>
    [Fact]
    public void TheDeclaredLineKeepsItsPlaceholdersWhileTheCommandIsFilled()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "guardrails": {
                "replacements": { "feature-branch": "hebdenbridge" },
                "trees": { "work": { "path": "." } },
                "tasks": { "feature": { "run": "pwsh -File new.ps1 {feature-branch}" } }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Contains("{feature-branch}", found.Value.Tasks[0].Display);
        Assert.Contains("hebdenbridge", found.Value.Tasks[0].Command);
    }

    /// <summary>
    /// Passing a placeholder through is the only outcome that SUCCEEDS at
    /// the wrong thing, so a declared one never reaches the argument list.
    /// </summary>
    [Fact]
    public void ADeclaredPlaceholderNeverReachesTheArgumentList()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "guardrails": {
                "replacements": { "feature-branch": "hebdenbridge" },
                "trees": { "work": { "path": "." } },
                "tasks": { "feature": { "run": "pwsh -File new.ps1 {feature-branch}" } }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        foreach (string word in found.Value.Tasks[0].Command)
            Assert.False(word.Contains("{feature-branch}", StringComparison.Ordinal),
                $"a placeholder reached the argument list as '{word}'");
    }

    /// <summary>A replacement that is not shaped like one is refused with
    /// the file named, never skipped.</summary>
    [Theory]
    [InlineData("""{"guardrails":{"trees":{"w1":{"path":"."}},"replacements":[]}}""")]
    [InlineData("""{"guardrails":{"trees":{"w1":{"path":"."}},"replacements":{"a":7}}}""")]
    [InlineData("""{"guardrails":{"trees":{"w1":{"path":"."}},"replacements":{"a":null}}}""")]
    [InlineData("""{"guardrails":{"trees":{"w1":{"path":"."}},"replacements":{"a b":"x"}}}""")]
    [InlineData("""{"guardrails":{"trees":{"w1":{"path":"."}},"replacements":{"":"x"}}}""")]
    [InlineData("""{"guardrails":{"trees":{"w1":{"path":"."}},"replacements":{"0":"x"}}}""")]
    public void AMalformedReplacementIsRefused(string json)
    {
        using var tree = new Tree();
        tree.Config("", json);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk, "a malformed replacement must be refused, not skipped");
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
    }

    // ---- the screening models directory reaches the opened boundary ----

    /// <summary>
    /// The defect this exists to stop coming back: reading "models" out
    /// of the file and then not passing it on when the boundary is
    /// opened. A null Models makes the first tier the whole screen, so a
    /// tree screening <c>legal</c> would serve everything.
    /// </summary>
    [Fact]
    public void TheModelsDirectoryReachesTheBoundaryTheCommandLineOpens()
    {
        using var tree = new Tree();
        string file = tree.Config("", """
            {
              "guardrails": { "trees": { "work": { "path": "." } } },
              "screenings": { "models": "screening-models", "screen": { "work": ["legal"] } }
            }
            """);

        Result<Boundary> opened = Arguments.Parse(["read", "--config", file]).DeclaredBoundary(Host.None, tree.Root);

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal(Path.Combine(tree.Root, "screening-models"), opened.Value.Roots.Models);
    }

    /// <summary>And it is resolved against the FILE, like every other
    /// declared path.</summary>
    [Fact]
    public void TheModelsDirectoryIsResolvedAgainstTheFile()
    {
        using var tree = new Tree();
        tree.Config("nested", """
            { "guardrails": { "trees": { "work": { "path": "." } } }, "screenings": { "models": "../shared-models" } }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Dir("nested/deeper"));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(Path.Combine(tree.Root, "shared-models"), found.Value.Models);
    }
}
