using SeriTerm.Core.Framing;

namespace SeriTerm.Tests.Framing;

public class DelimiterFrameSplitterTests
{
    private static readonly byte[] Crlf = [0x0D, 0x0A];

    private static List<RawFrame> Append(DelimiterFrameSplitter splitter, byte[] data, int timestamp = 0)
    {
        var output = new List<RawFrame>();
        splitter.Append(data, timestamp, output);
        return output;
    }

    [Fact]
    public void 单个帧_应切开且显示长度不含分隔符()
    {
        var splitter = new DelimiterFrameSplitter(Crlf);

        var frames = Append(splitter, "OK\r\n"u8.ToArray());

        Assert.Single(frames);
        Assert.Equal(new byte[] { 0x4F, 0x4B, 0x0D, 0x0A }, frames[0].Data);
        Assert.Equal(2, frames[0].DisplayLength);
        Assert.Equal(new byte[] { 0x4F, 0x4B }, frames[0].DisplayBytes.ToArray());
    }

    [Fact]
    public void 一块数据里的多帧应全部产出()
    {
        var splitter = new DelimiterFrameSplitter(Crlf);

        var frames = Append(splitter, "A1\r\nB2\r\nC3\r\n"u8.ToArray());

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal(2, f.DisplayLength));
        Assert.Equal("A1"u8.ToArray(), frames[0].DisplayBytes.ToArray());
        Assert.Equal("C3"u8.ToArray(), frames[2].DisplayBytes.ToArray());
    }

    [Fact]
    public void 分隔符跨块时也应成帧()
    {
        var splitter = new DelimiterFrameSplitter(Crlf);

        Assert.Empty(Append(splitter, "OK\r"u8.ToArray()));
        var frames = Append(splitter, "\n"u8.ToArray(), 5);

        Assert.Single(frames);
        Assert.Equal("OK"u8.ToArray(), frames[0].DisplayBytes.ToArray());
    }

    [Fact]
    public void 没有分隔符时不产出()
    {
        var splitter = new DelimiterFrameSplitter(Crlf);

        var frames = Append(splitter, "PARTIAL"u8.ToArray());

        Assert.Empty(frames);
        Assert.Equal(7, splitter.PendingByteCount);
    }

    [Fact]
    public void 空帧应被丢弃()
    {
        var splitter = new DelimiterFrameSplitter(Crlf);

        var frames = Append(splitter, "\r\n\r\nA\r\n"u8.ToArray());

        Assert.Single(frames);
        Assert.Equal("A"u8.ToArray(), frames[0].DisplayBytes.ToArray());
    }

    [Fact]
    public void 剩余不完整数据应保留在挂起缓冲()
    {
        var splitter = new DelimiterFrameSplitter(Crlf);

        var frames = Append(splitter, "A\r\nB"u8.ToArray());

        Assert.Single(frames);
        Assert.Equal(1, splitter.PendingByteCount);
    }

    [Fact]
    public void FlushAll_应把挂起数据作为一帧吐出()
    {
        var splitter = new DelimiterFrameSplitter(Crlf);
        Append(splitter, "PARTIAL"u8.ToArray());

        var output = new List<RawFrame>();
        splitter.FlushAll(output);

        Assert.Single(output);
        Assert.Equal(7, output[0].DisplayLength);
        Assert.Equal(0, splitter.PendingByteCount);
    }

    [Fact]
    public void FlushIdle_没有分隔符时不应产出()
    {
        var splitter = new DelimiterFrameSplitter(Crlf);
        Append(splitter, "WAIT"u8.ToArray());

        var output = new List<RawFrame>();
        splitter.FlushIdle(long.MaxValue, output);

        Assert.Empty(output);
    }

    [Fact]
    public void 超过挂起上限应强制成帧()
    {
        var splitter = new DelimiterFrameSplitter(Crlf, maxPendingBytes: 8);

        var frames = Append(splitter, "0123456789"u8.ToArray());

        Assert.Single(frames);
        Assert.Equal(10, frames[0].DisplayLength);
        Assert.Equal(0, splitter.PendingByteCount);
    }

    [Fact]
    public void Reset_应清空挂起数据()
    {
        var splitter = new DelimiterFrameSplitter(Crlf);
        Append(splitter, "DATA"u8.ToArray());

        splitter.Reset();

        Assert.Equal(0, splitter.PendingByteCount);
    }

    [Fact]
    public void 多字节分隔符应支持()
    {
        var splitter = new DelimiterFrameSplitter("END"u8.ToArray());

        var frames = Append(splitter, "A1END"u8.ToArray());

        Assert.Single(frames);
        Assert.Equal(2, frames[0].DisplayLength);
    }

    [Fact]
    public void 空分隔符应抛出异常()
        => Assert.Throws<ArgumentException>(() => new DelimiterFrameSplitter([]));

    [Fact]
    public void 帧起始时间戳应是本批数据的到达时刻()
    {
        var splitter = new DelimiterFrameSplitter(Crlf);

        var frames = Append(splitter, "A\r\nB\r\n"u8.ToArray(), 12345);

        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(12345, f.StartTimestamp));
    }
}
