namespace Fettler.Core;

/// <summary>
/// How an operation ended. Every failure Fettler can produce is one of
/// these, and the list is deliberately short: R3.5 requires a script to
/// branch on the outcome without parsing text, and an outcome set that
/// grows a member per message is one nobody can branch on.
///
/// <para><b>Denied is not Refused, and the split is load-bearing (R3.13).</b>
/// Refused means Fettler declined: the path left the root, the file was
/// already there, the caller did not pass --overwrite. Denied means the
/// operating system declined an operation Fettler was perfectly willing
/// to perform - a sandbox, a restricted account, a file another process
/// holds open, a read-only attribute. Collapsing the two sends a caller
/// to debug a containment bug that is not there.</para>
/// </summary>
public enum Outcome
{
    /// <summary>It worked.</summary>
    Ok = 0,

    /// <summary>The named path is not there.</summary>
    NotFound = 1,

    /// <summary>The path resolved outside every declared root (R8).</summary>
    OutsideRoot = 2,

    /// <summary>Something is already at the destination and the caller
    /// did not say to replace it (R6.6).</summary>
    TargetExists = 3,

    /// <summary>The file changed since the caller read it (R5.5).</summary>
    Stale = 4,

    /// <summary>Two edits in one batch contend for the same text, or a
    /// pattern matched in more places than it could be applied (R5.3).</summary>
    Conflict = 5,

    /// <summary>Fettler declined for a stated reason that is none of the
    /// above: a binary file, a wildcard where none is accepted, a
    /// non-empty directory without --recursive.</summary>
    Refused = 6,

    /// <summary>The operating system declined. Not Fettler's decision,
    /// and never to be reported as one (R3.13).</summary>
    Denied = 7,

    /// <summary>A task outran the timeout it was given (R7.5).</summary>
    TimedOut = 8,

    /// <summary>The request itself did not make sense - an unknown verb,
    /// a missing argument, a pattern that will not compile.</summary>
    Invalid = 9,

    /// <summary>
    /// The path names a file that tells Fettler what it may do, and
    /// Fettler does not write those (see <see cref="RuleFiles"/>).
    ///
    /// <para>Its own member rather than <see cref="Refused"/> because
    /// nothing is missing: no permission, flag or configuration would
    /// have allowed it, and a caller sent looking for one is being sent
    /// somewhere there is nothing to find.</para>
    /// </summary>
    Governed = 10,

    /// <summary>
    /// The write would have INTRODUCED a credential that the file did
    /// not already carry (see <see cref="Secrets"/>).
    ///
    /// <para>Its own member because the remedy is specific and no other
    /// outcome suggests it: take the secret out and use a reference, or
    /// say explicitly that this is not one. A caller told
    /// <see cref="Refused"/> would go hunting for a permission, and a
    /// caller told <see cref="Governed"/> would conclude nothing could
    /// ever allow it - and something can.</para>
    /// </summary>
    Credential = 11,

    /// <summary>
    /// The response would have DISCLOSED regulated personal data out of
    /// a screened scope (see <see cref="Screen"/>).
    ///
    /// <para>Its own member because it is the mirror of
    /// <see cref="Credential"/> and has to be told apart from it: that
    /// one is a write going in, this one is a payload coming out, and a
    /// script branching on "a secret was involved" would take entirely
    /// the wrong action for each. A caller told <see cref="Refused"/>
    /// would go hunting for a permission, and there is none to find -
    /// what changes the answer is reading a narrower range, or a person
    /// editing the screen on that scope.</para>
    ///
    /// <para>It is also the outcome a screen that could not RUN
    /// produces. A sidecar that will not start, a model that will not
    /// load and an inference that outran its timeout all refuse the
    /// disclosure rather than serving it unscreened, so the one thing
    /// this can never mean is that nothing was checked.</para>
    /// </summary>
    Screened = 12,
}

/// <summary>
/// A failure, carrying enough to act on without reading prose. The
/// message is for a person; the outcome is for a script; the path is
/// whichever one went wrong, since an operation naming two paths that
/// fails on one of them has told the caller nothing useful.
/// </summary>
public sealed record Failure(Outcome Outcome, string Message, string? Path = null)
{
    public override string ToString() =>
        Path is null ? Message : $"{Message} ({Path})";
}

/// <summary>
/// An answer or a failure, never both and never neither.
///
/// <para>There are no exceptions across the core's surface. An operation
/// that a caller could reasonably handle comes back as a value, which is
/// the same choice the language this repository is built around makes,
/// and for the same reason: a failure that must be caught is a failure
/// easy to not catch.</para>
/// </summary>
public readonly struct Result<T>
{
    readonly T? value;

    Result(T? value, Failure? failure)
    {
        this.value = value;
        Failure = failure;
    }

    /// <summary>The failure, or null when this is an answer.</summary>
    public Failure? Failure { get; }

    /// <summary>True when there is an answer to read.</summary>
    public bool IsOk => Failure is null;

    /// <summary>The answer. Reading it on a failed result is a bug in
    /// the caller, and throws rather than handing back a default that
    /// would travel a long way before anyone noticed.</summary>
    public T Value => IsOk
        ? value!
        : throw new InvalidOperationException($"read Value of a failed Result: {Failure}");

    public static Result<T> Ok(T value) => new(value, null);

    public static Result<T> Fail(Failure failure) => new(default, failure);

    public static Result<T> Fail(Outcome outcome, string message, string? path = null) =>
        new(default, new Failure(outcome, message, path));

    /// <summary>Carry a failure across a change of answer type. The
    /// alternative is re-writing the same failure at every layer, which
    /// is how a message loses the path it started with.</summary>
    public Result<TOther> Carry<TOther>() => IsOk
        ? throw new InvalidOperationException("carried a successful Result as a failure")
        : Result<TOther>.Fail(Failure!);
}
