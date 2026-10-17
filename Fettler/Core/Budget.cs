using System.Text;

namespace Fettler.Core;

/// <summary>
/// The size every answer is held to, and the one way a list is cut to
/// fit it.
///
/// <para><b>Measured in UTF-8 bytes of what is written.</b> The caller's
/// ceiling is on the answer it receives: the escaped JSON, the field
/// names, the line numbers, all of it. The cap this replaced counted the
/// raw text of a line or a hit and nothing else, so a quote-heavy file
/// went a quarter over, and a list with no text in it (find, replace)
/// was not measured at all. A byte count is never smaller than the
/// character count a client measures, so a byte ceiling is a character
/// ceiling too.</para>
///
/// <para><b>Why 40,000.</b> Claude Code saves a successful answer to a
/// file, and shows the model a pointer instead, once it passes 50,000
/// characters or 25,000 tokens. It cuts an error answer over about 11,000
/// characters to its head and tail, which leaves JSON that does not
/// parse. Forty thousand bytes stays under the first for any text and
/// under the second for any real text; ten thousand stays under the
/// third.</para>
///
/// <para><b>Core measures nothing itself.</b> How big an item is depends
/// on how it is written, and writing is the front end's business (Core
/// names no serializer). The front end measures; Core decides where the
/// cut falls, once, for both output modes.</para>
/// </summary>
public static class Budget
{
    /// <summary>The most an answer may be, in UTF-8 bytes.</summary>
    public const int Ceiling = 40_000;

    /// <summary>The most a refusal may be, in UTF-8 bytes.</summary>
    public const int ErrorCeiling = 10_000;

    /// <summary>How many characters of a long line a search hit keeps
    /// either side of its match.</summary>
    public const int MatchWindow = 200;

    /// <summary>How many characters of the match itself a hit keeps. A
    /// pattern such as <c>.*</c> matches a whole line, and a whole line
    /// is the thing a window exists to avoid.</summary>
    public const int MatchCap = 1_000;

    /// <summary>The largest image sent to the model, in bytes: the most
    /// one image may be on the Claude API.</summary>
    public const long ImageCap = 5L * 1024 * 1024;

    /// <summary>The bytes <paramref name="text"/> takes as UTF-8.</summary>
    public static int Utf8(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>
    /// How many of <paramref name="items"/>, from the front, fit in
    /// <paramref name="room"/> bytes.
    ///
    /// <para><paramref name="next"/> is what the answer adds when it stops
    /// after an item: the position to carry on from. It is paid only by
    /// the item an answer stops after, so it is checked on every item
    /// but spent on none. When <paramref name="more"/> is false, the last
    /// item of the list needs no next.</para>
    /// </summary>
    public static int Fit<T>(IReadOnlyList<T> items, int room, Func<T, int> cost,
                             Func<T, int>? next = null, bool more = false)
    {
        int spent = 0;
        for (int i = 0; i < items.Count; i++)
        {
            int c = cost(items[i]);
            bool final = i == items.Count - 1 && !more;
            int carry = final || next is null ? 0 : next(items[i]);

            if (spent + c + carry > room) return i;
            spent += c;
        }

        return items.Count;
    }

    /// <summary>
    /// The length of the longest start of <paramref name="text"/> whose
    /// cost fits in <paramref name="room"/>, never ending between the two
    /// halves of a surrogate pair. The cost has to grow with the length,
    /// which every way of writing text does.
    /// </summary>
    public static int Prefix(string text, int room, Func<string, int> cost)
    {
        if (cost(text) <= room) return text.Length;

        int low = 0, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (cost(text[..Whole(text, middle)]) <= room) low = middle;
            else high = middle - 1;
        }

        return Whole(text, low);
    }

    /// <summary>The length of the longest end of <paramref name="text"/>
    /// whose cost fits, never starting on the second half of a surrogate
    /// pair.</summary>
    public static int Suffix(string text, int room, Func<string, int> cost)
    {
        if (cost(text) <= room) return text.Length;

        int low = 0, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            int start = Start(text, text.Length - middle);
            if (cost(text[start..]) <= room) low = middle;
            else high = middle - 1;
        }

        return text.Length - Start(text, text.Length - low);
    }

    /// <summary>A cut at <paramref name="at"/>, moved back one if it would
    /// split a surrogate pair.</summary>
    static int Whole(string text, int at) =>
        at > 0 && at < text.Length && char.IsLowSurrogate(text[at]) ? at - 1 : at;

    /// <summary>A start at <paramref name="at"/>, moved on one if it would
    /// begin on the second half of a surrogate pair.</summary>
    static int Start(string text, int at) =>
        at > 0 && at < text.Length && char.IsLowSurrogate(text[at]) ? at + 1 : at;
}

/// <summary>
/// How big the parts of an answer are once written, measured by the
/// front end that writes them.
///
/// <para><b>Each cost is the larger of the two output modes</b>, so one
/// cut serves both: the terminal and the machine-readable answer stop at
/// the same line, and both fit.</para>
/// </summary>
public interface IMeter
{
    /// <summary>The bytes left for the parts this meter measures: the
    /// ceiling, less everything else in the answer.</summary>
    int Room { get; }

    /// <summary>One file of a read with no text in it: its path, its
    /// facts, and the largest position to carry on from that it could
    /// need.</summary>
    int Slice(ContainedPath path, int lines);

    /// <summary>One line of a read, or the start of one.</summary>
    int Line(int number, string text, string ending, bool numbered);

    /// <summary>One search hit, with what separates it from the
    /// next.</summary>
    int Hit(Hit hit);

    /// <summary>The position to carry on from, if an answer stops after
    /// this hit.</summary>
    int After(Hit hit);

    /// <summary>One file in a search that names files only.</summary>
    int Matched(ContainedPath path, int hits);

    /// <summary>The position to carry on from, if an answer stops after
    /// this file.</summary>
    int After(ContainedPath path);
}

/// <summary>
/// A meter for a caller with no front end, such as a test that drives
/// the core directly. It counts the UTF-8 bytes of the text and nothing
/// else, which is what such a caller receives.
/// </summary>
public sealed class PlainMeter(int room = Budget.Ceiling) : IMeter
{
    public static readonly PlainMeter Default = new();

    public int Room { get; } = room;

    public int Slice(ContainedPath path, int lines) => Budget.Utf8(path.Display);

    public int Line(int number, string text, string ending, bool numbered) =>
        Budget.Utf8(text) + Budget.Utf8(ending) + (numbered ? Bench.PrefixLength(number) : 0);

    public int Hit(Hit hit) =>
        Budget.Utf8(hit.Path.Display) + Budget.Utf8(hit.Text) + hit.Context.Sum(c => Budget.Utf8(c.Text));

    public int After(Hit hit) => Budget.Utf8(hit.Path.Display) + 12;

    public int Matched(ContainedPath path, int hits) => Budget.Utf8(path.Display) + 12;

    public int After(ContainedPath path) => Budget.Utf8(path.Display);
}
