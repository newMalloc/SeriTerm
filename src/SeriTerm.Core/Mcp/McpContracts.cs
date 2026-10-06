namespace SeriTerm.Core.Mcp;

/// <summary>读帧时的方向过滤。</summary>
public enum McpFrameDirection
{
    Rx,
    Tx,
    System,
    All,
}

/// <summary>等待关键词时的匹配方式。</summary>
public enum McpPatternMode
{
    /// <summary>解码成文本后做包含匹配。</summary>
    Text,

    /// <summary>解码成文本后按正则匹配。</summary>
    Regex,

    /// <summary>按十六进制字节序列匹配（如 <c>0D 0A</c>）。</summary>
    Hex,
}

public sealed record McpPortInfo(string Name, string DisplayName, bool IsCurrent);

/// <summary>读帧参数（已由 <see cref="McpSessionDispatcher"/> 校验并夹到合法范围）。</summary>
public sealed record McpFrameQuery(
    long Since,
    int Limit,
    McpFrameDirection Direction,
    bool IncludeHex,
    bool IncludeText,
    int MaxChars)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int DefaultMaxChars = 512;
    public const int MaxCharsLimit = 4096;
}

/// <summary>一帧的对外表示。<see cref="Seq"/> 是游标：下次读帧把它原样传回 <c>since</c> 即可续读。</summary>
public sealed record McpFrameInfo(
    long Seq,
    DateTime Time,
    string Direction,
    int Bytes,
    string? Hex,
    string? Text,
    bool Truncated);

public sealed record McpFramePage(
    IReadOnlyList<McpFrameInfo> Frames,
    long NextCursor,
    long OldestCursor,
    long TotalFrames,
    long EvictedFrames,
    bool HasMore);

public sealed record McpPatternQuery(
    string Pattern,
    McpPatternMode Mode,
    bool CaseSensitive,
    long Since,
    McpFrameDirection Direction,
    int TimeoutMilliseconds,
    int ContextFrames,
    int MaxMatches,
    int MaxChars)
{
    public const int DefaultTimeoutMilliseconds = 5_000;
    public const int MaxTimeoutMilliseconds = 120_000;
    public const int MaxContextFrames = 20;
    public const int DefaultMaxMatches = 10;
    public const int MaxMatchesLimit = 50;
}

public sealed record McpPatternResult(
    bool Matched,
    bool TimedOut,
    int ScannedFrames,
    long NextCursor,
    IReadOnlyList<McpFrameInfo> Frames);

/// <summary>打开串口时的参数覆盖；为 <c>null</c> 的字段沿用界面上的当前设置。</summary>
public sealed record McpOpenRequest(
    string? PortName,
    int? BaudRate,
    int? DataBits,
    string? Parity,
    string? StopBits,
    string? Handshake,
    bool? DtrEnable,
    bool? RtsEnable);

/// <summary>一次 AI 发送。<see cref="Payload"/> 已由派发层按 HEX/文本与行尾组装完成。</summary>
public sealed record McpWriteRequest(byte[] Payload, string Preview, bool HexInput);

public sealed record McpWriteResult(int SentBytes, string Hex, int RateLimitRemaining);

/// <summary>会话快照。字段都是"用户在界面上能看到的东西"，让 AI 有据可依。</summary>
public sealed record McpStatus(
    bool Open,
    string? PortName,
    string? PortDisplayName,
    int? BaudRate,
    int? DataBits,
    string? Parity,
    string? StopBits,
    string? Handshake,
    bool DtrEnable,
    bool RtsEnable,
    long RxBytes,
    long TxBytes,
    string EncodingName,
    string FramingMode,
    int AutoFrameGapMilliseconds,
    string DelimiterText,
    bool TimestampEnabled,
    string ReconnectStatus,
    bool AutoReconnect,
    long JournalFrames,
    long JournalEvictedFrames,
    long LogDroppedLines,
    McpPermission Permission,
    int ConnectedClients);

/// <summary>最后一次打开失败等链路错误，读出来给 AI 解释用。</summary>
public sealed record McpLinkError(string Message, DateTime Time);

/// <summary>
/// MCP 工具真正落到串口会话上的动作。实现在 App 层（见 <c>MainViewModel.Mcp.cs</c>），
/// 派发层（权限、护栏、参数校验、结果整形）留在 Core 里，这样这部分可以脱离 WPF 单测。
/// </summary>
public interface IMcpSession
{
    McpPermission Permission { get; }

    /// <summary>当前文本编码名（用于把帧解码给 AI 看）。</summary>
    string EncodingName { get; }

    /// <summary>最近一次链路故障；没有则为 <c>null</c>。</summary>
    McpLinkError? LastError { get; }

    Task<IReadOnlyList<McpPortInfo>> ListPortsAsync(CancellationToken cancellationToken);

    Task<McpStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<McpFramePage> ReadFramesAsync(McpFrameQuery query, CancellationToken cancellationToken);

    Task<McpPatternResult> WaitForPatternAsync(McpPatternQuery query, CancellationToken cancellationToken);

    Task<McpStatus> OpenAsync(McpOpenRequest request, CancellationToken cancellationToken);

    Task<McpStatus> CloseAsync(CancellationToken cancellationToken);

    Task<McpStatus> SetBaudRateAsync(int baudRate, CancellationToken cancellationToken);

    /// <summary>把 <see cref="McpWriteRequest.Payload"/> 写进串口，返回实际写入的字节数。</summary>
    Task<int> WriteAsync(McpWriteRequest request, CancellationToken cancellationToken);

    /// <summary>把一条"来自 AI"的审计提示写进日志区（界面上必须看得见）。</summary>
    void Audit(string message);
}
