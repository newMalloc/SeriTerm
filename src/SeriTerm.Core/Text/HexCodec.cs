using System.Text;

namespace SeriTerm.Core.Text;

/// <summary>十六进制显示与解析。</summary>
public static class HexCodec
{
    private const string Digits = "0123456789ABCDEF";

    /// <summary>格式化为 <c>01 02 FF</c>。</summary>
    public static string Format(ReadOnlySpan<byte> data, char separator = ' ')
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var length = separator == '\0' ? data.Length * 2 : (data.Length * 3) - 1;
        return string.Create(length, (Bytes: data.ToArray(), Separator: separator), static (span, state) =>
        {
            var index = 0;
            for (var i = 0; i < state.Bytes.Length; i++)
            {
                if (i > 0 && state.Separator != '\0')
                {
                    span[index++] = state.Separator;
                }

                span[index++] = Digits[state.Bytes[i] >> 4];
                span[index++] = Digits[state.Bytes[i] & 0x0F];
            }
        });
    }

    /// <summary>格式化为带 ASCII 侧栏的多行文本：<c>0000  48 65 6C 6C 6F  Hello</c>。</summary>
    public static string FormatWithAscii(ReadOnlySpan<byte> data, int bytesPerLine = 16)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        if (bytesPerLine <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerLine));
        }

        var builder = new StringBuilder();
        var bytes = data.ToArray();

        for (var offset = 0; offset < bytes.Length; offset += bytesPerLine)
        {
            var count = Math.Min(bytesPerLine, bytes.Length - offset);

            if (offset > 0)
            {
                builder.Append('\n');
            }

            builder.Append(offset.ToString("X4")).Append("  ");

            for (var i = 0; i < count; i++)
            {
                var value = bytes[offset + i];
                builder.Append(Digits[value >> 4]).Append(Digits[value & 0x0F]).Append(' ');
            }

            // 补齐对齐，保证 ASCII 侧栏竖直对齐
            for (var i = count; i < bytesPerLine; i++)
            {
                builder.Append("   ");
            }

            builder.Append(' ');

            for (var i = 0; i < count; i++)
            {
                var value = bytes[offset + i];
                builder.Append(value is >= 0x20 and < 0x7F ? (char)value : '.');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 宽容解析用户输入的十六进制：允许空格、逗号、短横线、换行分隔，允许 <c>0x</c> 前缀，
    /// 允许连续十六进制串（如 <c>0102FF</c>）。
    /// </summary>
    public static bool TryParse(string? text, out byte[] bytes, out string error)
    {
        bytes = [];
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "十六进制内容为空。";
            return false;
        }

        var cleaned = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c) || c is ',' or '-' or ';')
            {
                continue;
            }

            // 跳过 0x / 0X 前缀
            if (c is '0' && i + 1 < text.Length && (text[i + 1] is 'x' or 'X'))
            {
                i++;
                continue;
            }

            if (!Uri.IsHexDigit(c))
            {
                error = $"十六进制内容包含非法字符 '{c}'（位置 {i + 1}）。";
                return false;
            }

            cleaned.Append(c);
        }

        if (cleaned.Length == 0)
        {
            error = "十六进制内容为空。";
            return false;
        }

        if (cleaned.Length % 2 != 0)
        {
            error = $"十六进制字节必须成对出现，当前有 {cleaned.Length} 个字符。";
            return false;
        }

        var result = new byte[cleaned.Length / 2];

        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (byte)((HexValue(cleaned[i * 2]) << 4) | HexValue(cleaned[(i * 2) + 1]));
        }

        bytes = result;
        return true;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0,
    };
}
