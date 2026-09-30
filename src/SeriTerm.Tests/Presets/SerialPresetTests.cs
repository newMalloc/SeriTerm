using System.IO.Ports;
using SeriTerm.Core.Framing;
using SeriTerm.Core.Presets;
using SeriTerm.Core.Send;
using SeriTerm.Core.Serial;

namespace SeriTerm.Tests.Presets;

public class SerialPresetTests
{
    private static SerialSettings Settings(string port = "COM5", int baud = 115200) => new()
    {
        PortName = port,
        BaudRate = baud,
        DataBits = 8,
        Parity = Parity.None,
        StopBits = StopBits.One,
    };

    private static SerialPreset Preset(string name, string port = "COM5", int baud = 115200)
        => new() { Name = name, Serial = Settings(port, baud) };

    [Fact]
    public void 默认名称应含端口与串口参数()
    {
        var name = SerialPreset.BuildDefaultName(Settings("COM5", 921600));

        Assert.Contains("COM5", name);
        Assert.Contains("921600", name);
        Assert.Contains("8N1", name);
    }

    [Theory]
    [InlineData(Parity.Odd, 'O')]
    [InlineData(Parity.Even, 'E')]
    [InlineData(Parity.Mark, 'M')]
    [InlineData(Parity.Space, 'S')]
    public void 默认名称应反映校验位(Parity parity, char expected)
    {
        var name = SerialPreset.BuildDefaultName(Settings() with { Parity = parity });

        Assert.Contains($"8{expected}1", name);
    }

    [Fact]
    public void 未选端口时名称应有兜底文案()
        => Assert.Contains("未选串口", SerialPreset.BuildDefaultName(Settings() with { PortName = "" }));

    [Fact]
    public void ToString应返回名称_供下拉框显示()
        => Assert.Equal("调试设备", Preset("调试设备").ToString());

    [Fact]
    public void Upsert_新名称应追加到末尾()
    {
        var presets = new[] { Preset("A"), Preset("B") };

        var result = SerialPreset.Upsert(presets, Preset("C"));

        Assert.Equal(["A", "B", "C"], result.Select(p => p.Name));
    }

    [Fact]
    public void Upsert_同名应覆盖且保持位置()
    {
        var presets = new[] { Preset("A", baud: 9600), Preset("B"), Preset("C") };

        var result = SerialPreset.Upsert(presets, Preset("B", baud: 921600));

        Assert.Equal(["A", "B", "C"], result.Select(p => p.Name));
        Assert.Equal(921600, result[1].Serial.BaudRate);
    }

    [Fact]
    public void Upsert_名称比较应忽略大小写()
    {
        var presets = new[] { Preset("Debug") };

        var result = SerialPreset.Upsert(presets, Preset("debug", baud: 9600));

        Assert.Single(result);
        Assert.Equal(9600, result[0].Serial.BaudRate);
    }

    [Fact]
    public void Upsert_不应修改入参()
    {
        var presets = new List<SerialPreset> { Preset("A") };

        SerialPreset.Upsert(presets, Preset("B"));

        Assert.Single(presets);
    }

    [Fact]
    public void Remove_应删除指定名称()
    {
        var presets = new[] { Preset("A"), Preset("B") };

        var result = SerialPreset.Remove(presets, "A", out var removed);

        Assert.True(removed);
        Assert.Equal(["B"], result.Select(p => p.Name));
    }

    [Fact]
    public void Remove_名称不存在时应返回原样()
    {
        var presets = new[] { Preset("A") };

        var result = SerialPreset.Remove(presets, "Z", out var removed);

        Assert.False(removed);
        Assert.Single(result);
    }

    [Fact]
    public void Remove_空名称应不删除任何项()
    {
        var presets = new[] { Preset("A") };

        SerialPreset.Remove(presets, null, out var removed);

        Assert.False(removed);
    }

    [Fact]
    public void 预设应能承载显示与发送配置()
    {
        var preset = new SerialPreset
        {
            Name = "带断帧",
            Serial = Settings(),
            Framing = FramingMode.Delimiter,
            DelimiterText = "hex:0D 0A",
            HexDisplay = true,
            EncodingName = "GB2312",
            SendHex = true,
            SendLineEnding = LineEnding.None,
        };

        Assert.Equal(FramingMode.Delimiter, preset.Framing);
        Assert.Equal("hex:0D 0A", preset.DelimiterText);
        Assert.True(preset.HexDisplay);
        Assert.Equal("GB2312", preset.EncodingName);
        Assert.True(preset.SendHex);
        Assert.Equal(LineEnding.None, preset.SendLineEnding);
    }
}
