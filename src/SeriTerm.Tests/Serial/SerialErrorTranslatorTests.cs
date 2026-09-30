using SeriTerm.Core.Serial;

namespace SeriTerm.Tests.Serial;

/// <summary>
/// 故障归类的回归测试。
///
/// 背景（真机反馈的 bug）：USB-TTL 被手动拔出时，.NET 抛的是
/// <c>UnauthorizedAccessException: Access to the path 'COM5' is denied.</c>，
/// 而"端口被别的程序占用"抛的是同一个异常类型、同一个错误码（ERROR_ACCESS_DENIED）。
/// 旧实现只看异常类型，于是把拔线报成了"设备可能被其它程序抢占"——指着用户去找一个不存在的元凶。
/// 唯一的判据是"端口现在还枚举得到吗"，这批测试就是钉住这条规则。
/// </summary>
public class SerialErrorTranslatorTests
{
    private static readonly SerialSettings Settings = new() { PortName = "COM5", BaudRate = 1000000 };

    /// <summary>构造带指定 Win32 错误码的 IOException（HRESULT = 0x80070000 | code）。</summary>
    private static IOException IoWithCode(int win32Code, string message = "模拟 I/O 错误")
        => new(message, unchecked((int)(0x80070000u | (uint)win32Code)));

    /// <summary>真机上拔线时观察到的异常原样复刻。</summary>
    private static UnauthorizedAccessException UnplugException()
        => new("Access to the path 'COM5' is denied.");

    [Fact]
    public void 拔线时应判为设备移除而不是被占用()
    {
        var kind = SerialErrorTranslator.Classify(UnplugException(), portPresent: false);

        Assert.Equal(SerialFaultKind.DeviceRemoved, kind);
    }

    [Fact]
    public void 端口仍在时访问被拒绝应判为被占用()
    {
        var kind = SerialErrorTranslator.Classify(UnplugException(), portPresent: true);

        Assert.Equal(SerialFaultKind.PortBusy, kind);
    }

    [Fact]
    public void 拔线的链路提示必须说拔出且不得提其它程序()
    {
        var message = SerialErrorTranslator.DescribeLinkFailure(UnplugException(), Settings, portPresent: false);

        Assert.Contains("拔出", message);
        Assert.DoesNotContain("其它程序", message);
        Assert.Contains("COM5", message);
    }

    [Fact]
    public void 被占用的链路提示应指出其它程序独占()
    {
        var message = SerialErrorTranslator.DescribeLinkFailure(UnplugException(), Settings, portPresent: true);

        Assert.Contains("其它程序", message);
        Assert.DoesNotContain("拔出", message);
    }

    [Fact]
    public void 拔线的打开失败提示应引导刷新端口()
    {
        var message = SerialErrorTranslator.DescribeOpenFailure(UnplugException(), Settings, portPresent: false);

        Assert.Contains("COM5", message);
        Assert.Contains("刷新", message);
        Assert.DoesNotContain("其它程序", message);
    }

    [Fact]
    public void 被占用的打开失败提示应引导关闭占用程序()
    {
        var message = SerialErrorTranslator.DescribeOpenFailure(UnplugException(), Settings, portPresent: true);

        Assert.Contains("独占", message);
        Assert.DoesNotContain("拔出", message);
    }

    [Theory]
    [InlineData(2)]      // ERROR_FILE_NOT_FOUND
    [InlineData(22)]     // ERROR_BAD_COMMAND
    [InlineData(31)]     // ERROR_GEN_FAILURE
    [InlineData(55)]     // ERROR_DEV_NOT_EXIST
    [InlineData(995)]    // ERROR_OPERATION_ABORTED
    [InlineData(1167)]   // ERROR_DEVICE_NOT_CONNECTED
    public void 已知的设备消失错误码应判为设备移除(int code)
    {
        Assert.Equal(SerialFaultKind.DeviceRemoved, SerialErrorTranslator.Classify(IoWithCode(code), portPresent: true));
        Assert.Equal(SerialFaultKind.DeviceRemoved, SerialErrorTranslator.Classify(IoWithCode(code), portPresent: false));
    }

    [Fact]
    public void 错误码五在端口消失时应判为设备移除()
    {
        Assert.Equal(SerialFaultKind.DeviceRemoved, SerialErrorTranslator.Classify(IoWithCode(5), portPresent: false));
    }

    [Fact]
    public void 错误码五在端口仍在时应判为被占用()
    {
        Assert.Equal(SerialFaultKind.PortBusy, SerialErrorTranslator.Classify(IoWithCode(5), portPresent: true));
    }

    [Fact]
    public void 其它IO错误应判为驱动错误()
    {
        Assert.Equal(SerialFaultKind.DriverError, SerialErrorTranslator.Classify(IoWithCode(87), portPresent: true));
    }

    [Fact]
    public void 端口名不合法应判为非法端口()
    {
        Assert.Equal(SerialFaultKind.InvalidPort, SerialErrorTranslator.Classify(new ArgumentException("端口名无效"), portPresent: false));
    }

    [Fact]
    public void 未知异常不应崩且给出兜底提示()
    {
        var message = SerialErrorTranslator.DescribeLinkFailure(new InvalidProgramException("怪事"), Settings, portPresent: true);

        Assert.Contains("通信中断", message);
        Assert.Contains("InvalidProgramException", message);
    }

    [Fact]
    public void 详细信息应带上内层异常()
    {
        var ex = new IOException("外层", new UnauthorizedAccessException("内层原因"));

        var message = SerialErrorTranslator.DescribeLinkFailure(ex, Settings, portPresent: true);

        Assert.Contains("内层异常：UnauthorizedAccessException: 内层原因", message);
    }

    [Fact]
    public void 端口设置缺失时也要给出可读提示()
    {
        var message = SerialErrorTranslator.DescribeLinkFailure(UnplugException(), null, portPresent: false);

        Assert.Contains("串口", message);
        Assert.Contains("拔出", message);
    }
}
