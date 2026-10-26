namespace SeriTerm.Core.Logging;

/// <summary>
/// 读取 <see cref="RawLogFormat.Framed"/> 格式的原始日志，用于重放与校验。
/// </summary>
public static class RawLogReader
{
    private const int HeaderSize = 13;

    public static IReadOnlyList<RawLogRecord> ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return EnumerateFile(path).ToArray();
    }

    /// <summary>Stream records so a large log can be reviewed with bounded memory.</summary>
    public static IEnumerable<RawLogRecord> EnumerateFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var snapshotLength = stream.Length;
        var header = new byte[HeaderSize];
        while (snapshotLength - stream.Position >= HeaderSize)
        {
            if (stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) != HeaderSize) yield break;
            var ticks = BitConverter.ToInt64(header, 0);
            var direction = header[8];
            var length = BitConverter.ToInt32(header, 9);
            if (!IsValidHeader(ticks, direction, length) || length > snapshotLength - stream.Position) yield break;
            var payload = new byte[length];
            if (stream.ReadAtLeast(payload, length, throwOnEndOfStream: false) != length) yield break;
            yield return new RawLogRecord(new DateTime(ticks, DateTimeKind.Utc), direction, payload);
        }
    }

    private static bool IsValidHeader(long ticks, byte direction, int length)
        => ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
            && direction <= 2 && length >= 0 && length <= 64 * 1024 * 1024;

    /// <summary>
    /// 解析成帧记录。遇到半截记录（写入过程中崩溃）就停止，不抛异常——
    /// 崩溃日志也应该能被读出来。
    /// </summary>
    public static IReadOnlyList<RawLogRecord> Read(ReadOnlySpan<byte> data)
    {
        var records = new List<RawLogRecord>();
        var offset = 0;

        while (offset + HeaderSize <= data.Length)
        {
            var ticks = BitConverter.ToInt64(data[offset..(offset + 8)]);
            var direction = data[offset + 8];
            var length = BitConverter.ToInt32(data[(offset + 9)..(offset + 13)]);

            if (!IsValidHeader(ticks, direction, length) || length > data.Length - offset - HeaderSize)
            {
                break;
            }

            var payload = data.Slice(offset + HeaderSize, length).ToArray();
            records.Add(new RawLogRecord(new DateTime(ticks, DateTimeKind.Utc), direction, payload));

            offset += HeaderSize + length;
        }

        return records;
    }
}
