using System.IO.Ports;

namespace SeriTerm.Core.Serial;

/// <summary>
/// 串口连接参数（不可变）。用于打开串口、保存配置、切换预设。
/// </summary>
public sealed record SerialSettings
{
    /// <summary>串口名，如 <c>COM5</c>。</summary>
    public string PortName { get; init; } = string.Empty;

    /// <summary>波特率，支持驱动允许的任意值（含 1000000 等非标准值）。</summary>
    public int BaudRate { get; init; } = 115200;

    /// <summary>数据位，取值 5–8。</summary>
    public int DataBits { get; init; } = 8;

    public Parity Parity { get; init; } = Parity.None;

    public StopBits StopBits { get; init; } = StopBits.One;

    /// <summary>流控方式。硬件流控（RTS/CTS）对应 <see cref="Handshake.RequestToSend"/>。</summary>
    public Handshake Handshake { get; init; } = Handshake.None;

    public bool DtrEnable { get; init; }

    public bool RtsEnable { get; init; }

    /// <summary>下拉框预置波特率；输入框可编辑，允许填任意正整数。</summary>
    public static readonly int[] CommonBaudRates =
    [
        1200, 2400, 4800, 9600, 19200, 38400, 57600,
        115200, 230400, 460800, 921600, 1000000, 1500000, 2000000,
    ];

    public static readonly int[] CommonDataBits = [5, 6, 7, 8];

    /// <summary>校验位下拉项（枚举值 + 中文显示名）。</summary>
    public static readonly (Parity Value, string Display)[] ParityOptions =
    [
        (Parity.None, "None（无校验）"),
        (Parity.Odd, "Odd（奇校验）"),
        (Parity.Even, "Even（偶校验）"),
        (Parity.Mark, "Mark（标记）"),
        (Parity.Space, "Space（空格）"),
    ];

    public static readonly (StopBits Value, string Display)[] StopBitsOptions =
    [
        (StopBits.One, "1"),
        (StopBits.OnePointFive, "1.5"),
        (StopBits.Two, "2"),
    ];

    public static readonly (Handshake Value, string Display)[] HandshakeOptions =
    [
        (Handshake.None, "无"),
        (Handshake.XOnXOff, "软件（XOn/XOff）"),
        (Handshake.RequestToSend, "硬件（RTS/CTS）"),
        (Handshake.RequestToSendXOnXOff, "硬件 + 软件"),
    ];

    /// <summary>校验参数；不合法时抛出带中文说明的 <see cref="ArgumentException"/>。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PortName))
        {
            throw new ArgumentException("请先选择串口。", nameof(PortName));
        }

        if (BaudRate <= 0)
        {
            throw new ArgumentException("波特率必须大于 0。", nameof(BaudRate));
        }

        if (DataBits is < 5 or > 8)
        {
            throw new ArgumentException("数据位只能是 5、6、7 或 8。", nameof(DataBits));
        }

        if (StopBits is not (StopBits.One or StopBits.OnePointFive or StopBits.Two))
        {
            throw new ArgumentException("停止位只能是 1、1.5 或 2。", nameof(StopBits));
        }

        if (Parity is Parity.None or Parity.Odd or Parity.Even or Parity.Mark or Parity.Space)
        {
            return;
        }

        throw new ArgumentException("校验位取值不合法。", nameof(Parity));
    }

    /// <summary>形如 <c>COM5 115200,8,None,1</c>，用于状态栏与日志。</summary>
    public string ToShortDescription()
        => $"{PortName} {BaudRate},{DataBits},{ParityText(Parity)},{StopBitsText(StopBits)}";

    public static string ParityText(Parity parity) => parity switch
    {
        Parity.None => "None",
        Parity.Odd => "Odd",
        Parity.Even => "Even",
        Parity.Mark => "Mark",
        Parity.Space => "Space",
        _ => parity.ToString(),
    };

    public static string StopBitsText(StopBits stopBits) => stopBits switch
    {
        StopBits.One => "1",
        StopBits.OnePointFive => "1.5",
        StopBits.Two => "2",
        _ => stopBits.ToString(),
    };

    public static string HandshakeText(Handshake handshake) => handshake switch
    {
        Handshake.None => "无",
        Handshake.XOnXOff => "软件（XOn/XOff）",
        Handshake.RequestToSend => "硬件（RTS/CTS）",
        Handshake.RequestToSendXOnXOff => "硬件 + 软件",
        _ => handshake.ToString(),
    };
}
