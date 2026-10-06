using System.Text.Json.Nodes;

namespace SeriTerm.Core.Mcp;

/// <summary>
/// 工具目录。它同时被三个地方使用：界面进程（<c>tools/call</c> 执行前查参数与权限）、
/// 桥接进程（<c>tools/list</c> 直接回答，不必打扰界面），以及测试。
/// 定义只写一份，避免"说明书"和"实现"对不上。
/// </summary>
public static class McpToolCatalog
{
    public const string ListPorts = "serial_list_ports";
    public const string GetStatus = "serial_get_status";
    public const string ReadFrames = "serial_read_frames";
    public const string WaitForPattern = "serial_wait_for_pattern";
    public const string Open = "serial_open";
    public const string Close = "serial_close";
    public const string SetBaudRate = "serial_set_baud_rate";
    public const string Write = "serial_write";

    /// <summary>全部工具，顺序即 <c>tools/list</c> 里的顺序。</summary>
    public static readonly string[] All =
    [
        ListPorts,
        GetStatus,
        ReadFrames,
        WaitForPattern,
        Open,
        Close,
        SetBaudRate,
        Write,
    ];

    /// <summary>需要完全权限的工具；其余在只读档下即可调用。</summary>
    public static readonly string[] RequiresFull =
    [
        Open,
        Close,
        SetBaudRate,
        Write,
    ];

    public static bool IsKnownTool(string? toolName)
        => toolName is not null && Array.IndexOf(All, toolName) >= 0;

    public static bool RequiresFullPermission(string? toolName)
        => toolName is not null && Array.IndexOf(RequiresFull, toolName) >= 0;

    /// <summary>
    /// 按权限给出工具清单。只读档下直接不暴露写入类工具：
    /// 让模型反复尝试一个注定被拒的动作，对谁都没好处。
    /// </summary>
    public static JsonArray BuildTools(McpPermission permission)
    {
        var tools = new JsonArray();

        foreach (var name in All)
        {
            if (permission != McpPermission.Full && RequiresFullPermission(name))
            {
                continue;
            }

            tools.Add(BuildTool(name));
        }

        return tools;
    }

    public static JsonObject BuildTool(string name) => name switch
    {
        ListPorts => Tool(
            ListPorts,
            "列出串口",
            "列出本机当前可用的串口（含设备友好名），并指出界面当前选中的是哪一个。",
            ObjectSchema([]),
            readOnly: true),

        GetStatus => Tool(
            GetStatus,
            "读取会话状态",
            "读取串口会话的完整状态：是否已打开、端口与参数、收发字节数、编码与断帧方式、"
            + "自动重连状态、AI 权限档位，以及帧缓冲的游标范围。"
            + "回答用户关于设备当前情况的问题前，先调用它。",
            ObjectSchema([]),
            readOnly: true),

        ReadFrames => Tool(
            ReadFrames,
            "读取帧",
            "按游标读取已经收到的帧（一帧 = 一次自动断帧的结果，带到达时间与方向）。"
            + "首次调用省略 since 取最近的帧；之后把返回的 nextCursor 作为 since 续读，"
            + "不会重复也不会漏。返回的 droppedEvicted 表示缓冲区已经淘汰掉的更早帧数。",
            ObjectSchema(
            [
                ("since", Integer("起始游标：上次返回的 nextCursor。省略表示从缓冲区里最早的一帧开始读。", minimum: 0)),
                ("limit", Integer($"本次最多返回多少帧，默认 {McpFrameQuery.DefaultLimit}，上限 {McpFrameQuery.MaxLimit}。", 1, McpFrameQuery.MaxLimit)),
                ("direction", Enum("方向过滤：rx 设备→本机（默认）、tx 本机→设备、system 工具提示、all 全部。", "rx", "tx", "system", "all")),
                ("hex", Boolean("是否返回十六进制内容，默认 true。")),
                ("text", Boolean("是否返回按当前编码解码的文本，默认 true。")),
                ("maxChars", Integer($"每个字段最多返回多少字符，默认 {McpFrameQuery.DefaultMaxChars}，上限 {McpFrameQuery.MaxCharsLimit}。超出会被截断并置 truncated=true。", 16, McpFrameQuery.MaxCharsLimit)),
            ]),
            readOnly: true),

        WaitForPattern => Tool(
            WaitForPattern,
            "等关键词",
            "阻塞等待，直到收到的帧里出现指定内容（或超时）。这是“复制粘贴日志”做不到的能力："
            + "可以等设备重启后打印的第一行、等 panic、等 OK。默认从当前最新位置开始等，"
            + "适合“让设备复现一次再看结果”的用法；填 since 则连历史帧一起扫。",
            ObjectSchema(
            [
                ("pattern", String("要找的内容。mode=text 时是子串，mode=regex 时是 .NET 正则，mode=hex 时是十六进制字节串（如 0D 0A）。")),
                ("mode", Enum("匹配方式，默认 text。", "text", "regex", "hex")),
                ("caseSensitive", Boolean("text/regex 模式下是否区分大小写，默认 false。")),
                ("since", Integer("从哪个游标开始扫描：省略或 -1 表示从现在开始等（推荐，不会匹配到旧数据），0 表示连缓冲区里的历史帧一起扫。", minimum: -1)),
                ("direction", Enum("方向过滤，默认 rx。", "rx", "tx", "system", "all")),
                ("timeoutMs", Integer($"最多等多少毫秒，默认 {McpPatternQuery.DefaultTimeoutMilliseconds}，上限 {McpPatternQuery.MaxTimeoutMilliseconds}。", 1, McpPatternQuery.MaxTimeoutMilliseconds)),
                ("context", Integer($"命中帧之前再带几帧上下文，默认 0，上限 {McpPatternQuery.MaxContextFrames}。", 0, McpPatternQuery.MaxContextFrames)),
                ("maxMatches", Integer($"最多返回几帧命中，默认 {McpPatternQuery.DefaultMaxMatches}，上限 {McpPatternQuery.MaxMatchesLimit}。", 1, McpPatternQuery.MaxMatchesLimit)),
            ],
            required: ["pattern"]),
            readOnly: true),

        Open => Tool(
            Open,
            "打开串口",
            "打开串口。参数省略时沿用界面上的当前设置。注意：打开会按设置拉 DTR/RTS，"
            + "很多开发板会因此复位——这既是风险也是“复位并抓启动日志”的常用手法。需要完全权限。",
            ObjectSchema(
            [
                ("port", String("端口名，如 COM5。省略则用界面当前选中的端口。")),
                ("baudRate", Integer("波特率，省略则用界面当前值。", 1)),
                ("dataBits", Enum("数据位，默认沿用界面当前值。", "5", "6", "7", "8")),
                ("parity", Enum("校验位：none / odd / even / mark / space。", "none", "odd", "even", "mark", "space")),
                ("stopBits", Enum("停止位：one / two / onePointFive。", "one", "two", "onePointFive")),
                ("handshake", Enum("流控：none / xonxoff / requestToSend / requestToSendXOnXOff。", "none", "xonxoff", "requestToSend", "requestToSendXOnXOff")),
                ("dtr", Boolean("打开时 DTR 是否有效。")),
                ("rts", Boolean("打开时 RTS 是否有效。")),
            ]),
            readOnly: false),

        Close => Tool(
            Close,
            "关闭串口",
            "关闭当前串口。需要完全权限。",
            ObjectSchema([]),
            readOnly: false),

        SetBaudRate => Tool(
            SetBaudRate,
            "改波特率",
            "在串口保持打开的情况下直接改波特率（不必关掉重开，收发缓冲不会清空）。需要完全权限。",
            ObjectSchema([("baudRate", Integer("目标波特率，如 115200。", 1))], required: ["baudRate"]),
            readOnly: false),

        Write => Tool(
            Write,
            "发送数据",
            "向串口发送数据。需要完全权限；单次上限 "
            + $"{McpSessionDispatcher.MaxWriteBytes} 字节，并且有发送速率限制——"
            + "每次发送都会在日志里留下带 [AI] 标记的审计行，用户随时可以在界面上收回权限。",
            ObjectSchema(
            [
                ("data", String("要发送的内容。mode=text 时按当前编码编码后发送，mode=hex 时是十六进制字节串（如 01 03 00 00）。")),
                ("mode", Enum("数据形式，默认 text。", "text", "hex")),
                ("lineEnding", Enum("结尾附加的换行：none（默认）/ cr / lf / crlf。文本模式下常用 crlf。", "none", "cr", "lf", "crlf")),
            ],
            required: ["data"]),
            readOnly: false),

        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "未登记的工具名。"),
    };

    private static JsonObject Tool(string name, string title, string description, JsonObject inputSchema, bool readOnly)
        => new()
        {
            ["name"] = name,
            ["title"] = title,
            ["description"] = description,
            ["inputSchema"] = inputSchema,
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = readOnly,

                // 写串口可能改设备状态（甚至把它写进 bootloader），按"可能有破坏性"如实标注
                ["destructiveHint"] = !readOnly,
                ["idempotentHint"] = readOnly,
                ["openWorldHint"] = true,
            },
        };

    private static JsonObject ObjectSchema((string Name, JsonNode Node)[] properties, string[]? required = null)
    {
        var props = new JsonObject();

        foreach (var (propertyName, node) in properties)
        {
            props[propertyName] = node;
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false,
        };

        if (required is { Length: > 0 })
        {
            var array = new JsonArray();

            foreach (var name in required)
            {
                array.Add(name);
            }

            schema["required"] = array;
        }

        return schema;
    }

    private static JsonObject String(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
    };

    private static JsonObject Boolean(string description) => new()
    {
        ["type"] = "boolean",
        ["description"] = description,
    };

    private static JsonObject Integer(string description, int? minimum = null, int? maximum = null)
    {
        var node = new JsonObject
        {
            ["type"] = "integer",
            ["description"] = description,
        };

        if (minimum is { } min)
        {
            node["minimum"] = min;
        }

        if (maximum is { } max)
        {
            node["maximum"] = max;
        }

        return node;
    }

    private static JsonObject Enum(string description, params string[] values)
    {
        var array = new JsonArray();

        foreach (var value in values)
        {
            array.Add(value);
        }

        return new JsonObject
        {
            ["type"] = "string",
            ["description"] = description,
            ["enum"] = array,
        };
    }
}
