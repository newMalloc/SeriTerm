namespace SeriTerm.Core.Framing;

/// <summary>
/// 断帧策略。串口是字节流、没有帧边界，需要按策略切分成"帧"再显示。
/// 约定：本接口不是线程安全的，调用方负责串行化（见 <c>ReceiveProcessor</c> 的使用方）。
/// </summary>
public interface IFrameSplitter
{
    /// <summary>当前挂起（尚未成帧）的字节数。</summary>
    int PendingByteCount { get; }

    /// <summary>喂入一块数据；满足断帧条件时把完整的帧写入 <paramref name="output"/>。</summary>
    void Append(ReadOnlySpan<byte> data, long timestamp, List<RawFrame> output);

    /// <summary>由定时器调用：空闲断帧策略用它把最后一帧吐出来。</summary>
    void FlushIdle(long now, List<RawFrame> output);

    /// <summary>无条件把挂起数据作为一帧吐出（切换断帧设置、清空前使用，避免数据丢失）。</summary>
    void FlushAll(List<RawFrame> output);

    /// <summary>丢弃尚未成帧的挂起数据。</summary>
    void Reset();
}
