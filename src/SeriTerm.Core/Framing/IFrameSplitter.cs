namespace SeriTerm.Core.Framing;

/// <summary>一个完整的帧：起始时间戳（<see cref="System.Diagnostics.Stopwatch"/> 刻度）+ 原始字节。</summary>
public readonly record struct RawFrame(long StartTimestamp, byte[] Data);

/// <summary>
/// 断帧策略。串口是字节流、没有帧边界，需要按策略切分成"帧"再显示。
/// 约定：本接口不是线程安全的，调用方负责串行化（见 <c>ReceiveProcessor</c> 的使用方）。
/// </summary>
public interface IFrameSplitter
{
    /// <summary>喂入一块数据；到达时间超过阈值时会先把挂起数据吐出来。</summary>
    void Append(ReadOnlySpan<byte> data, long timestamp, List<RawFrame> output);

    /// <summary>由定时器调用：把因为空闲而结束的最后一帧吐出来。</summary>
    void FlushIdle(long now, List<RawFrame> output);

    /// <summary>丢弃尚未成帧的挂起数据。</summary>
    void Reset();
}
