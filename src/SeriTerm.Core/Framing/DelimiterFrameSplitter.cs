using System.Runtime.InteropServices;

namespace SeriTerm.Core.Framing;

/// <summary>
/// 分隔符断帧：遇到分隔符（如 CRLF）就切出一帧。
///
/// 与空闲断帧的区别：分隔符断帧**不依赖时间**，报文间隔多长都能正确切分，
/// 数据不足一帧时会等待分隔符，长数据流会分段输出。
///
/// 约定：
/// <list type="bullet">
/// <item>帧的 <see cref="RawFrame.Data"/> 含分隔符，<see cref="RawFrame.DisplayLength"/> 不含，显示时行尾干净；</item>
/// <item>两个分隔符之间没有内容的"空帧"会被丢弃，避免刷出一堆空行。</item>
/// </list>
/// </summary>
public sealed class DelimiterFrameSplitter : IFrameSplitter
{
    /// <summary>单帧载荷上限。超长载荷分段，最后一段仍保留原始分隔符。</summary>
    public const int DefaultMaxPendingBytes = RawFrame.MaxReceivePayloadBytes;

    private readonly List<byte> _pending = [];
    private readonly byte[] _delimiter;

    private long _startTimestamp;
    private int _searchStart;

    public DelimiterFrameSplitter(byte[] delimiter, int maxPendingBytes = DefaultMaxPendingBytes)
    {
        ArgumentNullException.ThrowIfNull(delimiter);

        if (delimiter.Length == 0)
        {
            throw new ArgumentException("分隔符不能为空。", nameof(delimiter));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingBytes);

        _delimiter = delimiter;
        MaxPendingBytes = maxPendingBytes;
    }

    /// <summary>分隔符字节序列。</summary>
    public ReadOnlySpan<byte> Delimiter => _delimiter;

    public int MaxPendingBytes { get; }

    public int PendingByteCount => _pending.Count;

    public void Append(ReadOnlySpan<byte> data, long timestamp, List<RawFrame> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (data.IsEmpty)
        {
            return;
        }

        if (_pending.Count == 0)
        {
            _startTimestamp = timestamp;
        }

        _pending.AddRange(data);
        Drain(timestamp, output);
    }

    public void FlushIdle(long now, List<RawFrame> output)
    {
        ArgumentNullException.ThrowIfNull(output);
    }

    public void FlushAll(List<RawFrame> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var pending = CollectionsMarshal.AsSpan(_pending);
        var consumed = 0;
        EmitFullChunks(pending, ref consumed, pending.Length, _startTimestamp, output);
        if (consumed < pending.Length)
            output.Add(new RawFrame(_startTimestamp, pending[consumed..].ToArray()));
        Reset();
    }

    public void Reset()
    {
        _pending.Clear();
        _startTimestamp = 0;
        _searchStart = 0;
    }

    private void Drain(long timestamp, List<RawFrame> output)
    {
        var pending = CollectionsMarshal.AsSpan(_pending);
        var consumed = 0;
        var searchStart = _searchStart;

        while (true)
        {
            var match = pending[searchStart..].IndexOf(_delimiter);
            if (match < 0)
            {
                // 只重扫可能组成分隔符的尾部，保留最后一段载荷以便附上后续分隔符。
                searchStart = Math.Max(searchStart, pending.Length - _delimiter.Length + 1);
                EmitFullChunks(pending, ref consumed, searchStart, timestamp, output);
                break;
            }

            var delimiterStart = searchStart + match;
            var frameEnd = delimiterStart + _delimiter.Length;
            EmitFullChunks(pending, ref consumed, delimiterStart, timestamp, output);

            // 两个分隔符之间什么都没有 => 空帧，丢弃
            if (delimiterStart > consumed)
                output.Add(new RawFrame(_startTimestamp, pending[consumed..frameEnd].ToArray(), delimiterStart - consumed));

            consumed = searchStart = frameEnd;
            _startTimestamp = timestamp;
        }

        _searchStart = searchStart - consumed;
        if (consumed > 0)
            _pending.RemoveRange(0, consumed);
    }

    private void EmitFullChunks(ReadOnlySpan<byte> pending, ref int consumed, int payloadEnd, long timestamp, List<RawFrame> output)
    {
        while (payloadEnd - consumed > MaxPendingBytes)
        {
            output.Add(new RawFrame(_startTimestamp, pending.Slice(consumed, MaxPendingBytes).ToArray()));
            consumed += MaxPendingBytes;
            _startTimestamp = timestamp;
        }
    }
}
