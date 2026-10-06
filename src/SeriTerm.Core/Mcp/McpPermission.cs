namespace SeriTerm.Core.Mcp;

/// <summary>
/// AI（MCP 客户端）对本机串口会话的权限档位。
///
/// 分两档而不是"开关 + 一堆细项"：读错只是答复没用，写错可能把设备打进 bootloader、
/// 擦掉 Flash 或触发执行器，所以默认档只给读，写入必须由用户在界面上显式打开。
/// </summary>
public enum McpPermission
{
    /// <summary>只读（默认）：可列端口、看状态、读帧、等关键词，不能发送。</summary>
    ReadOnly,

    /// <summary>完全权限：额外允许打开/关闭串口、改波特率、发送数据。</summary>
    Full,
}

public static class McpPermissionExtensions
{
    /// <summary>界面与协议里使用的文案。</summary>
    public static string ToDisplayText(this McpPermission permission)
        => permission == McpPermission.Full ? "完全权限" : "只读";

    /// <summary>协议里使用的稳定标识（不随界面文案变化）。</summary>
    public static string ToProtocolText(this McpPermission permission)
        => permission == McpPermission.Full ? "full" : "readOnly";
}
