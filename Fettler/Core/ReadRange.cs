using System.Globalization;

namespace Fettler.Core;

/// <summary>A path as a read names it, and the range it carries when it
/// carries one: <c>PATH:FROM</c> or <c>PATH:FROM-TO</c>.</summary>
public readonly record struct RangedPath(string Path, int? From, int? To);

/// <summary>
/// The range a path carries on its end.
///
/// <para><b>Why a path may carry one.</b> A read takes several paths and
/// one <c>from</c>, so two files in one call start at the same line, and
/// a caller with a line for each had to make two calls, or read one of
/// them from a line it does not have. A search hit is already written
/// <c>PATH:LINE</c>, and so is a search <c>next</c>, so a hit passes
/// straight to <c>read</c> and the hand-off needs no arithmetic.</para>
///
/// <para><b>The rule is syntactic.</b> A trailing <c>:digits</c> or
/// <c>:digits-digits</c> is always a range. On Windows a name cannot hold
/// a colon. Elsewhere a file whose name ends that way is read by giving
/// it a range of its own: <c>notes:5:1</c> is the file <c>notes:5</c>
/// from line 1. Looking on disk to decide would make the meaning of a
/// call depend on what happens to be there.</para>
/// </summary>
public static class ReadRange
{
    public static Result<RangedPath> Split(string given)
    {
        int colon = given.LastIndexOf(':');
        if (colon <= 0 || colon == given.Length - 1) return Plain(given);

        string suffix = given[(colon + 1)..];
        int dash = suffix.IndexOf('-');
        string first = dash < 0 ? suffix : suffix[..dash];
        string? second = dash < 0 ? null : suffix[(dash + 1)..];

        if (!Digits(first) || (second is not null && !Digits(second))) return Plain(given);

        // All digits and still not a line number: too many of them. Taken
        // as a path, it would be refused as a file that is not there,
        // which is the wrong reason.
        if (!Line(first, out int from)) return TooLarge(given, first);

        int? to = null;
        if (second is not null)
        {
            if (!Line(second, out int end)) return TooLarge(given, second);
            to = end;
        }

        return Result<RangedPath>.Ok(new RangedPath(given[..colon], from, to));
    }

    static Result<RangedPath> Plain(string given) =>
        Result<RangedPath>.Ok(new RangedPath(given, null, null));

    static Result<RangedPath> TooLarge(string given, string number) =>
        Result<RangedPath>.Fail(Outcome.Invalid, $"'{number}' in '{given}' is too large for a line number");

    static bool Line(string digits, out int number) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    static bool Digits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
            if (!char.IsAsciiDigit(c)) return false;
        return true;
    }
}
