using System.Text.Json;
using System.Text.Json.Nodes;
using SeriTerm.Core.Mcp;
using SeriTerm.Core.Serial;
using SeriTerm.Core.Text;

namespace SeriTerm.Tests.Mcp;

/// <summary>
/// 派发层是"护栏"所在地（只读档拒绝发送、限速、单次长度上限、参数校验），
/// 所以这里用假会话把每种拒绝路径都走一遍——这些行为如果只靠真人手工点，
/// 迟早会在某次重构里悄悄失效。
/// </summary>
public class McpSessionDispatcherTests
{
    [Fact]
    public async Task 只读档_发送被拒绝且留下审计提示()
    {
        var session = new FakeSession { Permission = McpPermission.ReadOnly };
        var dispatcher = new McpSessionDispatcher(session);

        var (isError, text) = await CallAsync(dispatcher, McpToolCatalog.Write, """{"data":"hello"}""");

        Assert.True(isError);
        Assert.Contains("permission_denied", text);
        Assert.Empty(session.Writes);
        Assert.Single(session.Audits);
        Assert.Contains(McpToolCatalog.Write, session.Audits[0]);
    }

    [Fact]
    public async Task 只读档_打开与改波特率同样被拒绝()
    {
        var session = new FakeSession { Permission = McpPermission.ReadOnly };
        var dispatcher = new McpSessionDispatcher(session);

        var open = await CallAsync(dispatcher, McpToolCatalog.Open, "{}");
        var baud = await CallAsync(dispatcher, McpToolCatalog.SetBaudRate, """{"baudRate":9600}""");

        Assert.True(open.IsError);
        Assert.True(baud.IsError);
        Assert.Null(session.LastOpen);
        Assert.Equal(0, session.LastBaudRate);
    }

    [Fact]
    public async Task 只读档_读帧与等关键词照常可用()
    {
        var session = new FakeSession { Permission = McpPermission.ReadOnly };
        var dispatcher = new McpSessionDispatcher(session);

        var read = await CallAsync(dispatcher, McpToolCatalog.ReadFrames, """{"since":3,"limit":10}""");
        var wait = await CallAsync(dispatcher, McpToolCatalog.WaitForPattern, """{"pattern":"OK"}""");

        Assert.False(read.IsError);
        Assert.False(wait.IsError);
        Assert.Equal(3, session.LastFrameQuery!.Since);
        Assert.Equal(10, session.LastFrameQuery.Limit);
        Assert.Equal("OK", session.LastPattern!.Pattern);
    }

    [Fact]
    public async Task 完全权限_文本发送按当前编码组装字节()
    {
        var session = new FakeSession { Permission = McpPermission.Full };
        var dispatcher = new McpSessionDispatcher(session);

        var (isError, text) = await CallAsync(dispatcher, McpToolCatalog.Write, """{"data":"AT","lineEnding":"crlf"}""");

        Assert.False(isError);
        Assert.Contains("\"sentBytes\": 4", text);
        Assert.Equal([0x41, 0x54, 0x0D, 0x0A], session.Writes[0].Payload);
    }

    [Fact]
    public async Task 完全权限_十六进制发送按字节解析()
    {
        var session = new FakeSession { Permission = McpPermission.Full };
        var dispatcher = new McpSessionDispatcher(session);

        var (isError, _) = await CallAsync(dispatcher, McpToolCatalog.Write, """{"data":"01 03 FF","mode":"hex"}""");

        Assert.False(isError);
        Assert.Equal([0x01, 0x03, 0xFF], session.Writes[0].Payload);
    }

    [Fact]
    public async Task 超过单次长度上限_直接拒绝且不碰串口()
    {
        var session = new FakeSession { Permission = McpPermission.Full };
        var dispatcher = new McpSessionDispatcher(session);
        var huge = new string('A', McpSessionDispatcher.MaxWriteBytes + 1);

        var (isError, text) = await CallAsync(dispatcher, McpToolCatalog.Write, $$"""{"data":"{{huge}}"}""");

        Assert.True(isError);
        Assert.Contains("payload_too_large", text);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task 连续发送超过突发额度_被限速且不再写串口()
    {
        var session = new FakeSession { Permission = McpPermission.Full };

        // 时钟不动：令牌用完就不会再补，这样断言与机器速度无关
        var dispatcher = new McpSessionDispatcher(session, new CallRateLimiter(tokensPerSecond: 1, burst: 3, clock: () => 0));

        for (var i = 0; i < 3; i++)
        {
            var (isError, _) = await CallAsync(dispatcher, McpToolCatalog.Write, """{"data":"x"}""");
            Assert.False(isError);
        }

        var denied = await CallAsync(dispatcher, McpToolCatalog.Write, """{"data":"x"}""");

        Assert.True(denied.IsError);
        Assert.Contains("rate_limited", denied.Text);
        Assert.Equal(3, session.Writes.Count);
    }

    [Fact]
    public async Task 未知参数_报错并列出可用参数名()
    {
        var session = new FakeSession { Permission = McpPermission.Full };
        var dispatcher = new McpSessionDispatcher(session);

        var (isError, text) = await CallAsync(dispatcher, McpToolCatalog.ReadFrames, """{"limitt":5}""");

        Assert.True(isError);
        Assert.Contains("invalid_params", text);
        Assert.Contains("limitt", text);
        Assert.Contains("limit", text);
    }

    [Fact]
    public async Task 参数越界_报错并说明范围()
    {
        var session = new FakeSession { Permission = McpPermission.ReadOnly };
        var dispatcher = new McpSessionDispatcher(session);

        var (isError, text) = await CallAsync(dispatcher, McpToolCatalog.ReadFrames, $$"""{"limit":{{McpFrameQuery.MaxLimit + 1}}}""");

        Assert.True(isError);
        Assert.Contains("invalid_params", text);
        Assert.Contains("limit", text);
    }

    [Fact]
    public async Task 保留字_等关键词默认从现在开始扫()
    {
        var session = new FakeSession { Permission = McpPermission.ReadOnly };
        var dispatcher = new McpSessionDispatcher(session);

        await CallAsync(dispatcher, McpToolCatalog.WaitForPattern, """{"pattern":"boot"}""");

        // -1 的含义是"从现在开始等"，0 才是"连缓冲区里的历史一起扫"
        Assert.Equal(-1, session.LastPattern!.Since);
        Assert.Equal(McpPatternQuery.DefaultTimeoutMilliseconds, session.LastPattern.TimeoutMilliseconds);
    }

    [Fact]
    public async Task 未知工具_报错而不是抛异常()
    {
        var dispatcher = new McpSessionDispatcher(new FakeSession());

        var (isError, text) = await CallAsync(dispatcher, "serial_launch_missile", "{}");

        Assert.True(isError);
        Assert.Contains("unknown_tool", text);
    }

    [Fact]
    public async Task 链路故障_翻成_serial_fault_并带上中文原因()
    {
        var session = new FakeSession
        {
            Permission = McpPermission.Full,
            OpenFailure = new SerialLinkException("COM5 被其它程序占用，无法打开。"),
        };

        var dispatcher = new McpSessionDispatcher(session);

        var (isError, text) = await CallAsync(dispatcher, McpToolCatalog.Open, """{"port":"COM5"}""");

        Assert.True(isError);
        Assert.Contains("serial_fault", text);
        Assert.Contains("被其它程序占用", text);
    }

    [Fact]
    public async Task 串口未打开时发送_报_serial_busy()
    {
        var session = new FakeSession
        {
            Permission = McpPermission.Full,
            WriteFailure = new InvalidOperationException("串口尚未打开，无法发送。"),
        };

        var dispatcher = new McpSessionDispatcher(session);

        var (isError, text) = await CallAsync(dispatcher, McpToolCatalog.Write, """{"data":"x"}""");

        Assert.True(isError);
        Assert.Contains("serial_busy", text);
    }

    [Fact]
    public void 工具清单_只读档不含写入类工具_完全权限含全部()
    {
        var readOnly = new McpSessionDispatcher(new FakeSession { Permission = McpPermission.ReadOnly });
        var full = new McpSessionDispatcher(new FakeSession { Permission = McpPermission.Full });

        var readOnlyNames = Names(readOnly.BuildTools());
        var fullNames = Names(full.BuildTools());

        Assert.DoesNotContain(McpToolCatalog.Write, readOnlyNames);
        Assert.DoesNotContain(McpToolCatalog.Open, readOnlyNames);
        Assert.Contains(McpToolCatalog.ReadFrames, readOnlyNames);
        Assert.Equal(McpToolCatalog.All.Length, fullNames.Count);
        Assert.Contains(McpToolCatalog.Write, fullNames);
    }

    [Fact]
    public void 工具清单_每个工具的输入_schema_都是合法对象且带说明()
    {
        var tools = McpToolCatalog.BuildTools(McpPermission.Full);

        Assert.Equal(McpToolCatalog.All.Length, tools.Count);

        foreach (var node in tools)
        {
            var tool = Assert.IsType<JsonObject>(node);
            var name = tool["name"]!.GetValue<string>();
            var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);

            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.NotNull(schema["properties"]);

            // 描述是模型唯一的说明书，不能空着
            Assert.False(string.IsNullOrWhiteSpace(tool["description"]!.GetValue<string>()), name);
            Assert.Equal(McpToolCatalog.RequiresFullPermission(name), !tool["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        }
    }

    private static List<string> Names(JsonArray tools)
        => [.. tools.Select(node => node!["name"]!.GetValue<string>())];

    private static async Task<(bool IsError, string Text)> CallAsync(
        McpSessionDispatcher dispatcher,
        string toolName,
        string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);

        var result = await dispatcher.CallToolAsync(toolName, document.RootElement.Clone(), CancellationToken.None);

        return (
            result["isError"]!.GetValue<bool>(),
            result["content"]![0]!["text"]!.GetValue<string>());
    }

    private sealed class FakeSession : IMcpSession
    {
        public McpPermission Permission { get; set; } = McpPermission.ReadOnly;

        public string EncodingName => "UTF-8";

        public McpLinkError? LastError => null;

        public List<McpWriteRequest> Writes { get; } = [];

        public List<string> Audits { get; } = [];

        public McpFrameQuery? LastFrameQuery { get; private set; }

        public McpPatternQuery? LastPattern { get; private set; }

        public McpOpenRequest? LastOpen { get; private set; }

        public int LastBaudRate { get; private set; }

        public SerialLinkException? OpenFailure { get; set; }

        public Exception? WriteFailure { get; set; }

        public Task<IReadOnlyList<McpPortInfo>> ListPortsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<McpPortInfo>>([new McpPortInfo("COM5", "COM5 (USB-SERIAL)", true)]);

        public Task<McpStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(BuildStatus());

        public Task<McpFramePage> ReadFramesAsync(McpFrameQuery query, CancellationToken cancellationToken)
        {
            LastFrameQuery = query;
            return Task.FromResult(new McpFramePage([], query.Since, 0, 0, 0, false));
        }

        public Task<McpPatternResult> WaitForPatternAsync(McpPatternQuery query, CancellationToken cancellationToken)
        {
            LastPattern = query;
            return Task.FromResult(new McpPatternResult(false, true, 0, query.Since, []));
        }

        public Task<McpStatus> OpenAsync(McpOpenRequest request, CancellationToken cancellationToken)
        {
            LastOpen = request;

            if (OpenFailure is not null)
            {
                throw OpenFailure;
            }

            return Task.FromResult(BuildStatus());
        }

        public Task<McpStatus> CloseAsync(CancellationToken cancellationToken) => Task.FromResult(BuildStatus());

        public Task<McpStatus> SetBaudRateAsync(int baudRate, CancellationToken cancellationToken)
        {
            LastBaudRate = baudRate;
            return Task.FromResult(BuildStatus());
        }

        public Task<int> WriteAsync(McpWriteRequest request, CancellationToken cancellationToken)
        {
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            Writes.Add(request);
            return Task.FromResult(request.Payload.Length);
        }

        public void Audit(string message) => Audits.Add(message);

        private McpStatus BuildStatus() => new(
            Open: false,
            PortName: "COM5",
            PortDisplayName: "COM5",
            BaudRate: 115200,
            DataBits: 8,
            Parity: "None",
            StopBits: "1",
            Handshake: "None",
            DtrEnable: false,
            RtsEnable: false,
            RxBytes: 0,
            TxBytes: 0,
            EncodingName: EncodingName,
            FramingMode: "Gap",
            AutoFrameGapMilliseconds: 20,
            DelimiterText: "\\r\\n",
            TimestampEnabled: true,
            ReconnectStatus: string.Empty,
            AutoReconnect: true,
            JournalFrames: 0,
            JournalEvictedFrames: 0,
            LogDroppedLines: 0,
            Permission: Permission,
            ConnectedClients: 1);
    }
}

/// <summary>关键词匹配器：文本/正则/HEX 三种模式各有各的坑，逐个钉住。</summary>
public class McpPatternMatcherTests
{
    private static McpPatternMatcher Create(string pattern, McpPatternMode mode, bool caseSensitive = false)
    {
        var query = new McpPatternQuery(pattern, mode, caseSensitive, -1, McpFrameDirection.Rx, 1000, 0, 10, 512);

        Assert.True(McpPatternMatcher.TryCreate(query, out var matcher, out var error), error);
        return matcher!;
    }

    [Fact]
    public void 文本模式_默认不区分大小写()
    {
        var matcher = Create("ok", McpPatternMode.Text);

        Assert.True(matcher.IsMatch("all good"u8, "BOOT OK".AsSpan()));
        Assert.False(matcher.IsMatch("all good"u8, "ready".AsSpan()));
    }

    [Fact]
    public void 文本模式_区分大小写时按原样比()
    {
        var matcher = Create("OK", McpPatternMode.Text, caseSensitive: true);

        Assert.False(matcher.IsMatch("x"u8, "boot ok".AsSpan()));
        Assert.True(matcher.IsMatch("x"u8, "boot OK".AsSpan()));
    }

    [Fact]
    public void 十六进制模式_直接在字节上找_文本打不出的控制字符也能匹配()
    {
        var matcher = Create("0D 0A", McpPatternMode.Hex);

        Assert.False(matcher.NeedsText);
        Assert.True(matcher.IsMatch("boot ok\r\n"u8, ReadOnlySpan<char>.Empty));
        Assert.False(matcher.IsMatch("boot ok\n"u8, ReadOnlySpan<char>.Empty));
    }

    [Fact]
    public void 正则模式_命中文本()
    {
        var matcher = Create(@"^OK\s+\d+$", McpPatternMode.Regex);

        Assert.True(matcher.IsMatch("x"u8, "OK 42".AsSpan()));
        Assert.False(matcher.IsMatch("x"u8, "OK".AsSpan()));
    }

    [Fact]
    public void 非法正则与非法十六进制_在创建时就报错()
    {
        var badRegex = new McpPatternQuery("[", McpPatternMode.Regex, false, -1, McpFrameDirection.Rx, 1000, 0, 10, 512);
        var badHex = new McpPatternQuery("ZZ", McpPatternMode.Hex, false, -1, McpFrameDirection.Rx, 1000, 0, 10, 512);

        Assert.False(McpPatternMatcher.TryCreate(badRegex, out _, out var regexError));
        Assert.False(McpPatternMatcher.TryCreate(badHex, out _, out var hexError));
        Assert.Contains("正则", regexError);
        Assert.Contains("十六进制", hexError);
    }

    [Fact]
    public void 解码按指定编码_中文不会被当成问号()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("温度过高");

        Assert.Equal("温度过高", McpPatternMatcher.Decode(bytes, System.Text.Encoding.UTF8));
        Assert.Equal("温度过高", McpPatternMatcher.Decode(bytes, StatefulTextDecoder.Resolve("UTF-8")));
    }
}
