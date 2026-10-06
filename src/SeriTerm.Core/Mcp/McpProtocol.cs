using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SeriTerm.Core.Mcp;

/// <summary>
/// AI 接入的线路约定（App ↔ <c>--mcp-stdio</c> 桥接进程之间走本地命名管道）。
///
/// 为什么桥接要与界面分成两个进程：MCP 客户端要求 server 用 stdio 通信，而 stdio 属于
/// 客户端拉起的那个子进程。界面进程同时持有串口，所以把"协议"放在桥接进程、
/// 把"会话"留在界面进程，两者之间用命名管道（仅当前用户可连）对话。
/// </summary>
public static class McpProtocol
{
    /// <summary>对外声明的 MCP 协议版本。</summary>
    public const string ProtocolVersion = "2025-06-18";

    public const string ServerName = "seriterm";

    public const string ServerTitle = "SeriTerm 串口调试助手";

    /// <summary>
    /// 命名管道名。带当前用户标识的短哈希：管道名是"每台机器"的命名空间，
    /// 不同用户各开一个 SeriTerm 时不能互相抢（同名会让后启动的那个连不上）。
    /// </summary>
    public static string PipeName { get; } = BuildPipeName();

    /// <summary>线路上的压缩 JSON（不缩进，null 省略）。</summary>
    public static JsonSerializerOptions WireJson { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 必须显式给一个解析器：这组选项会被 JsonNode.ToJsonString / 序列化器反复用到，
        // 一旦被标记为只读而解析器仍为空，运行时会直接抛异常（"must specify a TypeInfoResolver"）。
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new McpPermissionJsonConverter() },
    };

    /// <summary>给 AI 看的文本内容用缩进 JSON，读起来舒服些。</summary>
    public static JsonSerializerOptions PrettyJson { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new McpPermissionJsonConverter() },
    };

    /// <summary>线路方法名。工具调用直接沿用 MCP 的 <c>tools/call</c>，桥接层不必做二次翻译。</summary>
    public static class Methods
    {
        /// <summary>问界面进程要运行信息与当前权限。</summary>
        public const string Ping = "seriterm/ping";

        public const string CallTool = "tools/call";
    }

    public static class Reasons
    {
        public const string InvalidParams = "invalid_params";
        public const string UnknownTool = "unknown_tool";
        public const string PermissionDenied = "permission_denied";
        public const string RateLimited = "rate_limited";
        public const string PayloadTooLarge = "payload_too_large";
        public const string SerialBusy = "serial_busy";
        public const string SerialFault = "serial_fault";
        public const string McpDisabled = "mcp_disabled";

        /// <summary>连不上界面进程（它没运行，或者跑在别的用户账户下）。</summary>
        public const string NotConnected = "not_connected";

        public const string NotFound = "not_found";
        public const string NotSupported = "not_supported";
        public const string Internal = "internal_error";
    }

    private static string BuildPipeName()
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"SeriTerm.Mcp.{Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant()}";
    }
}

/// <summary>权限枚举在线路上的稳定写法（<c>readOnly</c> / <c>full</c>）。</summary>
public sealed class McpPermissionJsonConverter : JsonConverter<McpPermission>
{
    public override McpPermission Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();

        return string.Equals(text, "full", StringComparison.OrdinalIgnoreCase)
            ? McpPermission.Full
            : McpPermission.ReadOnly;
    }

    public override void Write(Utf8JsonWriter writer, McpPermission value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToProtocolText());
}

/// <summary>界面进程的运行信息（<c>seriterm/ping</c> 的返回）。</summary>
public sealed record McpAppInfo(string App, string Version, McpPermission Permission, int ConnectedClients);

/// <summary>
/// 桥接进程从界面进程取数据的通道。测试里换成假实现即可验证协议层。
///
/// 线上形状（两边都按这个读，别搞混）：
/// <code>
/// {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"serial_read_frames","arguments":{"limit":5}}}
/// </code>
/// 也就是 <c>params</c> 是 <c>{name, arguments}</c> 两个字段（MCP 原生形状），
/// 而 <see cref="CallToolAsync"/> 的第二个参数是**工具自己的 arguments 对象**（上面例子里的 <c>{"limit":5}</c>）。
/// </summary>
public interface IMcpBackend : IAsyncDisposable
{
    /// <summary>界面进程是否在运行（<c>null</c> 表示没连上）。</summary>
    Task<McpAppInfo?> PingAsync(CancellationToken cancellationToken);

    /// <summary>转发一次工具调用，返回 MCP <c>tools/call</c> 的结果对象（含 content / isError）。</summary>
    Task<JsonElement> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>界面进程返回的协议层错误（工具执行失败由 result.isError 表达，不走这里）。</summary>
public sealed class McpBackendException(int code, string reason, string message) : Exception(message)
{
    public int Code { get; } = code;

    public string Reason { get; } = reason;
}
