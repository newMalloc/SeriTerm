using System.Threading.Channels;

namespace SeriTerm.Core.Logging;

/// <summary>
/// 后台缓冲写文件器：调用方只做"入队"，真正的磁盘写入由后台任务完成，
/// 因此串口读取线程与界面线程都不会被磁盘 I/O 拖住。
///
/// 设计要点：
/// <list type="bullet">
/// <item>有界队列 + <c>TryWrite</c>：队列满时**丢弃并计数**，而不是阻塞接收或无限吃内存；</item>
/// <item>按大小自动分卷，单文件不会无限增长；</item>
/// <item>每写满 32 KB 主动 Flush 一次，崩溃/断电最多丢最后一个缓冲块。</item>
/// </list>
/// </summary>
public sealed class BufferedLogWriter : IAsyncDisposable
{
    private const int FlushThresholdBytes = 32 * 1024;

    private readonly Channel<byte[]> _channel;
    private readonly Task _consumer;
    private readonly string _directory;
    private readonly string _prefix;
    private readonly string _extension;
    private readonly long _maxFileBytes;
    private readonly TimeProvider _timeProvider;

    private FileStream? _stream;
    private int _fileIndex;
    private long _writtenBytes;
    private long _droppedBytes;
    private long _droppedRecords;
    private bool _disposed;

    public BufferedLogWriter(
        string directory,
        string prefix,
        string extension,
        long maxFileBytes = 100L * 1024 * 1024,
        int queueCapacity = 8192,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);

        _directory = directory;
        _prefix = prefix;
        _extension = extension;
        _maxFileBytes = maxFileBytes;
        _timeProvider = timeProvider ?? TimeProvider.System;

        Directory.CreateDirectory(directory);

        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>当前正在写入的文件路径（尚未写入任何数据时为 null）。</summary>
    public string? CurrentFilePath => _stream?.Name;

    /// <summary>成功写入的字节数。</summary>
    public long WrittenBytes => Interlocked.Read(ref _writtenBytes);

    /// <summary>因队列满被丢弃的字节数与记录数。</summary>
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    public long DroppedRecords => Interlocked.Read(ref _droppedRecords);

    /// <summary>当前排队等待写入的记录数（用于界面提示积压）。</summary>
    public int PendingCount => _channel.Reader.Count;

    /// <summary>写入一批字节（非阻塞）。队列满时丢弃并计数。</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || _disposed)
        {
            return;
        }

        if (!_channel.Writer.TryWrite(data.ToArray()))
        {
            Interlocked.Add(ref _droppedBytes, data.Length);
            Interlocked.Increment(ref _droppedRecords);
        }
    }

    /// <summary>等待队列里的内容全部落盘。</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        // 排空队列：等到队列计数为 0 且消费者空闲
        while (_channel.Reader.Count > 0)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        var stream = _stream;

        if (stream is not null)
        {
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();

        try
        {
            await _consumer.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 消费任务里的异常在 ConsumeAsync 内部已经吞掉
        }

        await CloseStreamAsync().ConfigureAwait(false);
    }

    private async Task ConsumeAsync()
    {
        var reader = _channel.Reader;
        var sinceFlush = 0;

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var chunk))
            {
                try
                {
                    if (_stream is null)
                    {
                        await OpenStreamAsync().ConfigureAwait(false);
                    }

                    await _stream!.WriteAsync(chunk).ConfigureAwait(false);

                    Interlocked.Add(ref _writtenBytes, chunk.Length);
                    sinceFlush += chunk.Length;

                    if (sinceFlush >= FlushThresholdBytes)
                    {
                        await _stream.FlushAsync().ConfigureAwait(false);
                        sinceFlush = 0;
                    }

                    if (_maxFileBytes > 0 && _stream.Length >= _maxFileBytes)
                    {
                        await RollAsync().ConfigureAwait(false);
                        sinceFlush = 0;
                    }
                }
                catch (Exception)
                {
                    // 磁盘写失败（被占用、磁盘满）：丢弃这条并计数，绝不让接收线程受影响
                    Interlocked.Add(ref _droppedBytes, chunk.Length);
                    Interlocked.Increment(ref _droppedRecords);
                }
            }
        }
    }

    private async Task OpenStreamAsync()
    {
        var name = _fileIndex == 0
            ? $"{_prefix}_{_timeProvider.GetLocalNow():yyyyMMdd_HHmmss}.{_extension}"
            : $"{_prefix}_{_timeProvider.GetLocalNow():yyyyMMdd_HHmmss}_{_fileIndex:000}.{_extension}";

        var path = Path.Combine(_directory, name);

        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task RollAsync()
    {
        await CloseStreamAsync().ConfigureAwait(false);
        _fileIndex++;
    }

    private async Task CloseStreamAsync()
    {
        var stream = _stream;
        _stream = null;

        if (stream is null)
        {
            return;
        }

        try
        {
            await stream.FlushAsync().ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 忽略关闭异常
        }
    }
}
