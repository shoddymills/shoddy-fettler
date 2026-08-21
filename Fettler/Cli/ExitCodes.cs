using Fettler.Core;

namespace Fettler.Cli;

/// <summary>
/// R3.5: each distinct failure class gets its own code, so a script can
/// branch on <c>$LASTEXITCODE</c> without parsing text - which matters
/// most on the platform where parsing text is hardest, since PowerShell
/// 5.1 has no <c>&amp;&amp;</c> and callers chain on exit codes instead.
///
/// <para>The numbers are fixed and documented. Changing one silently
/// breaks every script that branched on it, so a new failure class takes
/// a new number rather than reusing a retired one.</para>
/// </summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Fault = 1;          // an unexpected internal fault, not a stated outcome
    public const int Invalid = 2;        // the request did not make sense
    public const int NotFound = 3;
    public const int OutsideRoot = 4;
    public const int TargetExists = 5;
    public const int Stale = 6;
    public const int Conflict = 7;
    public const int Refused = 8;

    /// <summary>R3.13: the operating system declined, which is not
    /// Fettler declining. A sandbox denial reported as a containment
    /// refusal sends the caller to debug a boundary that is working.</summary>
    public const int Denied = 9;

    public const int TimedOut = 10;

    /// <summary>The path governs Fettler, so Fettler will not write it.
    /// Distinct from Refused because no flag, permission or configuration
    /// changes the answer.</summary>
    public const int Governed = 11;

    /// <summary>A write would have introduced a credential. Distinct
    /// from Governed because this one CAN be allowed - by taking the
    /// secret out, or by a person saying it is not one.</summary>
    public const int Credential = 12;

    /// <summary>A response would have disclosed regulated data out of a
    /// screened scope, or the screen that would have judged it could not
    /// run. Distinct from Credential because that one is a write going
    /// in and this one is a payload coming out.</summary>
    public const int Screened = 13;

    public static int Of(Outcome outcome) => outcome switch
    {
        Outcome.Ok => Ok,
        Outcome.Invalid => Invalid,
        Outcome.NotFound => NotFound,
        Outcome.OutsideRoot => OutsideRoot,
        Outcome.TargetExists => TargetExists,
        Outcome.Stale => Stale,
        Outcome.Conflict => Conflict,
        Outcome.Refused => Refused,
        Outcome.Denied => Denied,
        Outcome.TimedOut => TimedOut,
        Outcome.Governed => Governed,
        Outcome.Credential => Credential,
        Outcome.Screened => Screened,
        _ => Fault,
    };

    /// <summary>The name a machine-readable result carries, so a caller
    /// reading JSON never has to map a number back.</summary>
    public static string NameOf(Outcome outcome) => outcome switch
    {
        Outcome.Ok => "ok",
        Outcome.Invalid => "invalid",
        Outcome.NotFound => "not-found",
        Outcome.OutsideRoot => "outside-root",
        Outcome.TargetExists => "target-exists",
        Outcome.Stale => "stale",
        Outcome.Conflict => "conflict",
        Outcome.Refused => "refused",
        Outcome.Denied => "denied",
        Outcome.TimedOut => "timed-out",
        Outcome.Governed => "governed",
        Outcome.Credential => "credential",
        Outcome.Screened => "screened",
        _ => "fault",
    };
}
