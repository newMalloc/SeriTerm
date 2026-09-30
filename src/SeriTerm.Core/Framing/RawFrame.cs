namespace SeriTerm.Core.Framing;

/// <summary>断帧方式。</summary>
public enum FramingMode
{
    /// <summary>不断帧：每块到达的数据作为一帧显示。</summary>
    None,

    /// <summary>空闲间隔断帧：距上一块数据超过设定时间即认为一帧结束。</summary>
    Gap,

    /// <summary>分隔符断帧：遇到分隔符（如 CRLF）即认为一帧结束。</summary>
    Delimiter,
}

/// <summary>
/// 一个完整的帧。
/// <para><see cref="Data"/> 保留**全部原始字节**（分隔符断帧时包含分隔符本身），
/// <see cref="DisplayLength"/> 指出其中属于"有效载荷"、应该显示出来的长度。
/// 这样既保证了原始数据不丢，又能让分隔符断帧下的显示干净（行尾不带 CRLF）。</para>
/// </summary>
public readonly record struct RawFrame(long StartTimestamp, byte[] Data, int DisplayLength)
{
    public RawFrame(long startTimestamp, byte[] data)
        : this(startTimestamp, data, data.Length)
    {
    }

    /// <summary>应该显示的字节（不含分隔符）。</summary>
    public ReadOnlySpan<byte> DisplayBytes => Data.AsSpan(0, Math.Clamp(DisplayLength, 0, Data.Length));
}
