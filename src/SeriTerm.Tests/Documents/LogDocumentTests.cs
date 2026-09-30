using System.Collections.Specialized;
using SeriTerm.Core.Documents;
using SeriTerm.Core.Pipeline;

namespace SeriTerm.Tests.Documents;

public class LogDocumentTests
{
    private static DisplayLine Line(int sequence, string text, int byteLength = 8, LineDirection direction = LineDirection.Rx)
        => new(sequence, new DateTime(2026, 9, 30, 12, 0, 0).AddMilliseconds(sequence), direction, new byte[byteLength], text);

    private static List<DisplayLine> Lines(int from, int count, Func<int, string> textFactory, int byteLength = 8)
        => [.. Enumerable.Range(from, count).Select(i => Line(i, textFactory(i), byteLength))];

    [Fact]
    public void Append_应累加行数与字节数()
    {
        var document = new LogDocument();

        document.Append(Lines(1, 10, i => $"line {i}"));

        Assert.Equal(10, document.Lines.Count);
        Assert.Equal(80, document.TotalBytes);
        Assert.Equal(0, document.DroppedLines);
    }

    [Fact]
    public void Append_一批数据只应发一次Reset通知()
    {
        var document = new LogDocument();
        var resets = 0;

        document.Lines.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
        };

        document.Append(Lines(1, 100, i => $"line {i}"));

        // 逐行通知会让 WPF 跑 100 次布局，这里必须是 1 次
        Assert.Equal(1, resets);
    }

    [Fact]
    public void 行数超限_应淘汰旧行且不超出上限()
    {
        var document = new LogDocument { MaxLines = 100 };

        document.Append(Lines(1, 300, i => $"line {i}"));

        Assert.True(document.Lines.Count <= 100, $"实际 {document.Lines.Count} 行");
        Assert.True(document.DroppedLines > 0);
        Assert.True(document.Lines[^1].Sequence > document.Lines[0].Sequence);
    }

    [Fact]
    public void 字节数超限_应淘汰旧行()
    {
        var document = new LogDocument { MaxLines = 1_000, MaxBytes = 1_000 };

        document.Append(Lines(1, 200, i => $"line {i}", byteLength: 100));

        Assert.True(document.TotalBytes <= 1_000, $"实际 {document.TotalBytes} 字节");
        Assert.True(document.DroppedLines > 0);
    }

    [Fact]
    public void 淘汰后保留的行应是最新的那些()
    {
        var document = new LogDocument { MaxLines = 50 };

        document.Append(Lines(1, 200, i => $"line {i}"));

        var sequences = document.Lines.Select(l => l.Sequence).ToArray();
        Assert.Equal(sequences.OrderBy(s => s), sequences);
        Assert.Contains(200, sequences);
    }

    [Fact]
    public void Clear_应清空全部状态()
    {
        var document = new LogDocument();
        document.Append(Lines(1, 10, i => $"line {i}"));

        document.Clear();

        Assert.Empty(document.Lines);
        Assert.Equal(0, document.TotalBytes);
        Assert.Equal(0, document.DroppedLines);
        Assert.Equal(0, document.MatchCount);
    }

    // ---------- 搜索 ----------

    [Fact]
    public void 搜索_应标记命中行并定位第一处()
    {
        var document = new LogDocument();
        document.Append([Line(1, "AT+OK"), Line(2, "ERROR"), Line(3, "at+ready")]);

        document.SetSearch("AT", caseSensitive: false);

        Assert.Equal(2, document.MatchCount);
        Assert.True(document.Lines[0].IsMatch);
        Assert.True(document.Lines[2].IsMatch);
        Assert.False(document.Lines[1].IsMatch);

        Assert.Equal(0, document.CurrentMatchIndex);
        Assert.True(document.Lines[0].IsCurrentMatch);
        Assert.False(document.Lines[2].IsCurrentMatch);
    }

    [Fact]
    public void 搜索_区分大小写时应生效()
    {
        var document = new LogDocument();
        document.Append([Line(1, "AT+OK"), Line(2, "at+ready")]);

        document.SetSearch("AT", caseSensitive: true);

        Assert.Equal(1, document.MatchCount);
        Assert.True(document.Lines[0].IsMatch);
        Assert.False(document.Lines[1].IsMatch);
    }

    [Fact]
    public void 搜索_无匹配时当前索引为负()
    {
        var document = new LogDocument();
        document.Append([Line(1, "hello")]);

        document.SetSearch("zzz", caseSensitive: false);

        Assert.Equal(0, document.MatchCount);
        Assert.Equal(-1, document.CurrentMatchIndex);
        Assert.Empty(document.Matches);
    }

    [Fact]
    public void 搜索_上下跳转应循环()
    {
        var document = new LogDocument();
        document.Append([Line(1, "hit 1"), Line(2, "miss"), Line(3, "hit 2"), Line(4, "hit 3")]);

        document.SetSearch("hit", caseSensitive: false);
        Assert.Equal(3, document.MatchCount);

        // 搜索定位在第一处命中（seq 1），"下一个"从它往后走
        Assert.Equal(1, document.Matches[document.CurrentMatchIndex].Sequence);

        Assert.Equal(3, document.MoveNextMatch()!.Sequence);
        Assert.Equal(4, document.MoveNextMatch()!.Sequence);
        Assert.Equal(1, document.MoveNextMatch()!.Sequence);   // 末尾之后回到第一处
        Assert.Equal(3, document.MoveNextMatch()!.Sequence);

        Assert.Equal(1, document.MovePreviousMatch()!.Sequence);
        Assert.Equal(4, document.MovePreviousMatch()!.Sequence);  // 第一处之前回到最后一处
    }

    [Fact]
    public void 搜索_当前命中标志应始终只有一个()
    {
        var document = new LogDocument();
        document.Append([Line(1, "hit 1"), Line(2, "hit 2"), Line(3, "hit 3")]);

        document.SetSearch("hit", caseSensitive: false);
        document.MoveNextMatch();
        document.MoveNextMatch();

        Assert.Single(document.Lines.Where(l => l.IsCurrentMatch));
    }

    [Fact]
    public void 搜索_新到达的行应实时参与匹配()
    {
        var document = new LogDocument();
        document.Append([Line(1, "hit 1")]);
        document.SetSearch("hit", caseSensitive: false);
        Assert.Equal(1, document.MatchCount);

        document.Append([Line(2, "miss"), Line(3, "hit 2")]);

        Assert.Equal(2, document.MatchCount);
        Assert.True(document.Lines[2].IsMatch);
    }

    [Fact]
    public void 搜索_清空关键字应移除全部标记()
    {
        var document = new LogDocument();
        document.Append([Line(1, "hit 1"), Line(2, "hit 2")]);
        document.SetSearch("hit", caseSensitive: false);

        document.ClearSearch();

        Assert.Equal(0, document.MatchCount);
        Assert.DoesNotContain(document.Lines, l => l.IsMatch);
        Assert.DoesNotContain(document.Lines, l => l.IsCurrentMatch);
    }

    [Fact]
    public void 搜索_命中行被淘汰后不应残留在命中集合中()
    {
        var document = new LogDocument { MaxLines = 20 };
        document.Append(Lines(1, 10, i => i % 2 == 0 ? $"hit {i}" : $"miss {i}"));
        document.SetSearch("hit", caseSensitive: false);
        var before = document.MatchCount;

        // 追加足够多的数据，把前面的命中行全部挤出去
        document.Append(Lines(11, 100, i => $"miss {i}"));

        Assert.True(before > 0);
        Assert.Equal(0, document.MatchCount);
        Assert.DoesNotContain(document.Lines, l => l.IsMatch);
        Assert.Equal(-1, document.CurrentMatchIndex);
    }

    [Fact]
    public void 搜索状态变化应触发事件()
    {
        var document = new LogDocument();
        document.Append([Line(1, "hit")]);
        var raised = 0;
        document.SearchChanged += (_, _) => raised++;

        document.SetSearch("hit", caseSensitive: false);
        document.SetSearch("hit", caseSensitive: false);   // 参数未变，不应重复触发

        Assert.Equal(1, raised);
    }

    // ---------- 重刷 ----------

    [Fact]
    public void Reformat_应重写所有行的显示文本()
    {
        var document = new LogDocument();
        var lines = new[] { Line(1, "old-1"), Line(2, "old-2") };
        document.Append(lines);

        document.Reformat(_ => "new");

        Assert.All(document.Lines, l => Assert.Equal("new", l.Text));
    }

    [Fact]
    public void Reformat_不应清空系统提示行()
    {
        var document = new LogDocument();
        var systemLine = Line(1, "串口已打开：COM5", direction: LineDirection.System);
        var dataLine = Line(2, "hello");
        document.Append([systemLine, dataLine]);

        document.Reformat(_ => "HEX");

        // 系统行没有原始字节，重刷时必须保留原文案
        Assert.Equal("串口已打开：COM5", document.Lines[0].Text);
        Assert.Equal("HEX", document.Lines[1].Text);
    }

    [Fact]
    public void Reformat_应重新计算命中()
    {
        var document = new LogDocument();
        document.Append([Line(1, "old")]);
        document.SetSearch("new", caseSensitive: false);
        Assert.Equal(0, document.MatchCount);

        document.Reformat(_ => "new content");

        Assert.Equal(1, document.MatchCount);
    }
}
