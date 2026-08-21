using System.Text.RegularExpressions;

namespace Fettler.Core;

/// <summary>
/// The second tier's door: something that can judge a payload for the
/// categories a model is needed for.
///
/// <para><b>An interface because the gate must be testable without a
/// model.</b> R7.1 requires the fail-closed behaviour - death, timeout, a
/// malformed answer - to be proven with a fake, and a gate that could
/// only be exercised by installing 400 MB of weights is one nobody would
/// exercise.</para>
/// </summary>
public interface IScreener
{
    /// <summary>
    /// The regulated entities in a payload, for the categories asked
    /// about.
    ///
    /// <para><b>A failure here is a refusal, never a pass.</b> Whatever
    /// went wrong - the sidecar would not start, a model is missing, the
    /// answer did not parse, the clock ran out - the caller turns it into
    /// <see cref="Outcome.Screened"/>. The message must therefore say
    /// what a person can do about it.</para>
    /// </summary>
    Result<IReadOnlyList<ScreenFinding>> Inspect(string payload, Screened categories);
}

/// <summary>
/// The one seam every payload leaves through.
///
/// <para><b>One function, for the same reason
/// <see cref="SafeWrite"/> is one function.</b> The write side puts its
/// credential check where every verb that writes text already passes, so
/// there is no verb to forget. This is that arrangement pointed
/// outwards: <c>read</c>, <c>search</c> and every document rendering
/// hand their payload to this before it is serialized, and a verb added
/// later that discloses text has to come through here to do it.</para>
///
/// <para><b>Tier one runs first and always.</b> It needs no sidecar, no
/// model and no configuration beyond the categories, so it is the thing
/// that still works on a machine where the models were never installed.
/// It is also the cheaper check, and a payload it refuses never costs an
/// inference.</para>
/// </summary>
public static class Disclosure
{
    /// <summary>
    /// Judge a payload, and answer the failure that refuses it - or null
    /// when it may be served.
    ///
    /// <para><b>Null for \"nothing screened\" is not a hole.</b> A tree
    /// that never asked to be screened is not a tree whose screen broke;
    /// it is the ordinary case, and the overwhelming majority of trees.
    /// The fail-closed rule of R1.3 is about a screen that was ASKED FOR
    /// and could not run, and that case comes back as a failure from
    /// <paramref name="sidecar"/>.</para>
    /// </summary>
    public static Failure? Check(
        string payload, Screened screened, IScreener? sidecar, string? path = null) =>
        Check(payload, screened, sidecar, path, Screen.Bound);

    /// <summary>The same check with a different ceiling on each tier-one
    /// pattern. Public for the reason given on
    /// <see cref="Screen.Scan(string, Screened, TimeSpan)"/>: the timeout
    /// branch below is unreachable with today's detectors, and a branch
    /// that cannot be reached cannot be proven.</summary>
    public static Failure? Check(
        string payload, Screened screened, IScreener? sidecar, string? path, TimeSpan bound)
    {
        if (screened == Screened.None || string.IsNullOrEmpty(payload)) return null;

        IReadOnlyList<ScreenFinding> structural;
        try
        {
            structural = Screen.Scan(payload, screened, bound);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pattern that ran out of time checked NOTHING, and a check
            // that did not happen may not be reported as a check that
            // found nothing. Searcher does the opposite with a caller's
            // own search pattern - it skips the line and carries on -
            // because there the cost of giving up is a missing hit. Here
            // it is a disclosure, so the two cannot be handled the same
            // way however similar the exception looks.
            return new Failure(Outcome.Screened,
                "this response could not be screened, so it is not being served: "
                + "a screening pattern ran out of time on this payload. Serving content "
                + "that was never checked is the one outcome a screen must never produce, "
                + "so this refuses instead.", path);
        }

        if (structural.Count > 0)
            return new Failure(Outcome.Screened, Screen.Describe(structural), path);

        // Only the model tiers need the sidecar. A tree screening
        // identifiers alone was fully judged by the scan above, so it is
        // done - and never starts a process or pays a model load for a
        // tier that has none.
        if ((screened & Screens.ModelBacked) == Screened.None) return null;

        // No sidecar means no models directory was ever declared, which
        // is a person who has not set the second tier up rather than a
        // second tier that failed. Tier one was the whole screen and it
        // passed. Once a directory IS named, Sidecar.For hands one back
        // and a missing model becomes a refusal below - because at that
        // point somebody has said they expect it to be there.
        if (sidecar is null) return null;

        Result<IReadOnlyList<ScreenFinding>> asked = sidecar.Inspect(payload, screened);

        if (!asked.IsOk)
            return new Failure(Outcome.Screened,
                $"this response could not be screened, so it is not being served: "
                + $"{asked.Failure!.Message}. Serving content that was never checked is the one "
                + "outcome a screen must never produce, so this refuses instead.", path);

        return asked.Value.Count > 0
            ? new Failure(Outcome.Screened, Screen.Describe(asked.Value), path)
            : null;
    }
}
