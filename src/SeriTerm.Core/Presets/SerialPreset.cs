using SeriTerm.Core.Framing;
using SeriTerm.Core.Send;
using SeriTerm.Core.Serial;

namespace SeriTerm.Core.Presets;

/// <summary>
/// 一套可复用的调试配置（对应参考工具里的"★ 收藏"）。
/// 覆盖连接参数与显示/发送习惯，切换设备时一键套用。
/// </summary>
public sealed record SerialPreset
{
    public string Name { get; init; } = string.Empty;

    public SerialSettings Serial { get; init; } = new();

    public FramingMode Framing { get; init; } = FramingMode.Gap;

    public int AutoFrameGapMilliseconds { get; init; } = 20;

    public string DelimiterText { get; init; } = "\\r\\n";

    public string EncodingName { get; init; } = "UTF-8";

    public bool HexDisplay { get; init; }

    public LineEnding SendLineEnding { get; init; } = LineEnding.CrLf;

    public bool SendHex { get; init; }

    /// <summary>下拉框直接显示名称（自定义 ComboBox 模板下依赖 ToString）。</summary>
    public override string ToString() => Name;

    /// <summary>生成默认名称，形如 <c>COM5 115200 8N1</c>。</summary>
    public static string BuildDefaultName(SerialSettings serial)
    {
        ArgumentNullException.ThrowIfNull(serial);

        var port = string.IsNullOrWhiteSpace(serial.PortName) ? "未选串口" : serial.PortName;
        return $"{port} {serial.BaudRate} {serial.DataBits}{ParityLetter(serial.Parity)}{SerialSettings.StopBitsText(serial.StopBits)}";
    }

    /// <summary>同名预设覆盖，其它保持原顺序；返回新列表（不改动入参）。</summary>
    public static IReadOnlyList<SerialPreset> Upsert(IReadOnlyList<SerialPreset> presets, SerialPreset preset)
    {
        ArgumentNullException.ThrowIfNull(presets);
        ArgumentNullException.ThrowIfNull(preset);

        var result = new List<SerialPreset>(presets.Count + 1);
        var replaced = false;

        foreach (var existing in presets)
        {
            if (string.Equals(existing.Name, preset.Name, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(preset);
                replaced = true;
            }
            else
            {
                result.Add(existing);
            }
        }

        if (!replaced)
        {
            result.Add(preset);
        }

        return result;
    }

    /// <summary>按名称删除；返回是否真的删掉了。</summary>
    public static IReadOnlyList<SerialPreset> Remove(IReadOnlyList<SerialPreset> presets, string? name, out bool removed)
    {
        ArgumentNullException.ThrowIfNull(presets);

        removed = false;
        var result = new List<SerialPreset>(presets.Count);

        foreach (var existing in presets)
        {
            if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                removed = true;
                continue;
            }

            result.Add(existing);
        }

        return result;
    }

    private static char ParityLetter(System.IO.Ports.Parity parity) => parity switch
    {
        System.IO.Ports.Parity.None => 'N',
        System.IO.Ports.Parity.Odd => 'O',
        System.IO.Ports.Parity.Even => 'E',
        System.IO.Ports.Parity.Mark => 'M',
        System.IO.Ports.Parity.Space => 'S',
        _ => '?',
    };
}
