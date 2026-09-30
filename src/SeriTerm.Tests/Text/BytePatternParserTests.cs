using SeriTerm.Core.Text;

namespace SeriTerm.Tests.Text;

public class BytePatternParserTests
{
    [Theory]
    [InlineData("\\r\\n", new byte[] { 0x0D, 0x0A })]
    [InlineData("\\n", new byte[] { 0x0A })]
    [InlineData("\\r", new byte[] { 0x0D })]
    [InlineData("\\t", new byte[] { 0x09 })]
    [InlineData("\\0", new byte[] { 0x00 })]
    [InlineData("\\\\", new byte[] { 0x5C })]
    [InlineData("\\x41\\x42", new byte[] { 0x41, 0x42 })]
    [InlineData("OK", new byte[] { 0x4F, 0x4B })]
    [InlineData("hex:0D 0A", new byte[] { 0x0D, 0x0A })]
    [InlineData("hex:0d0a", new byte[] { 0x0D, 0x0A })]
    [InlineData("HEX:41", new byte[] { 0x41 })]
    public void TryParse_应支持各种写法(string input, byte[] expected)
    {
        Assert.True(BytePatternParser.TryParse(input, out var bytes, out var error), error);
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void TryParse_普通中文应按UTF8编码()
    {
        Assert.True(BytePatternParser.TryParse("中", out var bytes, out var error), error);
        Assert.Equal(new byte[] { 0xE4, 0xB8, 0xAD }, bytes);
    }

    [Theory]
    [InlineData("", "不能为空")]
    [InlineData("\\", "缺少内容")]
    [InlineData("\\q", "不支持的转义")]
    [InlineData("\\x4", "两位十六进制")]
    [InlineData("\\xZZ", "两位十六进制")]
    [InlineData("hex:", "为空")]
    public void TryParse_非法输入应给出中文原因(string input, string expectedKeyword)
    {
        Assert.False(BytePatternParser.TryParse(input, out _, out var error));
        Assert.Contains(expectedKeyword, error);
    }

    [Fact]
    public void TryParse_null应报错()
    {
        Assert.False(BytePatternParser.TryParse(null, out _, out var error));
        Assert.Contains("不能为空", error);
    }
}
