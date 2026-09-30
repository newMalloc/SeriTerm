using System.Runtime.InteropServices;

namespace SeriTerm.Core.Framing;

/// <summary>
/// 分隔符断帧：遇到分隔符（如 CRLF）就切出一帧。
///
/// 与空闲断帧的区别：分隔符断帧**不依赖时间**，报文间隔多长都能正确切分，
/// 代价是数据里没有分隔符时不会显示（直到缓冲区到上限强制成帧）。
///
/// 约定：
/// <list type="bullet">
/// <item>帧的 <see cref="RawFrame.Data"/> 含分隔符，<see cref="RawFrame.DisplayLength"/> 不含，显示时行尾干净；</item>
/// <item>两个分隔符之间没有内容的"空帧"会被丢弃，避免刷出一堆空行。</item>
/// </list>
/// </summary>
public sealed class DelimiterFrameSplitter : IFrameSplitter
{
    /// <summary>挂起数据上限：超过就强制成帧，避免设备一直不发分隔符导致内存无限增长。</summary>
    public const int DefaultMaxPendingBytes = 1024 * 1024;

    private readonly List<byte> _pending = [];
    private readonly byte[] _delimiter;

    private long _startTimestamp;

    public DelimiterFrameSplitter(byte[] delimiter, int maxPendingBytes = DefaultMaxPendingBytes)
    {
        ArgumentNullException.ThrowIfNull(delimiter);

        if (delimiter.Length == 0)
        {
            throw new ArgumentException("分隔符不能为空。", nameof(delimiter));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxPendingBytes, delimiter.Length);

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
        Drain(output);

        // 一直等不到分隔符：强制成帧，保证内存有上限、数据也不会被无限期压住
        if (_pending.Count >= MaxPendingBytes)
        {
            Flush(output);
        }
    }

    public void FlushIdle(long now, List<RawFrame> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (_pending.Count >= MaxPendingBytes)
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
        _startTimestamp = 0;
    }

    private void Drain(List<RawFrame> output)
    {
        var pending = CollectionsMarshal.AsSpan(_pending);
        var consumed = 0;

        while (true)
        {
            var index = IndexOf(pending, _delimiter, consumed);

            if (index < 0)
            {
                break;
            }

            var frameLength = index + _delimiter.Length;
            var payloadLength = index - consumed;

            // 两个分隔符之间什么都没有 => 空帧，丢弃
            if (payloadLength > 0)
            {
                var frameData = pending[consumed..frameLength].ToArray();
                output.Add(new RawFrame(_startTimestamp, frameData, payloadLength));
            }

            consumed = frameLength;
        }

        if (consumed > 0)
        {
            _pending.RemoveRange(0, consumed);
        }
    }

    private void Flush(List<RawFrame> output)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        output.Add(new RawFrame(_startTimestamp, [.. _pending]));
        _pending.Clear();
    }

    /// <summary>在 <paramref name="haystack"/> 中从 <paramref name="start"/> 起查找 <paramref name="needle"/>。</summary>
    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int start)
    {
        if (needle.Length == 0 || haystack.Length - start < needle.Length)
        {
            return -1;
        }

        var first = needle[0];
        var limit = haystack.Length - needle.Length;

        for (var i = start; i <= limit; i++)
        {
            if (haystack[i] != first)
            {
                continue;
            }

            var matched = true;

            for (var j = 1; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }
}
