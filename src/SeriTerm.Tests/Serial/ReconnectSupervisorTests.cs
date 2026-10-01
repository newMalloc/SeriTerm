using SeriTerm.Core.Serial;

namespace SeriTerm.Tests.Serial;

public class ReconnectSupervisorTests
{
    private static readonly SerialSettings Settings = new() { PortName = "COM99", BaudRate = 115200 };

    private static ReconnectPolicy FastPolicy(int maxAttempts = 0) => new()
    {
        BaseSeconds = 0.02,
        MaxSeconds = 0.02,
        MaxAttempts = maxAttempts,
        PresencePollSeconds = 0.02,
    };

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    [Fact]
    public async Task 链路故障后应自动重连成功()
    {
        await using var transport = new FakeTransport();
        await using var supervisor = new ReconnectSupervisor(transport, FastPolicy(), _ => true) { Enabled = true };

        await transport.OpenAsync(Settings);
        Assert.Equal(1, transport.OpenCount);

        transport.RaiseFault("设备被拔出");

        Assert.True(await WaitUntilAsync(() => transport.OpenCount >= 2), "没有触发重连");
        Assert.True(await WaitUntilAsync(() => supervisor.State == ReconnectState.Reconnected));
        Assert.Equal(TransportState.Open, transport.State);
    }

    [Fact]
    public async Task 端口未回来时应持续等待不打开()
    {
        await using var transport = new FakeTransport();
        await using var supervisor = new ReconnectSupervisor(transport, FastPolicy(), _ => false) { Enabled = true };

        await transport.OpenAsync(Settings);
        transport.RaiseFault("设备被拔出");

        await Task.Delay(300);

        Assert.Equal(1, transport.OpenCount);
        Assert.Equal(ReconnectState.WaitingForPort, supervisor.State);
    }

    [Fact]
    public async Task 关闭自动重连时不应重连()
    {
        await using var transport = new FakeTransport();
        await using var supervisor = new ReconnectSupervisor(transport, FastPolicy(), _ => true) { Enabled = false };

        await transport.OpenAsync(Settings);
        transport.RaiseFault("设备被拔出");

        await Task.Delay(300);

        Assert.Equal(1, transport.OpenCount);
        Assert.Equal(ReconnectState.Idle, supervisor.State);
    }

    [Fact]
    public async Task 设备不在时只等待且不消耗重试次数()
    {
        await using var transport = new FakeTransport();
        var present = false;

        // MaxAttempts = 2：如果"设备不在"被算成失败，这里早就该 GaveUp 了
        await using var supervisor = new ReconnectSupervisor(transport, FastPolicy(maxAttempts: 2), _ => present)
        {
            Enabled = true,
        };

        await transport.OpenAsync(Settings);
        transport.RaiseFault("设备被拔出");

        Assert.True(await WaitUntilAsync(() => supervisor.State == ReconnectState.WaitingForPort));

        await Task.Delay(250);

        Assert.Equal(1, transport.OpenCount);
        Assert.Equal(0, supervisor.Attempts);
        Assert.NotEqual(ReconnectState.GaveUp, supervisor.State);

        // 设备插回：应当很快自动打开，而不是继续等到退避周期结束
        present = true;

        Assert.True(await WaitUntilAsync(() => supervisor.State == ReconnectState.Reconnected), "设备插回后没有自动重连");
        Assert.Equal(2, transport.OpenCount);
    }

    [Fact]
    public async Task 等待设备插入的提示不应说成重连失败()
    {
        await using var transport = new FakeTransport();
        await using var supervisor = new ReconnectSupervisor(transport, FastPolicy(), _ => false) { Enabled = true };

        var messages = new List<string>();
        supervisor.StatusChanged += (_, e) => messages.Add(e.Message);

        await transport.OpenAsync(Settings);
        transport.RaiseFault("设备被拔出");

        Assert.True(await WaitUntilAsync(() => messages.Count > 0));
        await Task.Delay(120);

        Assert.Contains(messages, m => m.Contains("等待设备插入"));
        Assert.DoesNotContain(messages, m => m.Contains("重连失败"));
    }

    [Fact]
    public async Task 达到最大尝试次数应放弃()
    {
        await using var transport = new FakeTransport();
        await using var supervisor = new ReconnectSupervisor(transport, FastPolicy(maxAttempts: 2), _ => true)
        {
            Enabled = true,
        };

        await transport.OpenAsync(Settings);
        Assert.Equal(1, transport.OpenCount);

        // 首次打开成功，之后的重连都失败
        transport.FailOpen = true;
        transport.RaiseFault("设备被拔出");

        Assert.True(await WaitUntilAsync(() => supervisor.State == ReconnectState.GaveUp));
        Assert.Equal(3, transport.OpenCount);   // 首次 + 两次重试
    }

    [Fact]
    public async Task 手动停止后不应继续重连()
    {
        await using var transport = new FakeTransport();
        await using var supervisor = new ReconnectSupervisor(transport, new ReconnectPolicy
        {
            BaseSeconds = 0.5,
            MaxSeconds = 0.5,
        }, _ => true) { Enabled = true };

        await transport.OpenAsync(Settings);
        transport.RaiseFault("设备被拔出");

        Assert.True(await WaitUntilAsync(() => supervisor.IsReconnecting));

        await supervisor.StopAsync();

        var countAfterStop = transport.OpenCount;
        await Task.Delay(300);

        Assert.Equal(countAfterStop, transport.OpenCount);
        Assert.False(supervisor.IsReconnecting);
    }

    /// <summary>
    /// 故障之后**不等它跑起来**就 StopAsync：StopAsync 会先把 <c>_cts</c> 置空、再 await 重连循环，
    /// 而 BeginReconnect 里 Task.Run 的 lambda 若直接读字段 <c>_cts</c>，就可能读到置空后的值，
    /// 于是句柄里的 <c>_cts.Token</c> 抛 NullReferenceException，并被 StopAsync 原样冒给调用方。
    /// CI 上真的炸过一次（窗口极小，本地反复跑都不出现），所以这里用"不停就开始停"反复打这个窗口。
    /// </summary>
    [Fact]
    public async Task 故障后立刻停止不应把异常抛给调用方()
    {
        for (var i = 0; i < 200; i++)
        {
            await using var transport = new FakeTransport();
            await using var supervisor = new ReconnectSupervisor(transport, FastPolicy(), _ => true)
            {
                Enabled = true,
            };

            await transport.OpenAsync(Settings);
            transport.RaiseFault("设备被拔出");

            // 刻意不等待 IsReconnecting：要的就是"重连任务还没真正跑起来就停"这个时序
            await supervisor.StopAsync();
        }
    }

    [Fact]
    public async Task 重连状态变化应上报给界面()
    {
        await using var transport = new FakeTransport();
        await using var supervisor = new ReconnectSupervisor(transport, FastPolicy(), _ => true) { Enabled = true };

        var states = new List<ReconnectState>();
        supervisor.StatusChanged += (_, e) => states.Add(e.State);

        await transport.OpenAsync(Settings);
        transport.RaiseFault("设备被拔出");

        Assert.True(await WaitUntilAsync(() => states.Contains(ReconnectState.Reconnected)));

        Assert.Contains(ReconnectState.WaitingForPort, states);
        Assert.Contains(ReconnectState.Connecting, states);
    }
}

/// <summary>可控的假传输层：用来在单测里模拟"链路故障 → 设备回来 → 重连"。</summary>
internal sealed class FakeTransport : ISerialTransport
{
    public TransportState State { get; private set; } = TransportState.Closed;

    public SerialSettings? CurrentSettings { get; private set; }

    public int OpenCount { get; private set; }

    public bool FailOpen { get; set; }

#pragma warning disable CS0067 // 测试里用不到接收事件
    public event EventHandler<BytesReceivedEventArgs>? BytesReceived;
#pragma warning restore CS0067

    public event EventHandler<TransportStateChangedEventArgs>? StateChanged;

    public Task OpenAsync(SerialSettings settings, CancellationToken cancellationToken = default)
    {
        OpenCount++;

        if (FailOpen)
        {
            SetState(TransportState.Closed, "打开失败");
            throw new SerialLinkException("模拟打开失败");
        }

        CurrentSettings = settings;
        SetState(TransportState.Open);
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        CurrentSettings = null;
        SetState(TransportState.Closed);
        return Task.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>模拟设备被拔出。</summary>
    public void RaiseFault(string message) => SetState(TransportState.Faulted, message);

    private void SetState(TransportState state, string? message = null)
    {
        var old = State;
        State = state;
        StateChanged?.Invoke(this, new TransportStateChangedEventArgs(old, state, message));
    }
}
