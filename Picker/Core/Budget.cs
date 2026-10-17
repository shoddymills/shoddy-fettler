namespace Picker.Core;

/// <summary>
/// The size every answer is held to, and the one way a list is cut to
/// fit it: Fettler's ceiling, carried here as Picker's own copy because
/// the two share a repository and nothing else.
///
/// <para><b>Measured in UTF-8 bytes of what is written</b>, the escaped
/// JSON and its field names included, because that is what the client
/// measures. Claude Code saves a successful answer to a file once it
/// passes 50,000 characters or 25,000 tokens, and cuts an error answer
/// over about 11,000 characters to its head and tail. Forty thousand
/// bytes stays under the first two for any real text; ten thousand stays
/// under the third.</para>
///
/// <para><b>The core measures nothing itself.</b> How big a row is
/// depends on how it is written, and writing is the front end's
/// business. The front end measures; the core decides where the cut
/// falls, before the screen judges what is left, so the screen judges
/// exactly what is sent.</para>
/// </summary>
public static class Budget
{
    /// <summary>The most an answer may be, in UTF-8 bytes.</summary>
    public const int Ceiling = 40_000;

    /// <summary>The most a refusal may be, in UTF-8 bytes.</summary>
    public const int ErrorCeiling = 10_000;

    /// <summary>The room a hint takes beside an answer, as Fettler's: the
    /// answer and the hint together stay under the ceiling.</summary>
    public const int HintRoom = 400;

    /// <summary>The most an answer itself may be, leaving room for a
    /// hint.</summary>
    public const int Answer = Ceiling - HintRoom;

    /// <summary>The most a refusal itself may be, leaving room for a
    /// hint.</summary>
    public const int ErrorAnswer = ErrorCeiling - HintRoom;

    /// <summary>
    /// How many of <paramref name="items"/>, from the front, fit in
    /// <paramref name="room"/> bytes. <paramref name="next"/> is what the
    /// answer adds when it stops after an item, paid only by that item;
    /// when <paramref name="more"/> is false, the last item needs none.
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

    /// <summary>The length of the longest start of <paramref name="text"/>
    /// whose cost fits, never ending between the two halves of a
    /// surrogate pair.</summary>
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

    static int Whole(string text, int at) =>
        at > 0 && at < text.Length && char.IsLowSurrogate(text[at]) ? at - 1 : at;
}

/// <summary>
/// How big a SELECT answer is once written, measured by the front end
/// that writes it. The columns, the SQL that ran and the objects it
/// reached are known only once the query has run, so the core asks for
/// them then.
/// </summary>
public interface ISelectMeter
{
    /// <summary>The bytes the answer takes with these columns and no
    /// rows.</summary>
    int Envelope(IReadOnlyList<ColumnFact> columns, string ran, IReadOnlyList<string> objects);

    /// <summary>The bytes one row takes, with what separates it from the
    /// next.</summary>
    int Row(IReadOnlyList<ColumnFact> columns, IReadOnlyList<Cell> row);
}
