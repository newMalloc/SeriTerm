using SeriTerm.Core.Documents;

namespace SeriTerm.Tests.Documents;

public class LogTextSelectionTests
{
    [Fact]
    public void Normalize_同一行内反向拖动应交换端点()
    {
        var (start, end) = LogTextSelection.Normalize(new RowCaret(3, 9), new RowCaret(3, 2));

        Assert.Equal(new RowCaret(3, 2), start);
        Assert.Equal(new RowCaret(3, 9), end);
    }

    [Fact]
    public void Normalize_从下往上拖动应交换行()
    {
        var (start, end) = LogTextSelection.Normalize(new RowCaret(5, 4), new RowCaret(2, 40));

        Assert.Equal(new RowCaret(2, 40), start);
        Assert.Equal(new RowCaret(5, 4), end);
    }

    [Fact]
    public void Extract_单行中间的一段()
    {
        var text = LogTextSelection.Extract(["SeriTerm loopback test"], 9, 17);

        Assert.Equal("loopback", text);
    }

    [Fact]
    public void Extract_跨行取首行后半段与末行前半段()
    {
        // 使用者报的那一例：第一行只要 "loopback test"，第二行从行首（时间戳）到 "SeriTerm "
        var rows = new[]
        {
            "14:27:52.276 Rx SeriTerm loopback test",
            "14:27:52.373 Tx SeriTerm loopback test",
        };

        // 第 1 行的 "loopback test" 从下标 25 开始；第 2 行要到这里为止的下标 25（含 "SeriTerm " 后面那个空格）
        var text = LogTextSelection.Extract(rows, 25, 25);

        Assert.Equal("loopback test\r\n14:27:52.373 Tx SeriTerm ", text);
    }

    [Fact]
    public void Extract_中间的行应整行取出并带CRLF()
    {
        var text = LogTextSelection.Extract(["abcde", "FGHIJ", "klmno"], 2, 3);

        Assert.Equal("cde\r\nFGHIJ\r\nklm", text);
    }

    [Fact]
    public void Extract_越界下标应裁剪到行内而不是抛异常()
    {
        var text = LogTextSelection.Extract(["ab", "cd"], -5, 99);

        Assert.Equal("ab\r\ncd", text);
    }

    [Fact]
    public void Extract_端点落在同一位置时应得到空串()
    {
        Assert.Equal(string.Empty, LogTextSelection.Extract(["abc"], 1, 1));
        Assert.Equal(string.Empty, LogTextSelection.Extract([], 0, 3));
    }

    [Fact]
    public void Extract_空行也要占一行()
    {
        var text = LogTextSelection.Extract(["ab", "", "cd"], 1, 1);

        Assert.Equal("b\r\n\r\nc", text);
    }
}
