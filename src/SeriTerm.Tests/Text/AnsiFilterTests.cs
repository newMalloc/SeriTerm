using SeriTerm.Core.Text;

namespace SeriTerm.Tests.Text;

public class AnsiFilterTests
{
    [Fact]
    public void 应去掉颜色序列()
        => Assert.Equal("RED", AnsiFilter.Strip("\u001b[31mRED\u001b[0m"));

    [Fact]
    public void 应去掉光标控制序列()
        => Assert.Equal("Ab", AnsiFilter.Strip("A\u001b[2Jb"));

    [Fact]
    public void 应去掉带中间字节的CSI序列()
        => Assert.Equal("X", AnsiFilter.Strip("\u001b[1;2HX"));

    [Fact]
    public void 应去掉两字节转义序列()
        => Assert.Equal("ok", AnsiFilter.Strip("o\u001bMk"));

    [Fact]
    public void 应去掉OSC序列()
        => Assert.Equal("title", AnsiFilter.Strip("\u001b]0;window\u0007title"));

    [Fact]
    public void 应去掉不可见控制字符但保留制表与换行()
        => Assert.Equal("a\tb\nc", AnsiFilter.Strip("a\u0007\tb\n\u0008c"));

    [Fact]
    public void 纯文本应原样返回()
        => Assert.Equal("AT+OK", AnsiFilter.Strip("AT+OK"));

    [Fact]
    public void 中文应保留()
        => Assert.Equal("串口就绪", AnsiFilter.Strip("\u001b[32m串口就绪\u001b[0m"));

    [Fact]
    public void 结尾不完整的转义序列不应越界()
        => Assert.Equal("abc", AnsiFilter.Strip("abc\u001b["));

    [Fact]
    public void 空输入应返回空串()
    {
        Assert.Equal(string.Empty, AnsiFilter.Strip(null));
        Assert.Equal(string.Empty, AnsiFilter.Strip(string.Empty));
    }
}
