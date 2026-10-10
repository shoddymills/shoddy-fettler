namespace Fettler.Core;

/// <summary>
/// What a checkout has checked out: a branch by name, or a commit by its
/// short hash when <c>HEAD</c> is detached. <c>Repository</c> is the
/// folder holding <c>.git</c>, which may be the tree's own top or a
/// folder above it.
/// </summary>
public sealed record Checkout(string Repository, string Name, bool Detached);

/// <summary>
/// The branch a tree is on, for <c>roots</c> to name.
///
/// <para><b>A narrow exception, on the doctor's precedent.</b> The walk
/// upward from a tree's top may leave the tree, because a tree is often
/// a folder inside a repository rather than the repository itself. So
/// the read is allowed by a fixed rule, two compiled-in file names and
/// nothing else, checked before every read, and never by a caller's
/// path: <c>.git</c> when it is a file, which is how a worktree or a
/// submodule points elsewhere, and <c>HEAD</c>. No ref, index, object or
/// configuration file is opened.</para>
///
/// <para><b>Local, and only local.</b> This is what this machine has
/// checked out. It says nothing about what a remote holds; only a fetch
/// answers that.</para>
///
/// <para><b>Nothing here fails.</b> <c>roots</c> is the call people make
/// to find out where they stand, so a <c>.git</c> that cannot be read
/// answers as no <c>.git</c> rather than breaking the rest of the
/// answer.</para>
/// </summary>
public static class Checkouts
{
    const string GitName = ".git";
    const string HeadName = "HEAD";

    /// <summary>Larger than any <c>HEAD</c> or <c>.git</c> pointer file
    /// git writes. Anything bigger is not one, and is not read.</summary>
    const long MaxBytes = 4096;

    /// <summary>The checkout at or above <paramref name="top"/>, or null
    /// when there is none or it cannot be read.</summary>
    public static Checkout? Of(string top)
    {
        try
        {
            for (DirectoryInfo? folder = new(top); folder is not null; folder = folder.Parent)
            {
                string git = Path.Combine(folder.FullName, GitName);

                if (Directory.Exists(git))
                    return FromHead(folder.FullName, Path.Combine(git, HeadName));

                if (File.Exists(git))
                    return Pointed(git) is { } elsewhere
                        ? FromHead(folder.FullName, Path.Combine(elsewhere, HeadName))
                        : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException
                                      or System.Security.SecurityException)
        {
            return null;
        }

        return null;
    }

    /// <summary>The rule, asked before every read: a file called
    /// <c>.git</c> or <c>HEAD</c>, and nothing else.</summary>
    static bool Allows(string path) =>
        Path.GetFileName(path) is GitName or HeadName;

    static string? ReadSmall(string path)
    {
        if (!Allows(path)) return null;

        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxBytes) return null;

        return File.ReadAllText(path);
    }

    /// <summary>The folder a <c>.git</c> file points at with
    /// <c>gitdir:</c>. A relative path is relative to the folder holding
    /// the <c>.git</c> file.</summary>
    static string? Pointed(string gitFile)
    {
        string? text = ReadSmall(gitFile);
        if (text is null) return null;

        const string prefix = "gitdir:";
        string line = text.Trim();
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;

        string target = line[prefix.Length..].Trim();
        if (target.Length == 0) return null;

        return Path.GetFullPath(target, Path.GetDirectoryName(gitFile)!);
    }

    static Checkout? FromHead(string repository, string head)
    {
        string? text = ReadSmall(head);
        if (text is null) return null;

        string line = text.Trim();

        const string refPrefix = "ref:";
        if (line.StartsWith(refPrefix, StringComparison.Ordinal))
        {
            string name = line[refPrefix.Length..].Trim();

            const string branches = "refs/heads/";
            if (name.StartsWith(branches, StringComparison.Ordinal)) name = name[branches.Length..];

            return name.Length == 0 ? null : new Checkout(repository, name, Detached: false);
        }

        // A detached HEAD holds the commit itself: forty hex digits, or
        // sixty-four in a SHA-256 repository.
        if (line.Length is 40 or 64 && line.All(char.IsAsciiHexDigit))
            return new Checkout(repository, line[..7], Detached: true);

        return null;
    }
}
