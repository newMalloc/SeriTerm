namespace SeriTerm.Core.Pipeline;

/// <summary>接收显示相关设置。</summary>
public sealed record ReceiveOptions
{
    /// <summary>自动断帧：空闲间隔断帧，把收到的数据处理成一帧一行的显示。</summary>
    public bool AutoFrame { get; init; } = true;

    /// <summary>断帧间隔（毫秒），1–1000。</summary>
    public int AutoFrameGapMilliseconds { get; init; } = 20;

    /// <summary>十六进制显示（不勾选则按 <see cref="EncodingName"/> 解码显示文本）。</summary>
    public bool HexDisplay { get; init; }

    public string EncodingName { get; init; } = "UTF-8";

    /// <summary>显示时间戳列。</summary>
    public bool ShowTimestamp { get; init; } = true;

    /// <summary>显示方向列（Rx/Tx）。</summary>
    public bool ShowDirection { get; init; } = true;

    public const int MinGapMilliseconds = 1;

    public const int MaxGapMilliseconds = 1000;

    /// <summary>把可能越界的输入夹到合法范围。</summary>
    public ReceiveOptions Normalize() => this with
    {
        AutoFrameGapMilliseconds = Math.Clamp(AutoFrameGapMilliseconds, MinGapMilliseconds, MaxGapMilliseconds),
        EncodingName = string.IsNullOrWhiteSpace(EncodingName) ? "UTF-8" : EncodingName,
    };
}
