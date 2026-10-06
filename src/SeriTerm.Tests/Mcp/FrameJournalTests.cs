using SeriTerm.Core.Mcp;
using SeriTerm.Core.Pipeline;

namespace SeriTerm.Tests.Mcp;

public class FrameJournalTests
{
    private static DisplayLine Rx(long sequence, string text, byte[]? raw = null)
        => new(sequence, new DateTime(2026, 1, 1).AddMilliseconds(sequence), LineDirection.Rx, raw ?? [1, 2, 3], text);

    private static DisplayLine Tx(long sequence, string text)
        => new(sequence, new DateTime(2026, 1, 1).AddMilliseconds(sequence), LineDirection.Tx, [4, 5], text);

    private static void Append(FrameJournal journal, params DisplayLine[] lines) => journal.Append(lines);

    [Fact]
    public void 按游标读帧_返回序号之后的所有帧并把游标推到最后一帧()
    {
        var journal = new FrameJournal();
        Append(journal, Rx(1, "a"), Rx(2, "b"), Rx(3, "c"));

        var (frames, cursor, hasMore) = journal.Read(0, 10, McpFrameDirection.All);

        Assert.Equal([1L, 2L, 3L], frames.Select(f => f.Seq));
        Assert.Equal(3, cursor);
        Assert.False(hasMore);
        Assert.Equal(3, journal.TotalFrames);
        Assert.Equal(1, journal.OldestSeq);
        Assert.Equal(3, journal.LastSeq);
    }

    [Fact]
    public void 达到上限时_hasMore_为真且游标停在已返回的最后一帧()
    {
        var journal = new FrameJournal();
        Append(journal, Rx(1, "a"), Rx(2, "b"), Rx(3, "c"));

        var (frames, cursor, hasMore) = journal.Read(0, 2, McpFrameDirection.All);

        Assert.Equal([1L, 2L], frames.Select(f => f.Seq));
        Assert.Equal(2, cursor);
        Assert.True(hasMore);

        // 用返回的游标续读：不重复也不漏
        var (next, nextCursor, _) = journal.Read(cursor, 10, McpFrameDirection.All);

        Assert.Equal([3L], next.Select(f => f.Seq));
        Assert.Equal(3, nextCursor);
    }

    [Fact]
    public void 方向过滤_不回退游标_避免同一个客户端反复重扫()
    {
        var journal = new FrameJournal();
        Append(journal, Rx(1, "a"), Tx(2, "b"), Rx(3, "c"));

        var (frames, cursor, _) = journal.Read(0, 10, McpFrameDirection.Rx);

        Assert.Equal([1L, 3L], frames.Select(f => f.Seq));
        Assert.Equal(3, cursor);
    }

    [Fact]
    public void 超出帧数预算_淘汰最旧的帧并累计淘汰数()
    {
        var journal = new FrameJournal(maxFrames: 5, maxBytes: long.MaxValue);

        for (var i = 1; i <= 20; i++)
        {
            Append(journal, Rx(i, i.ToString()));
        }

        Assert.True(journal.Count < 20);
        Assert.True(journal.EvictedFrames > 0);
        Assert.True(journal.OldestSeq > 1);
        Assert.Equal(20, journal.LastSeq);
        Assert.Equal(20, journal.TotalFrames);
    }

    [Fact]
    public void 超出字节预算_也淘汰最旧的帧()
    {
        // 每帧 3 字节，预算 9 字节：留不住全部
        var journal = new FrameJournal(maxFrames: 1000, maxBytes: 9);

        for (var i = 1; i <= 10; i++)
        {
            Append(journal, Rx(i, i.ToString()));
        }

        Assert.True(journal.Count < 10);
        Assert.True(journal.EvictedFrames > 0);
    }

    [Fact]
    public void 清空之后_序号不回退_淘汰数继续累计()
    {
        var journal = new FrameJournal();
        Append(journal, Rx(1, "a"), Rx(2, "b"));

        journal.Clear();

        Assert.Equal(0, journal.Count);
        Assert.Equal(2, journal.LastSeq);
        Assert.Equal(2, journal.EvictedFrames);

        Append(journal, Rx(3, "c"));

        var (frames, cursor, _) = journal.Read(0, 10, McpFrameDirection.All);

        Assert.Equal([3L], frames.Select(f => f.Seq));
        Assert.Equal(3, cursor);
    }

    [Fact]
    public void 系统提示行没有原始字节_用_UTF8_存一份文本()
    {
        var journal = new FrameJournal();
        var line = new DisplayLine(1, DateTime.Now, LineDirection.System, [], "串口已打开：COM5");

        Append(journal, line);

        var (frames, _, _) = journal.Read(0, 10, McpFrameDirection.System);

        Assert.Single(frames);
        Assert.Equal("串口已打开：COM5", System.Text.Encoding.UTF8.GetString(frames[0].Payload));
    }

    [Fact]
    public async Task 等新帧_已有新帧时立刻返回()
    {
        var journal = new FrameJournal();
        Append(journal, Rx(1, "a"));

        var signaled = await journal.WaitForNewAsync(0, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.True(signaled);
    }

    [Fact]
    public async Task 等新帧_等待期间到达的数据能把它叫醒()
    {
        var journal = new FrameJournal();

        var waiting = journal.WaitForNewAsync(0, TimeSpan.FromSeconds(5), CancellationToken.None);

        // 另一条线程投递一帧（模拟串口读取线程）
        var producer = Task.Run(async () =>
        {
            await Task.Delay(50);
            Append(journal, Rx(1, "a"));
        });

        Assert.True(await waiting);
        await producer;
    }

    [Fact]
    public async Task 等新帧_超时返回_false()
    {
        var journal = new FrameJournal();

        var signaled = await journal.WaitForNewAsync(0, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.False(signaled);
    }

    [Fact]
    public void 空缓冲_读出来是空的且游标停在调用方给的游标上()
    {
        var journal = new FrameJournal();

        var (frames, cursor, hasMore) = journal.Read(7, 10, McpFrameDirection.All);

        Assert.Empty(frames);
        Assert.Equal(7, cursor);
        Assert.False(hasMore);
        Assert.Equal(0, journal.OldestSeq);
    }
}
