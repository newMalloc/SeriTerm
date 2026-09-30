using System.Text;

namespace SeriTerm.Core.Text;

/// <summary>
/// 有状态的文本解码器：跨数据块保留解码状态。
///
/// 为什么必须有状态：串口是字节流，一个汉字（GB2312 2 字节、UTF-8 3 字节）很可能被切在
/// 两个数据块之间。每块都新建解码器就会把半个汉字解成 '?'，这就是"串口中文乱码"的根因。
/// </summary>
public sealed class StatefulTextDecoder
{
    /// <summary>界面可选的编码名（顺序即下拉框顺序）。</summary>
    public static readonly string[] SupportedEncodingNames =
    [
        "UTF-8", "GB2312", "GBK", "BIG5", "Shift-JIS", "UTF-16LE", "ASCII",
    ];

    static StatefulTextDecoder()
    {
        // .NET Core 起 GB2312/BIG5/Shift-JIS 等代码页需要显式注册
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private Decoder _decoder;
    private Encoding _encoding;

    public StatefulTextDecoder(string encodingName)
    {
        _encoding = Resolve(encodingName);
        _decoder = _encoding.GetDecoder();
        EncodingName = _encoding.WebName;
    }

    public string EncodingName { get; private set; }

    /// <summary>切换编码并重置解码状态（调用方通常需要同时清空已有显示）。</summary>
    public void SetEncoding(string encodingName)
    {
        var resolved = Resolve(encodingName);

        if (string.Equals(resolved.WebName, _encoding.WebName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _encoding = resolved;
        _decoder = _encoding.GetDecoder();
        EncodingName = _encoding.WebName;
    }

    /// <summary>解码一块字节。<paramref name="flush"/> 为 true 时把挂起的半个字符也吐出来。</summary>
    public string Decode(ReadOnlySpan<byte> bytes, bool flush = false)
    {
        if (bytes.IsEmpty && !flush)
        {
            return string.Empty;
        }

        // 注意：这里**不能**先 GetCharCount 再 GetChars。
        // 对有状态的 Decoder 来说，GetCharCount 会消费并推进内部挂起状态，
        // 紧接着的 GetChars 会在已被推进的状态上再解一次，导致半个汉字变成替换字符。
        // 正确做法：一次 GetChars 拿到结果，用返回值确定实际字符数。
        // 缓冲区上界：任意编码下单块 n 字节最多产出 n 个字符，再给 flush 留 2 个余量。
        var buffer = new char[bytes.Length + 2];
        var written = _decoder.GetChars(bytes, buffer, flush);
        return written == 0 ? string.Empty : new string(buffer, 0, written);
    }

    /// <summary>重置解码状态（清空显示、重新打开串口时调用）。</summary>
    public void Reset() => _decoder = _encoding.GetDecoder();

    public static Encoding Resolve(string? encodingName)
    {
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(encodingName);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
