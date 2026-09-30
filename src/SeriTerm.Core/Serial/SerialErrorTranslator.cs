using System.IO;
using System.Security;

namespace SeriTerm.Core.Serial;

/// <summary>
/// 把 .NET 串口抛出的底层异常翻译成用户能看懂的中文提示。
/// 原始异常类型与消息附在末尾，方便排查驱动层面的问题。
/// </summary>
public static class SerialErrorTranslator
{
    /// <summary>打开串口失败的说明。</summary>
    public static string DescribeOpenFailure(Exception ex, SerialSettings settings)
    {
        var reason = ex switch
        {
            UnauthorizedAccessException =>
                $"端口 {settings.PortName} 被其它程序占用，或当前用户无权访问。请关闭占用该端口的程序（例如另一个串口助手）后重试。",

            ArgumentException or FileNotFoundException =>
                $"串口 {settings.PortName} 不存在或已被移除。请点击刷新按钮重新选择端口。",

            IOException io when IsDeviceGone(io) =>
                $"串口 {settings.PortName} 已断开（设备可能被拔出）。请检查 USB 转串口连接后重试。",

            IOException =>
                $"无法按当前参数打开 {settings.PortName}。常见原因：波特率 {settings.BaudRate} 不被该驱动支持（可先降到 115200 试）、" +
                "驱动未安装或版本不匹配、USB 转串口被拔出。",

            InvalidOperationException =>
                $"串口 {settings.PortName} 当前不可用，可能已被打开或正在被其它程序使用。",

            _ => $"打开串口 {settings.PortName} 失败。",
        };

        return $"{reason}\n\n{Describe(ex)}";
    }

    /// <summary>链路运行中故障的说明。</summary>
    public static string DescribeLinkFailure(Exception ex, SerialSettings? settings)
    {
        var port = settings?.PortName ?? "串口";
        var reason = ex switch
        {
            IOException io when IsDeviceGone(io) =>
                $"{port} 连接已中断：设备被拔出或驱动异常。",

            UnauthorizedAccessException =>
                $"{port} 访问被拒绝：设备可能被其它程序抢占。",

            IOException =>
                $"{port} 通信中断（I/O 错误）。",

            InvalidOperationException =>
                $"{port} 已关闭或句柄失效。",

            _ => $"{port} 通信中断。",
        };

        return $"{reason}\n\n{Describe(ex)}";
    }

    /// <summary>写入失败的说明。</summary>
    public static string DescribeWriteFailure(Exception ex, SerialSettings? settings)
        => $"向 {settings?.PortName ?? "串口"} 写入数据失败。\n\n{Describe(ex)}";

    /// <summary>判断 IO 异常是否属于"设备已消失"。</summary>
    private static bool IsDeviceGone(IOException ex)
    {
        // 设备拔出常见表现：ERROR_ACCESS_DENIED(5)、ERROR_BAD_COMMAND(22)、
        // ERROR_DEV_NOT_EXIST(55)、ERROR_OPERATION_ABORTED(995)、ERROR_FILE_NOT_FOUND(2)
        var code = ex.HResult & 0xFFFF;
        return code is 2 or 5 or 22 or 55 or 995
            || ex.Message.Contains("not exist", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("removed", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("拒绝访问", StringComparison.Ordinal)
            || ex.Message.Contains("设备", StringComparison.Ordinal);
    }

    private static string Describe(Exception ex)
        => ex is SecurityException
            ? $"详细信息：{ex.GetType().Name}: {ex.Message}"
            : $"详细信息：{ex.GetType().Name}: {ex.Message}";
}
