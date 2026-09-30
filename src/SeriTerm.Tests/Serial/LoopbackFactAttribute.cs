using SeriTerm.Core.Serial;

namespace SeriTerm.Tests.Serial;

/// <summary>
/// 回环测试专用 Fact：只有目标串口存在时才执行，否则标记为"跳过"并给出原因，
/// 这样拔掉 USB-TTL 时测试结果是 skipped 而不是 failed。
/// </summary>
public sealed class LoopbackFactAttribute : FactAttribute
{
    public LoopbackFactAttribute()
    {
        if (!PortEnumerator.IsPortPresent(SerialPortLoopbackTests.LoopbackPort))
        {
            Skip = $"未检测到 {SerialPortLoopbackTests.LoopbackPort}，已跳过回环测试。" +
                   "请确认 USB-TTL 已插好、驱动正常，且该端口的 TX 与 RX 已短接。";
        }
    }
}
