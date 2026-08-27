namespace Picker.Core;

/// <summary>
/// How an operation ended. Every failure Picker can produce is one of
/// these, and the list is deliberately short, for Fettler's stated
/// reason: a script must branch on the outcome without parsing text, and
/// an outcome set that grows a member per message is one nobody can
/// branch on.
///
/// <para><b>Denied is not Refused, and the split is load-bearing.</b>
/// Refused means Picker declined: the statement was not a SELECT, the
/// name was four parts, the query reached for the system catalog.
/// Denied means the server or the network declined an operation Picker
/// was perfectly willing to perform - a login without rights, a
/// connection that will not open. Collapsing the two sends a caller to
/// debug a gate that is working.</para>
///
/// <para><b>NotFound deliberately covers hidden as well as absent.</b>
/// An object or column outside the grant is refused with this outcome
/// and the same words as one that is not there, so the difference cannot
/// be probed - the Permission.List rule, applied to a catalog. This is
/// why Picker has no OutsideRoot: separate wordings would let a caller
/// map the server by reading refusals.</para>
/// </summary>
public enum Outcome
{
    /// <summary>It worked.</summary>
    Ok = 0,

    /// <summary>The request itself did not make sense - an unknown verb,
    /// a missing argument, SQL that will not parse, a configuration that
    /// will not load.</summary>
    Invalid = 1,

    /// <summary>The named database, table, view or column is not there -
    /// or is not there as far as this caller can ever learn.</summary>
    NotFound = 2,

    /// <summary>Picker declined for a stated reason: a statement that is
    /// not a SELECT, a four-part name, a system-catalog reference, a
    /// construct the gate does not recognise, a view's definition text.</summary>
    Refused = 3,

    /// <summary>The server, the network or the operating system
    /// declined. Not Picker's decision, and never to be reported as
    /// one.</summary>
    Denied = 4,

    /// <summary>The query outran the timeout it was given.</summary>
    TimedOut = 5,

    /// <summary>The response would have disclosed regulated personal
    /// data out of a screened scope, or the screen that would have
    /// judged it could not run. The one thing this can never mean is
    /// that nothing was checked.</summary>
    Screened = 6,
}

/// <summary>
/// A failure, carrying enough to act on without reading prose. The
/// message is for a person; the outcome is for a script; the subject is
/// whichever name went wrong, when one did.
/// </summary>
public sealed record Failure(Outcome Outcome, string Message, string? Subject = null)
{
    public override string ToString() =>
        Subject is null ? Message : $"{Message} ({Subject})";
}

/// <summary>
/// An answer or a failure, never both and never neither. There are no
/// exceptions across the core's surface - the same choice Fettler makes,
/// for the same reason: a failure that must be caught is a failure easy
/// to not catch.
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

    public static Result<T> Fail(Outcome outcome, string message, string? subject = null) =>
        new(default, new Failure(outcome, message, subject));

    /// <summary>Carry a failure across a change of answer type, so a
    /// message never loses the name it started with.</summary>
    public Result<TOther> Carry<TOther>() => IsOk
        ? throw new InvalidOperationException("carried a successful Result as a failure")
        : Result<TOther>.Fail(Failure!);
}
