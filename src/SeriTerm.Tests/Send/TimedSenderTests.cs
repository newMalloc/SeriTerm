using SeriTerm.Core.Send;

namespace SeriTerm.Tests.Send;

public class TimedSenderTests
{
    [Fact]
    public async Task 启动后应立即发送并按间隔重复()
    {
        var count = 0;
        await using var sender = new TimedSender(_ =>
        {
            Interlocked.Increment(ref count);
            return ValueTask.CompletedTask;
        })
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };

        sender.Start();
        await Task.Delay(320);
        await sender.StopAsync();

        // 立即一次 + 约 6 次定时；放宽下界避免测试机负载导致抖动
        Assert.InRange(Volatile.Read(ref count), 4, 12);
        Assert.False(sender.IsRunning);
    }

    [Fact]
    public async Task 停止后不应再发送()
    {
        var count = 0;
        await using var sender = new TimedSender(_ =>
        {
            Interlocked.Increment(ref count);
            return ValueTask.CompletedTask;
        })
        {
            Interval = TimeSpan.FromMilliseconds(30),
        };

        sender.Start();
        await Task.Delay(120);
        await sender.StopAsync();

        var afterStop = Volatile.Read(ref count);
        await Task.Delay(150);

        Assert.Equal(afterStop, Volatile.Read(ref count));
    }

    [Fact]
    public async Task 默认启动时立即发送一次()
    {
        var count = 0;
        await using var sender = new TimedSender(_ =>
        {
            Interlocked.Increment(ref count);
            return ValueTask.CompletedTask;
        })
        {
            Interval = TimeSpan.FromSeconds(30),
        };

        sender.Start();
        await Task.Delay(120);
        await sender.StopAsync();

        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task 可关闭立即发送()
    {
        var count = 0;
        await using var sender = new TimedSender(_ =>
        {
            Interlocked.Increment(ref count);
            return ValueTask.CompletedTask;
        })
        {
            Interval = TimeSpan.FromSeconds(30),
            SendImmediately = false,
        };

        sender.Start();
        await Task.Delay(120);
        await sender.StopAsync();

        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public async Task 单次发送失败不应终止定时器()
    {
        var attempts = 0;
        var failures = 0;

        await using var sender = new TimedSender(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("故意失败");
            }

            return ValueTask.CompletedTask;
        })
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };

        sender.SendFailed += (_, _) => Interlocked.Increment(ref failures);

        sender.Start();
        await Task.Delay(200);
        await sender.StopAsync();

        Assert.Equal(1, Volatile.Read(ref failures));
        Assert.True(Volatile.Read(ref attempts) >= 3, $"实际尝试 {attempts} 次");
        Assert.True(sender.SentCount >= 2);
    }

    [Fact]
    public async Task 重复启动不应产生两个循环()
    {
        var count = 0;
        await using var sender = new TimedSender(_ =>
        {
            Interlocked.Increment(ref count);
            return ValueTask.CompletedTask;
        })
        {
            Interval = TimeSpan.FromSeconds(30),
        };

        sender.Start();
        sender.Start();
        await Task.Delay(150);
        await sender.StopAsync();

        Assert.Equal(1, Volatile.Read(ref count));
    }
}
