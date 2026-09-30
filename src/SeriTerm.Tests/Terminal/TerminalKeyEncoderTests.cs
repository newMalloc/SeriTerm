using System.Text;
using SeriTerm.Core.Send;
using SeriTerm.Core.Terminal;

namespace SeriTerm.Tests.Terminal;

public class TerminalKeyEncoderTests
{
    [Fact]
    public void 普通文本应按编码转字节()
        => Assert.Equal("AT"u8.ToArray(), TerminalKeyEncoder.EncodeText("AT", "UTF-8"));

    [Fact]
    public void 中文应按所选编码转字节()
        => Assert.Equal(
            Encoding.GetEncoding("GB2312").GetBytes("中"),
            TerminalKeyEncoder.EncodeText("中", "GB2312"));

    [Fact]
    public void 空文本应返回空数组()
        => Assert.Empty(TerminalKeyEncoder.EncodeText(string.Empty, "UTF-8"));

    [Theory]
    [InlineData(LineEnding.CrLf, new byte[] { 0x0D, 0x0A })]
    [InlineData(LineEnding.Cr, new byte[] { 0x0D })]
    [InlineData(LineEnding.Lf, new byte[] { 0x0A })]
    [InlineData(LineEnding.None, new byte[] { })]
    public void 回车应按设置附加行尾(LineEnding ending, byte[] expected)
        => Assert.Equal(expected, TerminalKeyEncoder.EncodeEnter(ending));

    [Fact]
    public void 退格应可配置为DEL或BS()
    {
        Assert.Equal(new byte[] { 0x7F }, TerminalKeyEncoder.EncodeBackspace(sendDel: true));
        Assert.Equal(new byte[] { 0x08 }, TerminalKeyEncoder.EncodeBackspace(sendDel: false));
    }

    [Theory]
    [InlineData('c', 0x03)]
    [InlineData('C', 0x03)]
    [InlineData('d', 0x04)]
    [InlineData('a', 0x01)]
    [InlineData('z', 0x1A)]
    public void 控制键应编码为对应控制码(char letter, int expected)
        => Assert.Equal(new byte[] { (byte)expected }, TerminalKeyEncoder.EncodeControl(letter));

    [Fact]
    public void 非字母控制键应返回空()
        => Assert.Empty(TerminalKeyEncoder.EncodeControl('1'));

    [Theory]
    [InlineData('a', true)]
    [InlineData('中', true)]
    [InlineData(' ', true)]
    [InlineData('\r', false)]
    [InlineData('\t', false)]
    public void 可打印判断应排除控制字符(char c, bool expected)
        => Assert.Equal(expected, TerminalKeyEncoder.IsPrintable(c));

    [Fact]
    public void 方向键应编码为CSI序列()
    {
        Assert.Equal("\u001b[A"u8.ToArray(), TerminalKeyEncoder.EncodeArrow(TerminalArrow.Up));
        Assert.Equal("\u001b[B"u8.ToArray(), TerminalKeyEncoder.EncodeArrow(TerminalArrow.Down));
        Assert.Equal("\u001b[C"u8.ToArray(), TerminalKeyEncoder.EncodeArrow(TerminalArrow.Right));
        Assert.Equal("\u001b[D"u8.ToArray(), TerminalKeyEncoder.EncodeArrow(TerminalArrow.Left));
    }

    [Fact]
    public void 未知编码应退回UTF8()
        => Assert.Equal("A"u8.ToArray(), TerminalKeyEncoder.EncodeText("A", "不存在的编码"));
}
