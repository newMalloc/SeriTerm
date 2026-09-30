namespace SeriTerm.Core.Serial;

/// <summary>
/// 串口故障的归类。归类决定两件事：给用户看哪一句话、以及要不要继续等设备回来。
/// </summary>
public enum SerialFaultKind
{
    /// <summary>无法归类的异常。</summary>
    Unknown = 0,

    /// <summary>设备已从系统里消失：被拔出、在设备管理器里被禁用、驱动被卸载。</summary>
    DeviceRemoved,

    /// <summary>端口还在，但被其它进程独占。Windows 串口同一时刻只能被一个进程打开。</summary>
    PortBusy,

    /// <summary>端口在、能拿到句柄，但驱动报错（参数不支持、驱动异常、I/O 错误）。</summary>
    DriverError,

    /// <summary>端口名不合法或根本不是串口。</summary>
    InvalidPort,
}
