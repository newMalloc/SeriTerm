using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SeriTerm.Core.Mcp;

namespace SeriTerm.App.Services.Mcp;

/// <summary>
/// 界面进程里的 MCP 控制面：一个**仅当前用户可连**的命名管道，外面套一层 JSON-RPC。
///
/// 为什么不用 stdio：MCP 客户端要求 server 用 stdio 说话，而 stdio 只属于它自己拉起的那个子进程。
/// 界面进程同时拿着串口、还要给用户看日志，不能交给客户端掌管；所以真正对外提供 stdio 的是
/// <c>SeriTerm.exe --mcp-stdio</c> 那个桥接进程，它再回到这里来办事。
///
/// 为什么一次连接只处理一个请求：桥接进程按需连接（一个工具调用连一次），
/// 这样"等关键词"这种长请求不会堵住别的调用，两端也不用维护心跳与编号复用。
/// </summary>
public sealed class McpControlServer : IAsyncDisposable
{
    private const int MaxInstances = 8;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>一次连接必须在这么长时间内把请求发过来，避免连接挂着占实例。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly McpSessionDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _clients;

    public McpControlServer(IMcpSession session, ILogger logger)
    {
        _logger = logger;
        _dispatcher = new McpSessionDispatcher(session);
    }

    /// <summary>连接数变化（0 → 1 表示有 AI 客户端接进来了）。</summary>
    public event EventHandler<int>? ClientCountChanged;

    /// <summary>监听起不来（例如管道名被占、权限不允许）。</summary>
    public event EventHandler<string>? Failed;

    public string PipeName => McpProtocol.PipeName;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _loop is { IsCompleted: false };
            }
        }
    }

    public int ConnectedClients => Volatile.Read(ref _clients);

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            var token = _cts.Token;
            _loop = Task.Run(() => AcceptLoopAsync(token), CancellationToken.None);
        }

        _logger.LogInformation("MCP 控制面已启动：管道 {Pipe}", PipeName);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;

        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 取消失败不影响下面的等待
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP 监听循环退出时出现异常");
            }
        }

        cts.Dispose();
        _logger.LogInformation("MCP 控制面已停止。");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;

            try
            {
                server = CreateServer();
            }
            catch (Exception ex)
            {
                // 起不来就别空转：把原因交给界面去说（比如管道名被别的进程占了）
                Failed?.Invoke(this, ex.Message);
                _logger.LogError(ex, "MCP 控制面无法创建命名管道 {Pipe}", PipeName);
                return;
            }

            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                await server.DisposeAsync().ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogDebug(ex, "等待 MCP 连接失败，稍后重试");

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            // 连接交给独立任务处理，立刻回去等下一个
            _ = HandleConnectionAsync(server, cancellationToken);
        }
    }

    private static NamedPipeServerStream CreateServer() => new(
        McpProtocol.PipeName,
        PipeDirection.InOut,
        MaxInstances,
        PipeTransmissionMode.Byte,
        // CurrentUserOnly：同机器上的其它用户连不上（管道名是每台机器共享的命名空间）
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _clients);
        ClientCountChanged?.Invoke(this, count);

        try
        {
            await using (server.ConfigureAwait(false))
            {
                using var reader = new StreamReader(server, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                using var writer = new StreamWriter(server, Utf8NoBom, bufferSize: 4096, leaveOpen: true) { NewLine = "\n" };

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);

                string? line;

                try
                {
                    line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                var response = await ProcessAsync(line, count, cancellationToken).ConfigureAwait(false);

                await writer.WriteLineAsync(response).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                // 等对端把数据读走再关：直接 Dispose 有可能把最后一行截断，
                // 客户端看到的就是"JSON 解析失败"这种莫名其妙的错误。
                try
                {
                    server.WaitForPipeDrain();
                }
                catch (Exception)
                {
                    // 对端已经关了，读走与否都无所谓
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "处理 MCP 连接时出错");
        }
        finally
        {
            ClientCountChanged?.Invoke(this, Interlocked.Decrement(ref _clients));
        }
    }

    private async Task<string> ProcessAsync(string line, int connectedClients, CancellationToken cancellationToken)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return ErrorLine(null, -32700, "请求不是合法的 JSON。", McpProtocol.Reasons.InvalidParams);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return ErrorLine(null, -32600, "请求必须是 JSON-RPC 对象。", McpProtocol.Reasons.InvalidParams);
            }

            var id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : (JsonElement?)null;
            var method = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
                ? methodElement.GetString()
                : null;

            if (string.IsNullOrEmpty(method))
            {
                return ErrorLine(id, -32600, "请求缺少 method。", McpProtocol.Reasons.InvalidParams);
            }

            try
            {
                if (string.Equals(method, McpProtocol.Methods.Ping, StringComparison.Ordinal))
                {
                    var info = _dispatcher.BuildAppInfo(AppInfo.Version, connectedClients);
                    return ResultLine(id, JsonSerializer.SerializeToNode(info, McpProtocol.WireJson));
                }

                if (string.Equals(method, McpProtocol.Methods.CallTool, StringComparison.Ordinal))
                {
                    if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
                    {
                        return ErrorLine(id, -32602, "tools/call 需要 params 对象。", McpProtocol.Reasons.InvalidParams);
                    }

                    var name = parameters.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()
                        : null;

                    var arguments = parameters.TryGetProperty("arguments", out var argumentsElement)
                        ? argumentsElement.Clone()
                        : (JsonElement?)null;

                    var result = await _dispatcher.CallToolAsync(name, arguments, cancellationToken).ConfigureAwait(false);
                    return ResultLine(id, result);
                }

                return ErrorLine(id, -32601, $"未实现的方法：{method}", McpProtocol.Reasons.NotSupported);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理 MCP 请求失败：{Method}", method);
                return ErrorLine(id, -32603, $"{ex.GetType().Name}: {ex.Message}", McpProtocol.Reasons.Internal);
            }
        }
    }

    private static string ResultLine(JsonElement? id, JsonNode? result)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = ToIdNode(id),
            ["result"] = result ?? new JsonObject(),
        };

        return envelope.ToJsonString(McpProtocol.WireJson);
    }

    private static string ErrorLine(JsonElement? id, int code, string message, string reason)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = ToIdNode(id),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["data"] = new JsonObject { ["reason"] = reason },
            },
        };

        return envelope.ToJsonString(McpProtocol.WireJson);
    }

    private static JsonNode? ToIdNode(JsonElement? id)
        => id is { } element ? JsonNode.Parse(element.GetRawText()) : null;
}
