using SeriTerm.Core.Logging;

namespace SeriTerm.Tests.Logging;

public class RawLogReaderTests
{
    private static byte[] BuildRecord(long ticksUtc, byte direction, byte[] payload)
    {
        var record = new byte[13 + payload.Length];
        BitConverter.TryWriteBytes(record.AsSpan(0, 8), ticksUtc);
        record[8] = direction;
        BitConverter.TryWriteBytes(record.AsSpan(9, 4), payload.Length);
        payload.CopyTo(record.AsSpan(13));
        return record;
    }

    [Fact]
    public void 空数据应返回空列表()
        => Assert.Empty(RawLogReader.Read([]));

    [Fact]
    public void 应解析出全部记录()
    {
        var ticks = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks;
        var data = BuildRecord(ticks, 0, [1, 2]).Concat(BuildRecord(ticks, 1, [3])).ToArray();

        var records = RawLogReader.Read(data);

        Assert.Equal(2, records.Count);
        Assert.Equal(new byte[] { 1, 2 }, records[0].Payload);
        Assert.Equal(new byte[] { 3 }, records[1].Payload);
    }

    [Fact]
    public void 半截记录应被忽略而不抛异常()
    {
        var ticks = DateTime.UtcNow.Ticks;
        var data = BuildRecord(ticks, 0, [1, 2, 3]).Concat(new byte[] { 0x01, 0x02 }).ToArray();

        var records = RawLogReader.Read(data);

        Assert.Single(records);
    }

    [Fact]
    public void 长度字段超出剩余数据时应停止解析()
    {
        var ticks = DateTime.UtcNow.Ticks;
        var broken = new byte[13];
        BitConverter.TryWriteBytes(broken.AsSpan(0, 8), ticks);
        BitConverter.TryWriteBytes(broken.AsSpan(9, 4), 999);

        Assert.Empty(RawLogReader.Read(broken));
    }
}
