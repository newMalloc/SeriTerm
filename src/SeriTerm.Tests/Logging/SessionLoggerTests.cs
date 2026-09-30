using System.Text;
using SeriTerm.Core.Logging;
using SeriTerm.Core.Pipeline;

namespace SeriTerm.Tests.Logging;

public class SessionLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SeriTermTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static DisplayLine Line(string text, LineDirection direction = LineDirection.Rx, byte[]? raw = null)
        => new(1, new DateTime(2026, 9, 30, 23, 15, 20, 217), direction, raw ?? Encoding.UTF8.GetBytes(text), text);

    private static string? FindFile(string directory, string pattern)
        => Directory.GetFiles(directory, pattern).FirstOrDefault();

    [Fact]
    public async Task 文本日志应包含时间戳方向与内容()
    {
        var logger = new SessionLogger(new LogSessionOptions
        {
            Directory = _directory,
            TextEnabled = true,
            RawEnabled = false,
        });

        logger.Log(Line("OK", LineDirection.Rx));
        logger.Log(Line("AT", LineDirection.Tx));
        await logger.DisposeAsync();

        var file = FindFile(_directory, "SeriTerm_*.log");
        Assert.NotNull(file);

        var content = await File.ReadAllTextAsync(file!);
        Assert.Contains("2026-09-30 23:15:20.217 [Rx] OK", content);
        Assert.Contains("[Tx] AT", content);
    }

    [Fact]
    public async Task 成帧原始日志应能完整读回()
    {
        var rawPayload = new byte[] { 0x41, 0x0D, 0x0A };

        var logger = new SessionLogger(new LogSessionOptions
        {
            Directory = _directory,
            TextEnabled = false,
            RawEnabled = true,
            RawFormat = RawLogFormat.Framed,
        });

        logger.Log(Line("A", LineDirection.Rx, rawPayload));
        logger.Log(Line("B", LineDirection.Tx, [0x42]));
        await logger.DisposeAsync();

        var file = FindFile(_directory, "SeriTerm_raw_*.bin");
        Assert.NotNull(file);

        var records = RawLogReader.ReadFile(file!);

        Assert.Equal(2, records.Count);
        Assert.Equal(rawPayload, records[0].Payload);
        Assert.Equal((byte)LineDirection.Rx, records[0].Direction);
        Assert.Equal(new byte[] { 0x42 }, records[1].Payload);
        Assert.Equal((byte)LineDirection.Tx, records[1].Direction);
        Assert.Equal(new DateTime(2026, 9, 30, 23, 15, 20, 217, DateTimeKind.Local).ToUniversalTime().Ticks,
            records[0].TimestampUtc.Ticks);
    }

    [Fact]
    public async Task 纯字节原始日志应是拼接的载荷()
    {
        var logger = new SessionLogger(new LogSessionOptions
        {
            Directory = _directory,
            TextEnabled = false,
            RawEnabled = true,
            RawFormat = RawLogFormat.Plain,
        });

        logger.Log(Line("A", LineDirection.Rx, [0x41, 0x42]));
        logger.Log(Line("C", LineDirection.Rx, [0x43]));
        await logger.DisposeAsync();

        var file = FindFile(_directory, "SeriTerm_raw_*.bin");
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43 }, await File.ReadAllBytesAsync(file!));
    }

    [Fact]
    public async Task 十六进制文本日志应按HEX记录()
    {
        var logger = new SessionLogger(new LogSessionOptions
        {
            Directory = _directory,
            TextEnabled = true,
            RawEnabled = false,
            HexText = true,
        });

        logger.Log(Line("AB", LineDirection.Rx, [0x41, 0x42]));
        await logger.DisposeAsync();

        var content = await File.ReadAllTextAsync(FindFile(_directory, "SeriTerm_*.log")!);
        Assert.Contains("41 42", content);
        Assert.DoesNotContain("[Rx] AB", content);
    }

    [Fact]
    public async Task GB2312文本应按编码解码写入()
    {
        var logger = new SessionLogger(new LogSessionOptions
        {
            Directory = _directory,
            TextEnabled = true,
            RawEnabled = false,
            EncodingName = "GB2312",
        });

        logger.Log(Line("中", LineDirection.Rx, Encoding.GetEncoding("GB2312").GetBytes("中文")));
        await logger.DisposeAsync();

        var content = await File.ReadAllTextAsync(FindFile(_directory, "SeriTerm_*.log")!);
        Assert.Contains("中文", content);
    }

    [Fact]
    public async Task 系统提示行只进文本日志不进原始日志()
    {
        var logger = new SessionLogger(new LogSessionOptions
        {
            Directory = _directory,
            TextEnabled = true,
            RawEnabled = true,
        });

        logger.Log(Line("串口已打开", LineDirection.System, []));
        logger.Log(Line("DATA", LineDirection.Rx, [0x44]));
        await logger.DisposeAsync();

        var text = await File.ReadAllTextAsync(FindFile(_directory, "SeriTerm_*.log")!);
        Assert.Contains("串口已打开", text);

        var records = RawLogReader.ReadFile(FindFile(_directory, "SeriTerm_raw_*.bin")!);
        Assert.Single(records);
        Assert.Equal(new byte[] { 0x44 }, records[0].Payload);
    }

    [Fact]
    public void 默认目录应在文档目录下()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "SeriTerm",
            "Logs");

        Assert.Equal(expected, LogSessionOptions.DefaultDirectory);
    }
}
