namespace SeriTerm.App.ViewModels;

/// <summary>
/// 下拉框选项：<see cref="Value"/> 供逻辑使用，<see cref="Display"/> 供界面显示。
/// 重写 <see cref="ToString"/> 是为了让自带模板的下拉框直接显示中文名。
/// </summary>
public sealed record Choice<T>(T Value, string Display)
{
    public override string ToString() => Display;
}

/// <summary>串口下拉项。友好名取不到时 <see cref="Display"/> 就是端口名本身。</summary>
public sealed record PortItem(string PortName, string Display)
{
    public override string ToString() => Display;
}
