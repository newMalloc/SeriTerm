using System.Text;

namespace SeriTerm.Core.Text;

/// <summary>
/// 去掉 ANSI 转义序列，让终端类设备的输出在日志里可读。
///
/// 终端模式最基本的要求是"不破坏显示"：彩色/光标控制序列（<c>ESC [ 3 1 m</c> 之类）
/// 直接显示出来会变成一堆乱码，所以这里把它们过滤掉（v1 不做着色）。
/// </summary>
public static class AnsiFilter
{
    private const char Escape = '\u001b';

    /// <summary>过滤 ESC 序列与不可见控制字符（保留 \t \n \r）。</summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == Escape)
            {
                i = SkipEscapeSequence(text, i);
                continue;
            }

            // 去掉 BEL / 退格 之类的控制字符，但保留制表与换行
            if (char.IsControl(c) && c is not ('\t' or '\n' or '\r'))
            {
                i++;
                continue;
            }

            builder.Append(c);
            i++;
        }

        return builder.ToString();
    }

    /// <summary>返回跳过整个转义序列后的下一个位置。</summary>
    private static int SkipEscapeSequence(string text, int escapeIndex)
    {
        var i = escapeIndex + 1;

        if (i >= text.Length)
        {
            return i;
        }

        // CSI：ESC [ 参数 中间字节 终止字节
        if (text[i] == '[')
        {
            i++;

            while (i < text.Length && text[i] is >= '\u0020' and <= '\u003F')
            {
                i++;
            }

            while (i < text.Length && text[i] is >= '\u0020' and <= '\u002F')
            {
                i++;
            }

            if (i < text.Length && text[i] is >= '\u0040' and <= '\u007E')
            {
                i++;
            }

            return i;
        }

        // OSC：ESC ] ... BEL 或 ST
        if (text[i] == ']')
        {
            i++;

            while (i < text.Length && text[i] != '\u0007' && text[i] != Escape)
            {
                i++;
            }

            return i < text.Length ? i + 1 : i;
        }

        // 其它两字节序列：ESC + 单字符
        return i + 1;
    }
}
