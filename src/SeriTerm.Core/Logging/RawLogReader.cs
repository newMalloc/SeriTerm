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
        return Read(File.ReadAllBytes(path));
    }

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

            if (length < 0 || offset + HeaderSize + length > data.Length)
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
