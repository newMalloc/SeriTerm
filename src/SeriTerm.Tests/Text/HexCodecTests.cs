using SeriTerm.Core.Text;

namespace SeriTerm.Tests.Text;

public class HexCodecTests
{
    [Fact]
    public void Format_应输出大写十六进制且以空格分隔()
        => Assert.Equal("01 02 FF", HexCodec.Format([0x01, 0x02, 0xFF]));

    [Fact]
    public void Format_空数据应返回空串()
        => Assert.Equal(string.Empty, HexCodec.Format([]));

    [Theory]
    [InlineData("01 02 FF")]
    [InlineData("01,02,ff")]
    [InlineData("0x01 0x02 0xFF")]
    [InlineData("0102FF")]
    [InlineData("01-02-FF")]
    [InlineData("01 02\nFF")]
    public void TryParse_宽容输入应解析成功(string input)
    {
        Assert.True(HexCodec.TryParse(input, out var bytes, out var error), error);
        Assert.Equal(new byte[] { 0x01, 0x02, 0xFF }, bytes);
    }

    [Theory]
    [InlineData("", "为空")]
    [InlineData("   ", "为空")]
    [InlineData("01 0", "成对")]
    [InlineData("01 GG", "非法字符")]
    [InlineData("0x1", "成对")]
    public void TryParse_非法输入应给出中文原因(string input, string expectedKeyword)
    {
        Assert.False(HexCodec.TryParse(input, out _, out var error));
        Assert.Contains(expectedKeyword, error);
    }

    [Fact]
    public void TryParse_null应报为空()
    {
        Assert.False(HexCodec.TryParse(null, out _, out var error));
        Assert.Contains("为空", error);
    }

    [Fact]
    public void FormatWithAscii_应包含偏移与可打印字符()
    {
        var text = HexCodec.FormatWithAscii("Hello"u8.ToArray());

        Assert.Contains("0000", text);
        Assert.Contains("48 65 6C 6C 6F", text);
        Assert.Contains("Hello", text);
    }

    [Fact]
    public void FormatWithAscii_不可打印字符应显示为点()
    {
        var text = HexCodec.FormatWithAscii([0x00, 0x41]);

        Assert.Contains("A", text);
        Assert.Contains(".", text);
    }

    [Fact]
    public void 往返解析应保持一致()
    {
        var original = new byte[] { 0x00, 0x7F, 0x80, 0xFF, 0xAB };
        var text = HexCodec.Format(original);

        Assert.True(HexCodec.TryParse(text, out var parsed, out var error), error);
        Assert.Equal(original, parsed);
    }
}
