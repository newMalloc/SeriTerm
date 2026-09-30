using System.Diagnostics;
using SeriTerm.Core.Pipeline;

namespace SeriTerm.Tests.Pipeline;

public class ReceiveProcessorTests
{
    private static long Ticks(int milliseconds) => milliseconds * Stopwatch.Frequency / 1000;

    private static readonly DateTime WallClock = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Local);

    private static List<DisplayLine> Feed(ReceiveProcessor processor, byte[] data, int atMilliseconds)
    {
        var output = new List<DisplayLine>();
        processor.ProcessReceived(data, Ticks(atMilliseconds), WallClock, output);
        return output;
    }

    [Fact]
    public void 自动断帧关闭_每块数据一行()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions { AutoFrame = false });

        var first = Feed(processor, "AB"u8.ToArray(), 0);
        var second = Feed(processor, "CD"u8.ToArray(), 1);

        Assert.Single(first);
        Assert.Equal("AB", first[0].Text);
        Assert.Single(second);
        Assert.Equal("CD", second[0].Text);
    }

    [Fact]
    public void 自动断帧开启_应按空闲间隔切分()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions { AutoFrame = true, AutoFrameGapMilliseconds = 20 });

        var first = Feed(processor, "AB"u8.ToArray(), 0);
        var second = Feed(processor, "CD"u8.ToArray(), 50);

        Assert.Empty(first);              // 第一块还在等断帧
        Assert.Single(second);            // 第二块到达时把第一帧挤出来
        Assert.Equal("AB", second[0].Text);
    }

    [Fact]
    public void FlushIdle_应把空闲帧吐出来()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions { AutoFrame = true, AutoFrameGapMilliseconds = 20 });
        Feed(processor, "HELLO"u8.ToArray(), 0);

        var output = new List<DisplayLine>();
        processor.FlushIdle(Ticks(100), WallClock, output);

        Assert.Single(output);
        Assert.Equal("HELLO", output[0].Text);
        Assert.Equal(LineDirection.Rx, output[0].Direction);
    }

    [Fact]
    public void 十六进制显示_应按HEX渲染并保留原始字节()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions { AutoFrame = false, HexDisplay = true });

        var lines = Feed(processor, [0x01, 0xAB, 0xFF], 0);

        Assert.Single(lines);
        Assert.Equal("01 AB FF", lines[0].Text);
        Assert.Equal(new byte[] { 0x01, 0xAB, 0xFF }, lines[0].Raw);
    }

    [Fact]
    public void 文本模式_CRLF应显示为空格而不是把一行拆成多行()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions { AutoFrame = false });

        var lines = Feed(processor, "AT\r\nOK\r\n"u8.ToArray(), 0);

        Assert.Single(lines);
        Assert.Equal("AT OK", lines[0].Text);
        Assert.DoesNotContain('\n', lines[0].Text);
        Assert.DoesNotContain('\r', lines[0].Text);
    }

    [Fact]
    public void 发送的数据应作为Tx行立即产出()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions { AutoFrame = true });
        var output = new List<DisplayLine>();

        processor.ProcessTransmitted("AT+VERSION\r\n"u8.ToArray(), WallClock, output);

        Assert.Single(output);
        Assert.Equal(LineDirection.Tx, output[0].Direction);
        Assert.Equal("AT+VERSION", output[0].Text);
        Assert.Equal(12, output[0].ByteLength);
    }

    [Fact]
    public void 序号应单调递增()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions { AutoFrame = false });

        var lines = new List<DisplayLine>();
        lines.AddRange(Feed(processor, [0x01], 0));
        lines.AddRange(Feed(processor, [0x02], 1));

        Assert.Equal(2, lines.Count);
        Assert.True(lines[1].Sequence > lines[0].Sequence);
    }

    [Fact]
    public void 帧时间应反映数据到达时刻_而不是断帧吐出时刻()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions { AutoFrame = true, AutoFrameGapMilliseconds = 20 });
        Feed(processor, "X"u8.ToArray(), 0);

        var output = new List<DisplayLine>();
        var flushWallClock = WallClock.AddMilliseconds(100);
        processor.FlushIdle(Ticks(100), flushWallClock, output);

        Assert.Single(output);
        // 帧起始于 0 ms、在 100 ms 时被吐出 => 显示时间应回退约 100 ms
        var lag = flushWallClock - output[0].Timestamp;
        Assert.InRange(lag.TotalMilliseconds, 80, 120);
    }

    [Fact]
    public void 修改断帧间隔_应先吐出挂起数据()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions { AutoFrame = true, AutoFrameGapMilliseconds = 20 });
        Feed(processor, "PENDING"u8.ToArray(), 0);

        var output = new List<DisplayLine>();
        processor.ApplyOptions(
            new ReceiveOptions { AutoFrame = true, AutoFrameGapMilliseconds = 50 },
            WallClock,
            output);

        Assert.Single(output);
        Assert.Equal("PENDING", output[0].Text);
    }

    [Fact]
    public void 断帧间隔应被夹到合法范围()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions { AutoFrameGapMilliseconds = 99999 });
        Assert.Equal(ReceiveOptions.MaxGapMilliseconds, processor.Options.AutoFrameGapMilliseconds);

        processor.ApplyOptions(new ReceiveOptions { AutoFrameGapMilliseconds = 0 }, WallClock, []);
        Assert.Equal(ReceiveOptions.MinGapMilliseconds, processor.Options.AutoFrameGapMilliseconds);
    }

    [Fact]
    public void Reformat_切换显示方式应重新渲染已有行()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions { AutoFrame = false, HexDisplay = false });
        var lines = Feed(processor, "AB"u8.ToArray(), 0);
        Assert.Equal("AB", lines[0].Text);

        processor.ApplyOptions(
            new ReceiveOptions { AutoFrame = false, HexDisplay = true },
            WallClock,
            []);

        Assert.Equal("41 42", processor.Reformat(lines[0].Raw));
    }

    [Fact]
    public void GB2312中文跨帧显示不应乱码()
    {
        // 关闭自动断帧以便精确控制切包位置
        var processor = new ReceiveProcessor(new ReceiveOptions
        {
            AutoFrame = false,
            EncodingName = "GB2312",
        });

        var first = Feed(processor, [0xD6], 0);
        var second = Feed(processor, [0xD0], 1);

        Assert.Equal(string.Empty, first[0].Text);
        Assert.Equal("中", second[0].Text);
    }

    [Fact]
    public void 系统提示行应标记为System方向()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions());
        var output = new List<DisplayLine>();

        processor.ProcessSystem("串口已打开", WallClock, output);

        Assert.Single(output);
        Assert.Equal(LineDirection.System, output[0].Direction);
        Assert.Equal("串口已打开", output[0].Text);
    }

    [Fact]
    public void Reset_应清空挂起数据()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions { AutoFrame = true });
        Feed(processor, "HALF"u8.ToArray(), 0);
        Assert.Equal(4, processor.PendingByteCount);

        processor.Reset();

        Assert.Equal(0, processor.PendingByteCount);

        var output = new List<DisplayLine>();
        processor.FlushIdle(Ticks(1000), WallClock, output);
        Assert.Empty(output);
    }
}
