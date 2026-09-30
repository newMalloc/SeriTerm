using System.Text;
using SeriTerm.Core.Pipeline;
using SeriTerm.Core.Text;

namespace SeriTerm.Core.Logging;

/// <summary>
/// 一次"接收保存到文件"会话：同时维护原始字节日志与文本日志。
///
/// 两种日志各有用途：
/// <list type="bullet">
/// <item><b>原始日志</b>（.bin，成帧格式）：完整保留方向与时间，可以用
///       <see cref="RawLogReader"/> 读回来重放，用于复现问题；</item>
/// <item><b>文本日志</b>（.log）：人直接看的，保留真实换行，中文按所选编码解码。</item>
/// </list>
/// </summary>
public sealed class SessionLogger : IAsyncDisposable
{
    private readonly LogSessionOptions _options;
    private readonly BufferedLogWriter? _rawWriter;
    private readonly BufferedLogWriter? _textWriter;
    private readonly StatefulTextDecoder _decoder;

    public SessionLogger(LogSessionOptions options)
    {
        _options = (options ?? new LogSessionOptions()).Normalize();

        if (_options.RawEnabled)
        {
            _rawWriter = new BufferedLogWriter(_options.Directory, "SeriTerm_raw", "bin", _options.MaxFileBytes);
        }

        if (_options.TextEnabled)
        {
            _textWriter = new BufferedLogWriter(_options.Directory, "SeriTerm", "log", _options.MaxFileBytes);
        }

        _decoder = new StatefulTextDecoder(_options.EncodingName);
    }

    public string Directory => _options.Directory;

    /// <summary>当前原始日志文件（还没写数据时为 null）。</summary>
    public string? RawFilePath => _rawWriter?.CurrentFilePath;

    /// <summary>当前文本日志文件（还没写数据时为 null）。</summary>
    public string? TextFilePath => _textWriter?.CurrentFilePath;

    public long WrittenBytes => (_rawWriter?.WrittenBytes ?? 0) + (_textWriter?.WrittenBytes ?? 0);

    /// <summary>因队列积压被丢弃的记录数（> 0 说明磁盘跟不上）。</summary>
    public long DroppedRecords => (_rawWriter?.DroppedRecords ?? 0) + (_textWriter?.DroppedRecords ?? 0);

    /// <summary>记录一行数据。</summary>
    public void Log(DisplayLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var payload = line.DisplayBytes;

        if (_rawWriter is not null && payload.Length > 0)
        {
            if (_options.RawFormat == RawLogFormat.Framed)
            {
                _rawWriter.Write(BuildRecord(line, payload));
            }
            else
            {
                _rawWriter.Write(payload);
            }
        }

        _textWriter?.Write(Encoding.UTF8.GetBytes(FormatTextLine(line, payload)));
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_rawWriter is not null)
        {
            await _rawWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_textWriter is not null)
        {
            await _textWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_rawWriter is not null)
        {
            await _rawWriter.DisposeAsync().ConfigureAwait(false);
        }

        if (_textWriter is not null)
        {
            await _textWriter.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>成帧格式：<c>[8 字节 UTC Ticks][1 字节方向][4 字节长度][载荷]</c>。</summary>
    private static byte[] BuildRecord(DisplayLine line, ReadOnlySpan<byte> payload)
    {
        var record = new byte[13 + payload.Length];

        BitConverter.TryWriteBytes(record.AsSpan(0, 8), line.Timestamp.ToUniversalTime().Ticks);
        record[8] = (byte)line.Direction;
        BitConverter.TryWriteBytes(record.AsSpan(9, 4), payload.Length);

        payload.CopyTo(record.AsSpan(13));

        return record;
    }

    private string FormatTextLine(DisplayLine line, ReadOnlySpan<byte> payload)
    {
        string body;

        if (_options.HexText)
        {
            body = HexCodec.Format(payload);
        }
        else if (payload.Length == 0)
        {
            body = line.Text;
        }
        else
        {
            // 每行独立 flush：日志文件里一行就是一个完整帧，不需要跨行续解
            body = _decoder.Decode(payload, flush: true);
        }

        return $"{line.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{line.DirectionText}] {body}{Environment.NewLine}";
    }
}
