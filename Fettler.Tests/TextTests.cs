using System.Text;
using Fettler.Core;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// R10.2: a read-then-write with no edit produces a byte-identical file,
/// for every combination of encoding, line ending and trailing newline.
/// R10.22: the encoding rule refuses rather than guesses.
/// </summary>
public sealed class TextTests
{
    public static TheoryData<string, byte[], string> Files()
    {
        byte[] utf8Bom = [0xEF, 0xBB, 0xBF];
        byte[] utf16Bom = [0xFF, 0xFE];

        var data = new TheoryData<string, byte[], string>
        {
            { "lf, no bom, final newline", [], "one\ntwo\n" },
            { "lf, no bom, no final newline", [], "one\ntwo" },
            { "crlf, no bom, final newline", [], "one\r\ntwo\r\n" },
            { "crlf, no bom, no final newline", [], "one\r\ntwo" },
            { "cr only", [], "one\rtwo\r" },
            { "mixed endings", [], "one\r\ntwo\nthree\r\n" },
            { "utf-8 bom, crlf", utf8Bom, "one\r\ntwo\r\n" },
            { "utf-8 bom, lf, no final newline", utf8Bom, "one\ntwo" },
            { "empty file", [], "" },
            { "one line, no ending at all", [], "solitary" },
            { "non-ascii", [], "café naïve — 中文\n" },
        };

        // UTF-16 is carried by its mark and has to survive too.
        data.Add("utf-16 le bom, crlf", utf16Bom, "one\r\ntwo\r\n");
        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ReadThenWriteWithNoEditIsByteIdentical(string because, byte[] bom, string body)
    {
        using var box = new Sandbox();

        byte[] original = bom.Length switch
        {
            2 => [.. bom, .. Encoding.Unicode.GetBytes(body)],
            _ => [.. bom, .. new UTF8Encoding(false).GetBytes(body)],
        };

        box.WriteRaw("subject.txt", original);
        ContainedPath path = box.Path_("subject.txt");

        Result<TextFile> read = TextIo.Read(path);
        Assert.True(read.IsOk, $"{because}: {read.Failure?.Message}");

        Result<Saved> written = SafeWrite.Text(
            path, read.Value.Text, read.Value.EncodingName, read.Value.LineEnding, overwrite: true);
        Assert.True(written.IsOk, $"{because}: {written.Failure?.Message}");

        Assert.True(original.SequenceEqual(box.ReadRaw("subject.txt")),
            $"{because}: the file changed when nothing was edited");
    }

    [Fact]
    public void ReadReportsEncodingLineEndingAndTrailingNewline()
    {
        using var box = new Sandbox();
        box.WriteRaw("crlf.txt", [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("a\r\nb")]);

        Result<TextFile> read = TextIo.Read(box.Path_("crlf.txt"));

        Assert.True(read.IsOk, read.Failure?.Message);
        Assert.Equal("utf-8-bom", read.Value.EncodingName);
        Assert.Equal(LineEnding.CrLf, read.Value.LineEnding);
        Assert.False(read.Value.FinalNewline);
        Assert.Equal(2, read.Value.LineCount);
    }

    /// <summary>
    /// R4.14, stated rather than guessed: no byte-order mark and content
    /// that does not decode as UTF-8 is binary, and is refused rather
    /// than corrupted.
    /// </summary>
    [Fact]
    public void ContentThatDoesNotDecodeAsUtf8IsRefusedAsBinary()
    {
        using var box = new Sandbox();
        // 0xC3 0x28 is not a legal UTF-8 sequence, and there is no mark
        // saying to read it as anything else.
        box.WriteRaw("blob.bin", [0x41, 0xC3, 0x28, 0x42, 0x80, 0x9F]);

        Result<TextFile> read = TextIo.Read(box.Path_("blob.bin"));

        Assert.False(read.IsOk);
        Assert.Equal(Outcome.Refused, read.Failure!.Outcome);
    }

    [Fact]
    public void ContentCarryingANulIsRefusedAsBinary()
    {
        using var box = new Sandbox();
        box.WriteRaw("blob.bin", [.. "MZ"u8.ToArray(), 0x00, 0x00, .. "text"u8.ToArray()]);

        Result<TextFile> read = TextIo.Read(box.Path_("blob.bin"));

        Assert.False(read.IsOk);
        Assert.Equal(Outcome.Refused, read.Failure!.Outcome);
        Assert.Contains("binary", read.Failure.Message);
    }

    /// <summary>R5.9: the hash is over the bytes, so a BOM or a line
    /// ending changing underneath a caller is caught.</summary>
    [Fact]
    public void TheHashChangesWhenOnlyTheLineEndingsDo()
    {
        using var box = new Sandbox();
        box.WriteRaw("a.txt", Encoding.UTF8.GetBytes("one\ntwo\n"));
        string lf = TextIo.Read(box.Path_("a.txt")).Value.Hash;

        box.WriteRaw("a.txt", Encoding.UTF8.GetBytes("one\r\ntwo\r\n"));
        string crlf = TextIo.Read(box.Path_("a.txt")).Value.Hash;

        Assert.NotEqual(lf, crlf);
    }

    [Fact]
    public void LinesKeepTheirOwnEndingsSoAMixedFileIsNotRegularised()
    {
        IReadOnlyList<Line> lines = TextIo.SplitLines("a\r\nb\nc");

        Assert.Equal(3, lines.Count);
        Assert.Equal("\r\n", lines[0].Ending);
        Assert.Equal("\n", lines[1].Ending);
        Assert.Equal("", lines[2].Ending);
        Assert.Equal("a\r\nb\nc", TextIo.Join(lines));
    }
}
