using SeriTerm.Core.Send;

namespace SeriTerm.Tests.Send;

public class StreamSendJobTests
{
    [Fact]
    public async Task 应分块发送全部内容()
    {
        var source = new byte[5000];
        Random.Shared.NextBytes(source);

        var received = new List<byte>();
        var chunkSizes = new List<int>();

        var sent = await StreamSendJob.SendAsync(
            new MemoryStream(source),
            (chunk, _) =>
            {
                chunkSizes.Add(chunk.Length);
                received.AddRange(chunk);
                return ValueTask.CompletedTask;
            },
            chunkSize: 1024);

        Assert.Equal(5000, sent);
        Assert.Equal(source, received);
        Assert.Equal([1024, 1024, 1024, 1024, 904], chunkSizes);
    }

    [Fact]
    public async Task 应上报进度到100percent()
    {
        var source = new byte[3000];
        var reports = new List<double>();

        await StreamSendJob.SendAsync(
            new MemoryStream(source),
            (_, _) => ValueTask.CompletedTask,
            chunkSize: 1000,
            progress: new SyncProgress(reports.Add));

        Assert.Equal(1.0, reports[^1]);
        Assert.Contains(0.3333, reports.Select(r => Math.Round(r, 4)));
    }

    [Fact]
    public async Task 取消后应抛出OperationCanceled()
    {
        var source = new byte[100_000];
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await StreamSendJob.SendAsync(
                new MemoryStream(source),
                async (_, token) =>
                {
                    cts.Cancel();
                    await Task.Delay(10, token);
                },
                chunkSize: 100,
                cancellationToken: cts.Token);
        });
    }

    [Fact]
    public async Task 块间延迟应生效()
    {
        var source = new byte[300];

        var started = Environment.TickCount64;
        await StreamSendJob.SendAsync(
            new MemoryStream(source),
            (_, _) => ValueTask.CompletedTask,
            chunkSize: 100,
            interChunkDelay: TimeSpan.FromMilliseconds(60));

        // 3 块 => 2 次块间延迟
        Assert.True(Environment.TickCount64 - started >= 100, "块间延迟似乎没有生效");
    }

    [Fact]
    public async Task 空流应发送0字节()
    {
        var sent = await StreamSendJob.SendAsync(
            new MemoryStream([]),
            (_, _) => ValueTask.CompletedTask);

        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task 非法分块大小应抛出异常()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => StreamSendJob.SendAsync(
            new MemoryStream([1, 2, 3]),
            (_, _) => ValueTask.CompletedTask,
            chunkSize: 0));
    }

    /// <summary>把 IProgress 回调同步执行的实现，便于断言。</summary>
    private sealed class SyncProgress(Action<double> handler) : IProgress<double>
    {
        public void Report(double value) => handler(value);
    }
}
