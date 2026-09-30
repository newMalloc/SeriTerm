using System.Text;

namespace SeriTerm.Core.Text;

/// <summary>
/// 解析用户输入的字节模式（如断帧分隔符）。
///
/// 支持的写法：
/// <list type="bullet">
/// <item>转义序列：<c>\r</c>、<c>\n</c>、<c>\t</c>、<c>\0</c>、<c>\\</c>、<c>\xNN</c>（两位十六进制）</item>
/// <item>普通文本：按 UTF-8 编码，例如直接输入 <c>OK</c></item>
/// <item>十六进制：以 <c>hex:</c> 开头，例如 <c>hex:0D 0A</c></item>
/// </list>
/// </summary>
public static class BytePatternParser
{
    private const string HexPrefix = "hex:";

    public static bool TryParse(string? text, out byte[] bytes, out string error)
    {
        bytes = [];
        error = string.Empty;

        if (string.IsNullOrEmpty(text))
        {
            error = "内容不能为空。";
            return false;
        }

        if (text.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!HexCodec.TryParse(text[HexPrefix.Length..], out bytes, out error))
            {
                return false;
            }

            return bytes.Length > 0 || Fail(out bytes, out error, "十六进制内容为空。");
        }

        var buffer = new List<byte>(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c != '\\')
            {
                // 普通字符：按 UTF-8 编码（一个汉字会变成 3 个字节）
                buffer.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }

            if (i + 1 >= text.Length)
            {
                return Fail(out bytes, out error, "转义符 '\\' 后面缺少内容。");
            }

            var escape = text[++i];

            switch (escape)
            {
                case 'r':
                    buffer.Add(0x0D);
                    break;
                case 'n':
                    buffer.Add(0x0A);
                    break;
                case 't':
                    buffer.Add(0x09);
                    break;
                case '0':
                    buffer.Add(0x00);
                    break;
                case '\\':
                    buffer.Add(0x5C);
                    break;
                case 'x':
                case 'X':
                    if (i + 2 >= text.Length
                        || !Uri.IsHexDigit(text[i + 1])
                        || !Uri.IsHexDigit(text[i + 2]))
                    {
                        return Fail(out bytes, out error, "\\x 后面需要两位十六进制数字。");
                    }

                    buffer.Add((byte)((HexValue(text[i + 1]) << 4) | HexValue(text[i + 2])));
                    i += 2;
                    break;
                default:
                    return Fail(out bytes, out error, $"不支持的转义序列 '\\{escape}'。");
            }
        }

        if (buffer.Count == 0)
        {
            return Fail(out bytes, out error, "内容不能为空。");
        }

        bytes = [.. buffer];
        return true;
    }

    private static bool Fail(out byte[] bytes, out string error, string message)
    {
        bytes = [];
        error = message;
        return false;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0,
    };
}
