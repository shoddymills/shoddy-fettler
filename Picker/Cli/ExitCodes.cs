using Picker.Core;

namespace Picker.Cli;

/// <summary>
/// Each distinct failure class gets its own code, so a script can
/// branch on <c>$LASTEXITCODE</c> without parsing text.
///
/// <para><b>The numbers are fettle's numbers where the words match</b> -
/// ok 0, invalid 2, not-found 3, refused 8, denied 9, timed-out 10,
/// screened 13 - so a script driving both tools branches one way. The
/// gaps are fettle's outcomes this tool cannot produce (a stale hash, a
/// governed file), and they stay gaps: reusing a number for a different
/// word would break the very scripts the alignment is for.</para>
/// </summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Fault = 1;          // an unexpected internal fault, not a stated outcome
    public const int Invalid = 2;        // the request did not make sense
    public const int NotFound = 3;       // not there, or not there as far as anyone can learn
    public const int Refused = 8;        // Picker declined for a stated reason

    /// <summary>The server, network or OS declined - not Picker. A
    /// login failure reported as a gate refusal sends the caller to
    /// debug a boundary that is working.</summary>
    public const int Denied = 9;

    public const int TimedOut = 10;

    /// <summary>A response would have disclosed regulated data out of a
    /// screened scope, or the screen that would have judged it could
    /// not run.</summary>
    public const int Screened = 13;

    public static int Of(Outcome outcome) => outcome switch
    {
        Outcome.Ok => Ok,
        Outcome.Invalid => Invalid,
        Outcome.NotFound => NotFound,
        Outcome.Refused => Refused,
        Outcome.Denied => Denied,
        Outcome.TimedOut => TimedOut,
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
        Outcome.Refused => "refused",
        Outcome.Denied => "denied",
        Outcome.TimedOut => "timed-out",
        Outcome.Screened => "screened",
        _ => "fault",
    };
}
