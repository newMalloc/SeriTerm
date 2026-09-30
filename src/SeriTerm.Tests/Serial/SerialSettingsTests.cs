using System.IO.Ports;
using SeriTerm.Core.Serial;

namespace SeriTerm.Tests.Serial;

public class SerialSettingsTests
{
    private static SerialSettings Valid() => new()
    {
        PortName = "COM5",
        BaudRate = 115200,
        DataBits = 8,
        Parity = Parity.None,
        StopBits = StopBits.One,
    };

    [Fact]
    public void 合法参数_校验应通过() => Valid().Validate();

    [Fact]
    public void 端口名为空_应给出中文提示()
    {
        var settings = Valid() with { PortName = "" };
        var ex = Assert.Throws<ArgumentException>(settings.Validate);
        Assert.Contains("请先选择串口", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 波特率非正_应报错(int baudRate)
    {
        var settings = Valid() with { BaudRate = baudRate };
        var ex = Assert.Throws<ArgumentException>(settings.Validate);
        Assert.Contains("波特率", ex.Message);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(9)]
    public void 数据位越界_应报错(int dataBits)
    {
        var settings = Valid() with { DataBits = dataBits };
        var ex = Assert.Throws<ArgumentException>(settings.Validate);
        Assert.Contains("数据位", ex.Message);
    }

    [Fact]
    public void 停止位非法_应报错()
    {
        var settings = Valid() with { StopBits = StopBits.None };
        var ex = Assert.Throws<ArgumentException>(settings.Validate);
        Assert.Contains("停止位", ex.Message);
    }

    [Fact]
    public void 支持非标准波特率()
    {
        var settings = Valid() with { BaudRate = 1000000 };
        settings.Validate();
        Assert.Equal(1000000, settings.BaudRate);
    }

    [Fact]
    public void 摘要应包含端口与参数()
    {
        var text = Valid().ToShortDescription();
        Assert.Contains("COM5", text);
        Assert.Contains("115200", text);
        Assert.Contains("8", text);
    }
}
