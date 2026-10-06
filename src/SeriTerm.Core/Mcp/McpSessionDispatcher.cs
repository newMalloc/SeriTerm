using System.Text.Json;
using System.Text.Json.Nodes;
using SeriTerm.Core.Send;
using SeriTerm.Core.Serial;
using SeriTerm.Core.Text;

namespace SeriTerm.Core.Mcp;

/// <summary>参数不合法（会被翻成 <c>isError</c> 的结果，而不是协议错误）。</summary>
public sealed class McpArgumentException(string message) : Exception(message);

/// <summary>
/// 工具调用的派发层：参数校验、权限检查、护栏、结果整形都在这里，
/// 真正碰串口的动作交给 <see cref="IMcpSession"/>。
///
/// 这样分层的目的很实际：护栏（只读档拒绝发送、限速、单次长度上限）是最需要被测试的部分，
/// 而它们不该依赖 WPF 界面——所以留在这个纯 Core 类里。
/// </summary>
public sealed class McpSessionDispatcher
{
    /// <summary>单次发送上限。够发一帧协议命令，又不至于让模型一口气灌几十 KB。</summary>
    public const int MaxWriteBytes = 4096;

    private readonly IMcpSession _session;
    private readonly CallRateLimiter _rateLimiter;

    public McpSessionDispatcher(IMcpSession session, CallRateLimiter? rateLimiter = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _rateLimiter = rateLimiter ?? new CallRateLimiter();
    }

    public McpPermission Permission => _session.Permission;

    /// <summary>按当前权限给出工具清单（只读档不暴露写入类工具）。</summary>
    public JsonArray BuildTools() => McpToolCatalog.BuildTools(_session.Permission);

    /// <summary>界面进程的运行信息。</summary>
    public McpAppInfo BuildAppInfo(string version, int connectedClients)
        => new(McpProtocol.ServerName, version, _session.Permission, connectedClients);

    /// <summary>
    /// 执行一次 <c>tools/call</c>。任何失败都以 <c>isError</c> 结果返回
    /// （MCP 的约定：工具执行失败是结果的一部分，不是协议错误）。
    /// </summary>
    public async Task<JsonObject> CallToolAsync(string? toolName, JsonElement? arguments, CancellationToken cancellationToken)
    {
        if (!McpToolCatalog.IsKnownTool(toolName))
        {
            return Fail(McpProtocol.Reasons.UnknownTool, $"没有名为 {toolName} 的工具。");
        }

        var permissionDenied = McpToolCatalog.RequiresFullPermission(toolName)
            && _session.Permission != McpPermission.Full;

        if (permissionDenied)
        {
            _session.Audit($"[AI] 想调用 {toolName}，但当前权限是只读，已拒绝。需要发送/开关串口时请在上方把 AI 权限切到“完全权限”。");
            return Fail(
                McpProtocol.Reasons.PermissionDenied,
                "当前 AI 权限是只读，这个工具需要完全权限。请让用户在 SeriTerm 界面左侧的「AI 接入」里把权限切到“完全权限”。");
        }

        // 会碰设备状态的动作统一限速（读帧、等关键词不限）
        if (McpToolCatalog.RequiresFullPermission(toolName) && !_rateLimiter.TryAcquire(out _))
        {
            return Fail(
                McpProtocol.Reasons.RateLimited,
                $"操作过快：AI 侧限速为每秒 {CallRateLimiter.DefaultTokensPerSecond:0.#} 次、突发 {CallRateLimiter.DefaultBurst:0} 次。稍等片刻再试。");
        }

        try
        {
            var args = new McpArguments(arguments);

            return toolName switch
            {
                McpToolCatalog.ListPorts => Ok(await _session.ListPortsAsync(cancellationToken).ConfigureAwait(false)),
                McpToolCatalog.GetStatus => Ok(await _session.GetStatusAsync(cancellationToken).ConfigureAwait(false)),
                McpToolCatalog.ReadFrames => await ReadFramesAsync(args, cancellationToken).ConfigureAwait(false),
                McpToolCatalog.WaitForPattern => await WaitForPatternAsync(args, cancellationToken).ConfigureAwait(false),
                McpToolCatalog.Open => await OpenAsync(args, cancellationToken).ConfigureAwait(false),
                McpToolCatalog.Close => Ok(await _session.CloseAsync(cancellationToken).ConfigureAwait(false)),
                McpToolCatalog.SetBaudRate => await SetBaudRateAsync(args, cancellationToken).ConfigureAwait(false),
                McpToolCatalog.Write => await WriteAsync(args, cancellationToken).ConfigureAwait(false),
                _ => Fail(McpProtocol.Reasons.UnknownTool, $"没有名为 {toolName} 的工具。"),
            };
        }
        catch (McpArgumentException ex)
        {
            return Fail(McpProtocol.Reasons.InvalidParams, ex.Message);
        }
        catch (SerialLinkException ex)
        {
            return Fail(McpProtocol.Reasons.SerialFault, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(McpProtocol.Reasons.SerialBusy, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail(McpProtocol.Reasons.Internal, "操作已取消（客户端可能已断开）。");
        }
        catch (Exception ex)
        {
            _session.Audit($"[AI] 工具 {toolName} 执行失败：{ex.Message}");
            return Fail(McpProtocol.Reasons.Internal, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<JsonObject> ReadFramesAsync(McpArguments args, CancellationToken cancellationToken)
    {
        var query = new McpFrameQuery(
            Since: args.GetLong("since", 0, minimum: 0),
            Limit: (int)args.GetLong("limit", McpFrameQuery.DefaultLimit, minimum: 1, maximum: McpFrameQuery.MaxLimit),
            Direction: args.GetEnum("direction", McpFrameDirection.Rx),
            IncludeHex: args.GetBool("hex", true),
            IncludeText: args.GetBool("text", true),
            MaxChars: (int)args.GetLong("maxChars", McpFrameQuery.DefaultMaxChars, minimum: 16, maximum: McpFrameQuery.MaxCharsLimit));

        var page = await _session.ReadFramesAsync(query, cancellationToken).ConfigureAwait(false);
        return Ok(page);
    }

    private async Task<JsonObject> WaitForPatternAsync(McpArguments args, CancellationToken cancellationToken)
    {
        var query = new McpPatternQuery(
            Pattern: args.GetRequiredString("pattern"),
            Mode: args.GetEnum("mode", McpPatternMode.Text),
            CaseSensitive: args.GetBool("caseSensitive", false),

            // -1 = "从现在开始等"（默认），0 = "连缓冲区里的历史帧一起扫"
            Since: args.GetLong("since", -1, minimum: -1),
            Direction: args.GetEnum("direction", McpFrameDirection.Rx),
            TimeoutMilliseconds: (int)args.GetLong("timeoutMs", McpPatternQuery.DefaultTimeoutMilliseconds, minimum: 1, maximum: McpPatternQuery.MaxTimeoutMilliseconds),
            ContextFrames: (int)args.GetLong("context", 0, minimum: 0, maximum: McpPatternQuery.MaxContextFrames),
            MaxMatches: (int)args.GetLong("maxMatches", McpPatternQuery.DefaultMaxMatches, minimum: 1, maximum: McpPatternQuery.MaxMatchesLimit),
            MaxChars: McpFrameQuery.DefaultMaxChars);

        var result = await _session.WaitForPatternAsync(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    private async Task<JsonObject> OpenAsync(McpArguments args, CancellationToken cancellationToken)
    {
        var request = new McpOpenRequest(
            PortName: args.GetString("port"),
            BaudRate: args.GetOptionalInt("baudRate", minimum: 1, maximum: 12_000_000),
            DataBits: args.GetOptionalInt("dataBits", minimum: 5, maximum: 8),
            Parity: args.GetString("parity"),
            StopBits: args.GetString("stopBits"),
            Handshake: args.GetString("handshake"),
            DtrEnable: args.GetOptionalBool("dtr"),
            RtsEnable: args.GetOptionalBool("rts"));

        var status = await _session.OpenAsync(request, cancellationToken).ConfigureAwait(false);
        return Ok(status);
    }

    private async Task<JsonObject> SetBaudRateAsync(McpArguments args, CancellationToken cancellationToken)
    {
        var baudRate = (int)args.GetLong("baudRate", 0, minimum: 1, maximum: 12_000_000);
        var status = await _session.SetBaudRateAsync(baudRate, cancellationToken).ConfigureAwait(false);
        return Ok(status);
    }

    private async Task<JsonObject> WriteAsync(McpArguments args, CancellationToken cancellationToken)
    {
        var data = args.GetRequiredString("data");
        var mode = args.GetEnum("mode", McpWriteMode.Text, ("text", McpWriteMode.Text), ("hex", McpWriteMode.Hex));
        var lineEnding = args.GetEnum(
            "lineEnding",
            LineEnding.None,
            ("none", LineEnding.None),
            ("cr", LineEnding.Cr),
            ("lf", LineEnding.Lf),
            ("crlf", LineEnding.CrLf));

        var hexInput = mode == McpWriteMode.Hex;

        if (!SendPayloadBuilder.TryBuild(data, hexInput, lineEnding, _session.EncodingName, out var payload, out var error))
        {
            throw new McpArgumentException(error);
        }

        if (payload.Length > MaxWriteBytes)
        {
            return Fail(
                McpProtocol.Reasons.PayloadTooLarge,
                $"单次发送上限 {MaxWriteBytes} 字节，本次是 {payload.Length} 字节。请拆成多条发送。");
        }

        var preview = SendPayloadBuilder.Describe(payload, hexInput, _session.EncodingName);
        var written = await _session.WriteAsync(new McpWriteRequest(payload, preview, hexInput), cancellationToken).ConfigureAwait(false);

        return Ok(new McpWriteResult(written, HexCodec.Format(payload), _rateLimiter.Available));
    }

    private static JsonObject Ok(object payload) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = JsonSerializer.Serialize(payload, McpProtocol.PrettyJson),
            },
        },
        ["isError"] = false,
    };

    private static JsonObject Fail(string reason, string message) => new()
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

    private enum McpWriteMode
    {
        Text,
        Hex,
    }
}

/// <summary>
/// 工具参数读取器：统一处理"缺省值 / 类型不对 / 越界 / 多余字段"，
/// 报错文案直接面向模型（它要靠这句话自我纠正，所以要说清是哪个字段、允许什么）。
/// </summary>
internal sealed class McpArguments
{
    private readonly JsonElement? _element;

    public McpArguments(JsonElement? element)
    {
        if (element is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            throw new McpArgumentException("arguments 必须是对象。");
        }

        _element = element;

        if (_element is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (!KnownArguments.Contains(property.Name))
                {
                    throw new McpArgumentException(
                        $"未知参数 “{property.Name}”。可用参数：{string.Join('、', KnownArguments)}。");
                }
            }
        }
    }

    /// <summary>各工具参数名的全集。多传字段直接报错，比静默忽略更容易让模型改对。</summary>
    private static readonly HashSet<string> KnownArguments = new(StringComparer.Ordinal)
    {
        "since", "limit", "direction", "hex", "text", "maxChars",
        "pattern", "mode", "caseSensitive", "timeoutMs", "context", "maxMatches",
        "port", "baudRate", "dataBits", "parity", "stopBits", "handshake", "dtr", "rts",
        "data", "lineEnding",
    };

    public string GetRequiredString(string name)
        => GetString(name) ?? throw new McpArgumentException($"缺少必填参数 {name}。");

    public string? GetString(string name)
    {
        if (!TryGet(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new McpArgumentException($"参数 {name} 必须是字符串。");
        }

        return value.GetString();
    }

    public bool GetBool(string name, bool fallback)
        => GetOptionalBool(name) ?? fallback;

    public bool? GetOptionalBool(string name)
    {
        if (!TryGet(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new McpArgumentException($"参数 {name} 必须是布尔值。"),
        };
    }

    public long GetLong(string name, long fallback, long minimum = long.MinValue, long maximum = long.MaxValue)
    {
        if (!TryGet(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw new McpArgumentException($"参数 {name} 必须是整数。");
        }

        if (number < minimum || number > maximum)
        {
            throw new McpArgumentException($"参数 {name} 必须在 {minimum} 到 {maximum} 之间，当前是 {number}。");
        }

        return number;
    }

    public int? GetOptionalInt(string name, int minimum = int.MinValue, int maximum = int.MaxValue)
        => TryGet(name, out _) ? (int)GetLong(name, 0, minimum, maximum) : null;

    public TEnum GetEnum<TEnum>(string name, TEnum fallback)
        where TEnum : struct, Enum
    {
        var text = GetString(name);

        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        if (!Enum.TryParse<TEnum>(text, ignoreCase: true, out var value))
        {
            throw new McpArgumentException(
                $"参数 {name} 的值 “{text}” 无效。可选：{string.Join('、', Enum.GetNames<TEnum>().Select(ToCamel))}。");
        }

        return value;
    }

    /// <summary>个别参数的取值与枚举名不一致时（如 text/hex 的写法），用显式映射。</summary>
    public TValue GetEnum<TValue>(string name, TValue fallback, params (string Text, TValue Value)[] map)
    {
        var text = GetString(name);

        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        foreach (var (candidate, value) in map)
        {
            if (string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        throw new McpArgumentException(
            $"参数 {name} 的值 “{text}” 无效。可选：{string.Join('、', map.Select(m => m.Text))}。");
    }

    private static string ToCamel(string name)
        => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private bool TryGet(string name, out JsonElement value)
    {
        value = default;

        if (_element is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty(name, out var found))
        {
            return false;
        }

        if (found.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        value = found;
        return true;
    }
}
