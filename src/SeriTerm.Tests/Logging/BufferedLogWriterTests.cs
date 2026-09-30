using System.Text;
using SeriTerm.Core.Logging;

namespace SeriTerm.Tests.Logging;

public class BufferedLogWriterTests : IDisposable
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
            // 清理失败不影响测试结论
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 写入的数据应落盘()
    {
        var payload = Encoding.UTF8.GetBytes("hello log");

        await using (var writer = new BufferedLogWriter(_directory, "test", "log"))
        {
            writer.Write(payload);
            await writer.FlushAsync();
        }

        var files = Directory.GetFiles(_directory);
        Assert.Single(files);
        Assert.Equal(payload, await File.ReadAllBytesAsync(files[0]));
    }

    [Fact]
    public async Task 超过单文件上限应自动分卷()
    {
        await using (var writer = new BufferedLogWriter(_directory, "roll", "log", maxFileBytes: 1024))
        {
            for (var i = 0; i < 10; i++)
            {
                writer.Write(new byte[400]);
            }

            await writer.FlushAsync();
        }

        var files = Directory.GetFiles(_directory);
        Assert.True(files.Length >= 3, $"实际分卷 {files.Length} 个");

        // 每个文件都不应超过上限太多（分卷在写完当前块后判断）
        foreach (var file in files)
        {
            Assert.True(new FileInfo(file).Length <= 1024 + 400, $"文件 {Path.GetFileName(file)} 超出上限");
        }
    }

    [Fact]
    public async Task 空数据不应创建文件()
    {
        await using (var writer = new BufferedLogWriter(_directory, "empty", "log"))
        {
            writer.Write([]);
            await writer.FlushAsync();
        }

        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task 队列积压时应丢弃并计数而不是阻塞或无限增长()
    {
        const int chunkSize = 1024;
        const int total = 2000;

        var writer = new BufferedLogWriter(_directory, "drop", "log", queueCapacity: 4);
        try
        {
            for (var i = 0; i < total; i++)
            {
                writer.Write(new byte[chunkSize]);
            }

            await writer.FlushAsync();
        }
        finally
        {
            await writer.DisposeAsync();
        }

        // 不变式：每条记录要么写成功、要么被丢弃，不会凭空消失
        Assert.Equal((long)total * chunkSize, writer.WrittenBytes + writer.DroppedBytes);
        Assert.Equal(writer.WrittenBytes, await TotalWrittenAsync());
    }

    private async Task<long> TotalWrittenAsync()
    {
        long total = 0;

        foreach (var file in Directory.GetFiles(_directory))
        {
            total += (await File.ReadAllBytesAsync(file)).Length;
        }

        return total;
    }

    [Fact]
    public async Task 重复释放不应抛异常()
    {
        var writer = new BufferedLogWriter(_directory, "twice", "log");
        writer.Write([1, 2, 3]);

        await writer.DisposeAsync();
        await writer.DisposeAsync();
    }
}
