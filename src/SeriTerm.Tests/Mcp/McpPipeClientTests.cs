using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeriTerm.Core.Mcp;

namespace SeriTerm.Tests.Mcp;

/// <summary>
/// 命名管道客户端的测试。这里起的是真的 <see cref="NamedPipeServerStream"/>——
/// 管道名每次都用 <see cref="Guid"/> 生成，既不撞真实运行时的 <see cref="McpProtocol.PipeName"/>，
/// 也不受"测试程序集禁用了并行"之外的因素影响；每个测试自己的后台任务都会被显式收掉。
/// </summary>
public class McpPipeClientTests
{
    [Fact]
    public async Task ping应反序列化界面进程信息()
    {
        var pipeName = NewPipeName();

        await using var server = new FakePipeServer(
            pipeName,
            request =>
            {
                Assert.Equal(McpProtocol.Methods.Ping, request["method"]!.GetValue<string>());
                Assert.Equal("2.0", request["jsonrpc"]!.GetValue<string>());
                Assert.True(request.ContainsKey("id"));

                return Success(request, BuildAppInfo(McpPermission.ReadOnly));
            });

        var client = new McpPipeClient(pipeName);
        var app = await client.PingAsync(CancellationToken.None);

        Assert.NotNull(app);
        Assert.Equal(McpProtocol.ServerName, app!.App);
        Assert.Equal("1.0.4", app.Version);

        // 权限是按 "readOnly" / "full" 两个稳定字符串走的，转换器必须还原成枚举
        Assert.Equal(McpPermission.ReadOnly, app.Permission);
        Assert.Equal(2, app.ConnectedClients);
    }

    [Fact]
    public async Task 工具调用应返回结果元素()
    {
        var pipeName = NewPipeName();
        var payload = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = "{\"rxBytes\":128}",
                },
            },
            ["isError"] = false,
        };

        await using var server = new FakePipeServer(
            pipeName,
            request =>
            {
                Assert.Equal(McpProtocol.Methods.CallTool, request["method"]!.GetValue<string>());

                var parameters = Assert.IsType<JsonObject>(request["params"]);
                Assert.Equal(McpToolCatalog.GetStatus, parameters["name"]!.GetValue<string>());
                Assert.Equal(42, parameters["arguments"]!["since"]!.GetValue<int>());

                return Success(request, payload);
            });

        var client = new McpPipeClient(pipeName);

        using var arguments = JsonDocument.Parse("""{"since":42}""");
        var result = await client.CallToolAsync(McpToolCatalog.GetStatus, arguments.RootElement, CancellationToken.None);

        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal("{\"rxBytes\":128}", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task 服务端返回错误时应抛出带原因的异常()
    {
        var pipeName = NewPipeName();

        await using var server = new FakePipeServer(
            pipeName,
            request => new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"]!.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = -32601,
                    ["message"] = "没有名为 serial_nope 的工具。",
                    ["data"] = new JsonObject
                    {
                        ["reason"] = McpProtocol.Reasons.UnknownTool,
                    },
                },
            });

        var client = new McpPipeClient(pipeName);

        using var arguments = JsonDocument.Parse("{}");
        var exception = await Assert.ThrowsAsync<McpBackendException>(
            () => client.CallToolAsync("serial_nope", arguments.RootElement, CancellationToken.None));

        Assert.Equal(-32601, exception.Code);
        Assert.Equal(McpProtocol.Reasons.UnknownTool, exception.Reason);
        Assert.Contains("serial_nope", exception.Message);
    }

    [Fact]
    public async Task 没有服务端时ping返回null()
    {
        // 不存在的管道：NamedPipeClientStream 立刻抛 FileNotFoundException，客户端应吞掉它
        var client = new McpPipeClient(NewPipeName());

        var app = await client.PingAsync(CancellationToken.None);

        Assert.Null(app);
    }

    [Fact]
    public async Task 没有服务端时工具调用应抛未连接异常()
    {
        var client = new McpPipeClient(NewPipeName());

        using var arguments = JsonDocument.Parse("{}");
        var exception = await Assert.ThrowsAsync<McpBackendException>(
            () => client.CallToolAsync(McpToolCatalog.GetStatus, arguments.RootElement, CancellationToken.None));

        Assert.Equal(-32000, exception.Code);
        Assert.Equal("not_connected", exception.Reason);
    }

    private static string NewPipeName() => "SeriTerm.Mcp.Test." + Guid.NewGuid().ToString("N");

    private static string BuildAppInfo(McpPermission permission)
        => JsonSerializer.Serialize(
            new McpAppInfo(McpProtocol.ServerName, "1.0.4", permission, 2),
            McpProtocol.WireJson);

    /// <summary>组装一条 JSON-RPC 成功响应（<c>id</c> 原样回传）。</summary>
    private static JsonObject Success(JsonObject request, JsonNode result)
        => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]!.DeepClone(),
            ["result"] = result,
        };

    /// <summary>组装一条成功响应，其中 <c>result</c> 是已经序列化好的 JSON 文本。</summary>
    private static JsonObject Success(JsonObject request, string resultJson)
        => Success(request, JsonNode.Parse(resultJson)!);

    /// <summary>
    /// 一次性管道服务端：按 <c>request → response</c> 的规则回应每一次连接，直到被取消。
    /// 上下文是无 <c>using</c> 的写法，测试里用 <c>await using var server = new ...</c> 才能保证收尾。
    /// </summary>
    private sealed class FakePipeServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakePipeServer(string pipeName, Func<JsonObject, JsonObject> respond)
        {
            _loop = Task.Run(() => RunAsync(pipeName, respond));
        }

        public async ValueTask DisposeAsync()
        {
            var token = _cts.Token;
            await _cts.CancelAsync();

            Exception? loopFailure = null;

            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                loopFailure = ex;
            }

            _cts.Dispose();

            // 收尾阶段的取消/拆管道不算失败，但请求处理里的断言失败必须重抛，
            // 否则测试会"绿灯"通过而实际没验证到东西
            if (loopFailure is not null && !token.IsCancellationRequested)
            {
                throw loopFailure;
            }
        }

        private async Task RunAsync(string pipeName, Func<JsonObject, JsonObject> respond)
        {
            var token = _cts.Token;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 允许多个实例：某些用例会在同一条测试里连两次
                    await using var server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await WaitForConnectionAsync(server, token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                    using var writer = new StreamWriter(server, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
                    {
                        NewLine = "\n",
                        AutoFlush = true,
                    };

                    var line = await reader.ReadLineAsync(token).ConfigureAwait(false);

                    if (line is null)
                    {
                        continue;
                    }

                    var request = JsonNode.Parse(line) as JsonObject;
                    Assert.NotNull(request);

                    var response = respond(request!);

                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, McpProtocol.WireJson)).ConfigureAwait(false);
                    await writer.FlushAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常收尾：测试结束时会取消这条循环
            }
            catch (IOException)
            {
                // 取消时正在等待连接，底层管道被拆掉：同样是收尾
            }
        }

        /// <summary>
        /// 等一个客户端连上来，并且"可被取消"。
        ///
        /// 为什么不直接 <c>WaitForConnectionAsync(token)</c>：管道等待是同步阻塞的原语，
        /// 取消它只能靠再连一次把 <c>ConnectNamedPipe</c> 打醒——那会留下一个永远没人读的
        /// 连接，而这个测试用的服务端本来就是被测客户端的对端，多连一次会污染后续调用。
        /// 这里改成"把连接完成包成任务，与取消令牌赛跑"：取消就立刻退出，不碰管道。
        /// </summary>
        private static async Task WaitForConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
        {
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // 注意顺序：先启动等待，再用 token 把它包起来。这样即使令牌已经取消，
            // 等待也已经挂上去了，不会留下"取消后连接永远没人接"的窗口。
            var waiting = server.WaitForConnectionAsync();

            using var registration = cancellationToken.Register(() => connected.TrySetCanceled(cancellationToken));

            var finished = await Task.WhenAny(waiting, connected.Task).ConfigureAwait(false);

            if (finished == connected.Task)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            await waiting.ConfigureAwait(false);
        }
    }
}
