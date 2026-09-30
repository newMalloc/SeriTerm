using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SeriTerm.Core.Pipeline;

/// <summary>数据方向。</summary>
public enum LineDirection
{
    /// <summary>设备 → 本机。</summary>
    Rx,

    /// <summary>本机 → 设备。</summary>
    Tx,

    /// <summary>工具自身产生的提示行。</summary>
    System,
}

/// <summary>
/// 日志区的一行。
///
/// 说明：这里实现了 BCL 的 <see cref="INotifyPropertyChanged"/>（不是 WPF 依赖），
/// 目的是让"是否命中搜索""是否当前命中"两个状态能直接驱动界面高亮，避免为每行再包一层 ViewModel。
/// <see cref="Text"/> 是可变的，但切换 HEX/编码时采用整体 Reset 刷新，因此不做逐行通知。
/// </summary>
public sealed class DisplayLine : INotifyPropertyChanged
{
    private bool _isMatch;
    private bool _isCurrentMatch;

    public DisplayLine(
        long sequence,
        DateTime timestamp,
        LineDirection direction,
        byte[] raw,
        string text,
        int? displayLength = null)
    {
        Sequence = sequence;
        Timestamp = timestamp;
        Direction = direction;
        Raw = raw;
        Text = text;
        DisplayLength = Math.Clamp(displayLength ?? raw.Length, 0, raw.Length);
    }

    /// <summary>单调递增序号（清空显示后仍继续递增，用于稳定标识）。</summary>
    public long Sequence { get; }

    public DateTime Timestamp { get; }

    public LineDirection Direction { get; }

    /// <summary>原始字节，保留下来以便切换 HEX/文本显示时重新渲染。</summary>
    public byte[] Raw { get; }

    /// <summary>其中属于有效载荷、应该显示的长度（分隔符断帧时不含行尾分隔符）。</summary>
    public int DisplayLength { get; }

    /// <summary>应该显示的字节。</summary>
    public ReadOnlySpan<byte> DisplayBytes => Raw.AsSpan(0, DisplayLength);

    /// <summary>显示文本（HEX 串或解码后的文本）。</summary>
    public string Text { get; set; }

    public int ByteLength => Raw.Length;

    /// <summary>是否命中当前搜索。</summary>
    public bool IsMatch
    {
        get => _isMatch;
        set => SetField(ref _isMatch, value);
    }

    /// <summary>是否是当前定位到的那一条命中。</summary>
    public bool IsCurrentMatch
    {
        get => _isCurrentMatch;
        set => SetField(ref _isCurrentMatch, value);
    }

    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

    public string DirectionText => Direction switch
    {
        LineDirection.Rx => "Rx",
        LineDirection.Tx => "Tx",
        _ => "--",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
