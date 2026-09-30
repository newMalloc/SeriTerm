namespace SeriTerm.Core.Logging;

/// <summary>原始日志的封装格式。</summary>
public enum RawLogFormat
{
    /// <summary>纯字节流：只写有效载荷，适合直接喂给别的工具，但丢失方向与时间。</summary>
    Plain,

    /// <summary>成帧格式：<c>[8 字节 UTC Ticks][1 字节方向][4 字节长度][载荷]</c>，可完整重放。</summary>
    Framed,
}

/// <summary>一条成帧原始日志记录。</summary>
public readonly record struct RawLogRecord(DateTime TimestampUtc, byte Direction, byte[] Payload);

/// <summary>日志落盘设置。</summary>
public sealed record LogSessionOptions
{
    /// <summary>是否写原始字节日志。</summary>
    public bool RawEnabled { get; init; }

    /// <summary>是否写文本日志。</summary>
    public bool TextEnabled { get; init; } = true;

    public RawLogFormat RawFormat { get; init; } = RawLogFormat.Framed;

    /// <summary>日志目录。</summary>
    public string Directory { get; init; } = DefaultDirectory;

    /// <summary>单文件大小上限，超过自动分卷。</summary>
    public long MaxFileBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>文本日志使用的编码。</summary>
    public string EncodingName { get; init; } = "UTF-8";

    /// <summary>文本日志是否按十六进制记录。</summary>
    public bool HexText { get; init; }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "SeriTerm",
        "Logs");

    public LogSessionOptions Normalize() => this with
    {
        Directory = string.IsNullOrWhiteSpace(Directory) ? DefaultDirectory : Directory,
        MaxFileBytes = MaxFileBytes <= 0 ? 100L * 1024 * 1024 : MaxFileBytes,
        EncodingName = string.IsNullOrWhiteSpace(EncodingName) ? "UTF-8" : EncodingName,
    };
}
