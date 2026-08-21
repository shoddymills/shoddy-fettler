namespace Fettler.Core;

public sealed record MoveReport(
    ContainedPath From, ContainedPath To, bool Atomic, bool CaseOnly, bool CrossedRoot, bool Directory);

public sealed record CopyReport(
    ContainedPath From, ContainedPath To, int Files, int Directories, long Bytes,
    bool CrossedRoot, IReadOnlyList<string> LinksSkipped);

public sealed record DeleteReport(ContainedPath Path, int Files, int Directories);

public sealed record MadeReport(ContainedPath Path, bool Created);

public sealed record ExecReport(ContainedPath Path, bool Executable, bool Supported);

/// <summary>
/// The whole-file operations of R6. None of these inspects content.
///
/// <para><b>No wildcard reaches any of them (R6.4).</b> Delete, move and
/// overwriting copy take explicit paths, one target at a time. A
/// pattern-driven bulk delete is exactly the operation whose blast
/// radius nobody predicts correctly, and the refusal is checked here,
/// once, rather than in each front end where one of them would forget.</para>
/// </summary>
public static class FileOps
{
    /// <summary>
    /// R6.4's gate. A pattern in a destructive path is refused before
    /// anything is resolved, so there is no code path on which a
    /// wildcard could be expanded by accident.
    /// </summary>
    static Result<T> RefuseWildcard<T>(string given, string verb)
    {
        if (given.Contains('*') || given.Contains('?'))
            return Result<T>.Fail(Outcome.Refused,
                $"{verb} does not accept a pattern; name one path");
        return Result<T>.Ok(default!);
    }

    // ---- create ----

    /// <summary>An empty file, refusing rather than truncating one that
    /// is already there (R6.6).</summary>
    public static Result<MadeReport> NewFile(Roots roots, string path)
    {
        Result<ContainedPath> p = roots.Resolve(path, Permission.Create);
        if (!p.IsOk) return p.Carry<MadeReport>();

        if (File.Exists(p.Value.Full) || Directory.Exists(p.Value.Full))
            return Result<MadeReport>.Fail(Outcome.TargetExists,
                "something is already there; new never truncates", p.Value.Display);

        string? parent = Path.GetDirectoryName(p.Value.Full);
        if (parent is null || !Directory.Exists(parent))
            return Result<MadeReport>.Fail(Outcome.NotFound,
                "the directory to create it in does not exist", p.Value.Display);

        try
        {
            using (File.Open(p.Value.Full, FileMode.CreateNew, FileAccess.Write)) { }
            return Result<MadeReport>.Ok(new MadeReport(p.Value, Created: true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<MadeReport>.Fail(Outcome.Denied, TextIo.Denied(p.Value, e), p.Value.Display);
        }
    }

    public static Result<MadeReport> MakeDirectory(Roots roots, string path)
    {
        Result<ContainedPath> p = roots.Resolve(path, Permission.Create);
        if (!p.IsOk) return p.Carry<MadeReport>();

        if (File.Exists(p.Value.Full))
            return Result<MadeReport>.Fail(Outcome.TargetExists,
                "a file is already using that name", p.Value.Display);

        if (Directory.Exists(p.Value.Full))
            return Result<MadeReport>.Ok(new MadeReport(p.Value, Created: false));

        try
        {
            Directory.CreateDirectory(p.Value.Full);
            return Result<MadeReport>.Ok(new MadeReport(p.Value, Created: true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<MadeReport>.Fail(Outcome.Denied, TextIo.Denied(p.Value, e), p.Value.Display);
        }
    }

    // ---- move ----

    /// <summary>
    /// One operation for rename and relocation alike (R6.2), on a file or
    /// a directory alike (R6.13), reporting which atomicity guarantee it
    /// actually gave (R6.7).
    /// </summary>
    public static Result<MoveReport> Move(Roots roots, string from, string to, bool overwrite)
    {
        Result<MoveReport> guard = RefuseWildcard<MoveReport>(from, "move");
        if (!guard.IsOk) return guard;
        guard = RefuseWildcard<MoveReport>(to, "move");
        if (!guard.IsOk) return guard;

        // B.7: a move is TWO permissions, and which two depends on where
        // it lands. Rename on the source and create at the destination
        // covers reorganising within a scope; leaving the scope needs
        // delete as well, because from here a file that leaves is gone
        // either way. That is what makes "may reorganise, may not
        // destroy" expressible.
        Result<ContainedPath> source = roots.Resolve(from, Permission.Rename);
        if (!source.IsOk) return source.Carry<MoveReport>();
        Result<ContainedPath> target = roots.Resolve(to, Permission.Create);
        if (!target.IsOk) return target.Carry<MoveReport>();

        string governing = source.Value.Governing.Length == 0
            ? roots.PathOf(source.Value.RootName)
            : source.Value.Governing;

        if (!Roots.IsWithin(governing, target.Value.Full))
        {
            Result<ContainedPath> leaving = Roots.Allow(source.Value, Permission.Delete);
            if (!leaving.IsOk) return leaving.Carry<MoveReport>();
        }

        bool isDirectory = Directory.Exists(source.Value.Full);
        if (!isDirectory && !File.Exists(source.Value.Full))
            return Result<MoveReport>.Fail(Outcome.NotFound, "no such file or directory", source.Value.Display);

        bool caseOnly = IsCaseOnlyRename(source.Value.Full, target.Value.Full);

        if (!caseOnly && (File.Exists(target.Value.Full) || Directory.Exists(target.Value.Full)))
        {
            if (!overwrite)
                return Result<MoveReport>.Fail(Outcome.TargetExists,
                    "something is already there; pass overwrite to replace it", target.Value.Display);

            // Replacing something destroys it, so the destination needs
            // delete as well as create.
            Result<ContainedPath> replacing = Roots.Allow(target.Value, Permission.Delete);
            if (!replacing.IsOk) return replacing.Carry<MoveReport>();

            Result<MoveReport> readOnly = SafeWrite.RefuseIfReadOnly<MoveReport>(target.Value.Full, target.Value);
            if (!readOnly.IsOk) return readOnly;
        }

        bool atomic = SameVolume(source.Value.Full, target.Value.Full);

        try
        {
            if (caseOnly) MoveCaseOnly(source.Value.Full, target.Value.Full, isDirectory);
            else if (isDirectory) MoveDirectory(source.Value.Full, target.Value.Full, overwrite);
            else File.Move(source.Value.Full, target.Value.Full, overwrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<MoveReport>.Fail(Outcome.Denied, TextIo.Denied(source.Value, e), source.Value.Display);
        }

        return Result<MoveReport>.Ok(new MoveReport(
            source.Value, target.Value, atomic, caseOnly,
            CrossedRoot: !source.Value.RootName.Equals(target.Value.RootName, Roots.PathComparison),
            Directory: isDirectory));
    }

    /// <summary>
    /// R6.3: both required platforms are case-insensitive and
    /// case-preserving, and the naive <c>move a.txt A.txt</c> either
    /// fails or silently does nothing on both. Going through a name
    /// neither spelling occupies is what makes it a real operation.
    /// </summary>
    static void MoveCaseOnly(string from, string to, bool isDirectory)
    {
        string parent = Path.GetDirectoryName(from) ?? from;
        string staging = Path.Combine(parent, $".fettle-case-{Guid.NewGuid():N}");

        if (isDirectory)
        {
            Directory.Move(from, staging);
            Directory.Move(staging, to);
        }
        else
        {
            File.Move(from, staging);
            File.Move(staging, to);
        }
    }

    static void MoveDirectory(string from, string to, bool overwrite)
    {
        if (overwrite && Directory.Exists(to)) Directory.Delete(to, recursive: true);
        Directory.Move(from, to);
    }

    static bool IsCaseOnlyRename(string from, string to) =>
        !string.Equals(from, to, StringComparison.Ordinal)
        && string.Equals(from, to, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether two paths sit on one volume, which is what decides
    /// between a rename and a copy-then-delete and therefore what R6.7
    /// has to report. Mount points are compared rather than guessed at,
    /// so a macOS <c>/Volumes/...</c> is told apart from <c>/</c>.
    /// </summary>
    static bool SameVolume(string a, string b)
    {
        try
        {
            return MountOf(a).Equals(MountOf(b), Roots.PathComparison);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static string MountOf(string path)
    {
        string best = Path.GetPathRoot(Path.GetFullPath(path)) ?? "/";
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string mount = drive.RootDirectory.FullName;
            if (mount.Length > best.Length && Roots.IsWithin(mount, path)) best = mount;
        }
        return best;
    }

    // ---- copy ----

    /// <summary>
    /// A copy, requiring <c>recursive</c> to be said out loud for a tree
    /// (R6.8) and carrying the executable bit (R6.9).
    ///
    /// <para><b>Symbolic links inside a tree are not followed and not
    /// recreated (R6.8, R6.11).</b> Following one copies whatever it
    /// points at, possibly from outside the root and possibly forever
    /// round a cycle; recreating one needs Developer Mode or elevation
    /// on Windows. Each is named in the answer instead, so a caller sees
    /// what the copy does not contain rather than discovering it.</para>
    /// </summary>
    public static Result<CopyReport> Copy(Roots roots, string from, string to, bool recursive,
        bool overwrite, bool preserveTimes)
    {
        Result<ContainedPath> source = roots.Resolve(from, Permission.Read);
        if (!source.IsOk) return source.Carry<CopyReport>();
        Result<ContainedPath> target = roots.Resolve(to, Permission.Create);
        if (!target.IsOk) return target.Carry<CopyReport>();

        if (overwrite && (File.Exists(target.Value.Full) || Directory.Exists(target.Value.Full)))
        {
            Result<ContainedPath> replacing = Roots.Allow(target.Value, Permission.Delete);
            if (!replacing.IsOk) return replacing.Carry<CopyReport>();
        }

        bool crossed = !source.Value.RootName.Equals(target.Value.RootName, Roots.PathComparison);

        if (Directory.Exists(source.Value.Full))
        {
            if (!recursive)
                return Result<CopyReport>.Fail(Outcome.Refused,
                    "that is a directory; pass recursive to copy a tree", source.Value.Display);

            // A recursive copy reads everything below the source without
            // naming any of it, so a hidden scope inside would be copied
            // out into the open where anything may read it.
            Result<ContainedPath> readable =
                roots.AllowThroughout(source.Value, Permission.List | Permission.Read);
            if (!readable.IsOk) return readable.Carry<CopyReport>();

            var skipped = new List<string>();
            int files = 0, directories = 0;
            long bytes = 0;

            try
            {
                CopyTree(source.Value.Full, target.Value.Full, source.Value.Full, overwrite, preserveTimes,
                    ref files, ref directories, ref bytes, skipped);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Result<CopyReport>.Fail(Outcome.Denied, TextIo.Denied(source.Value, e), source.Value.Display);
            }

            return Result<CopyReport>.Ok(new CopyReport(
                source.Value, target.Value, files, directories, bytes, crossed, skipped));
        }

        if (!File.Exists(source.Value.Full))
            return Result<CopyReport>.Fail(Outcome.NotFound, "no such file", source.Value.Display);

        // R6.4: an overwriting copy is destructive, so it takes an
        // explicit path like the others.
        if (overwrite)
        {
            Result<CopyReport> guard = RefuseWildcard<CopyReport>(to, "copy");
            if (!guard.IsOk) return guard;
        }

        if (File.Exists(target.Value.Full) && !overwrite)
            return Result<CopyReport>.Fail(Outcome.TargetExists,
                "something is already there; pass overwrite to replace it", target.Value.Display);

        try
        {
            long size = CopyFile(source.Value.Full, target.Value.Full, overwrite, preserveTimes);
            return Result<CopyReport>.Ok(new CopyReport(
                source.Value, target.Value, 1, 0, size, crossed, []));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<CopyReport>.Fail(Outcome.Denied, TextIo.Denied(source.Value, e), source.Value.Display);
        }
    }

    static void CopyTree(string from, string to, string treeRoot, bool overwrite, bool preserveTimes,
        ref int files, ref int directories, ref long bytes, List<string> skipped)
    {
        Directory.CreateDirectory(to);
        directories++;

        foreach (string entry in Directory.EnumerateFileSystemEntries(from))
        {
            string name = Path.GetFileName(entry);
            string destination = Path.Combine(to, name);
            bool isDirectory = Directory.Exists(entry);

            if (Tree.Facts(entry, isDirectory).Link != LinkKind.None)
            {
                skipped.Add(Path.GetRelativePath(treeRoot, entry).Replace('\\', '/'));
                continue;
            }

            if (isDirectory)
            {
                CopyTree(entry, destination, treeRoot, overwrite, preserveTimes,
                    ref files, ref directories, ref bytes, skipped);
                continue;
            }

            bytes += CopyFile(entry, destination, overwrite, preserveTimes);
            files++;
        }
    }

    static long CopyFile(string from, string to, bool overwrite, bool preserveTimes)
    {
        File.Copy(from, to, overwrite);

        // R6.9: the bit git tracks and the only one that means the same
        // thing on both required platforms. NTFS has none to carry, so
        // on Windows this is a no-op rather than an invention.
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(to, File.GetUnixFileMode(from)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        // R6.12: a build decides what to rebuild from modified times, so
        // a copy that resets mtime triggers or suppresses a rebuild, and
        // which of those the caller wanted is theirs to say.
        //
        // Both branches are explicit on purpose. Doing nothing is NOT the
        // same behaviour on both platforms: Windows File.Copy carries the
        // source's modified time across, where POSIX cp gives the copy a
        // new one - so leaving it to the platform makes `copy` mean two
        // different things, which is exactly what R9.1 forbids.
        try
        {
            File.SetLastWriteTimeUtc(to, preserveTimes ? File.GetLastWriteTimeUtc(from) : DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        try { return new FileInfo(to).Length; }
        catch (IOException) { return 0; }
    }

    // ---- delete ----

    public static Result<DeleteReport> Delete(Roots roots, string path, bool recursive, bool force)
    {
        Result<DeleteReport> guard = RefuseWildcard<DeleteReport>(path, "delete");
        if (!guard.IsOk) return guard;

        Result<ContainedPath> p = roots.Resolve(path, Permission.Delete);
        if (!p.IsOk) return p.Carry<DeleteReport>();

        bool isDirectory = Directory.Exists(p.Value.Full);
        if (!isDirectory && !File.Exists(p.Value.Full))
            return Result<DeleteReport>.Fail(Outcome.NotFound, "no such file or directory", p.Value.Display);

        if (isDirectory)
        {
            int files, directories;
            try
            {
                files = Directory.EnumerateFiles(p.Value.Full, "*", SearchOption.AllDirectories).Count();
                directories = Directory.EnumerateDirectories(p.Value.Full, "*", SearchOption.AllDirectories).Count() + 1;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Result<DeleteReport>.Fail(Outcome.Denied, TextIo.Denied(p.Value, e), p.Value.Display);
            }

            // R6.5: a directory with anything in it needs the flag.
            if ((files > 0 || directories > 1) && !recursive)
                return Result<DeleteReport>.Fail(Outcome.Refused,
                    "the directory is not empty; pass recursive", p.Value.Display);

            // And a recursive delete reaches paths it never named, which
            // may be governed by a scope of their own.
            if (recursive)
            {
                Result<ContainedPath> throughout = roots.AllowThroughout(p.Value, Permission.Delete);
                if (!throughout.IsOk) return throughout.Carry<DeleteReport>();
            }

            try
            {
                if (force) ClearReadOnlyUnder(p.Value.Full);
                Directory.Delete(p.Value.Full, recursive);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Result<DeleteReport>.Fail(Outcome.Denied, TextIo.Denied(p.Value, e), p.Value.Display);
            }

            return Result<DeleteReport>.Ok(new DeleteReport(p.Value, files, directories));
        }

        if (!force)
        {
            Result<DeleteReport> readOnly = SafeWrite.RefuseIfReadOnly<DeleteReport>(p.Value.Full, p.Value);
            if (!readOnly.IsOk) return readOnly;
        }

        try
        {
            if (force && OperatingSystem.IsWindows())
                File.SetAttributes(p.Value.Full, File.GetAttributes(p.Value.Full) & ~FileAttributes.ReadOnly);

            File.Delete(p.Value.Full);
            return Result<DeleteReport>.Ok(new DeleteReport(p.Value, 1, 0));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<DeleteReport>.Fail(Outcome.Denied, TextIo.Denied(p.Value, e), p.Value.Display);
        }
    }

    static void ClearReadOnlyUnder(string directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---- the one permission bit ----

    /// <summary>
    /// Set or clear the executable bit (R6.9). On Windows there is no
    /// such bit, and the answer says so rather than pretending to have
    /// done something.
    /// </summary>
    public static Result<ExecReport> SetExecutable(Roots roots, string path, bool executable)
    {
        // The executable bit is a property of the file, so changing it is
        // an update to the file - not the `execute` permission, which
        // governs whether a TASK may run.
        Result<ContainedPath> p = roots.Resolve(path, Permission.Update);
        if (!p.IsOk) return p.Carry<ExecReport>();

        if (!File.Exists(p.Value.Full))
            return Result<ExecReport>.Fail(Outcome.NotFound, "no such file", p.Value.Display);

        if (OperatingSystem.IsWindows())
            return Result<ExecReport>.Ok(new ExecReport(p.Value, executable, Supported: false));

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(p.Value.Full);
            const UnixFileMode bits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            File.SetUnixFileMode(p.Value.Full, executable ? mode | bits : mode & ~bits);
            return Result<ExecReport>.Ok(new ExecReport(p.Value, executable, Supported: true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result<ExecReport>.Fail(Outcome.Denied, TextIo.Denied(p.Value, e), p.Value.Display);
        }
    }

    /// <summary>
    /// Carry an archive member's executable bit onto the file just
    /// written. Already resolved and already inside the boundary, so
    /// there is no permission question left to ask here.
    ///
    /// <para>Silent on failure by design: the file itself is written and
    /// correct, and a bit that could not be set on a filesystem that has
    /// none - or has refused it - must not turn a good extraction into a
    /// failed one. Windows has no such bit at all, which is exactly why
    /// the release tars are cut on a Linux runner.</para>
    /// </summary>
    public static void MarkExecutable(string full)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            const UnixFileMode bits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(full, File.GetUnixFileMode(full) | bits);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
