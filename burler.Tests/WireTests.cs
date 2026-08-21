using System.Text.Json;
using Burler;
using Xunit;

namespace Burler.Tests;

/// <summary>
/// R3.3: burler's half of the protocol test.
///
/// <para><b>Its twin lives in Fettler.Tests and asserts the same wire
/// independently.</b> Neither project can see the other's types, which
/// is the point: a shared DTO assembly would make both sides agree by
/// construction, and then the one day they should disagree - when
/// somebody changes the shape on one side - nothing would say so.</para>
/// </summary>
public sealed class WireTests
{
    /// <summary>The model-backed category words, spelled out here as
    /// well as in the protocol, so changing the set means changing a
    /// test on purpose rather than watching one stop failing.
    /// 'identifiers' is deliberately not here: that tier runs inside
    /// Fettler and never crosses the pipe.</summary>
    static readonly string[] Expected = ["clinical", "legal", "scientific"];

    [Fact]
    public void TheCategoriesAreTheModelTiersAndOnlyThose() =>
        Assert.Equal(Expected, Protocol.Categories);

    // ---- requests ----

    [Fact]
    public void AnOrdinaryRequestIsRead()
    {
        Request? request = Protocol.Parse(
            """{"op":"screen","categories":["clinical","scientific"],"payload":"a note"}""", out string error);

        Assert.NotNull(request);
        Assert.Equal("", error);
        Assert.Equal("screen", request.Op);
        Assert.Equal(["clinical", "scientific"], request.Categories);
        Assert.Equal("a note", request.Payload);
    }

    /// <summary>
    /// A payload full of newlines still crosses as ONE line, which is the
    /// whole reason the framing needs no length prefix.
    ///
    /// <para>Non-ASCII travels with it, because a clinical note is full
    /// of it and a mangled payload is one whose entities quietly stop
    /// matching - a clean verdict on text nothing really read.</para>
    /// </summary>
    [Fact]
    public void ADocumentCrossesAsOneLine()
    {
        const string payload = "line one\nline two\r\nname: Renée — seen 3/4/1980\n";

        string request = $$"""{"op":"screen","categories":["clinical"],"payload":{{JsonSerializer.Serialize(payload)}}}""";

        Assert.DoesNotContain('\n', request);

        Request? read = Protocol.Parse(request, out _);

        Assert.NotNull(read);
        Assert.Equal(payload, read.Payload);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"payload":"x"}""")]
    [InlineData("""{"op":"screen"}""")]
    [InlineData("""{"op":"screen","payload":7}""")]
    [InlineData("""{"op":"screen","payload":"x","categories":["lgal"]}""")]
    [InlineData("""{"op":"screen","payload":"x","categories":[7]}""")]
    public void AMalformedRequestIsRefusedWithAReason(string line)
    {
        Request? request = Protocol.Parse(line, out string error);

        Assert.Null(request);
        Assert.NotEqual("", error);
    }

    /// <summary>A misspelt category is refused rather than dropped. A
    /// request that quietly screened three of the four asked for would
    /// report a clean verdict on an unscreened category.</summary>
    [Fact]
    public void AnUnknownCategoryNamesItself()
    {
        Protocol.Parse("""{"op":"screen","payload":"x","categories":["lgal"]}""", out string error);

        Assert.Contains("lgal", error, StringComparison.Ordinal);
    }

    // ---- answers ----

    [Fact]
    public void FindingsCarryACategoryACountAndSpans()
    {
        string answer = Protocol.Found([new Finding("clinical", [(3, 9), (20, 28)])]);

        using JsonDocument doc = JsonDocument.Parse(answer);

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        JsonElement one = doc.RootElement.GetProperty("findings")[0];
        Assert.Equal("clinical", one.GetProperty("category").GetString());
        Assert.Equal(2, one.GetProperty("count").GetInt32());
        Assert.Equal(3, one.GetProperty("spans")[0][0].GetInt32());
        Assert.Equal(28, one.GetProperty("spans")[1][1].GetInt32());
    }

    /// <summary>An EMPTY findings array is how burler says it found
    /// nothing. Fettler treats a missing one as a fault instead, so the
    /// two can never be confused - "I checked and it is clean" and "I did
    /// not answer properly" must not be spelled the same way.</summary>
    [Fact]
    public void FoundNothingIsAnEmptyArrayAndNotAMissingOne()
    {
        using JsonDocument doc = JsonDocument.Parse(Protocol.Found([]));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("findings").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public void ARefusalSaysWhy()
    {
        using JsonDocument doc = JsonDocument.Parse(Protocol.Failed("no model for clinical"));

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("no model for clinical", doc.RootElement.GetProperty("error").GetString());
    }

    /// <summary>Every answer is one line, for the same reason every
    /// request is.</summary>
    [Fact]
    public void EveryAnswerIsOneLine()
    {
        Assert.DoesNotContain('\n', Protocol.Found([new Finding("clinical", [(0, 1)])]));
        Assert.DoesNotContain('\n', Protocol.Failed("something\nwith a newline in it"));
    }

    [Fact]
    public void ACountIsHowManySpansThereAre() =>
        Assert.Equal(3, new Finding("legal", [(0, 1), (2, 3), (4, 5)]).Count);
}
