using System.Diagnostics;
using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace SeriTerm.Core.Serial;

/// <summary>
/// 基于 <see cref="SerialPort"/> 的传输实现。
///
/// 设计要点（这三条是这类工具不丢数据、不卡死的关键）：
/// <list type="number">
/// <item>不使用 <c>DataReceived</c> 事件：它在线程池上触发、会丢事件，高波特率必丢数据。
///       这里用专用后台线程做阻塞读。</item>
/// <item>读超时设为有限值（100 ms）而不是无限等待：<see cref="SerialPort"/> 的取消令牌无法可靠
///       中断已阻塞的读，<c>Close()</c> 可能永久挂住；有限超时 + 取消标志是最稳的退出方式。</item>
/// <item>打开/关闭用信号量串行化，避免用户连点导致句柄泄漏或状态错乱。</item>
/// </list>
/// </summary>
public sealed class SerialPortTransport : ISerialTransport
{
    /// <summary>单次读取的缓冲区大小。</summary>
    private const int ReadBlockSize = 64 * 1024;

    /// <summary>读超时（毫秒）。既是"多久检查一次取消标志"的节拍，也是链路故障的检测间隔。</summary>
    private const int ReadTimeoutMs = 100;

    /// <summary>写超时（毫秒）。</summary>
    private const int WriteTimeoutMs = 2000;

    /// <summary>
    /// 确认"端口是否还在"时的等待窗口（毫秒）。
    /// 拔线瞬间注册表 SERIALCOMM 可能还没更新，直接查会把"设备已拔出"误判成"被其它程序占用"，
    /// 所以第一次查到"还在"时稍等再确认一次。只在故障路径上付出这点代价。
    /// </summary>
    private const int PresenceGraceMs = 120;

    private readonly ILogger<SerialPortTransport>? _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _stateLock = new();

    private SerialPort? _port;
    private Thread? _readerThread;
    private CancellationTokenSource? _cts;
    private TransportState _state = TransportState.Closed;
    private bool _disposed;

    public SerialPortTransport(ILogger<SerialPortTransport>? logger = null) => _logger = logger;

    public TransportState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public SerialSettings? CurrentSettings { get; private set; }

    public event EventHandler<TransportStateChangedEventArgs>? StateChanged;

    public event EventHandler<BytesReceivedEventArgs>? BytesReceived;

    public async Task OpenAsync(SerialSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 打开前清掉上一次的残留（例如链路故障后用户直接再点"打开"，或自动重连）
            if (_port is not null || _cts is not null || _readerThread is not null)
            {
                await CleanupCoreAsync().ConfigureAwait(false);
            }

            SetState(TransportState.Opening);

            var port = new SerialPort(
                settings.PortName,
                settings.BaudRate,
                settings.Parity,
                settings.DataBits,
                settings.StopBits)
            {
                Handshake = settings.Handshake,
                DtrEnable = settings.DtrEnable,
                RtsEnable = settings.RtsEnable,
                ReadTimeout = ReadTimeoutMs,
                WriteTimeout = WriteTimeoutMs,
            };

            try
            {
                // Open() 可能阻塞数百毫秒，不能占用调用线程（UI 线程）
                await Task.Run(port.Open, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TryDispose(port);

                // 打开失败也要先问清楚"端口还在不在"，否则拔线和被占用会给出同一句误导性提示
                var present = await Task
                    .Run(() => ConfirmPortPresence(settings.PortName), CancellationToken.None)
                    .ConfigureAwait(false);

                var message = SerialErrorTranslator.DescribeOpenFailure(ex, settings, present);
                _logger?.LogWarning(ex, "打开串口 {Port} 失败（端口存在={Present}）", settings.PortName, present);
                SetState(TransportState.Closed, message);
                throw new SerialLinkException(message, ex);
            }

            _port = port;
            CurrentSettings = settings;
            _cts = new CancellationTokenSource();

            var token = _cts.Token;
            _readerThread = new Thread(() => ReaderLoop(port, token))
            {
                IsBackground = true,
                Name = "SeriTerm.SerialReader",
            };
            _readerThread.Start();

            _logger?.LogInformation("串口已打开：{Description}", settings.ToShortDescription());
            SetState(TransportState.Open);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task CloseAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CleanupCoreAsync().ConfigureAwait(false);
            CurrentSettings = null;
            SetState(TransportState.Closed);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 已打开时改波特率。走 <see cref="SerialPort.BaudRate"/> 的 setter（底层 SetCommState），
    /// Windows 允许在句柄打开期间改，收发缓冲不会被清掉、读取线程也不必停。
    /// 整个动作在生命周期闸门内做，避免与"用户点关闭"、"自动重连重开"交叉。
    /// </summary>
    public async Task SetBaudRateAsync(int baudRate, CancellationToken cancellationToken = default)
    {
        if (baudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate), "波特率必须大于 0。");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var port = _port;

            if (port is null || !port.IsOpen)
            {
                throw new SerialLinkException("串口尚未打开，无法直接修改波特率。");
            }

            var previous = port.BaudRate;
            var portName = CurrentSettings?.PortName ?? "串口";

            if (previous == baudRate)
            {
                return;
            }

            try
            {
                port.BaudRate = baudRate;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "修改波特率失败：{Previous} → {Requested}", previous, baudRate);
                throw new SerialLinkException(
                    $"{portName} 的波特率仍为 {previous}，驱动不接受 {baudRate}：{ex.Message}",
                    ex);
            }

            // 回读确认：有些驱动对不支持的波特率不报错、只是保持原值（SetCommState 静默失败）。
            // 不确认的话，界面显示的波特率会和实际链路不一致，收到的是乱码还不知道为什么。
            var applied = port.BaudRate;
            if (applied != baudRate)
            {
                _logger?.LogWarning(
                    "驱动未接受 {Requested} 波特率，回读为 {Applied}（原 {Previous}）",
                    baudRate,
                    applied,
                    previous);

                // 回读值才是链路真实状态，按它记账，别让 CurrentSettings 撒谎
                UpdateCurrentBaudRate(applied);

                throw new SerialLinkException($"{portName} 的驱动没有接受 {baudRate}，实际是 {applied}。");
            }

            UpdateCurrentBaudRate(applied);
            _logger?.LogInformation(
                "波特率已改为 {BaudRate}（端口保持打开，原 {Previous}）",
                applied,
                previous);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 把新的波特率记进 <see cref="CurrentSettings"/>。
    /// 自动重连用的是 <see cref="CurrentSettings"/>，不更新的话拔线重连会退回旧波特率。
    /// </summary>
    private void UpdateCurrentBaudRate(int baudRate)
    {
        if (CurrentSettings is not null)
        {
            CurrentSettings = CurrentSettings with { BaudRate = baudRate };
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var port = _port ?? throw new SerialLinkException("串口尚未打开，无法发送数据。");

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!port.IsOpen)
            {
                throw new SerialLinkException("串口已关闭，无法发送数据。");
            }

            // Write 在驱动缓冲满时会阻塞，放到线程池执行
            var payload = data.ToArray();
            await Task.Run(() => port.Write(payload, 0, payload.Length), cancellationToken).ConfigureAwait(false);
        }
        catch (SerialLinkException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = SerialErrorTranslator.DescribeWriteFailure(ex, CurrentSettings);
            _logger?.LogWarning(ex, "写串口失败");
            var linkLost = ex is IOException or UnauthorizedAccessException;
            if (linkLost)
            {
                HandleReaderFault(ex);
            }

            throw new SerialLinkException(message, ex) { IsLinkLost = linkLost };
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CleanupCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        _lifecycleGate.Dispose();
        _writeGate.Dispose();
    }

    /// <summary>专用读取线程：有限超时阻塞读 + 取消标志轮询。</summary>
    private void ReaderLoop(SerialPort port, CancellationToken token)
    {
        var buffer = new byte[ReadBlockSize];

        while (!token.IsCancellationRequested)
        {
            try
            {
                var read = port.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    continue;
                }

                RaiseBytesReceived(buffer, read);
            }
            catch (TimeoutException)
            {
                // 100 ms 空转，回到循环顶部检查取消标志。正常路径，不是错误。
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException) when (token.IsCancellationRequested)
            {
                // Close() 导致正在阻塞的 Read 抛异常，属于正常关闭流程
                break;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                if (!token.IsCancellationRequested)
                {
                    HandleReaderFault(ex);
                }

                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    _logger?.LogError(ex, "串口读取线程异常退出");
                    HandleReaderFault(ex);
                }

                break;
            }
        }
    }

    /// <summary>链路故障：先报状态（触发自动重连），再摘掉句柄释放资源。</summary>
    private void HandleReaderFault(Exception ex)
    {
        // 在读取线程（或写失败时的工作线程）上，只阻塞 120 ms 做一次确认，之后状态立刻上报
        var present = ConfirmPortPresence(CurrentSettings?.PortName);
        var message = SerialErrorTranslator.DescribeLinkFailure(ex, CurrentSettings, present);
        _logger?.LogWarning(ex, "串口链路故障（端口存在={Present}）", present);
        SetState(TransportState.Faulted, message);

        // 这里刻意不取 _lifecycleGate：避免与用户主动 CloseAsync 相互等待。
        // 只是把句柄摘下来释放，重复 Dispose 已在 TryDispose 里被吞掉。
        var port = Interlocked.Exchange(ref _port, null);
        TryDispose(port);
    }

    /// <summary>
    /// 端口现在是否真的还在系统里。查到"在"时等 <see cref="PresenceGraceMs"/> 毫秒再确认一次，
    /// 用来吸收拔线瞬间的注册表滞后。
    /// </summary>
    private static bool ConfirmPortPresence(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName) || !PortEnumerator.IsPortPresent(portName))
        {
            return false;
        }

        Thread.Sleep(PresenceGraceMs);
        return PortEnumerator.IsPortPresent(portName);
    }

    /// <summary>释放句柄并等待读取线程退出；不改变状态（调用方决定置为 Closed 还是保留 Faulted）。</summary>
    private async Task CleanupCoreAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();

        var port = Interlocked.Exchange(ref _port, null);
        if (port is not null)
        {
            // 先关再放：Close() 会让正在阻塞的 Read 立刻抛异常退出
            try
            {
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "关闭串口句柄时出现异常（已忽略）");
            }

            TryDispose(port);
        }

        var reader = Interlocked.Exchange(ref _readerThread, null);
        if (reader is not null && reader != Thread.CurrentThread)
        {
            // 读超时 100 ms，正常情况下几十毫秒内就能退出；这里给足余量但不无限等待
            await Task.Run(() => reader.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }

        cts?.Dispose();
    }

    /// <summary>把数据复制成独立数组后交给订阅方（订阅方可安全保留，不必再拷贝）。</summary>
    private void RaiseBytesReceived(byte[] buffer, int length)
    {
        var handler = BytesReceived;
        if (handler is null)
        {
            return;
        }

        var copy = new byte[length];
        Buffer.BlockCopy(buffer, 0, copy, 0, length);

        var args = new BytesReceivedEventArgs(copy, Stopwatch.GetTimestamp());

        // 订阅方抛出的异常绝不能杀死读取线程，否则链路会静默停摆
        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<BytesReceivedEventArgs>)target).Invoke(this, args);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "接收数据订阅方抛出异常（已忽略）");
            }
        }
    }

    private void SetState(TransportState newState, string? message = null)
    {
        TransportStateChangedEventArgs args;

        lock (_stateLock)
        {
            if (_state == newState && message is null)
            {
                return;
            }

            var old = _state;
            _state = newState;
            args = new TransportStateChangedEventArgs(old, newState, message);
        }

        try
        {
            StateChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "状态变更订阅方抛出异常（已忽略）");
        }
    }

    private static void TryDispose(SerialPort? port)
    {
        try
        {
            port?.Dispose();
        }
        catch (Exception)
        {
            // 设备已拔出时 Dispose 也可能抛异常，忽略即可
        }
    }
}
