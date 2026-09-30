using System.Diagnostics;

namespace SeriTerm.Core.Framing;

/// <summary>
/// 空闲间隔断帧：距上一块数据超过 <c>gap</c> 就认为一帧结束。
///
/// 实现要点：用"时间戳扫描"而不是"计时器塌缩"。计时器塌缩（每收一块就重置计时）
/// 在高速流下会漏切分；这里直接把数据块的到达时刻与阈值比较，语义明确、可单测。
///
/// 已知局限：同一块数据内部如果本来存在时间间隔，驱动已经把它合并了，这里无法再切开
/// —— 任何基于空闲间隔的工具都有这个限制。
/// </summary>
public sealed class GapFrameSplitter : IFrameSplitter
{
    private readonly long _gapTicks;
    private readonly List<byte> _pending = [];

    private long _startTimestamp;
    private long _lastTimestamp;
    private bool _hasPending;

    public GapFrameSplitter(TimeSpan gap)
    {
        if (gap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gap), "断帧间隔必须大于 0。");
        }

        _gapTicks = Math.Max(1, (long)(gap.TotalSeconds * Stopwatch.Frequency));
        Gap = gap;
    }

    public TimeSpan Gap { get; }

    /// <summary>当前挂起（尚未成帧）的字节数。</summary>
    public int PendingByteCount => _pending.Count;

    public void Append(ReadOnlySpan<byte> data, long timestamp, List<RawFrame> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (data.IsEmpty)
        {
            return;
        }

        // 与上一块的间隔达到阈值 => 上一帧结束
        if (_hasPending && timestamp - _lastTimestamp >= _gapTicks)
        {
            Flush(output);
        }

        if (!_hasPending)
        {
            _startTimestamp = timestamp;
            _hasPending = true;
        }

        _pending.AddRange(data);
        _lastTimestamp = timestamp;
    }

    public void FlushIdle(long now, List<RawFrame> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (_hasPending && now - _lastTimestamp >= _gapTicks)
        {
            Flush(output);
        }
    }

    public void FlushAll(List<RawFrame> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        Flush(output);
    }

    public void Reset()
    {
        _pending.Clear();
        _hasPending = false;
        _startTimestamp = 0;
        _lastTimestamp = 0;
    }

    private void Flush(List<RawFrame> output)
    {
        if (_pending.Count > 0)
        {
            output.Add(new RawFrame(_startTimestamp, [.. _pending]));
        }

        _pending.Clear();
        _hasPending = false;
    }
}
