namespace SeriTerm.Core.Send;

/// <summary>
/// 流式发送（发送文件）：分块读取、逐块写出，带进度与取消。
/// 抽成静态方法便于单测——测试里用内存流当输入、用回调收集写出的字节即可。
/// </summary>
public static class StreamSendJob
{
    /// <summary>默认分块大小：串口驱动写缓冲通常几 KB，1 KB 一块比较稳。</summary>
    public const int DefaultChunkSize = 1024;

    /// <summary>执行发送，返回实际发送的字节数。</summary>
    public static async Task<long> SendAsync(
        Stream source,
        Func<byte[], CancellationToken, ValueTask> writeAsync,
        int chunkSize = DefaultChunkSize,
        TimeSpan interChunkDelay = default,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(writeAsync);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);

        var totalLength = source.CanSeek ? source.Length : -1L;
        var buffer = new byte[chunkSize];
        long sent = 0;
        int read;

        while ((read = await source.ReadAsync(buffer.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var chunk = buffer[..read];

            await writeAsync(chunk, cancellationToken).ConfigureAwait(false);
            sent += read;

            if (totalLength > 0)
            {
                progress?.Report(Math.Min(1.0, (double)sent / totalLength));
            }

            if (interChunkDelay > TimeSpan.Zero)
            {
                await Task.Delay(interChunkDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        progress?.Report(1.0);
        return sent;
    }
}
