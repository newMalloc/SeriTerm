using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SeriTerm.Core.Mcp;

/// <summary>
/// <see cref="IMcpBackend"/> 的命名管道实现：每次调用新开一条连接，写一行请求、读一行响应、断开。
///
/// 为什么不做长连接 + 多路复用：管道服务端（界面进程）是"一次连接一次请求"的模型，
/// 这样它不必维护会话状态、也不必处理半包粘包；代价只是每次调用多一次 <c>Connect</c>，
/// 在本地命名管道上是微秒级的事，而 MCP 的调用频率是"每秒个位数"，完全够用。
///
/// 也正因为连接是一次性的，<see cref="DisposeAsync"/> 是空操作——没有需要释放的常驻资源。
/// </summary>
public sealed class McpPipeClient : IMcpBackend
{
    /// <summary>管道连不上时使用的 JSON-RPC 错误码（沿用 JSON-RPC 的自定义服务端错误区间）。</summary>
    private const int NotConnectedCode = -32000;

    private readonly string _pipeName;
    private int _nextId;

    public McpPipeClient(string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? McpProtocol.PipeName : pipeName;
    }

    /// <summary>单次连接的超时。界面进程没在跑时要尽快失败，不能让模型干等。</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 界面进程是否在运行。<c>null</c> 表示连不上（没启动 / 管道名不对 / 超时），
    /// 这是"探测"语义，所以连不上不抛异常——调用方用它来决定要不要拉起界面进程。
    /// </summary>
    public async Task<McpAppInfo?> PingAsync(CancellationToken cancellationToken)
    {
        var result = await SendAsync(McpProtocol.Methods.Ping, parameters: null, cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            return null;
        }

        try
        {
            return result.Value.Deserialize<McpAppInfo>(McpProtocol.WireJson);
        }
        catch (JsonException)
        {
            // 管道对面不是 SeriTerm（或版本不兼容），按"不在运行"处理更安全
            return null;
        }
    }

    /// <summary>
    /// 转发一次工具调用，返回界面进程给的 <c>tools/call</c> 结果对象（content / isError 原样带回）。
    /// 连不上界面进程属于"调用失败"，所以这里抛出 <see cref="McpBackendException"/>，
    /// 由 <see cref="McpStdioServer"/> 翻成工具级错误结果。
    /// </summary>
    public async Task<JsonElement> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = toolName,
            ["arguments"] = arguments,
        };

        var result = await SendAsync(McpProtocol.Methods.CallTool, parameters, cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            throw new McpBackendException(
                NotConnectedCode,
                McpProtocol.Reasons.NotConnected,
                "连不上 SeriTerm 界面进程（命名管道 " + _pipeName + "）。请确认 SeriTerm 正在运行。");
        }

        return result.Value;
    }

    /// <summary>没有常驻连接，无需释放。</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// 走一次完整的"连接 → 写一行 → 读一行 → 断开"。
    /// 返回 <c>null</c> 表示连不上（界面进程没在跑）；其余协议层错误抛异常。
    /// </summary>
    private async Task<JsonElement?> SendAsync(
        string method,
        Dictionary<string, object?>? parameters,
        CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _nextId),
            ["method"] = method,
        };

        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        var requestLine = JsonSerializer.Serialize(request, McpProtocol.WireJson);

        // 每次调用一条独立连接：服务端因此不必维护任何跨请求状态
        var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await using (client.ConfigureAwait(false))
        {
            try
            {
                var timeout = (int)ConnectTimeout.TotalMilliseconds;

                await client.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                // 界面没运行（FileNotFoundException）、管道被占满、拒绝访问、连接超时：都等价于"连不上"
                return null;
            }

            // UTF-8 且不写 BOM：对面用一个 JSON 解析器读整行，前导 BOM 会让它解析失败。
            // leaveOpen 全部打开，由上面的 await using 统一收掉底层管道。
            using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true)
            {
                // 行尾固定 '\n'，不跟随平台：契约里线路就是 LF
                NewLine = "\n",
                AutoFlush = true,
            };

            using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

            await writer.WriteLineAsync(requestLine.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            var responseLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(responseLine))
            {
                // 连上了但对面什么都没说：管道服务端可能正在关闭，等价于连不上
                return null;
            }

            return ReadResponse(responseLine);
        }
    }

    /// <summary>
    /// 拆一行响应：有 <c>error</c> 就抛（协议层错误），否则返回 <c>result</c>。
    /// command 的 <c>result</c> 用 <see cref="JsonElement.Clone"/> 复制一份，
    /// 因为原始 <see cref="JsonDocument"/> 随 using 释放后元素就不能再访问了。
    /// </summary>
    private static JsonElement? ReadResponse(string responseLine)
    {
        using var document = JsonDocument.Parse(responseLine);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
                ? parsedCode
                : NotConnectedCode;

            var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString() ?? string.Empty
                : string.Empty;

            var reason = McpProtocol.Reasons.Internal;

            if (error.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("reason", out var reasonElement)
                && reasonElement.ValueKind == JsonValueKind.String)
            {
                reason = reasonElement.GetString() ?? McpProtocol.Reasons.Internal;
            }

            throw new McpBackendException(code, reason, message);
        }

        if (!root.TryGetProperty("result", out var result) || result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return result.Clone();
    }
}
