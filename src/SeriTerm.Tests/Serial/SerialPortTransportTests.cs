using System.IO.Ports;
using SeriTerm.Core.Serial;

namespace SeriTerm.Tests.Serial;

/// <summary>不依赖硬件的传输层行为测试。</summary>
public class SerialPortTransportTests
{
    [Fact]
    public async Task 打开不存在的端口_应抛出中文异常且状态为Closed()
    {
        await using var transport = new SerialPortTransport();
        var settings = new SerialSettings { PortName = "COM255", BaudRate = 115200 };

        var ex = await Assert.ThrowsAsync<SerialLinkException>(() => transport.OpenAsync(settings));

        Assert.Contains("COM255", ex.Message);
        Assert.Equal(TransportState.Closed, transport.State);
        Assert.Null(transport.CurrentSettings);
    }

    [Fact]
    public async Task 未打开就发送_应抛出异常()
    {
        await using var transport = new SerialPortTransport();

        await Assert.ThrowsAsync<SerialLinkException>(
            async () => await transport.WriteAsync(new byte[] { 0x01 }));
    }

    [Fact]
    public async Task 打开失败应上报状态变更事件()
    {
        await using var transport = new SerialPortTransport();
        var states = new List<TransportState>();
        transport.StateChanged += (_, e) => states.Add(e.NewState);

        await Assert.ThrowsAsync<SerialLinkException>(
            () => transport.OpenAsync(new SerialSettings { PortName = "COM255" }));

        Assert.Contains(TransportState.Opening, states);
        Assert.Equal(TransportState.Closed, states[^1]);
    }

    [Fact]
    public async Task 重复关闭不应抛异常()
    {
        await using var transport = new SerialPortTransport();

        await transport.CloseAsync();
        await transport.CloseAsync();

        Assert.Equal(TransportState.Closed, transport.State);
    }

    [Fact]
    public async Task 空数据发送应直接返回()
    {
        await using var transport = new SerialPortTransport();

        await transport.WriteAsync(ReadOnlyMemory<byte>.Empty);

        Assert.Equal(TransportState.Closed, transport.State);
    }
}

/// <summary>
/// 回环集成测试：需要 USB-TTL 的 TX 与 RX 短接。
/// 端口不存在时整类测试会被标记为 skipped（见 <see cref="LoopbackFactAttribute"/>）。
/// </summary>
public class SerialPortLoopbackTests
{
    /// <summary>
    /// 回环测试使用的端口。默认 <c>COM5</c>，可用环境变量 <c>SERITERM_LOOPBACK_PORT</c> 覆盖，
    /// 这样换一台机器（或换一个 USB-TTL）不必改源码。
    /// </summary>
    internal static readonly string LoopbackPort = ResolveLoopbackPort();

    private static string ResolveLoopbackPort()
    {
        var configured = Environment.GetEnvironmentVariable("SERITERM_LOOPBACK_PORT");

        return string.IsNullOrWhiteSpace(configured) ? "COM5" : configured.Trim();
    }

    private const int BaudRate = 115200;

    private static SerialSettings Settings() => new()
    {
        PortName = LoopbackPort,
        BaudRate = BaudRate,
        DataBits = 8,
        Parity = Parity.None,
        StopBits = StopBits.One,
    };

    [LoopbackFact]
    public async Task 打开与关闭_状态迁移应正确()
    {
        await using var transport = new SerialPortTransport();

        // 先预热一次，确保上一个用例的句柄已被系统释放，避免"端口被占用"造成的偶发失败
        await OpenWithRetryAsync(transport, Settings());
        await transport.CloseAsync();

        var states = new List<TransportState>();
        transport.StateChanged += (_, e) => states.Add(e.NewState);

        await OpenWithRetryAsync(transport, Settings());
        Assert.Equal(TransportState.Open, transport.State);
        Assert.NotNull(transport.CurrentSettings);

        await transport.CloseAsync();
        Assert.Equal(TransportState.Closed, transport.State);

        Assert.Contains(TransportState.Opening, states);
        Assert.Contains(TransportState.Open, states);
        Assert.Equal(TransportState.Closed, states[^1]);
    }

    /// <summary>
    /// 打开串口并按需重试：USB 转串口在关闭后，系统释放句柄可能要几十到几百毫秒，
    /// 紧接着的下一个用例直接打开会偶发"被占用"。重试是这类硬件测试的正确做法。
    /// </summary>
    internal static async Task OpenWithRetryAsync(
        ISerialTransport transport,
        SerialSettings settings,
        int attempts = 6)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await transport.OpenAsync(settings);
                return;
            }
            catch (SerialLinkException) when (attempt < attempts)
            {
                await Task.Delay(200);
            }
        }
    }

    [LoopbackFact]
    public async Task 订阅方抛异常不应杀死读取线程()
    {
        await using var transport = new SerialPortTransport();
        transport.BytesReceived += (_, _) => throw new InvalidOperationException("故意从订阅方抛出");

        await OpenWithRetryAsync(transport, Settings());
        await transport.WriteAsync(new byte[] { 0xFF });
        await Task.Delay(150);

        // 读取线程存活 => 串口仍是打开状态；关闭流程也不会被卡住
        Assert.Equal(TransportState.Open, transport.State);
        await transport.CloseAsync();
        Assert.Equal(TransportState.Closed, transport.State);
    }

    [LoopbackFact]
    public async Task 回环_写入的256字节应能被原样读回()
    {
        var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

        await using var transport = new SerialPortTransport();
        await OpenWithRetryAsync(transport, Settings());

        var received = await SendAndCollectAsync(transport, payload, TimeSpan.FromSeconds(3));

        Assert.True(Contains(received, payload),
            $"未在收到 {received.Length} 字节中匹配到完整回环数据。");
    }

    [LoopbackFact]
    public async Task 回环_2048字节连续传输不应丢字节()
    {
        var payload = new byte[2048];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        await using var transport = new SerialPortTransport();
        await OpenWithRetryAsync(transport, Settings());

        var received = await SendAndCollectAsync(transport, payload, TimeSpan.FromSeconds(5));

        Assert.True(Contains(received, payload),
            $"未在收到 {received.Length} 字节中匹配到完整回环数据（期望 2048 字节）。");
    }

    [LoopbackFact]
    public async Task 端口已被占用时_再打开应给出中文占用提示()
    {
        await using var first = new SerialPortTransport();
        await first.OpenAsync(Settings());

        await using var second = new SerialPortTransport();
        var ex = await Assert.ThrowsAsync<SerialLinkException>(() => second.OpenAsync(Settings()));

        Assert.Contains("占用", ex.Message);

        await first.CloseAsync();
    }

    [LoopbackFact]
    public async Task 关闭后可以再次打开()
    {
        await using var transport = new SerialPortTransport();

        await OpenWithRetryAsync(transport, Settings());
        await transport.CloseAsync();
        await OpenWithRetryAsync(transport, Settings());

        Assert.Equal(TransportState.Open, transport.State);

        var payload = "reopen"u8.ToArray();
        var received = await SendAndCollectAsync(transport, payload, TimeSpan.FromSeconds(3));
        Assert.True(Contains(received, payload));
    }

    private static async Task<byte[]> SendAndCollectAsync(
        ISerialTransport transport,
        byte[] payload,
        TimeSpan timeout)
    {
        var received = new List<byte>(payload.Length * 2);
        using var completed = new ManualResetEventSlim(false);

        void OnBytes(object? sender, BytesReceivedEventArgs e)
        {
            lock (received)
            {
                received.AddRange(e.Data);
                if (received.Count >= payload.Length)
                {
                    completed.Set();
                }
            }
        }

        transport.BytesReceived += OnBytes;
        try
        {
            // 串口是独占硬件，上一个用例可能残留数据在驱动缓冲里：先排空再发
            await Task.Delay(150);
            lock (received)
            {
                received.Clear();
            }

            completed.Reset();

            await transport.WriteAsync(payload);
            completed.Wait(timeout);

            // 已经收到足够字节，稍等一下让尾包到齐
            await Task.Delay(80);

            lock (received)
            {
                return [.. received];
            }
        }
        finally
        {
            transport.BytesReceived -= OnBytes;
        }
    }

    /// <summary>判断 received 中是否包含 payload 这段连续子序列（容忍杂散字节）。</summary>
    private static bool Contains(byte[] received, byte[] payload)
    {
        if (payload.Length == 0)
        {
            return true;
        }

        for (var start = 0; start + payload.Length <= received.Length; start++)
        {
            var matched = true;
            for (var i = 0; i < payload.Length; i++)
            {
                if (received[start + i] != payload[i])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
