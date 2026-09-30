using System.Diagnostics;
using SeriTerm.Core.Framing;

namespace SeriTerm.Tests.Framing;

public class GapFrameSplitterTests
{
    private const int GapMs = 20;

    private static long Ticks(int milliseconds) => milliseconds * Stopwatch.Frequency / 1000;

    private static GapFrameSplitter CreateSplitter() => new(TimeSpan.FromMilliseconds(GapMs));

    private static List<RawFrame> Append(GapFrameSplitter splitter, byte[] data, int atMilliseconds)
    {
        var output = new List<RawFrame>();
        splitter.Append(data, Ticks(atMilliseconds), output);
        return output;
    }

    [Fact]
    public void 单块数据_未到空闲阈值前不产出()
    {
        var splitter = CreateSplitter();

        var frames = Append(splitter, [0x01, 0x02], 0);

        Assert.Empty(frames);
        Assert.Equal(2, splitter.PendingByteCount);
    }

    [Fact]
    public void 间隔达到阈值_应切分成两帧()
    {
        var splitter = CreateSplitter();

        Append(splitter, [0x01, 0x02], 0);
        var frames = Append(splitter, [0x03], GapMs);

        Assert.Single(frames);
        Assert.Equal(new byte[] { 0x01, 0x02 }, frames[0].Data);
        Assert.Equal(1, splitter.PendingByteCount);
    }

    [Fact]
    public void 间隔未达阈值_应合并为一帧()
    {
        var splitter = CreateSplitter();

        Append(splitter, [0x01], 0);
        var frames = Append(splitter, [0x02], GapMs - 1);

        Assert.Empty(frames);

        var flushed = new List<RawFrame>();
        splitter.FlushIdle(Ticks(GapMs * 3), flushed);

        Assert.Single(flushed);
        Assert.Equal(new byte[] { 0x01, 0x02 }, flushed[0].Data);
    }

    [Fact]
    public void FlushIdle_未到阈值不产出()
    {
        var splitter = CreateSplitter();
        Append(splitter, [0xAA], 0);

        var output = new List<RawFrame>();
        splitter.FlushIdle(Ticks(GapMs - 1), output);

        Assert.Empty(output);
        Assert.Equal(1, splitter.PendingByteCount);
    }

    [Fact]
    public void 帧的起始时间戳应是本帧第一块数据的到达时刻()
    {
        var splitter = CreateSplitter();

        Append(splitter, [0xFF], 5);
        // 第二块到达时会把第一帧挤出来，因此这一帧在本次调用的输出里
        var frames = Append(splitter, [0xFE], 5 + GapMs);

        var output = new List<RawFrame>(frames);
        splitter.FlushIdle(Ticks(1000), output);

        Assert.Equal(2, output.Count);
        Assert.Equal(Ticks(5), output[0].StartTimestamp);
        Assert.Equal(Ticks(5 + GapMs), output[1].StartTimestamp);
    }

    [Fact]
    public void 空数据不应产生帧也不应改变挂起状态()
    {
        var splitter = CreateSplitter();

        var frames = Append(splitter, [], 0);

        Assert.Empty(frames);
        Assert.Equal(0, splitter.PendingByteCount);
    }

    [Fact]
    public void Reset_应丢弃挂起数据()
    {
        var splitter = CreateSplitter();
        Append(splitter, [0x01, 0x02], 0);

        splitter.Reset();

        var output = new List<RawFrame>();
        splitter.FlushIdle(Ticks(1000), output);

        Assert.Empty(output);
        Assert.Equal(0, splitter.PendingByteCount);
    }

    [Fact]
    public void 连续多块_应按间隔切出多帧()
    {
        var splitter = CreateSplitter();
        var output = new List<RawFrame>();

        splitter.Append([0x01], Ticks(0), output);
        splitter.Append([0x02], Ticks(GapMs * 2), output);
        splitter.Append([0x03], Ticks(GapMs * 3), output);
        splitter.FlushIdle(Ticks(GapMs * 10), output);

        Assert.Equal(3, output.Count);
        Assert.Equal(new byte[] { 0x01 }, output[0].Data);
        Assert.Equal(new byte[] { 0x02 }, output[1].Data);
        Assert.Equal(new byte[] { 0x03 }, output[2].Data);
    }

    [Fact]
    public void 非法间隔_应抛出异常()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new GapFrameSplitter(TimeSpan.Zero));
}
