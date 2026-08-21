using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Fettler.Core;

/// <summary>How an archive is packed, decided by the name it was given.</summary>
public enum ArchiveKind { Zip, Tar, TarGz }

/// <summary>
/// One entry in an archive, as Fettler reports it.
///
/// <para><c>Executable</c> and <c>Link</c> are carried rather than
/// summarised because both decide whether an extraction is safe or
/// useful. A tar that loses the executable bit produces a `fettle` that
/// will not run - R6.9 is this project's own clause about exactly that -
/// and a link member is the one entry type that can point out of the
/// tree after every path check has passed.</para>
/// </summary>
public sealed record ArchiveMember(
    string Name,
    long Length,
    long Compressed,
    DateTimeOffset Modified,
    bool IsDirectory,
    bool IsExecutable,
    bool IsLink);

/// <summary>
/// Archives, read but never written.
///
/// <para><b>Why reading one belongs here.</b> With the assistant's own
/// reader denied, Fettler is the only reader on the machine, and an
/// archive is the thing a person holds at exactly the moment they are
/// least willing to open a shell: verifying what a release is about to
/// ship. Answering "what is in this zip" needed a hand-loaded
/// compression assembly before this existed.</para>
///
/// <para><b>Why writing one does not.</b> Producing an archive is a
/// BUILD OUTPUT, and building is what a declared task and <c>run</c> are
/// for - <c>fettler/build.ps1</c> already cuts the release archives.
/// A <c>compress</c> verb would put this tool in the business of
/// producing artefacts rather than tending trees.</para>
///
/// <para><b>Why find and search do not reach inside one.</b> It sounds
/// free and is not: it would make members into paths, and then the
/// boundary model has to answer what a scope means inside a container,
/// doubling every permission question. Read the manifest, extract, then
/// search the tree - three honest steps beat one murky one.</para>
///
/// <para><b>No new dependency.</b> System.IO.Compression and
/// System.Formats.Tar are both in the shared framework, so R1.2's
/// allowlist is untouched and NOTICE gains no entry. That is most of why
/// this was a smaller decision than PdfPig was.</para>
/// </summary>
public static class Archives
{
    /// <summary>A refusal rather than a full disk. A compressed archive
    /// can name far more content than it occupies, and an unpack that
    /// discovers this by running out of space has already done the
    /// damage. Same posture as the regex bound: refused, not endured.</summary>
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    public const int MaxMembers = 20_000;

    /// <summary>Whether this name is an archive, and which sort. By name
    /// and not by content, for the reason <see cref="Typed.KindOf"/>
    /// gives: the extension is what the caller and their tools already
    /// agree the file is.</summary>
    public static ArchiveKind? KindOf(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".tar.gz", StringComparison.Ordinal)) return ArchiveKind.TarGz;
        if (name.EndsWith(".tgz", StringComparison.Ordinal)) return ArchiveKind.TarGz;
        if (name.EndsWith(".zip", StringComparison.Ordinal)) return ArchiveKind.Zip;
        if (name.EndsWith(".tar", StringComparison.Ordinal)) return ArchiveKind.Tar;
        return null;
    }

    /// <summary>A lone gzip - one compressed file, not an archive of
    /// several. `foo.json.gz` is a JSON file wearing a coat.</summary>
    public static bool IsLoneGzip(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        return name.EndsWith(".gz", StringComparison.Ordinal)
            && !name.EndsWith(".tar.gz", StringComparison.Ordinal);
    }

    public static Result<IReadOnlyList<ArchiveMember>> Members(ContainedPath path)
    {
        ArchiveKind? kind = KindOf(path.Full);
        if (kind is null)
            return Result<IReadOnlyList<ArchiveMember>>.Fail(
                Outcome.Invalid, "this is not an archive name", path.Display);

        try
        {
            return Result<IReadOnlyList<ArchiveMember>>.Ok(
                kind == ArchiveKind.Zip ? ZipMembers(path.Full) : TarMembers(path.Full, kind.Value));
        }
        catch (Exception e) when (Recoverable(e))
        {
            return Result<IReadOnlyList<ArchiveMember>>.Fail(
                Outcome.Refused, "this archive could not be read: " + e.Message, path.Display);
        }
    }

    static IReadOnlyList<ArchiveMember> ZipMembers(string full)
    {
        var members = new List<ArchiveMember>();
        using ZipArchive zip = ZipFile.OpenRead(full);
        foreach (ZipArchiveEntry entry in zip.Entries) members.Add(Describe(entry));
        return members;
    }

    static ArchiveMember Describe(ZipArchiveEntry entry)
    {
        // A zip written on unix carries the mode in the top sixteen bits
        // of the external attributes; one written on Windows carries
        // nothing there, and reports no bit rather than a wrong one.
        long attributes = entry.ExternalAttributes >> 16;
        int mode = (int)(attributes & 0x1FF);
        bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

        return new ArchiveMember(
            entry.FullName, entry.Length, entry.CompressedLength,
            entry.LastWriteTime, directory,
            IsExecutable: (mode & 0b001_001_001) != 0,
            IsLink: (attributes & 0xF000) == 0xA000);
    }

    static IReadOnlyList<ArchiveMember> TarMembers(string full, ArchiveKind kind)
    {
        var members = new List<ArchiveMember>();
        using Stream stream = OpenTar(full, kind);
        using var reader = new TarReader(stream);

        while (reader.GetNextEntry(copyData: false) is { } entry)
            members.Add(Describe(entry));

        return members;
    }

    static ArchiveMember Describe(TarEntry entry) =>
        new(entry.Name,
            entry.Length,
            entry.Length,
            entry.ModificationTime,
            entry.EntryType == TarEntryType.Directory,
            (entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute
                           | UnixFileMode.OtherExecute)) != 0,
            entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink);

    static Stream OpenTar(string full, ArchiveKind kind)
    {
        FileStream file = File.OpenRead(full);
        return kind == ArchiveKind.TarGz
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
    }

    /// <summary>The manifest, rendered as text so that <c>--from</c>,
    /// <c>--to</c> and <c>--tail</c> mean for an archive exactly what they
    /// mean for every other kind, and a caller has one rule to learn.</summary>
    public static string Manifest(IReadOnlyList<ArchiveMember> members)
    {
        int width = 4;
        foreach (ArchiveMember m in members) width = Math.Max(width, m.Length.ToString().Length);

        long total = 0;
        var text = new StringBuilder();

        foreach (ArchiveMember m in members)
        {
            total += m.Length;
            text.Append(m.Length.ToString().PadLeft(width)).Append("  ")
                .Append(m.Modified.UtcDateTime.ToString("yyyy-MM-dd HH:mm")).Append("  ");

            if (m.IsDirectory) text.Append("d ");
            else if (m.IsLink) text.Append("l ");
            else if (m.IsExecutable) text.Append("x ");
            else text.Append("- ");

            text.AppendLine(m.Name);
        }

        text.Append(members.Count).Append(members.Count == 1 ? " member, " : " members, ")
            .Append(total).AppendLine(" bytes uncompressed");
        return text.ToString();
    }

    /// <summary>One member's bytes, without unpacking anything to disk.
    /// Verification that writes nothing needs only <c>read</c>.</summary>
    public static Result<byte[]> Member(ContainedPath path, string member)
    {
        ArchiveKind? kind = KindOf(path.Full);
        if (kind is null)
            return Result<byte[]>.Fail(Outcome.Invalid, "this is not an archive name", path.Display);

        string wanted = member.Replace('\\', '/');

        try
        {
            if (kind == ArchiveKind.Zip)
            {
                using ZipArchive zip = ZipFile.OpenRead(path.Full);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (!string.Equals(entry.FullName.Replace('\\', '/'), wanted, StringComparison.Ordinal))
                        continue;

                    using Stream content = entry.Open();
                    return Result<byte[]>.Ok(Drain(content, entry.Length));
                }
            }
            else
            {
                using Stream stream = OpenTar(path.Full, kind.Value);
                using var reader = new TarReader(stream);
                while (reader.GetNextEntry(copyData: true) is { } entry)
                {
                    if (!string.Equals(entry.Name.Replace('\\', '/'), wanted, StringComparison.Ordinal))
                        continue;
                    if (entry.DataStream is null) break;
                    return Result<byte[]>.Ok(Drain(entry.DataStream, entry.Length));
                }
            }
        }
        catch (Exception e) when (Recoverable(e))
        {
            return Result<byte[]>.Fail(
                Outcome.Refused, "this archive could not be read: " + e.Message, path.Display);
        }

        return Result<byte[]>.Fail(
            Outcome.NotFound, $"the archive has no member called '{member}'", path.Display);
    }

    /// <summary>A lone gzip, decompressed. Bounded by the same total as an
    /// archive, because a gzip conceals its size just as well.</summary>
    public static Result<byte[]> Gunzip(ContainedPath path)
    {
        try
        {
            using FileStream file = File.OpenRead(path.Full);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return Result<byte[]>.Ok(Drain(gzip, 0));
        }
        catch (Exception e) when (Recoverable(e))
        {
            return Result<byte[]>.Fail(
                Outcome.Refused, "this gzip could not be read: " + e.Message, path.Display);
        }
    }

    static byte[] Drain(Stream source, long expected)
    {
        using var into = new MemoryStream(expected > 0 && expected < 1 << 20 ? (int)expected : 0);
        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxTotalBytes)
                throw new InvalidDataException(
                    $"the content passed {MaxTotalBytes} bytes uncompressed, which is where this stops");
            into.Write(buffer, 0, read);
        }

        return into.ToArray();
    }

    /// <summary>
    /// Walk the archive once, handing each file member and its content to
    /// the writer, which returns a failure to stop the walk.
    ///
    /// <para>A callback rather than an enumerable because the content
    /// stream is only valid until the reader advances, and an enumerable
    /// invites a caller to keep one. Streaming rather than a byte array
    /// because an archive may be larger than memory.</para>
    /// </summary>
    public static Result<int> Unpack(ContainedPath path, Func<ArchiveMember, Stream, Failure?> write)
    {
        ArchiveKind? kind = KindOf(path.Full);
        if (kind is null)
            return Result<int>.Fail(Outcome.Invalid, "this is not an archive name", path.Display);

        int written = 0;

        try
        {
            if (kind == ArchiveKind.Zip)
            {
                using ZipArchive zip = ZipFile.OpenRead(path.Full);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    ArchiveMember member = Describe(entry);
                    if (member.IsDirectory || member.IsLink) continue;

                    using Stream content = entry.Open();
                    if (write(member, content) is { } stop) return Result<int>.Fail(stop);
                    written++;
                }
            }
            else
            {
                using Stream stream = OpenTar(path.Full, kind.Value);
                using var reader = new TarReader(stream);
                while (reader.GetNextEntry(copyData: true) is { } entry)
                {
                    ArchiveMember member = Describe(entry);
                    if (member.IsDirectory || member.IsLink || entry.DataStream is null) continue;

                    if (write(member, entry.DataStream) is { } stop) return Result<int>.Fail(stop);
                    written++;
                }
            }
        }
        catch (Exception e) when (Recoverable(e))
        {
            return Result<int>.Fail(
                Outcome.Refused, "this archive could not be read: " + e.Message, path.Display);
        }

        return Result<int>.Ok(written);
    }

    internal static bool Recoverable(Exception e) =>
        e is not OutOfMemoryException and not StackOverflowException;
}
