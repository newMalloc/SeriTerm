using System.Text.Json;
using System.Text.Json.Nodes;
using SeriTerm.Core.Mcp;

namespace SeriTerm.Tests.Mcp;

/// <summary>
/// stdio 桥接层的协议测试。整层逻辑只依赖 <see cref="TextReader"/> / <see cref="TextWriter"/>，
/// 所以可以完全脱离管道和 WPF，用字符串喂请求、收响应，断言每一条输出行的形状。
/// </summary>
public class McpStdioServerTests
{
    private const string ServerVersion = "9.9.9-test";

    [Fact]
    public async Task initialize应返回协议版本与服务端信息()
    {
        var fake = new FakeBackend();

        var lines = await RunAsync(
            fake,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"claude-desktop","version":"1.2"}}}""");

        var response = ParseSingle(lines);
        var result = response.RootElement.GetProperty("result");

        Assert.Equal(McpProtocol.ProtocolVersion, result.GetProperty("protocolVersion").GetString());

        var serverInfo = result.GetProperty("serverInfo");
        Assert.Equal(McpProtocol.ServerName, serverInfo.GetProperty("name").GetString());
        Assert.Equal(ServerVersion, serverInfo.GetProperty("version").GetString());

        Assert.False(result.GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean());

        var instructions = result.GetProperty("instructions").GetString();
        Assert.NotNull(instructions);
        Assert.Contains("SeriTerm", instructions);
        Assert.Contains("serial_get_status", instructions);
    }

    [Fact]
    public async Task tools列表在只读档下不含发送工具()
    {
        var fake = new FakeBackend { App = AppInfo(McpPermission.ReadOnly) };
        var tools = await ListToolNamesAsync(fake);

        Assert.DoesNotContain(McpToolCatalog.Write, tools);
        Assert.DoesNotContain(McpToolCatalog.Open, tools);
        Assert.Contains(McpToolCatalog.GetStatus, tools);
        Assert.Equal(
            McpToolCatalog.All.Length - McpToolCatalog.RequiresFull.Length,
            tools.Count);
    }

    [Fact]
    public async Task tools列表在完全权限档下含全部工具()
    {
        var fake = new FakeBackend { App = AppInfo(McpPermission.Full) };
        var tools = await ListToolNamesAsync(fake);

        Assert.Equal(McpToolCatalog.All.Length, tools.Count);

        foreach (var name in McpToolCatalog.All)
        {
            Assert.Contains(name, tools);
        }
    }

    [Fact]
    public async Task 工具调用应原样透传后端结果()
    {
        var payload = "{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"open\\\":true}\"}],\"isError\":false}";
        var fake = new FakeBackend
        {
            App = AppInfo(McpPermission.ReadOnly),
            OnCallTool = (name, arguments) =>
            {
                Assert.Equal(McpToolCatalog.GetStatus, name);

                // 后端只该收到 params.arguments 里的内容：多带一个 name 会被派发层
                // 当成未知参数报 invalid_params，所有工具调用都会失败
                Assert.Equal(JsonValueKind.Object, arguments!.Value.ValueKind);
                Assert.False(arguments.Value.TryGetProperty("name", out _));
                Assert.Equal(5L, arguments.Value.GetProperty("limit").GetInt64());
                Assert.Single(arguments.Value.EnumerateObject());

                return JsonNode.Parse(payload)!;
            },
        };

        var lines = await RunAsync(
            fake,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"serial_get_status","arguments":{"limit":5}}}""");

        var response = ParseSingle(lines);
        var result = response.RootElement.GetProperty("result");

        // 桥接层不理解工具语义，界面进程给什么就写什么：内容必须与后端给的一模一样。
        // 这里比结构而不是比字符串：结果在桥接层被重新序列化过一次，
        // 内嵌的引号可能写成 \u0022 而不是 \"——两者是同一个 JSON，不该算"改了内容"。
        using var expected = JsonDocument.Parse(payload);
        AssertTrueDeepEquals(expected.RootElement, result, "桥接层不应改动后端返回的 result");
    }

    [Fact]
    public async Task 工具调用省略参数时传给后端的是未定义值()
    {
        var fake = new FakeBackend
        {
            App = AppInfo(McpPermission.ReadOnly),
            OnCallTool = (_, arguments) =>
            {
                // 没有 arguments 字段：ValueKind 是 Undefined，派发层会按各参数缺省值处理
                Assert.Equal(JsonValueKind.Undefined, arguments!.Value.ValueKind);

                return new JsonObject
                {
                    ["content"] = new JsonArray(),
                    ["isError"] = false,
                };
            },
        };

        var lines = await RunAsync(
            fake,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"serial_get_status"}}""");

        var result = ParseSingle(lines).RootElement.GetProperty("result");

        // 走到后端并拿到结果（断言在 OnCallTool 里，失败会顺着 McpBackendException 之外的异常暴露）
        Assert.False(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task 字符串id应原样回传()
    {
        var lines = await RunAsync(new FakeBackend(), """{"jsonrpc":"2.0","id":"req-7","method":"tools/list"}""");

        var response = ParseSingle(lines);

        // JSON-RPC 要求 id 原样回传：字符串 id 写成 null 会让客户端配不上请求
        Assert.Equal("req-7", response.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task 后端不可用时工具调用应返回工具级错误而不是协议错误()
    {
        var fake = new FakeBackend { App = null };

        var lines = await RunAsync(
            fake,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"serial_get_status","arguments":{}}}""");

        var response = ParseSingle(lines);

        // MCP 约定：工具执行失败属于 result 的一部分，不能回 JSON-RPC error
        Assert.False(response.RootElement.TryGetProperty("error", out _));

        var result = response.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());

        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.Contains("not_connected", text);

        // 没配 EnsureBackendAvailable 就不该去拉起界面，也不该反复 ping
        Assert.Equal(1, fake.PingCount);
    }

    [Fact]
    public async Task 后端不可用时工具列表仍应正常回答()
    {
        var fake = new FakeBackend { App = null };

        var lines = await RunAsync(fake, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        var response = ParseSingle(lines);
        Assert.False(response.RootElement.TryGetProperty("error", out _));

        // 拿不到权限就按完全权限列出全部工具：先让客户端看到能力，调用时再报错
        var tools = response.RootElement.GetProperty("result").GetProperty("tools");
        Assert.Equal(McpToolCatalog.All.Length, tools.GetArrayLength());
    }

    [Fact]
    public async Task 未知方法应返回方法不存在()
    {
        var lines = await RunAsync(new FakeBackend(), """{"jsonrpc":"2.0","id":5,"method":"seriterm/nope"}""");

        var response = ParseSingle(lines);
        var error = response.RootElement.GetProperty("error");

        Assert.Equal(-32601, error.GetProperty("code").GetInt32());
        Assert.Equal(5, response.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task 非法JSON应返回解析错误且id为null()
    {
        var lines = await RunAsync(new FakeBackend(), "{ this is not json");

        var response = ParseSingle(lines);
        var error = response.RootElement.GetProperty("error");

        Assert.Equal(-32700, error.GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task 通知不应产生任何输出行()
    {
        var lines = await RunAsync(
            new FakeBackend(),
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1,"reason":"user"}}""");

        Assert.Empty(lines);
    }

    [Fact]
    public async Task 每行输出都应是单行合法JSON()
    {
        var fake = new FakeBackend { App = AppInfo(McpPermission.Full) };

        var lines = await RunAsync(
            fake,
            "   ",
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"test","version":"1.0"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"serial_get_status","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":5,"method":"oops"}""",
            "not json at all");

        // 空行不算消息，其余 7 条里只有 6 条需要回应（没有通知）
        Assert.Equal(6, lines.Count);

        foreach (var line in lines)
        {
            // stdio 传输里换行就是消息分隔符：输出绝不能含裸换行
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);

            using var document = JsonDocument.Parse(line);
            Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        }
    }

    private static McpAppInfo AppInfo(McpPermission permission)
        => new(McpProtocol.ServerName, "1.0.0", permission, 1);

    /// <summary>跑完整的一轮：喂若干行请求，回收所有输出行。</summary>
    private static async Task<IReadOnlyList<string>> RunAsync(FakeBackend fake, params string[] requestLines)
    {
        var input = new StringReader(string.Join('\n', requestLines) + "\n");
        var output = new StringWriter();

        var server = new McpStdioServer(fake, input, output, ServerVersion);

        var exitCode = await server.RunAsync(CancellationToken.None);
        Assert.Equal(0, exitCode);

        return ReadLines(output);
    }

    private static async Task<List<string>> ListToolNamesAsync(FakeBackend fake)
    {
        var lines = await RunAsync(fake, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var response = ParseSingle(lines);
        var tools = response.RootElement.GetProperty("result").GetProperty("tools");
        var names = new List<string>();

        foreach (var tool in tools.EnumerateArray())
        {
            names.Add(tool.GetProperty("name").GetString()!);
        }

        return names;
    }

    /// <summary>把输出拆成单行。同时校验"写出去的每一行都以 '\n' 收尾"。</summary>
    private static IReadOnlyList<string> ReadLines(StringWriter writer)
    {
        var text = writer.ToString();
        var lines = new List<string>();

        if (text.Length == 0)
        {
            return lines;
        }

        Assert.EndsWith("\n", text);

        foreach (var line in text.Split('\n'))
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static JsonDocument ParseSingle(IReadOnlyList<string> lines)
    {
        Assert.Single(lines);
        return JsonDocument.Parse(lines[0]);
    }

    /// <summary>
    /// 结构相等（.NET 8 还没有 <c>JsonElement.DeepEquals</c>）。对象比字段名与值、数组比顺序，
    /// 数字统一按 <see cref="double"/> 比——同一份 JSON 的写法差异（缩进、转义）不该算不相等。
    /// </summary>
    private static void AssertTrueDeepEquals(JsonElement expected, JsonElement actual, string because)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedFields = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var actualFields = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

                Assert.Equal(
                    expectedFields.Keys.OrderBy(k => k, StringComparer.Ordinal),
                    actualFields.Keys.OrderBy(k => k, StringComparer.Ordinal));

                foreach (var (name, value) in expectedFields)
                {
                    AssertTrueDeepEquals(value, actualFields[name], because);
                }

                break;

            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToList();
                var actualItems = actual.EnumerateArray().ToList();

                Assert.Equal(expectedItems.Count, actualItems.Count);

                for (var i = 0; i < expectedItems.Count; i++)
                {
                    AssertTrueDeepEquals(expectedItems[i], actualItems[i], because);
                }

                break;

            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), actual.GetString());
                break;

            case JsonValueKind.Number:
                Assert.Equal(expected.GetDouble(), actual.GetDouble());
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.Equal(expected.GetBoolean(), actual.GetBoolean());
                break;

            default:
                Assert.Fail($"{because}：出现了意外的 JSON 值类型 {expected.ValueKind}。");
                break;
        }
    }

    /// <summary>
    /// 假的界面进程后端。默认"连得上且只读"，各测试按需改 <see cref="App"/> 与 <see cref="OnCallTool"/>。
    /// </summary>
    private sealed class FakeBackend : IMcpBackend
    {
        private int _pingCount;

        /// <summary><c>null</c> 表示界面进程没在运行。</summary>
        public McpAppInfo? App { get; set; } = AppInfo(McpPermission.ReadOnly);

        public Func<string, JsonElement?, JsonNode?>? OnCallTool { get; set; }

        public int PingCount => Volatile.Read(ref _pingCount);

        public Task<McpAppInfo?> PingAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _pingCount);
            return Task.FromResult(App);
        }

        public Task<JsonElement> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
        {
            var node = OnCallTool?.Invoke(toolName, arguments)
                ?? new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "ok",
                        },
                    },
                    ["isError"] = false,
                };

            return Task.FromResult(JsonSerializer.SerializeToElement(node, McpProtocol.WireJson));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
