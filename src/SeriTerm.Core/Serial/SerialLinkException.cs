namespace SeriTerm.Core.Serial;

/// <summary>
/// 串口链路层异常。<see cref="Exception.Message"/> 一律是可直接展示给用户的中文说明。
/// </summary>
public sealed class SerialLinkException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>true 表示链路已断（设备拔出等），自动重连应当介入。</summary>
    public bool IsLinkLost { get; init; }
}
