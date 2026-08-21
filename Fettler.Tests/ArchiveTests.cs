using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Fettler.Cli;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// Archives, read but never written.
///
/// <para>The reading half is ordinary. The extracting half is where the
/// work is, because an archive is a list of paths somebody else chose -
/// and every one of them has to come back through the same boundary a
/// typed path does, or the boundary has a hole shaped like a zip.</para>
/// </summary>
public sealed class ArchiveTests
{
    static Task<CliResult> Run(Sandbox box, params string[] argv) =>
        Command.RunAsync(argv, new StringReader(string.Empty), box.Bench);

    // ---- building archives to read ----

    static byte[] Zip(params (string Name, string Body)[] members)
    {
        using var into = new MemoryStream();
        using (var zip = new ZipArchive(into, ZipArchiveMode.Create, leaveOpen: true))
            foreach ((string name, string body) in members)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);
                using StreamWriter writer = new(entry.Open());
                writer.Write(body);
            }

        return into.ToArray();
    }

    static byte[] Tar(bool gzip, params (string Name, string Body, bool Executable)[] members)
    {
        using var plain = new MemoryStream();

        using (var writer = new TarWriter(plain, leaveOpen: true))
            foreach ((string name, string body, bool executable) in members)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(bytes),
                };
                if (executable) entry.Mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                writer.WriteEntry(entry);
            }

        if (!gzip) return plain.ToArray();

        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionMode.Compress, leaveOpen: true))
            gz.Write(plain.ToArray());

        return packed.ToArray();
    }

    static byte[] TarWithLink(string name, string target)
    {
        using var plain = new MemoryStream();
        using (var writer = new TarWriter(plain, leaveOpen: true))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, name) { LinkName = target });
        return plain.ToArray();
    }

    // ---- reading ----

    /// <summary>
    /// The question I actually had, the day this was written: what is in
    /// the archive a release is about to publish. It took a hand-loaded
    /// compression assembly to answer before this existed, which is to
    /// say it took leaving the tool.
    /// </summary>
    [Fact]
    public async Task AZipReadsAsItsManifest()
    {
        using var box = new Sandbox();
        box.WriteRaw("dist.zip", Zip(("fettle.exe", "binary"), ("NOTICE", "notices"), ("LICENSE", "mit")));

        CliResult said = await Run(box, "read", "dist.zip");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Contains("fettle.exe", said.Stdout);
        Assert.Contains("NOTICE", said.Stdout);
        Assert.Contains("LICENSE", said.Stdout);
        Assert.Contains("3 members", said.Stdout);
    }

    [Fact]
    public async Task ATarGzReadsAsItsManifestToo()
    {
        using var box = new Sandbox();
        box.WriteRaw("dist.tar.gz", Tar(gzip: true, ("fettle", "binary", true), ("NOTICE", "notices", false)));

        CliResult said = await Run(box, "read", "dist.tar.gz");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Contains("fettle", said.Stdout);
        Assert.Contains("2 members", said.Stdout);
    }

    /// <summary>The execute bit is reported, because whether a shipped
    /// binary carries one is the whole of R6.9 and the thing a release
    /// most wants to check without unpacking.</summary>
    [Fact]
    public async Task TheManifestSaysWhichMembersAreExecutable()
    {
        using var box = new Sandbox();
        box.WriteRaw("dist.tar", Tar(gzip: false, ("fettle", "binary", true), ("NOTICE", "notices", false)));

        CliResult said = await Run(box, "read", "dist.tar");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        string[] lines = said.Stdout.Split('\n');
        Assert.Contains(lines, l => l.Contains("fettle") && l.Contains(" x "));
        Assert.Contains(lines, l => l.Contains("NOTICE") && l.Contains(" - "));
    }

    /// <summary>One member, decoded by the ordinary rules, with nothing
    /// unpacked to disk. Verification that writes nothing needs only
    /// <c>read</c>.</summary>
    [Fact]
    public async Task AMemberIsReadWithoutUnpackingAnything()
    {
        using var box = new Sandbox();
        box.WriteRaw("dist.zip", Zip(("NOTICE", "PdfPig is here\n"), ("other.txt", "not this one\n")));

        CliResult said = await Run(box, "read", "dist.zip", "--member", "NOTICE");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Contains("PdfPig is here", said.Stdout);
        Assert.DoesNotContain("not this one", said.Stdout);
        Assert.False(File.Exists(box.Full("NOTICE")));
    }

    [Fact]
    public async Task AMemberThatIsNotThereIsNotFound()
    {
        using var box = new Sandbox();
        box.WriteRaw("dist.zip", Zip(("NOTICE", "notices")));

        CliResult said = await Run(box, "read", "dist.zip", "--member", "absent");

        Assert.Equal(ExitCodes.NotFound, said.ExitCode);
    }

    [Fact]
    public async Task MemberOnSomethingThatIsNotAnArchiveIsRefused()
    {
        using var box = new Sandbox();
        box.Write("plain.txt", "ordinary\n");

        CliResult said = await Run(box, "read", "plain.txt", "--member", "anything");

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.Contains("not one", said.Stdout + said.Stderr);
    }

    /// <summary>A lone gzip is one file wearing a coat, and reading it
    /// should hand back what is underneath rather than the coat.</summary>
    [Fact]
    public async Task ALoneGzipReadsAsWhatIsInsideIt()
    {
        using var box = new Sandbox();
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionMode.Compress, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes("the log line\n"));

        box.WriteRaw("build.log.gz", packed.ToArray());

        CliResult said = await Run(box, "read", "build.log.gz");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Contains("the log line", said.Stdout);
    }

    /// <summary>--tail works on an archive's manifest like on anything
    /// else, which is the point of rendering it as text: one rule.</summary>
    [Fact]
    public async Task TailWorksOnAManifestLikeOnAnyOtherText()
    {
        using var box = new Sandbox();
        box.WriteRaw("dist.zip", Zip(("a", "1"), ("b", "2"), ("c", "3")));

        CliResult said = await Run(box, "read", "dist.zip", "--tail", "1");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Contains("3 members", said.Stdout);
        Assert.DoesNotContain(" a\n", said.Stdout);
    }

    // ---- extracting ----

    [Fact]
    public async Task ExtractWritesTheMembersAndSaysHowMany()
    {
        using var box = new Sandbox();
        box.WriteRaw("dist.zip", Zip(("one.txt", "first\n"), ("sub/two.txt", "second\n")));

        CliResult said = await Run(box, "extract", "dist.zip", "--into", "out");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Equal("first\n", box.ReadText("out/one.txt"));
        Assert.Equal("second\n", box.ReadText("out/sub/two.txt"));
        Assert.Contains("2 members", said.Stdout);
    }

    /// <summary>
    /// Zip slip, one step short of the tree edge - and the case this
    /// implementation got wrong first time. `out/../escaped.txt`
    /// normalises to somewhere the BOUNDARY is perfectly happy with, so
    /// the containment check passes and the file still lands nowhere near
    /// where the caller said to put it. Refused whole; the harmless member
    /// beside it does not land either.
    /// </summary>
    [Fact]
    public async Task AMemberThatClimbsOutOfTheDestinationIsRefusedAndNothingIsWritten()
    {
        using var box = new Sandbox();
        box.WriteRaw("evil.zip", Zip(("harmless.txt", "fine\n"), ("../escaped.txt", "not fine\n")));

        CliResult said = await Run(box, "extract", "evil.zip", "--into", "out");

        Assert.Equal(ExitCodes.Refused, said.ExitCode);
        Assert.False(File.Exists(box.Full("out/harmless.txt")));
        Assert.False(File.Exists(box.Full("escaped.txt")));
    }

    /// <summary>
    /// And the same trick with enough dots to leave the tree entirely,
    /// which the boundary refuses exactly as it refuses a typed path -
    /// the point being that an archive member is not a special kind of
    /// path with its own rules.
    /// </summary>
    [Fact]
    public async Task AMemberThatClimbsOutOfTheTreeIsRefusedAsAnyOutsidePathIs()
    {
        using var box = new Sandbox();
        box.WriteRaw("evil.zip", Zip(("../../../escaped.txt", "not fine\n")));

        CliResult said = await Run(box, "extract", "evil.zip", "--into", "out");

        Assert.Equal(ExitCodes.OutsideRoot, said.ExitCode);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(box.Root)!, "escaped.txt")));
    }

    /// <summary>
    /// A link is the one member that can point out of the tree after every
    /// path check has passed. Refused whole and named, rather than skipped
    /// silently - a skip produces an extraction that looks complete and is
    /// not.
    /// </summary>
    [Fact]
    public async Task ALinkMemberIsRefusedRatherThanSkipped()
    {
        using var box = new Sandbox();
        box.WriteRaw("linked.tar", TarWithLink("escape", "/etc/passwd"));

        CliResult said = await Run(box, "extract", "linked.tar", "--into", "out");

        Assert.Equal(ExitCodes.Refused, said.ExitCode);
        Assert.Contains("link", said.Stdout + said.Stderr);
    }

    [Fact]
    public async Task ExtractWillNotWriteOverSomethingWithoutBeingTold()
    {
        using var box = new Sandbox();
        box.WriteRaw("dist.zip", Zip(("one.txt", "from the archive\n")));
        box.Write("out/one.txt", "already here\n");

        CliResult said = await Run(box, "extract", "dist.zip", "--into", "out");

        Assert.Equal(ExitCodes.TargetExists, said.ExitCode);
        Assert.Equal("already here\n", box.ReadText("out/one.txt"));

        CliResult again = await Run(box, "extract", "dist.zip", "--into", "out", "--overwrite");

        Assert.Equal(ExitCodes.Ok, again.ExitCode);
        Assert.Equal("from the archive\n", box.ReadText("out/one.txt"));
    }

    /// <summary>There is no working directory to fall back on, so where
    /// the members land is stated rather than guessed - guessing a
    /// destination for an operation that writes many files is the wrong
    /// place to be helpful.</summary>
    [Fact]
    public async Task ExtractWithoutADestinationIsRefused()
    {
        using var box = new Sandbox();
        box.WriteRaw("dist.zip", Zip(("one.txt", "first\n")));

        CliResult said = await Run(box, "extract", "dist.zip");

        Assert.Equal(ExitCodes.Invalid, said.ExitCode);
        Assert.Contains("--into", said.Stdout + said.Stderr);
    }

    /// <summary>A tree that may be read and listed and nothing else must
    /// not gain a way to write through an archive.</summary>
    [Fact]
    public async Task ExtractingIntoAReadOnlyTreeIsRefused()
    {
        using var box = new Sandbox(Permissions.ReadOnly);
        box.WriteRaw("dist.zip", Zip(("one.txt", "first\n")));

        CliResult said = await Run(box, "extract", "dist.zip", "--into", "out");

        Assert.Equal(ExitCodes.Refused, said.ExitCode);
        Assert.False(File.Exists(box.Full("out/one.txt")));
    }

    /// <summary>R6.9 again, on the platform that has the bit: a `fettle`
    /// that arrives out of a tar without it does not run, which is the
    /// whole reason the release tars are cut on a Linux runner.</summary>
    [UnixFact]
    public async Task TheExecutableBitSurvivesExtraction()
    {
        // The attribute skips this off POSIX; the guard is what the
        // platform-compatibility analyzer reads, and it has to be here
        // rather than in the attribute for the build to stay warning-free.
        if (OperatingSystem.IsWindows()) return;

        using var box = new Sandbox();
        box.WriteRaw("dist.tar", Tar(gzip: false, ("fettle", "#!/bin/sh\n", true), ("NOTICE", "notices", false)));

        CliResult said = await Run(box, "extract", "dist.tar", "--into", "out");

        Assert.Equal(ExitCodes.Ok, said.ExitCode);

        UnixFileMode mode = File.GetUnixFileMode(box.Full("out/fettle"));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute), "the extracted fettle lost its execute bit");

        UnixFileMode plain = File.GetUnixFileMode(box.Full("out/NOTICE"));
        Assert.False(plain.HasFlag(UnixFileMode.UserExecute), "NOTICE gained an execute bit it never had");
    }

    /// <summary>Reading must never write. Stated as a test because it is
    /// the sort of convenience somebody adds later.</summary>
    [Fact]
    public async Task ReadingAnArchiveUnpacksNothing()
    {
        using var box = new Sandbox();
        box.WriteRaw("dist.zip", Zip(("one.txt", "first\n")));

        await Run(box, "read", "dist.zip");

        Assert.False(File.Exists(box.Full("one.txt")));
        Assert.Single(Directory.GetFiles(box.Root));
    }
}
