using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R8.9 and B.2: where the boundary comes from when the command line does
/// not say it, and what it grants when it does.
///
/// <para>These tests take the directory to search from as an argument
/// rather than setting the process's current directory, because the
/// suite runs concurrently and a current directory is process-wide -
/// one test changing it would move the ground under every other test
/// at once, intermittently.</para>
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

        string Put(string at, string name, string json)
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

    [Fact]
    public void DiscoveryWalksUpFromASubdirectoryToTheTreeItIsIn()
    {
        using var tree = new Tree();
        string file = tree.Config("", """{ "trees": { "work": { "path": "." } } }""");
        string deep = tree.Dir("src/inner/deeper");

        Result<RootsFile.Found> found = RootsFile.Discover(deep);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(file, found.Value.File);
        Assert.Equal("work", found.Value.Trees[0].Name);
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
        tree.Config("", """{ "trees": { "work": { "path": "sub" } } }""");
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
        tree.Config("", """{ "trees": { "outer": { "path": "." } } }""");
        string inner = tree.Config("nested", """{ "trees": { "inner": { "path": "." } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Dir("nested/further"));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(inner, found.Value.File);
        Assert.Equal("inner", found.Value.Trees[0].Name);
    }

    /// <summary>
    /// The one that matters. Falling back to the current directory on a
    /// broken configuration would usually WIDEN the boundary, silently,
    /// because a caller's cwd tends to sit above the tree they meant.
    /// </summary>
    [Fact]
    public void AMalformedConfigurationIsRefusedAndNeverFallenBackFrom()
    {
        using var tree = new Tree();
        tree.Config("", "{ this is not json");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Dir("src"));

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
        Assert.NotEqual(Outcome.NotFound, found.Failure!.Outcome);
        Assert.Contains("JSON", found.Failure!.Message);
    }

    [Theory]
    [InlineData("""{ "trees": { } }""")]
    [InlineData("""{ "nothing": true }""")]
    [InlineData("""{ "trees": { "work": 3 } }""")]
    [InlineData("""{ "trees": { "work": { } } }""")]
    [InlineData("""{ "trees": { "work": { "path": "" } } }""")]
    [InlineData("""{ "trees": { "work": { "path": ".", "can": "read" } } }""")]
    [InlineData("""{ "trees": { "work": { "path": ".", "can": ["delte"] } } }""")]
    [InlineData("""{ "trees": { "work": { "path": ".", "scopes": [] } } }""")]
    [InlineData("""{ "trees": { "work": { "path": ".", "scopes": { "sub": { } } } } }""")]
    [InlineData("""[ "trees" ]""")]
    public void AConfigurationThatDeclaresNoUsableTreeIsRefused(string json)
    {
        using var tree = new Tree();
        tree.Config("", json);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
    }

    /// <summary>
    /// B.2: the old shape meant something different - a root was
    /// readable and writable in full - so reading it as the new one would
    /// GRANT more than the file says. Refused, with the whole of what to
    /// do rather than a complaint.
    /// </summary>
    [Fact]
    public void TheOldRootsShapeIsRefusedWithTheMigrationNamed()
    {
        using var tree = new Tree();
        tree.Config("", """{ "roots": { "repo": "." } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
        Assert.Contains("\"trees\"", found.Failure.Message);
        Assert.Contains("execute is never granted by default", found.Failure.Message);
    }

    [Fact]
    public void ATreeWrittenAsABarePathSaysWhatToWriteInstead()
    {
        using var tree = new Tree();
        tree.Config("", """{ "trees": { "work": "." } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Contains("\"path\"", found.Failure!.Message);
    }

    /// <summary>NotFound is the ordinary case and the ONLY one the
    /// caller may fall back from, so it is distinguished by outcome
    /// rather than by reading the message.</summary>
    [Fact]
    public void NoConfigurationAnywhereIsNotFound()
    {
        using var tree = new Tree();

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Dir("src/inner"));

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.NotFound, found.Failure!.Outcome);
    }

    [Fact]
    public void AnAbsolutePathInTheConfigurationIsLeftAbsolute()
    {
        using var tree = new Tree();
        using var other = new Tree();
        tree.Config("", $$"""
            { "trees": { "far": { "path": {{System.Text.Json.JsonSerializer.Serialize(other.Root)}} } } }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(other.Root, found.Value.Trees[0].Path);
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
              "trees": {
                "work": { "path": "." },
                "here": { "path": {{System.Text.Json.JsonSerializer.Serialize(tree.Root)}} },
                "far":  { "path": {{System.Text.Json.JsonSerializer.Serialize(other.Root)}} }
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
            { "trees": { "work": { "path": ".", "scopes": { "../elsewhere": { "can": [] } } } } }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);
        Assert.True(found.IsOk, found.Failure?.Message);

        Result<Roots> opened = Roots.Open(found.Value.Trees);
        Assert.False(opened.IsOk);
        Assert.Contains("not inside", opened.Failure!.Message);
    }

    // ---- the personal overlay (B.12) ----

    [Fact]
    public void TheLocalOverlayAddsTreesAndReplacesOnesOfTheSameName()
    {
        using var tree = new Tree();
        using var mine = new Tree();
        tree.Config("", """{ "trees": { "work": { "path": ".", "can": ["list"] } } }""");
        tree.Local("", $$"""
            {
              "trees": {
                "work": { "path": ".", "can": ["list", "read"] },
                "mine": { "path": {{System.Text.Json.JsonSerializer.Serialize(mine.Root)}} }
              }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["work", "mine"], found.Value.Trees.Select(t => t.Name));
        Assert.Equal(Permissions.ReadOnly, found.Value.Trees[0].Can);
        Assert.Contains(RootsFile.LocalFileName, found.Value.Origin);
    }

    [Fact]
    public void ABrokenOverlayIsRefusedRatherThanIgnored()
    {
        using var tree = new Tree();
        tree.Config("", """{ "trees": { "work": { "path": "." } } }""");
        tree.Local("", "{ not json");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk);
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
    }

    // ---- where a permission may come from ----

    /// <summary>
    /// R8.9: --root REPLACES the configuration. Merging would leave a
    /// caller unable to narrow the boundary - the tree's own trees would
    /// stay open beside whatever they asked for.
    /// </summary>
    [Fact]
    public void TheCommandLineReplacesTheConfigurationRatherThanAddingToIt()
    {
        using var tree = new Tree();
        string file = tree.Config("", """{ "trees": { "work": { "path": "." } } }""");
        string only = tree.Dir("sub");

        Arguments args = Arguments.Parse(["roots", "--config", file, "--root", $"just={only}"]);
        Result<Roots> opened = args.DeclaredRoots();

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal(["just"], opened.Value.Names);
    }

    /// <summary>
    /// B.4, and the hole it closes. A command line is not a deliberate,
    /// reviewable act inside the tree it governs - it is one line
    /// somebody typed, or one line in a client configuration a model
    /// never saw. It may open a tree for reading and it may not do more,
    /// whatever combination of flags is offered.
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
            Result<Roots> opened = Arguments.Parse(argv).DeclaredRoots();
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
            { "trees": { "work": { "path": "." }, "sub": { "path": "sub" } } }
            """);
        tree.Dir("sub");

        Arguments args = Arguments.Parse(["roots", "--config", file]);
        Result<Roots> opened = args.DeclaredRoots();

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
        Result<Roots> opened = args.DeclaredRoots();

        Assert.False(opened.IsOk);
        Assert.Equal(Outcome.NotFound, opened.Failure!.Outcome);
    }

    /// <summary>
    /// B.3. The current directory used to become a tree when nothing was
    /// declared, and that is the "searching around" the boundary exists
    /// to stop: a cwd sits ABOVE the tree somebody meant, so the fallback
    /// widened the boundary at exactly the moment nobody had stated one.
    /// </summary>
    [Fact]
    public void DeclaringNothingIsARefusalThatSaysHowToDeclareSomething()
    {
        Arguments args = Arguments.Parse(["roots", "--no-config"]);
        Result<Roots> opened = args.DeclaredRoots();

        Assert.False(opened.IsOk);
        Assert.Equal(Outcome.Invalid, opened.Failure!.Outcome);
        Assert.Contains(RootsFile.FileName, opened.Failure.Message);
        Assert.Contains("--root", opened.Failure.Message);
    }

    // ---- R7: tasks, declared by the file that declares the trees ----

    /// <summary>
    /// The command line is one string, split once. The awkward argument is
    /// the point: a space survives because quotes group it, and a double
    /// quote survives because a doubled one is a literal - and nothing
    /// between the split and the child re-splits either.
    /// </summary>
    [Fact]
    public void TasksAreReadFromTheFileThatDeclaresTheTrees()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "trees": { "work": { "path": "." } },
              "tasks": {
                "build": { "run": "pwsh -File build.ps1" },
                "gate":  { "run": "node driver.mjs \"a \"\"quoted\"\" thing\"", "cwd": "src" }
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
    /// quote, and <b>a backslash is an ordinary character</b> - which is
    /// what lets a Windows path be written once rather than doubled.
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
    /// A relative declared path travels, so it is held to forward slashes;
    /// an absolute one names a disk and cannot travel whatever it uses, so
    /// it is left alone.
    /// </summary>
    [Fact]
    public void ARelativeDeclaredPathMustUseForwardSlashes()
    {
        using var tree = new Tree();
        tree.Config("", """{ "trees": { "work": { "path": "..\\sibling" } } }""");

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.False(found.IsOk, "a relative path with a backslash does not travel");
        Assert.Equal(Outcome.Invalid, found.Failure!.Outcome);
        Assert.Contains("backslash", found.Failure.Message);
    }

    /// <summary>
    /// The overlay carries tasks by the same rule it carries trees: a name
    /// in both is the overlay's entirely, a name only it has is added.
    ///
    /// <para>The overlay is gitignored, so a task added here is a command
    /// that runs and never appears in a diff. That is a property of the
    /// overlay rather than of tasks, and is equally true of a tree it
    /// grants.</para>
    /// </summary>
    [Fact]
    public void TheOverlayReplacesATaskByNameAndAddsItsOwn()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "trees": { "work": { "path": "." } },
              "tasks": { "build": { "run": "pwsh -File build.ps1" } }
            }
            """);
        tree.Local("", """
            {
              "trees": { "work": { "path": "." } },
              "tasks": {
                "build": { "run": "pwsh -File build.ps1 --fast" },
                "mine":  { "run": "node scratch.mjs" }
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
    /// can say what may run. This falls out of <c>--root</c> granting
    /// <c>list read</c> rather than being enforced separately, and the
    /// test is here so it stays fallen out.
    /// </summary>
    [Fact]
    public void ARootOnTheCommandLineDeclaresNoTasks()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "trees": { "work": { "path": "." } },
              "tasks": { "build": { "run": "pwsh" } }
            }
            """);

        Arguments args = Arguments.Parse(["roots", "--root", tree.Root]);
        Result<Roots> opened = args.DeclaredRoots();

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Empty(opened.Value.Tasks);
    }

    /// <summary>
    /// A task that is not shaped like a task is refused with the file
    /// named, never skipped. Skipping one silently answers "no such task"
    /// to somebody looking straight at it.
    /// </summary>
    [Theory]
    [InlineData("""{"trees":{"w":{"path":"."}},"tasks":{"build":"pwsh"}}""")]
    [InlineData("""{"trees":{"w":{"path":"."}},"tasks":{"build":{"run":["pwsh"]}}}""")]
    [InlineData("""{"trees":{"w":{"path":"."}},"tasks":{"build":{"run":""}}}""")]
    [InlineData("""{"trees":{"w":{"path":"."}},"tasks":{"build":{"run":7}}}""")]
    [InlineData("""{"trees":{"w":{"path":"."}},"tasks":{"build":{"run":"pwsh \"x"}}}""")]
    [InlineData("""{"trees":{"w":{"path":"."}},"tasks":{"build":{"run":"pwsh","cwd":""}}}""")]
    [InlineData("""{"trees":{"w":{"path":"."}},"tasks":{"build":{"run":"pwsh","cwd":"..\\up"}}}""")]
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
    /// caller - it came from a file Fettler refuses to write, which is
    /// what makes a branch name in it a person's act and not a model's.
    /// </summary>
    [Fact]
    public void AReplacementFillsAPlaceholderInATaskCommand()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "replacements": { "feature-branch": "hebdenbridge" },
              "trees": { "work": { "path": "." } },
              "tasks": { "feature": { "run": "pwsh -File new.ps1 {feature-branch}" } }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["pwsh", "-File", "new.ps1", "hebdenbridge"], found.Value.Tasks[0].Command);
    }

    /// <summary>
    /// Why filling happens AFTER the split and never before it. Filled
    /// into the LINE, a value of "two words" would silently become two
    /// arguments and one carrying a quote would change the parse - the
    /// exact failure class the split-once rule exists to remove. Filled
    /// into a WORD, every one of these is one argument holding precisely
    /// those characters.
    ///
    /// <para>The <c>{another-name}</c> case is also the no-recursion
    /// proof: the value is inserted literally and never looked up, so it
    /// must NOT be refused for naming something undeclared.</para>
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
              "replacements": { "note": {{System.Text.Json.JsonSerializer.Serialize(value)}} },
              "trees": { "work": { "path": "." } },
              "tasks": { "commit": { "run": "git commit -m {note}" } }
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
              "replacements": { "version": "1.2.3" },
              "trees": { "work": { "path": "." } },
              "tasks": { "tag": { "run": "git tag v{version} -m rel-{version}" } }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["git", "tag", "v1.2.3", "-m", "rel-1.2.3"], found.Value.Tasks[0].Command);
    }

    /// <summary>
    /// The clause that keeps every configuration written before this
    /// feature working unchanged. A brace on a command line was ordinary,
    /// and the braces people actually write hold spaces, dollars, pipes,
    /// colons or bare digits - none of which can begin a name. Reading
    /// every <c>{...}</c> as one would have turned each of these into an
    /// undeclared-name refusal.
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
              "trees": { "work": { "path": "." } },
              "tasks": { "t": { "run": {{System.Text.Json.JsonSerializer.Serialize(run)}} } }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);

        // Filling changed nothing at all: the command is what the split
        // alone produces.
        Assert.Equal(Tasks.Split(run).Value, found.Value.Tasks[0].Command);
    }

    /// <summary>A doubled brace is the escape for the one case the name
    /// rule cannot cover: a command line that must carry the text of a
    /// placeholder itself.</summary>
    [Fact]
    public void ADoubledBraceIsALiteralBraceAndNotAPlaceholder()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "trees": { "work": { "path": "." } },
              "tasks": { "echo": { "run": "echo {{feature-branch}" } }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["echo", "{feature-branch}"], found.Value.Tasks[0].Command);
    }

    /// <summary>
    /// An unresolved name refuses the read, names itself, and says what
    /// IS declared - the same strictness a malformed "run" already gets.
    /// </summary>
    [Fact]
    public void AnUnresolvedPlaceholderRefusesTheReadAndNamesItself()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "replacements": { "release-tag": "v1.0.0" },
              "trees": { "work": { "path": "." } },
              "tasks": { "feature": { "run": "pwsh -File new.ps1 {feature-branch}" } }
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
              "replacements": { "unused": "parked" },
              "trees": { "work": { "path": "." } },
              "tasks": { "build": { "run": "pwsh -File build.ps1" } }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["pwsh", "-File", "build.ps1"], found.Value.Tasks[0].Command);
    }

    /// <summary>
    /// Why filling waits for the merge instead of happening as each file
    /// is read. The checked-in file declares the task and the gitignored
    /// overlay beside it carries today's value - which is the entire
    /// reason to have both, since a branch name is one person's and one
    /// moment's and has no business being a diff. Filling during the
    /// first read would refuse a configuration that is complete the
    /// instant both halves are in hand.
    /// </summary>
    [Fact]
    public void TheOverlaySuppliesTheValueForATaskTheCheckedInFileDeclares()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "trees": { "work": { "path": "." } },
              "tasks": { "feature": { "run": "pwsh -File new.ps1 {feature-branch}" } }
            }
            """);
        tree.Local("", """
            {
              "replacements": { "feature-branch": "hebdenbridge" },
              "trees": { "work": { "path": "." } }
            }
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
              "replacements": { "branch": "placeholder", "shared": "kept" },
              "trees": { "work": { "path": "." } },
              "tasks": { "t": { "run": "x {branch} {shared} {mine}" } }
            }
            """);
        tree.Local("", """
            {
              "replacements": { "branch": "hebdenbridge", "mine": "extra" },
              "trees": { "work": { "path": "." } }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(["x", "hebdenbridge", "kept", "extra"], found.Value.Tasks[0].Command);
    }

    /// <summary>
    /// Both spellings survive, and both are needed. <c>Command</c> is
    /// what would actually be launched; <c>Display</c> is the line as
    /// declared, braces and all. A listing that cannot be compared with
    /// the file is a listing nobody can check, and one that hides the
    /// value is a listing that lies about what runs.
    /// </summary>
    [Fact]
    public void TheDeclaredLineKeepsItsPlaceholdersWhileTheCommandIsFilled()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "replacements": { "feature-branch": "hebdenbridge" },
              "trees": { "work": { "path": "." } },
              "tasks": { "feature": { "run": "pwsh -File new.ps1 {feature-branch}" } }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Contains("{feature-branch}", found.Value.Tasks[0].Display);
        Assert.Contains("hebdenbridge", found.Value.Tasks[0].Command);
    }

    /// <summary>
    /// The regression this feature has to be judged against. Before it
    /// existed a brace was ordinary text and reached the child verbatim,
    /// so <c>shoddy-feature.ps1 new {feature-branch}</c> did not fail -
    /// it created a branch called <c>{feature-branch}</c>. Of the three
    /// things this could do, passing a placeholder through is the only
    /// one that SUCCEEDS at the wrong thing.
    /// </summary>
    [Fact]
    public void ADeclaredPlaceholderNeverReachesTheArgumentList()
    {
        using var tree = new Tree();
        tree.Config("", """
            {
              "replacements": { "feature-branch": "hebdenbridge" },
              "trees": { "work": { "path": "." } },
              "tasks": { "feature": { "run": "pwsh -File new.ps1 {feature-branch}" } }
            }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Root);

        Assert.True(found.IsOk, found.Failure?.Message);
        foreach (string word in found.Value.Tasks[0].Command)
            Assert.False(word.Contains("{feature-branch}", StringComparison.Ordinal),
                $"a placeholder reached the argument list as '{word}'");
    }

    /// <summary>A replacement that is not shaped like one is refused with
    /// the file named, never skipped - the same rule a malformed task
    /// gets, and for the same reason.</summary>
    [Theory]
    [InlineData("""{"trees":{"w":{"path":"."}},"replacements":[]}""")]
    [InlineData("""{"trees":{"w":{"path":"."}},"replacements":{"a":7}}""")]
    [InlineData("""{"trees":{"w":{"path":"."}},"replacements":{"a":null}}""")]
    [InlineData("""{"trees":{"w":{"path":"."}},"replacements":{"a b":"x"}}""")]
    [InlineData("""{"trees":{"w":{"path":"."}},"replacements":{"":"x"}}""")]
    [InlineData("""{"trees":{"w":{"path":"."}},"replacements":{"0":"x"}}""")]
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
    /// opened.
    ///
    /// <para><b>It fails silently and in the unsafe direction.</b> A null
    /// Models is how a boundary says "nobody named a models directory",
    /// so the sidecar is never started, the first tier becomes the whole
    /// screen, and a clean payload is SERVED. Drop the argument and a
    /// tree screening <c>legal</c> - a category with no first-tier
    /// detector at all - serves everything, while the file and
    /// <c>roots</c> both still say it is screened.</para>
    ///
    /// <para>Every other screening test builds the boundary directly and
    /// so never crosses this seam, which is exactly why it survived.</para>
    /// </summary>
    [Fact]
    public void TheModelsDirectoryReachesTheBoundaryTheCommandLineOpens()
    {
        using var tree = new Tree();
        string file = tree.Config("", """
            {
              "trees": { "work": { "path": ".", "screen": ["legal"] } },
              "models": "screening-models"
            }
            """);

        Result<Boundary> opened = Arguments.Parse(["read", "--config", file]).DeclaredBoundary();

        Assert.True(opened.IsOk, opened.Failure?.Message);
        Assert.Equal(Path.Combine(tree.Root, "screening-models"), opened.Value.Roots.Models);
    }

    /// <summary>And it is resolved against the FILE, like every other
    /// declared path, so the same configuration names the same directory
    /// from wherever it is read.</summary>
    [Fact]
    public void TheModelsDirectoryIsResolvedAgainstTheFile()
    {
        using var tree = new Tree();
        tree.Config("nested", """
            { "trees": { "work": { "path": "." } }, "models": "../shared-models" }
            """);

        Result<RootsFile.Found> found = RootsFile.Discover(tree.Dir("nested/deeper"));

        Assert.True(found.IsOk, found.Failure?.Message);
        Assert.Equal(Path.Combine(tree.Root, "shared-models"), found.Value.Models);
    }
}
