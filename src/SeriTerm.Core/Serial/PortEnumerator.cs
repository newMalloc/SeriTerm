using System.IO.Ports;

namespace SeriTerm.Core.Serial;

/// <summary>
/// 串口设备枚举。纯粹读取系统端口名，不含任何 Windows 专有 API
/// （友好名"COM5 (CH340)"由 App 层通过 WMI 补充，失败时降级为纯端口名）。
/// </summary>
public static class PortEnumerator
{
    /// <summary>按端口号自然排序返回可用串口名（COM9 排在 COM10 之前）。</summary>
    public static IReadOnlyList<string> GetPortNames()
    {
        try
        {
            return SerialPort.GetPortNames()
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(ParsePortNumber)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            // 枚举失败不应当让程序崩溃：返回空列表，界面提示"未检测到串口"
            return [];
        }
    }

    /// <summary>端口当前是否仍存在（自动重连时探测设备是否插回）。</summary>
    public static bool IsPortPresent(string portName)
        => !string.IsNullOrWhiteSpace(portName)
           && GetPortNames().Contains(portName, StringComparer.OrdinalIgnoreCase);

    /// <summary>从端口名中解析出数字部分；解析不出来时返回 <see cref="int.MaxValue"/> 排在最后。</summary>
    public static int ParsePortNumber(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return int.MaxValue;
        }

        var digits = new string(portName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) && number > 0 ? number : int.MaxValue;
    }
}
