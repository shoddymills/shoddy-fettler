namespace Picker.Core;

/// <summary>
/// Which of the three levels are on, and which files turned each on.
///
/// <para><b>The same three words as Fettler's</b>, and a twin of its
/// record, held equal by the twins verifier rather than shared: there is
/// no reference between the two programs. <c>tools</c> is always on.
/// <c>guardrails</c> and <c>screenings</c> are on when a file holds a
/// section of that name.</para>
/// </summary>
public sealed record Levels(
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Guardrails,
    IReadOnlyList<string> Screenings)
{
    public const string ToolsName = "tools";
    public const string GuardrailsName = "guardrails";
    public const string ScreeningsName = "screenings";

    /// <summary>The level words, in the order every report lists them.</summary>
    public static readonly string[] Names = [ToolsName, GuardrailsName, ScreeningsName];

    /// <summary>Tools on, and nothing else.</summary>
    public static readonly Levels ToolsOnly = new([], [], []);

    public bool GuardrailsOn => Guardrails.Count > 0;

    public bool ScreeningsOn => Screenings.Count > 0;

    /// <summary>Whether a level, named by its word, is on.</summary>
    public bool IsOn(string level) => level switch
    {
        ToolsName => true,
        GuardrailsName => GuardrailsOn,
        ScreeningsName => ScreeningsOn,
        _ => throw new ArgumentException($"no level called {level}", nameof(level)),
    };

    /// <summary>The files that turned a level on.</summary>
    public IReadOnlyList<string> Sources(string level) => level switch
    {
        ToolsName => Tools,
        GuardrailsName => Guardrails,
        ScreeningsName => Screenings,
        _ => throw new ArgumentException($"no level called {level}", nameof(level)),
    };

    /// <summary>One line for a level, as <c>catalogs</c> and <c>doctor</c>
    /// print it.</summary>
    public string Describe(string level)
    {
        IReadOnlyList<string> from = Sources(level);

        if (level == ToolsName)
            return from.Count == 0 ? "on, always" : $"on, always; settings from {string.Join(" and ", from)}";

        return from.Count == 0 ? "off" : $"on, from {string.Join(" and ", from)}";
    }
}
