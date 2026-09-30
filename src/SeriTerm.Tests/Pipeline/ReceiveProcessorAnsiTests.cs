using System.Diagnostics;
using SeriTerm.Core.Framing;
using SeriTerm.Core.Pipeline;

namespace SeriTerm.Tests.Pipeline;

/// <summary>终端类设备输出（含 ANSI 序列）的显示处理。</summary>
public class ReceiveProcessorAnsiTests
{
    private static readonly DateTime WallClock = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Local);

    private static List<DisplayLine> Feed(ReceiveProcessor processor, byte[] data)
    {
        var output = new List<DisplayLine>();
        processor.ProcessReceived(data, Stopwatch.GetTimestamp(), WallClock, output);
        return output;
    }

    [Fact]
    public void 开启ANSI过滤后彩色序列应被去掉()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions
        {
            Framing = FramingMode.None,
            StripAnsi = true,
        });

        var lines = Feed(processor, "\u001b[32mOK\u001b[0m\r\n"u8.ToArray());

        Assert.Single(lines);
        Assert.Equal("OK", lines[0].Text);
    }

    [Fact]
    public void 关闭ANSI过滤时序列会原样显示()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions
        {
            Framing = FramingMode.None,
            StripAnsi = false,
        });

        var lines = Feed(processor, "\u001b[32mOK\u001b[0m"u8.ToArray());

        Assert.Single(lines);
        Assert.Contains('\u001b', lines[0].Text);
    }

    [Fact]
    public void 切换ANSI过滤应能重刷已有行()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions
        {
            Framing = FramingMode.None,
            StripAnsi = false,
        });

        var lines = Feed(processor, "\u001b[31mERR\u001b[0m"u8.ToArray());

        processor.ApplyOptions(
            new ReceiveOptions { Framing = FramingMode.None, StripAnsi = true },
            WallClock,
            []);

        Assert.Equal("ERR", processor.Reformat(lines[0]));
    }

    [Fact]
    public void HEX显示不受ANSI过滤影响()
    {
        var processor = new ReceiveProcessor(new ReceiveOptions
        {
            Framing = FramingMode.None,
            HexDisplay = true,
            StripAnsi = true,
        });

        var lines = Feed(processor, [0x1B, 0x5B, 0x33, 0x31, 0x6D]);

        Assert.Equal("1B 5B 33 31 6D", lines[0].Text);
    }
}
