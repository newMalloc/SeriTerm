using System.Text;
using SeriTerm.Core.Send;

namespace SeriTerm.Tests.Send;

public class SendPayloadBuilderTests
{
    [Fact]
    public void 文本发送_应附加CRLF()
    {
        Assert.True(SendPayloadBuilder.TryBuild("AT", false, LineEnding.CrLf, "UTF-8", out var payload, out var error), error);
        Assert.Equal("AT\r\n"u8.ToArray(), payload);
    }

    [Fact]
    public void 文本发送_不附加行尾()
    {
        Assert.True(SendPayloadBuilder.TryBuild("AT", false, LineEnding.None, "UTF-8", out var payload, out var error), error);
        Assert.Equal("AT"u8.ToArray(), payload);
    }

    [Theory]
    [InlineData(LineEnding.Cr, new byte[] { 0x0D })]
    [InlineData(LineEnding.Lf, new byte[] { 0x0A })]
    [InlineData(LineEnding.CrLf, new byte[] { 0x0D, 0x0A })]
    [InlineData(LineEnding.None, new byte[] { })]
    public void 行尾字节应正确(LineEnding ending, byte[] expected)
        => Assert.Equal(expected, SendPayloadBuilder.GetLineEndingBytes(ending));

    [Fact]
    public void 十六进制发送_应解析后附加行尾()
    {
        Assert.True(SendPayloadBuilder.TryBuild("01 AB", true, LineEnding.CrLf, "UTF-8", out var payload, out var error), error);
        Assert.Equal(new byte[] { 0x01, 0xAB, 0x0D, 0x0A }, payload);
    }

    [Fact]
    public void 十六进制发送_非法内容应报错()
    {
        Assert.False(SendPayloadBuilder.TryBuild("01 GG", true, LineEnding.None, "UTF-8", out _, out var error));
        Assert.Contains("非法字符", error);
    }

    [Fact]
    public void 内容与行尾都为空应报错()
    {
        Assert.False(SendPayloadBuilder.TryBuild("", false, LineEnding.None, "UTF-8", out _, out var error));
        Assert.Contains("为空", error);
    }

    [Fact]
    public void 内容为空但行尾非空应允许_用于只发一个换行()
    {
        Assert.True(SendPayloadBuilder.TryBuild("", false, LineEnding.CrLf, "UTF-8", out var payload, out var error), error);
        Assert.Equal(new byte[] { 0x0D, 0x0A }, payload);
    }

    [Fact]
    public void 文本发送应使用指定编码()
    {
        var expected = Encoding.GetEncoding("GB2312").GetBytes("中");

        Assert.True(SendPayloadBuilder.TryBuild("中", false, LineEnding.None, "GB2312", out var payload, out var error), error);
        Assert.Equal(expected, payload);
    }

    [Fact]
    public void 未知编码应退回UTF8()
    {
        Assert.True(SendPayloadBuilder.TryBuild("中", false, LineEnding.None, "不存在的编码", out var payload, out var error), error);
        Assert.Equal("中"u8.ToArray(), payload);
    }

    [Fact]
    public void Describe_文本模式应把换行显示为空格()
        => Assert.Equal("AT OK", SendPayloadBuilder.Describe("AT\r\nOK\r\n"u8, false, "UTF-8"));

    [Fact]
    public void Describe_十六进制模式应输出HEX()
        => Assert.Equal("41 42", SendPayloadBuilder.Describe([0x41, 0x42], true, "UTF-8"));

    [Fact]
    public void Preview_超长内容应截断并标注总长度()
    {
        var payload = new byte[100];
        Array.Fill(payload, (byte)'A');

        var preview = SendPayloadBuilder.Preview(payload, maxBytes: 8);

        Assert.Contains("共 100 字节", preview);
        Assert.Contains("AAAAAAAA", preview);
    }
}
