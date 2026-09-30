using System.IO;
using System.IO.Ports;

namespace SeriTerm.Core.Serial;

/// <summary>
/// 把 .NET 串口抛出的底层异常翻译成用户能看懂的中文提示，并归类故障原因。
///
/// <para><b>为什么需要"端口是否还在"这个参数：</b>
/// 设备被拔出时，<see cref="SerialPort"/> 底层拿到的是 Win32 的 ERROR_ACCESS_DENIED(5)，
/// 于是抛出 <see cref="UnauthorizedAccessException"/>——和"端口被别的程序占用"是**同一个异常、同一个错误码**。
/// 只看异常类型无法区分，会得出"设备可能被其它程序抢占"这种误导性结论。
/// 唯一可靠的判据是问系统：这个端口名现在还枚举得到吗？</para>
/// </summary>
public static class SerialErrorTranslator
{
    /// <summary>
    /// 归类故障。<paramref name="portPresent"/> 必须由调用方在**故障发生当下**查询
    /// （见 <see cref="PortEnumerator.IsPortPresent"/>），不能缓存。
    /// </summary>
    public static SerialFaultKind Classify(Exception ex, bool portPresent) => ex switch
    {
        // 拔线与"被占用"同码不同因，只能靠端口存在性区分
        UnauthorizedAccessException => portPresent ? SerialFaultKind.PortBusy : SerialFaultKind.DeviceRemoved,

        ArgumentException => SerialFaultKind.InvalidPort,
        FileNotFoundException => SerialFaultKind.DeviceRemoved,

        IOException io when IsRemovalCode(io) => SerialFaultKind.DeviceRemoved,
        IOException io when (io.HResult & 0xFFFF) == 5 => portPresent ? SerialFaultKind.PortBusy : SerialFaultKind.DeviceRemoved,
        IOException => SerialFaultKind.DriverError,

        // 句柄失效/端口已被关闭
        InvalidOperationException => SerialFaultKind.DriverError,

        _ => SerialFaultKind.Unknown,
    };

    /// <summary>打开串口失败的说明。</summary>
    public static string DescribeOpenFailure(Exception ex, SerialSettings settings, bool portPresent)
    {
        var reason = Classify(ex, portPresent) switch
        {
            SerialFaultKind.DeviceRemoved =>
                $"串口 {settings.PortName} 不存在：设备已被拔出、在设备管理器里被禁用，或驱动被卸载。" +
                "请检查 USB 转串口连接，插好后点击刷新按钮重新选择端口。",

            SerialFaultKind.PortBusy =>
                $"端口 {settings.PortName} 正被其它程序独占。Windows 串口同一时刻只能被一个程序打开，" +
                "请关闭占用它的程序（另一个串口助手、Arduino IDE 的串口监视器等）后重试。",

            SerialFaultKind.InvalidPort =>
                $"端口名 {settings.PortName} 不合法，请重新选择端口。",

            SerialFaultKind.DriverError =>
                $"无法按当前参数打开 {settings.PortName}。常见原因：波特率 {settings.BaudRate} 不被该驱动支持（可先降到 115200 试）、" +
                "驱动未安装或版本不匹配、USB 转串口被拔出。",

            _ => $"打开串口 {settings.PortName} 失败。",
        };

        return $"{reason}\n\n{Describe(ex)}";
    }

    /// <summary>链路运行中故障的说明。</summary>
    public static string DescribeLinkFailure(Exception ex, SerialSettings? settings, bool portPresent)
    {
        var port = settings?.PortName ?? "串口";

        var reason = Classify(ex, portPresent) switch
        {
            SerialFaultKind.DeviceRemoved =>
                $"{port} 连接已中断：设备已被拔出或被禁用。",

            SerialFaultKind.PortBusy =>
                $"{port} 访问被拒绝：该端口正被其它程序独占（另一个串口助手、Arduino IDE 等）。",

            SerialFaultKind.DriverError =>
                $"{port} 通信中断（驱动或 I/O 错误）。",

            _ => $"{port} 通信中断。",
        };

        return $"{reason}\n\n{Describe(ex)}";
    }

    /// <summary>写入失败的说明。</summary>
    public static string DescribeWriteFailure(Exception ex, SerialSettings? settings)
        => $"向 {settings?.PortName ?? "串口"} 写入数据失败。\n\n{Describe(ex)}";

    /// <summary>
    /// 判断 IO 异常是否属于"设备已消失"。只认错误码，不猜消息文本
    /// （消息是本地化的，按关键字匹配迟早误判）。
    /// </summary>
    private static bool IsRemovalCode(IOException ex)
    {
        // 2   ERROR_FILE_NOT_FOUND       设备不存在
        // 22  ERROR_BAD_COMMAND         设备被移除
        // 31  ERROR_GEN_FAILURE         设备故障（USB 拔出常见）
        // 55  ERROR_DEV_NOT_EXIST       设备不存在
        // 995 ERROR_OPERATION_ABORTED   被取消（驱动卸载时常见）
        // 1167 ERROR_DEVICE_NOT_CONNECTED
        var code = ex.HResult & 0xFFFF;
        return code is 2 or 22 or 31 or 55 or 995 or 1167;
    }

    private static string Describe(Exception ex)
    {
        var detail = $"详细信息：{ex.GetType().Name}: {ex.Message}";

        // 串口的异常常把真正的 Win32 错误藏在 InnerException 里，一并显示便于排查
        if (ex.InnerException is { } inner)
        {
            detail += $"\n内层异常：{inner.GetType().Name}: {inner.Message}";
        }

        return detail;
    }
}
