namespace SeriTerm.Core.Serial;

/// <summary>自动重连状态。</summary>
public enum ReconnectState
{
    /// <summary>未在重连。</summary>
    Idle,

    /// <summary>等待端口恢复（设备可能还没插回来）。</summary>
    WaitingForPort,

    /// <summary>正在尝试重新打开。</summary>
    Connecting,

    /// <summary>已重连成功。</summary>
    Reconnected,

    /// <summary>达到最大尝试次数后放弃。</summary>
    GaveUp,
}

public sealed class ReconnectEventArgs(ReconnectState state, int attempt, string message) : EventArgs
{
    public ReconnectState State { get; } = state;

    public int Attempt { get; } = attempt;

    /// <summary>面向用户的中文说明。</summary>
    public string Message { get; } = message;
}

/// <summary>
/// 自动重连监督者：监听链路故障，按退避策略等待设备回来并重新打开串口。
///
/// 之所以单独抽出来而不是塞进传输层：传输层只负责"报故障"，
/// "要不要重连、等多久、试几次"是策略问题，放在这里可单测、可替换。
/// </summary>
public sealed class ReconnectSupervisor : IAsyncDisposable
{
    private readonly ISerialTransport _transport;
    private readonly Func<string, bool> _isPortPresent;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// 是否正在执行"自己发起的重连尝试"。重连失败时传输层会退回 Closed，
    /// 如果不区分来源就会把自己的重连循环取消掉（用户主动关闭才应该取消）。
    /// </summary>
    private volatile bool _connecting;

    public ReconnectSupervisor(
        ISerialTransport transport,
        ReconnectPolicy? policy = null,
        Func<string, bool>? isPortPresent = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Policy = policy ?? ReconnectPolicy.Default;
        _isPortPresent = isPortPresent ?? PortEnumerator.IsPortPresent;

        _transport.StateChanged += OnTransportStateChanged;
    }

    public ReconnectPolicy Policy { get; set; }

    /// <summary>是否开启自动重连。</summary>
    public bool Enabled { get; set; }

    public ReconnectState State { get; private set; } = ReconnectState.Idle;

    public int Attempts { get; private set; }

    public bool IsReconnecting => _loop is { IsCompleted: false };

    /// <summary>最后一次成功打开的参数，用于重连。</summary>
    public SerialSettings? LastSettings { get; private set; }

    public event EventHandler<ReconnectEventArgs>? StatusChanged;

    /// <summary>手动停止重连（例如用户自己点了打开/关闭）。</summary>
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
        SetState(ReconnectState.Idle, Attempts, "自动重连已停止。");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.StateChanged -= OnTransportStateChanged;
        await StopAsync().ConfigureAwait(false);
    }

    private void OnTransportStateChanged(object? sender, TransportStateChangedEventArgs e)
    {
        switch (e.NewState)
        {
            case TransportState.Open:
                LastSettings = _transport.CurrentSettings ?? LastSettings;
                Attempts = 0;

                if (State != ReconnectState.Idle)
                {
                    SetState(ReconnectState.Idle, 0, "链路已恢复。");
                }

                // 打开成功说明不需要再重连了
                _cts?.Cancel();
                break;

            case TransportState.Faulted when Enabled:
                BeginReconnect();
                break;

            case TransportState.Closed:
                LastSettings ??= _transport.CurrentSettings;

                // 只响应用户主动关闭；重连尝试自己导致的 Closed 不能取消重连
                if (!_connecting)
                {
                    _cts?.Cancel();
                }

                break;
        }
    }

    private void BeginReconnect()
    {
        if (IsReconnecting)
        {
            return;
        }

        var settings = _transport.CurrentSettings ?? LastSettings;

        if (settings is null)
        {
            return;
        }

        // 先把 CTS 落进局部变量再交给后台任务：lambda 若直接读字段 _cts，
        // 就有机会读到 StopAsync 清空之后的值（它先 _cts = null 再 await 这个任务），
        // 于是 Task.Run 里的 _cts.Token 抛 NullReferenceException——故障任务被 StopAsync
        // await 时又原样冒给调用方。CI 上实测到过（ReconnectSupervisorTests 手动停止后不应继续重连）。
        var cts = new CancellationTokenSource();
        _cts = cts;

        _loop = Task.Run(() => ReconnectLoopAsync(settings, cts.Token));
    }

    private async Task ReconnectLoopAsync(SerialSettings settings, CancellationToken cancellationToken)
    {
        // 只统计"端口在、但打开失败"的次数。设备还没插回来只算等待，不该消耗重试次数，
        // 否则拔线放着不管一会儿就会"连续 N 次失败，已停止自动重连"。
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_isPortPresent(settings.PortName))
            {
                SetState(
                    ReconnectState.WaitingForPort,
                    attempt,
                    $"{settings.PortName} 未连接，正在等待设备插入…（插回后自动重新打开）");

                if (!await DelayAsync(Policy.PresencePollInterval, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            attempt++;
            Attempts = attempt;
            var delay = Policy.GetDelay(attempt);

            SetState(
                ReconnectState.WaitingForPort,
                attempt,
                $"{settings.PortName} 已断开，{delay.TotalSeconds:0.#} 秒后尝试第 {attempt} 次重连…");

            if (!await DelayAsync(delay, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            // 等待期间又被拔掉：不算一次失败，回到"等待插入"
            if (!_isPortPresent(settings.PortName))
            {
                continue;
            }

            SetState(ReconnectState.Connecting, attempt, $"正在重连 {settings.PortName}…");

            _connecting = true;

            try
            {
                await _transport.OpenAsync(settings, cancellationToken).ConfigureAwait(false);
                SetState(ReconnectState.Reconnected, attempt, $"已重连 {settings.PortName}（第 {attempt} 次尝试）。");
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SerialLinkException ex)
            {
                SetState(
                    ReconnectState.WaitingForPort,
                    attempt,
                    $"第 {attempt} 次重连失败：{ex.Message.Split('\n')[0]}");
            }
            finally
            {
                _connecting = false;
            }

            if (Policy.MaxAttempts > 0 && attempt >= Policy.MaxAttempts)
            {
                SetState(ReconnectState.GaveUp, attempt, $"连续 {attempt} 次重连失败，已停止自动重连。");
                return;
            }
        }
    }

    /// <summary>等待一段时间；被取消时返回 false。</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void SetState(ReconnectState state, int attempt, string message)
    {
        State = state;

        try
        {
            StatusChanged?.Invoke(this, new ReconnectEventArgs(state, attempt, message));
        }
        catch (Exception)
        {
            // 订阅方异常不影响重连流程
        }
    }
}
