using System.Runtime.InteropServices;
using System.Text;

namespace Fettler.Core;

/// <summary>
/// Everything about a file that is not its content, captured before a
/// staged write and re-applied after it.
///
/// <para>R4.18 is why this type exists. R4.3 requires a crash-safe write
/// and deliberately leaves the mechanism open, but the mechanism
/// silently decides what else survives: <c>File.Replace</c> carries the
/// original's attributes and ACLs, a move-with-overwrite does not, and
/// macOS <c>rename</c> does not carry the target's extended attributes.
/// What rides on that choice is not incidental - NTFS alternate data
/// streams carry the Mark-of-the-Web and macOS extended attributes carry
/// <c>com.apple.quarantine</c> - so an implementation that stages
/// carelessly strips a security marker as a side effect of changing
/// text. Nobody would write that clause down on purpose, which is
/// exactly why R4.18 writes it down.</para>
///
/// <para>Neither streams nor extended attributes have a managed API, so
/// both arrive here through platform interop. Everything is best-effort
/// and nothing throws: what could not be carried over is <b>named in the
/// result</b> rather than swallowed, because R4.18's alternative to
/// preserving is telling the caller, not pretending.</para>
/// </summary>
public sealed record FileMetadata(
    UnixFileMode? UnixMode,
    FileAttributes? WindowsAttributes,
    IReadOnlyList<NamedBlob> Streams,
    IReadOnlyList<NamedBlob> ExtendedAttributes)
{
    public static readonly FileMetadata None = new(null, null, [], []);

    /// <summary>Whether this file carried anything worth reporting on.</summary>
    public bool IsEmpty =>
        UnixMode is null && WindowsAttributes is null
        && Streams.Count == 0 && ExtendedAttributes.Count == 0;
}

/// <summary>A named lump of bytes: an alternate data stream or an
/// extended attribute, which are the same shape wearing two names.</summary>
public sealed record NamedBlob(string Name, byte[] Data);

public static class Metadata
{
    /// <summary>
    /// Read everything about a file that a staged write would otherwise
    /// drop. A file that is not there carries nothing, which is the right
    /// answer for a create rather than an error.
    /// </summary>
    public static FileMetadata Capture(string path)
    {
        if (!File.Exists(path)) return FileMetadata.None;

        UnixFileMode? mode = null;
        FileAttributes? attributes = null;

        try
        {
            if (OperatingSystem.IsWindows()) attributes = File.GetAttributes(path);
            else mode = File.GetUnixFileMode(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        return new FileMetadata(mode, attributes, ReadStreams(path), ReadXattrs(path));
    }

    /// <summary>
    /// Put back what <see cref="Capture"/> took, and name whatever would
    /// not go back. An empty list is the guarantee R4.18 asks for; a
    /// non-empty one is the disclosure it accepts instead.
    /// </summary>
    public static IReadOnlyList<string> Apply(string path, FileMetadata meta)
    {
        var lost = new List<string>();

        try
        {
            if (OperatingSystem.IsWindows() && meta.WindowsAttributes is { } a)
            {
                // Only the attributes a caller cares about survive: the
                // rest (Directory, Archive, the ones the OS maintains)
                // are the filesystem's business, not ours. ReadOnly is
                // applied last by the caller when it applies at all,
                // since setting it here would block our own later work.
                FileAttributes keep = a & (FileAttributes.Hidden | FileAttributes.System
                    | FileAttributes.NotContentIndexed | FileAttributes.Temporary);
                if (keep != 0) File.SetAttributes(path, File.GetAttributes(path) | keep);
            }
            else if (!OperatingSystem.IsWindows() && meta.UnixMode is { } m)
            {
                File.SetUnixFileMode(path, m);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lost.Add(OperatingSystem.IsWindows() ? "file attributes" : "unix permissions");
        }

        foreach (NamedBlob stream in meta.Streams)
            if (!WriteStream(path, stream)) lost.Add($"alternate data stream {stream.Name}");

        foreach (NamedBlob attr in meta.ExtendedAttributes)
            if (!WriteXattr(path, attr)) lost.Add($"extended attribute {attr.Name}");

        return lost;
    }

    // ---- Windows alternate data streams ----
    //
    // The Mark-of-the-Web lives here, as Zone.Identifier. There is no
    // managed enumeration of streams, so this is FindFirstStreamW and its
    // pair - a handful of lines against a preview-versioned package for
    // the same job, which is the trade R1.5 already made once.

    const int FindStreamInfoStandard = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Win32FindStreamData
    {
        public long StreamSize;

        // MAX_PATH + 36, the documented size of the stream-name field.
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string StreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstStreamW(string fileName, int infoLevel,
        out Win32FindStreamData data, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FindNextStreamW(IntPtr handle, out Win32FindStreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FindClose(IntPtr handle);

    static IReadOnlyList<NamedBlob> ReadStreams(string path)
    {
        if (!OperatingSystem.IsWindows()) return [];

        var found = new List<NamedBlob>();
        IntPtr handle = IntPtr.Zero;

        try
        {
            handle = FindFirstStreamW(path, FindStreamInfoStandard, out Win32FindStreamData data, 0);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return [];

            do
            {
                string? name = StreamNameOf(data.StreamName);
                if (name is null) continue;               // the file's own content

                try
                {
                    found.Add(new NamedBlob(name, File.ReadAllBytes($"{path}:{name}")));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A stream we cannot read is one we cannot restore.
                    // Recording the name with no data would produce a
                    // silent truncation on the way back, so it is left
                    // out and reported by its absence.
                }
            }
            while (FindNextStreamW(handle, out data));
        }
        catch (DllNotFoundException) { return []; }
        catch (EntryPointNotFoundException) { return []; }
        finally
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) FindClose(handle);
        }

        return found;
    }

    /// <summary>The API answers ":Zone.Identifier:$DATA" for a stream and
    /// "::$DATA" for the file's own content. Only the first is ours.</summary>
    static string? StreamNameOf(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw[0] != ':') return null;
        int end = raw.IndexOf(':', 1);
        string name = end < 0 ? raw[1..] : raw[1..end];
        return name.Length == 0 ? null : name;
    }

    static bool WriteStream(string path, NamedBlob stream)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            File.WriteAllBytes($"{path}:{stream.Name}", stream.Data);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    // ---- macOS extended attributes ----
    //
    // com.apple.quarantine lives here, and it is the same security
    // marker as the Mark-of-the-Web wearing the other platform's name.
    // Linux's listxattr takes three arguments where macOS takes four, so
    // this is deliberately macOS-only: R9.1 requires Windows and macOS,
    // and welcomes Linux only where it is free, which this is not.

    [DllImport("libc", EntryPoint = "listxattr", SetLastError = true)]
    static extern nint ListXattr(string path, byte[]? buffer, nint size, int options);

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    static extern nint GetXattr(string path, string name, byte[]? value, nint size, uint position, int options);

    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    static extern int SetXattr(string path, string name, byte[] value, nint size, uint position, int options);

    static IReadOnlyList<NamedBlob> ReadXattrs(string path)
    {
        if (!OperatingSystem.IsMacOS()) return [];

        try
        {
            nint size = ListXattr(path, null, 0, 0);
            if (size <= 0) return [];

            byte[] buffer = new byte[size];
            if (ListXattr(path, buffer, size, 0) <= 0) return [];

            var found = new List<NamedBlob>();
            foreach (string name in NullSeparated(buffer))
            {
                nint valueSize = GetXattr(path, name, null, 0, 0, 0);
                if (valueSize < 0) continue;

                byte[] value = new byte[valueSize];
                if (valueSize > 0 && GetXattr(path, name, value, valueSize, 0, 0) < 0) continue;
                found.Add(new NamedBlob(name, value));
            }

            return found;
        }
        catch (DllNotFoundException) { return []; }
        catch (EntryPointNotFoundException) { return []; }
    }

    static bool WriteXattr(string path, NamedBlob attr)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            return SetXattr(path, attr.Name, attr.Data, attr.Data.Length, 0, 0) == 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    static IEnumerable<string> NullSeparated(byte[] buffer)
    {
        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != 0) continue;
            if (i > start) yield return Encoding.UTF8.GetString(buffer, start, i - start);
            start = i + 1;
        }
    }
}
