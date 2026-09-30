using SeriTerm.Core.Framing;

namespace SeriTerm.Core.Pipeline;

/// <summary>接收显示相关设置。</summary>
public sealed record ReceiveOptions
{
    /// <summary>断帧方式。</summary>
    public FramingMode Framing { get; init; } = FramingMode.Gap;

    /// <summary>空闲断帧的间隔（毫秒），1–1000。</summary>
    public int AutoFrameGapMilliseconds { get; init; } = 20;

    /// <summary>分隔符断帧使用的分隔符写法，例如 <c>\r\n</c>、<c>hex:0D 0A</c>。</summary>
    public string DelimiterText { get; init; } = "\\r\\n";

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
        DelimiterText = string.IsNullOrWhiteSpace(DelimiterText) ? "\\r\\n" : DelimiterText,
    };
}
