using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SeriTerm.Core.Mcp;

/// <summary>
/// MCP 服务端的 stdio 传输层（运行在 <c>SeriTerm.exe --mcp-stdio</c> 这个桥接进程里）。
///
/// 为什么桥接要单独写一层协议：MCP 客户端（Claude Desktop / Cursor / VS Code）只会用
/// stdin/stdout 与 server 对话，而串口会话在界面进程手里。这一层只做"翻译"——
/// 把 stdio 上的每一行 JSON-RPC 用命名管道转给界面进程，再把结果原样写回 stdout；
/// 它自己不碰串口，也不理解工具语义，所以可以脱离 WPF 用假的 <see cref="IMcpBackend"/> 单测。
///
/// 两条容易踩的约定都固化在这里：
/// 1. stdio 传输是"一行一条消息"，因此输出必须是紧凑 JSON（不能带裸换行）并且每次 Flush
///    —— 客户端读到换行才会认为消息结束，缓冲住就等于把对话卡死。
/// 2. 工具执行失败属于"结果"，不是协议错误。没连上界面进程时返回
///    <c>{content:[...],isError:true}</c>，让模型看到 <c>[not_connected]</c> 并有机会自我纠正；
///    只有方法不存在、参数不合法、JSON 解析不了这类才回 JSON-RPC <c>error</c>。
/// </summary>
public sealed class McpStdioServer
{
    /// <summary>JSON-RPC 解析失败。</summary>
    private const int ParseErrorCode = -32700;

    /// <summary>JSON-RPC 方法不存在。</summary>
    private const int MethodNotFoundCode = -32601;

    /// <summary>JSON-RPC 参数不合法。</summary>
    private const int InvalidParamsCode = -32602;

    /// <summary>JSON-RPC 服务端内部错误。</summary>
    private const int InternalErrorCode = -32603;

    /// <summary>界面进程连不上时工具级错误用的原因标识（与派发层、管道客户端同一套）。</summary>
    private const string NotConnectedReason = McpProtocol.Reasons.NotConnected;

    /// <summary>拉起界面进程后等它开好管道的时间上限。</summary>
    private static readonly TimeSpan BackendWaitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>等待界面进程时的轮询间隔。</summary>
    private static readonly TimeSpan BackendPollInterval = TimeSpan.FromMilliseconds(250);

    private const string InitializeMethod = "initialize";
    private const string ToolsListMethod = "tools/list";
    private const string ToolsCallMethod = "tools/call";

    /// <summary>还不知道权限档位（界面进程没跑）。</summary>
    private const int UnknownPermissionState = -1;

    private readonly IMcpBackend _backend;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly string _serverVersion;

    /// <summary>
    /// 最近一次知道的 AI 权限档位。它是纯缓存：<c>tools/list</c> 会因此少一次往返，
    /// 而权限本来就只能在界面进程里改（改了必然重开会话），缓存不会长期失真。
    /// 取值是 <c>(int)权限 &lt;&lt; 1</c>，<c>-1</c> 表示还不知道（界面进程没跑），
    /// 此时按"完全权限"列出全部工具——先让客户端看到能力，真正调用时再由界面进程按真实权限拒绝。
    ///
    /// 为什么用 int 而不是 <c>McpPermission?</c>：<c>volatile</c> 不允许可空值类型字段，
    /// 而这里只需要"可见性"，用位移编码比每次都 Interlocked 更轻。
    /// </summary>
    private volatile int _lastPermissionState = UnknownPermissionState;

    public McpStdioServer(IMcpBackend backend, TextReader input, TextWriter output, string serverVersion)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _serverVersion = serverVersion ?? throw new ArgumentNullException(nameof(serverVersion));
    }

    /// <summary>
    /// 界面进程没在运行时用它把界面拉起来。桥接进程是 MCP 客户端拉起的子进程，
    /// 用户很可能只开了 Claude Desktop 而没开 SeriTerm，这一步决定"能不能自动连上"。
    /// 只调用一次，失败就算了（退回工具级错误），不反复弹窗。
    /// </summary>
    public Func<CancellationToken, Task>? EnsureBackendAvailable { get; set; }

    /// <summary>
    /// 跑 stdio 循环直到输入流结束（客户端关掉 stdin）。
    /// 返回进程退出码：正常结束是 0，被取消是 1。
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                var line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                // ReadLine 返回 null 表示 stdin 已关闭：客户端正常退出，不是错误
                if (line is null)
                {
                    return 0;
                }

                // 空行（含只有空白的行）不是消息，忽略即可
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                // 顺序处理：MCP 的请求量很小，而且每个请求只占一次管道往返，
                // 省掉并发原语换来的是"输出行与请求一一对应"这种容易验证的性质
                var response = await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);

                if (response is not null)
                {
                    WriteResponse(response);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 进程被要求退出（关窗口 / Ctrl+C），静默收场
            return 1;
        }
        catch (IOException)
        {
            // stdin/stdout 已经断开（客户端进程被杀），同样按正常退出处理
            return 1;
        }
    }

    /// <summary>
    /// 解析并处理一行请求。返回 <c>null</c> 表示"这一行不需要回应"（通知，或空方法名）。
    /// </summary>
    private async Task<string?> HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // 连 id 都拿不到，只能回 null（JSON-RPC 对解析错误的规定）
            return Serialize(ErrorResponse(null, ParseErrorCode, "无法解析这一行 JSON。"));
        }

        using (document)
        {
            var root = document.RootElement;

            // 不是 JSON-RPC 对象（比如客户端误把工具体也走 stdio）视作解析错误
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Serialize(ErrorResponse(null, ParseErrorCode, "消息必须是 JSON-RPC 对象。"));
            }

            // id 原样保留（数字、字符串都要能回传），JSON-RPC 靠它把响应配回请求
            var hasId = root.TryGetProperty("id", out var idElement);
            var id = hasId ? JsonNode.Parse(idElement.GetRawText()) : null;

            var method = root.TryGetProperty("method", out var methodElement)
                && methodElement.ValueKind == JsonValueKind.String
                    ? methodElement.GetString()
                    : null;

            if (string.IsNullOrEmpty(method))
            {
                return null;
            }

            var parameters = root.TryGetProperty("params", out var paramsElement)
                && paramsElement.ValueKind == JsonValueKind.Object
                    ? paramsElement
                    : (JsonElement?)null;

            var response = method switch
            {
                InitializeMethod => Serialize(Success(id, BuildInitializeResult())),

                // MCP 自己的心跳方法，只有 id 没有业务含义，回空对象即可
                "ping" => Serialize(Success(id, new JsonObject())),

                ToolsListMethod => Serialize(Success(
                    id,
                    await BuildToolsListResultAsync(cancellationToken).ConfigureAwait(false))),

                ToolsCallMethod => await CallToolAsync(id, parameters, cancellationToken).ConfigureAwait(false),

                // 通知一律不回应答（JSON-RPC 规定 + MCP 的 notifications/* 都是单向的），
                // 包括 notifications/initialized、notifications/cancelled 以及将来新增的通知
                _ when !hasId || method.StartsWith("notifications/", StringComparison.Ordinal) => null,

                _ => Serialize(ErrorResponse(id, MethodNotFoundCode, $"未实现的方法：{method}")),
            };

            return response;
        }
    }

    /// <summary><c>initialize</c> 的返回：协议版本、能力、服务端身份，以及给模型看的使用说明。</summary>
    private JsonObject BuildInitializeResult() => new()
    {
        ["protocolVersion"] = McpProtocol.ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject
            {
                // 工具清单只在会话开始时取一次，权限变化由用户在界面上决定，不主动推送
                ["listChanged"] = false,
            },
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = McpProtocol.ServerName,
            ["title"] = McpProtocol.ServerTitle,
            ["version"] = _serverVersion,
        },
        ["instructions"] = BuildInstructions(),
    };

    /// <summary>
    /// 给模型的常驻说明。写在这里而不是只写工具描述里，是为了把"跨工具的工作方式"
    /// （先看状态、按游标续读、只读档别尝试发送）讲一次就够，省掉每个工具的重复文字。
    /// </summary>
    private string BuildInstructions()
        => "这是 SeriTerm 串口调试助手（Windows 上的串口调试工具）的 AI 接入。"
            + "你操作的是用户界面上那个真实的串口会话：读到的帧是设备实际发来的数据，而不是用户粘贴给你的副本。"
            + Environment.NewLine
            + "当前权限档位：" + CurrentPermission().ToDisplayText()
            + "（可在 SeriTerm 界面左侧的「AI 接入」里切换，需由用户本人操作）。"
            + "只读档下不能发送数据、不能开关串口、不能改波特率，这些工具也不会出现在工具清单里——"
            + "遇到这类需求请直接告诉用户去切换权限，不要反复尝试调用。"
            + Environment.NewLine
            + "使用建议："
            + Environment.NewLine
            + "1. 回答任何关于设备当前情况的问题前，先调用 serial_get_status 看当前状态（是否已打开、端口与串口参数、收发字节数、断帧方式），不要凭猜测回答。"
            + Environment.NewLine
            + "2. 读帧请用 serial_read_frames 的游标：首次省略 since 取最近的帧，之后把返回的 nextCursor 原样传回 since 续读，这样既不会重复也不会漏。"
            + Environment.NewLine
            + "3. 需要等设备打印某段内容时用 serial_wait_for_pattern（可以等重启后的第一行、等 OK、等 panic），比让用户手动复制日志可靠。"
            + Environment.NewLine
            + "4. 帧内容可能被截断（truncated=true）或已被缓冲区淘汰（evictedFrames），回答时如实说明，不要脑补不存在的数据。";

    /// <summary>
    /// <c>tools/list</c>。后端不可用也必须能回答，否则客户端会认为服务端坏了；
    /// 此时按完全权限列出全部工具，调用时再由界面进程按真实权限拒绝。
    /// </summary>
    private async Task<JsonObject> BuildToolsListResultAsync(CancellationToken cancellationToken)
    {
        // 尽力刷新一次权限：界面进程在就顺手拿到真实档位，不在就沿用缓存/完全权限
        var app = await TryPingAsync(cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            ["tools"] = McpToolCatalog.BuildTools(app?.Permission ?? CurrentPermission()),
        };
    }

    /// <summary>
    /// <c>tools/call</c>。先确保后端可用，再把工具参数转发给界面进程，
    /// 把返回的结果对象原样写成 result（桥接层不理解工具语义，不该改动内容）。
    /// </summary>
    private async Task<string?> CallToolAsync(JsonNode? id, JsonElement? parameters, CancellationToken cancellationToken)
    {
        var toolName = ReadToolName(parameters);

        if (string.IsNullOrEmpty(toolName))
        {
            return Serialize(ErrorResponse(id, InvalidParamsCode, "tools/call 缺少参数 name。"));
        }

        // 只把 params.arguments 交给后端：IMcpBackend 的约定是"工具自己的参数对象"，
        // 多塞一个 name 会被派发层当成未知参数报 invalid_params，所有调用都会失败。
        // 没有 arguments 字段就传 default(JsonElement)（ValueKind = Undefined），
        // 派发层会按每个参数的缺省值处理。
        var arguments = parameters is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement
                : (JsonElement?)null;

        var app = await EnsureBackendAsync(cancellationToken).ConfigureAwait(false);

        if (app is null)
        {
            return Serialize(Success(id, ToolError(
                NotConnectedReason,
                "SeriTerm 界面没有运行，工具调用无法送达。请先启动 SeriTerm 并保持窗口打开，然后重试。")));
        }

        // 界面进程给的权限可能比缓存新（用户在界面上刚切过），以它为准
        RememberPermission(app.Permission);

        try
        {
            // 桥接层不理解工具语义：把界面进程给的 result 逐字节搬进响应，一个字段都不改
            var result = await _backend
                .CallToolAsync(toolName, arguments ?? default, cancellationToken)
                .ConfigureAwait(false);

            return Serialize(Success(id, JsonNode.Parse(result.GetRawText())!));
        }
        catch (McpBackendException ex)
        {
            // 后端已经给出结构化原因，直接翻成工具级错误，模型能据此决定下一步
            return Serialize(Success(id, ToolError(ex.Reason, $"{ex.Reason} {ex.Message}".Trim(), ex.Code)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Serialize(Success(
                id,
                ToolError(McpProtocol.Reasons.Internal, $"{ex.GetType().Name}: {ex.Message}")));
        }
    }

    /// <summary>
    /// 确保界面进程可用：先 ping 一次，没应答就（如果配了）把界面拉起来再轮询等待。
    /// 返回值是 <c>null</c> 表示最终仍然连不上——调用方负责翻成工具级错误，而不是抛异常。
    /// </summary>
    private async Task<McpAppInfo?> EnsureBackendAsync(CancellationToken cancellationToken)
    {
        var app = await TryPingAsync(cancellationToken).ConfigureAwait(false);

        if (app is not null)
        {
            return app;
        }

        if (EnsureBackendAvailable is null)
        {
            return null;
        }

        try
        {
            await EnsureBackendAvailable(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 拉起失败（找不到 exe、用户拒绝等）不该让桥接进程崩掉，往下走轮询即可
        }

        var deadline = DateTime.UtcNow + BackendWaitTimeout;

        // 界面进程要初始化串口层、开管道，不可能立刻可用，所以按固定间隔轮询而不是一次就放弃
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(BackendPollInterval, cancellationToken).ConfigureAwait(false);

            app = await TryPingAsync(cancellationToken).ConfigureAwait(false);

            if (app is not null)
            {
                return app;
            }
        }

        return null;
    }

    /// <summary>
    /// 问一次界面进程的运行信息。连不上、超时、管道出错都算"没在运行"，
    /// 因为这个方法的用处就是判断"能不能调用"，异常在这里没有额外信息量。
    /// </summary>
    private async Task<McpAppInfo?> TryPingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var app = await _backend.PingAsync(cancellationToken).ConfigureAwait(false);

            if (app is not null)
            {
                RememberPermission(app.Permission);
            }

            return app;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>记下这次看到的权限档位，供 <c>tools/list</c> 使用（位移编码避开可空字段）。</summary>
    private void RememberPermission(McpPermission permission) => _lastPermissionState = (int)permission << 1;

    private McpPermission CurrentPermission()
    {
        var state = _lastPermissionState;
        return state == UnknownPermissionState ? McpPermission.Full : (McpPermission)(state >> 1);
    }

    private static string? ReadToolName(JsonElement? parameters)
        => parameters is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;

    /// <summary>
    /// 响应外框。<c>id</c> 用 <see cref="JsonNode"/> 而不是 <c>long</c>：JSON-RPC 允许数字与字符串两种 id，
    /// 必须原样回传（不少客户端用字符串 id 配对自己发出去的请求），
    /// 而 <c>null</c> 表示"请求本身没有 id"（解析失败或通知），按规定写信面 null。
    /// </summary>
    private static JsonObject Success(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    /// <summary>没有 id 说明请求本身就没法对应（解析失败），按 JSON-RPC 规定写 <c>null</c>。</summary>
    private static JsonObject ErrorResponse(JsonNode? id, int code, string message, string? reason = null)
    {
        var error = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };

        if (reason is not null)
        {
            // data.reason 与界面进程返回的错误用同一套标识，方便模型按原因归类
            error["data"] = new JsonObject
            {
                ["reason"] = reason,
            };
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = error,
        };
    }

    /// <summary>
    /// 工具级错误：MCP 的约定是"工具执行失败"写在 result.isError 里，而不是协议层的 error。
    /// 文案前缀 <c>[reason]</c> 与界面进程的 <c>McpSessionDispatcher</c> 保持一致。
    /// </summary>
    private static JsonObject ToolError(string reason, string message, int? code = null)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"[{reason}] {message}",
                },
            },
            ["isError"] = true,
        };

        if (code is { } errorCode)
        {
            // 顺带带上后端的错误码，便于排障时对照日志
            result["_meta"] = new JsonObject
            {
                ["backendCode"] = errorCode,
            };
        }

        return result;
    }

    /// <summary>
    /// 序列化成一行紧凑 JSON。这里必须用不缩进的写法：
    /// stdio 传输里"换行"就是消息分隔符，缩进会把一条消息拆成多行，客户端直接读崩。
    ///
    /// 用 <see cref="JsonSerializer"/> 而不是 <see cref="JsonNode.ToJsonString(JsonSerializerOptions)"/>：
    /// 后者序列化 <see cref="JsonValue"/> 时会要求 <see cref="JsonSerializerOptions"/> 配好
    /// <c>TypeInfoResolver</c>，而契约里的 <see cref="McpProtocol.WireJson"/> 是给 POCO 用的、
    /// 没有（也不该由这一层去加）那个设置。
    /// </summary>
    private static string Serialize(JsonObject response)
        => JsonSerializer.Serialize(response, McpProtocol.WireJson);

    private void WriteResponse(string response)
    {
        // 显式写 '\n' 而不是用 WriteLine：MCP 的 stdio 约定行尾就是 LF，
        // 不能跟随平台的 CRLF，否则个别客户端会把 '\r' 当成内容的一部分
        _output.Write(response);
        _output.Write('\n');
        _output.Flush();
    }
}
