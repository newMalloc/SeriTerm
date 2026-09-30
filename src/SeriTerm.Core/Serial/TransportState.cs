namespace SeriTerm.Core.Serial;

/// <summary>串口链路状态。</summary>
public enum TransportState
{
    /// <summary>未打开。</summary>
    Closed,

    /// <summary>正在打开（打开可能阻塞数百毫秒，界面上要禁用参数控件）。</summary>
    Opening,

    /// <summary>已打开，可收发。</summary>
    Open,

    /// <summary>链路故障（设备被拔出、驱动报错）；自动重连开启时转入重连流程。</summary>
    Faulted,

    /// <summary>等待重连（退避计时中）。</summary>
    WaitingToReconnect,
}

public static class TransportStateExtensions
{
    public static string ToDisplayText(this TransportState state) => state switch
    {
        TransportState.Closed => "已关闭",
        TransportState.Opening => "正在打开…",
        TransportState.Open => "已打开",
        TransportState.Faulted => "链路故障",
        TransportState.WaitingToReconnect => "等待重连…",
        _ => state.ToString(),
    };
}

public sealed class TransportStateChangedEventArgs(TransportState oldState, TransportState newState, string? message = null)
    : EventArgs
{
    public TransportState OldState { get; } = oldState;

    public TransportState NewState { get; } = newState;

    /// <summary>面向用户的中文说明（可为空）。</summary>
    public string? Message { get; } = message;
}
