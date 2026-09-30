namespace SeriTerm.Core.Send;

/// <summary>
/// 定时发送器：按固定间隔重复执行发送动作。
///
/// 用 <see cref="PeriodicTimer"/> 而不是 UI 定时器，好处是发送节奏不受界面刷新影响，
/// 且完全不依赖 WPF，可以直接单测。启动时立即发一次，符合"点开就看到在发"的直觉。
/// </summary>
public sealed class TimedSender : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask> _send;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TimedSender(Func<CancellationToken, ValueTask> send)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
    }

    /// <summary>发送间隔；运行中修改需要先停止再启动。</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>启动时是否立刻发送一次。</summary>
    public bool SendImmediately { get; set; } = true;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>累计成功发送次数。</summary>
    public long SentCount { get; private set; }

    /// <summary>某次发送失败（例如链路断开）；定时器会继续跑，由调用方决定是否停止。</summary>
    public event EventHandler<Exception>? SendFailed;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;

        _cts = null;
        _loop = null;

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        }

        cts.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (SendImmediately)
            {
                await SendOnceAsync(cancellationToken).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(Interval);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 停止
        }
    }

    private async Task SendOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _send(cancellationToken).ConfigureAwait(false);
            SentCount++;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SendFailed?.Invoke(this, ex);
        }
    }
}
