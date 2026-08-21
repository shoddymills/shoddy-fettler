namespace Fettler.Core;

/// <summary>
/// The files that tell Fettler what it may do, which Fettler may
/// therefore never write.
///
/// <para><b>This is the rule that makes every other rule hold.</b>
/// Without it the permission model is decoration, and the demonstration
/// is two commands long: declare a tree, write a task into its
/// configuration, run what you just wrote. That was live in this tool,
/// and it defeats every other permission at once - a scope protected
/// from <c>delete</c> means nothing to a task that can run
/// <c>del</c>.</para>
///
/// <para><b>An explicit grant is not enough on its own either.</b> If the
/// declaration can be written by the thing the grant applies to, the
/// grant has no bound: whoever holds <see cref="Permission.Execute"/>
/// also chooses what executes. So the refusal is compiled in, applies at
/// every permission level, and is not configurable.</para>
///
/// <para><b>It is its own outcome and its own wording</b>, not a
/// permission denial. Nothing is missing. A caller told "you lack update"
/// looks for the grant that would fix it; there is no such grant, and
/// saying so is the honest answer.</para>
///
/// <para><b>The client's files are the same argument.</b> Fettler's own
/// declaration was protected while <c>.claude/settings.json</c> - the
/// file naming which tools the assistant may use at all - stayed an
/// ordinary writable file inside the tree. A model that can edit its own
/// deny list has no deny list, and being asked to edit it is not the
/// safeguard: an instruction can arrive from a prompt, a file it just
/// read, or a misreading of either. So the files that decide what may
/// run, and what may be called, are refused on the same terms as this
/// tool's own. <c>CLAUDE.md</c> deliberately is NOT among them - it
/// persuades rather than permits, and an assistant maintaining a project's
/// instructions is ordinary work.</para>
///
/// <para><b><c>setup</c> is unaffected, by construction rather than by
/// exemption.</b> It writes those files with plain file IO and never
/// resolves a path through <see cref="Roots"/>, so this guard - which
/// lives on the resolve path every tool verb takes - is not in its way.
/// Scaffolding a machine stays a person's act performed once at a
/// terminal, which is the same reason the verb is never offered to a
/// model.</para>
///
/// <para><b>The bound, stated:</b> this is reliable, not tamper-proof. It
/// stops an assistant reaching these files through a tool call. It does
/// not stop the person who wrote them opening them in an editor, and
/// nothing here should try - that person is the author of the rules, not
/// a threat to them.</para>
/// </summary>
public static class RuleFiles
{
    /// <summary>Matched on the file name alone, at any depth. Each of
    /// these names means one thing wherever it appears.</summary>
    public static readonly string[] Names =
    [
        RootsFile.FileName,
        RootsFile.LocalFileName,
        ".mcp.json",
    ];

    /// <summary>
    /// Matched on the last TWO segments, because the file name alone is
    /// far too common to claim. A tool that refused every
    /// <c>settings.json</c> in a tree would be unusable and would also be
    /// wrong: <c>.vscode/settings.json</c> is a person's editor
    /// configuration and nothing to do with what an assistant may do.
    /// </summary>
    public static readonly string[] Tails =
    [
        ".claude/settings.json",
        ".claude/settings.local.json",
        ".vscode/mcp.json",
    ];

    /// <summary>Everything refused, for the message that says so.</summary>
    public static readonly string[] Ruled = [.. Names, .. Tails];

    /// <summary>
    /// Whether a path names one of them.
    ///
    /// <para>Matched on the file name alone, at any depth: a
    /// <c>.fettler.json</c> in a subdirectory governs that subtree when a
    /// command is run from inside it, so protecting only the one at the
    /// top would leave every other one writable. Matched by name and not
    /// by prefix, so <c>.fettler.json.bak</c> and <c>.fettler.json.md</c>
    /// are ordinary files - they govern nothing, and refusing them would
    /// be superstition rather than a rule.</para>
    /// </summary>
    public static bool Governs(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        foreach (string ruled in Names)
            if (name.Equals(ruled, Roots.PathComparison)) return true;

        // Separators are normalized rather than compared as they came:
        // the same file is written a\b on one platform and a/b on the
        // other, and a rule that held on only one of them would be a rule
        // that quietly did not apply on the other.
        string slashed = path.Replace('\\', '/');
        foreach (string tail in Tails)
            if (slashed.Equals(tail, Roots.PathComparison)
                || slashed.EndsWith("/" + tail, Roots.PathComparison))
                return true;

        return false;
    }

    /// <summary>The refusal, worded once so every path that can put bytes
    /// somewhere says the same thing.</summary>
    public static Result<T> Refuse<T>(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        return Result<T>.Fail(Outcome.Governed,
            $"{name} is one of the files that say what this tool and the "
            + "assistant driving it may do, and it does not edit those. "
            + "Change it with an editor. "
            + $"The files are: {string.Join(", ", Ruled)}",
            path);
    }
}
