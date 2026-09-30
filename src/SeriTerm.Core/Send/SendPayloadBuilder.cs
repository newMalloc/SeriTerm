using System.Text;
using SeriTerm.Core.Text;

namespace SeriTerm.Core.Send;

/// <summary>发送内容末尾附加的换行符。</summary>
public enum LineEnding
{
    /// <summary>不附加。</summary>
    None,

    /// <summary>CR（0x0D）。</summary>
    Cr,

    /// <summary>LF（0x0A）。</summary>
    Lf,

    /// <summary>CRLF（0x0D 0x0A），AT 指令常用。</summary>
    CrLf,
}

/// <summary>
/// 把界面上的发送内容组装成真正要写入串口的字节。抽成纯函数便于单测。
/// </summary>
public static class SendPayloadBuilder
{
    public static readonly (LineEnding Value, string Display)[] LineEndingOptions =
    [
        (LineEnding.CrLf, "CRLF (\\r\\n)"),
        (LineEnding.Cr, "CR (\\r)"),
        (LineEnding.Lf, "LF (\\n)"),
        (LineEnding.None, "无"),
    ];

    /// <summary>按行尾设置取得要附加的字节。</summary>
    public static byte[] GetLineEndingBytes(LineEnding lineEnding) => lineEnding switch
    {
        LineEnding.Cr => [0x0D],
        LineEnding.Lf => [0x0A],
        LineEnding.CrLf => [0x0D, 0x0A],
        _ => [],
    };

    /// <summary>
    /// 组装发送字节。
    /// <paramref name="hexMode"/> 为 true 时把输入当十六进制解析（宽容格式），否则按编码转字节。
    /// </summary>
    public static bool TryBuild(
        string? text,
        bool hexMode,
        LineEnding lineEnding,
        string encodingName,
        out byte[] payload,
        out string error)
    {
        payload = [];
        error = string.Empty;

        var ending = GetLineEndingBytes(lineEnding);
        var body = Array.Empty<byte>();

        if (hexMode)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (!HexCodec.TryParse(text, out body, out error))
                {
                    return false;
                }
            }
        }
        else
        {
            var encoding = StatefulTextDecoder.Resolve(encodingName);
            body = encoding.GetBytes(text ?? string.Empty);
        }

        if (body.Length == 0 && ending.Length == 0)
        {
            error = hexMode ? "十六进制发送内容为空。" : "发送内容为空。";
            return false;
        }

        payload = new byte[body.Length + ending.Length];
        body.CopyTo(payload, 0);
        ending.CopyTo(payload, body.Length);
        return true;
    }

    /// <summary>把已发送的字节还原成用于显示的文本（发送回显用）。</summary>
    public static string Describe(ReadOnlySpan<byte> payload, bool hexMode, string encodingName)
    {
        if (hexMode)
        {
            return HexCodec.Format(payload);
        }

        var text = StatefulTextDecoder.Resolve(encodingName).GetString(payload);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\n', ' ')
            .TrimEnd();
    }

    /// <summary>把可能为空的行尾描述成界面提示。</summary>
    public static string LineEndingText(LineEnding lineEnding) => lineEnding switch
    {
        LineEnding.Cr => "CR",
        LineEnding.Lf => "LF",
        LineEnding.CrLf => "CRLF",
        _ => "无行尾",
    };

    /// <summary>把发送内容显示成日志里的一行提示（超长时截断）。</summary>
    public static string Preview(ReadOnlySpan<byte> payload, int maxBytes = 64)
    {
        if (payload.Length <= maxBytes)
        {
            return Encoding.UTF8.GetString(payload);
        }

        return $"{Encoding.UTF8.GetString(payload[..maxBytes])}…（共 {payload.Length} 字节）";
    }
}
