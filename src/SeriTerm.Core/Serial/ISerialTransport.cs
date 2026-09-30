namespace SeriTerm.Core.Serial;

/// <summary>
/// 收到一块串口数据。<see cref="Data"/> 是独立副本，订阅方可以安全保留、无需再拷贝。
/// </summary>
public sealed class BytesReceivedEventArgs(byte[] data, long timestamp) : EventArgs
{
    public byte[] Data { get; } = data;

    public int Length => Data.Length;

    /// <summary>本块数据到达时刻，取自 <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>（单调时钟）。</summary>
    public long Timestamp { get; } = timestamp;
}

/// <summary>
/// 字节流传输抽象。串口实现见 <see cref="SerialPortTransport"/>；
/// 将来的 TCP/UDP 调试实现同一接口，上层接收管线无需改动。
/// </summary>
public interface ISerialTransport : IAsyncDisposable
{
    TransportState State { get; }

    /// <summary>当前已打开的参数；未打开时为 <c>null</c>。</summary>
    SerialSettings? CurrentSettings { get; }

    event EventHandler<TransportStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 每读到一块数据触发一次。**在传输层自己的读取线程上同步触发**，
    /// 订阅方必须尽快返回（只做入队/拷贝），不要在其中做 UI 操作或耗时处理。
    /// </summary>
    event EventHandler<BytesReceivedEventArgs>? BytesReceived;

    /// <summary>打开串口。失败时抛出 <see cref="SerialLinkException"/>（消息为中文，可直接展示）。</summary>
    Task OpenAsync(SerialSettings settings, CancellationToken cancellationToken = default);

    /// <summary>关闭串口。可重复调用；不会抛异常。</summary>
    Task CloseAsync();

    /// <summary>写入数据。链路故障时抛出 <see cref="SerialLinkException"/>。</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}
