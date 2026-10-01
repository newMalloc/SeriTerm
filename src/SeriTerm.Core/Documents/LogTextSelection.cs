using System.Text;

namespace SeriTerm.Core.Documents;

/// <summary>日志区里的一个"插入点"：第 <paramref name="Row"/> 行的第 <paramref name="Char"/> 个字符之前。</summary>
/// <param name="Row">行号（0 = 选中的第一行）。</param>
/// <param name="Char">行内字符下标。</param>
public readonly record struct RowCaret(int Row, int Char);

/// <summary>
/// 日志区"自由选择"的文本提取。
///
/// 选择的两个端点都是"第几行的第几个字符"，所以可以从某一行的中间开始、
/// 在另一行的中间结束（跨行按字符选择），这正是"想复制哪一段就复制哪一段"需要的粒度。
/// 这里只有纯计算：界面负责把行文本喂进来，因此可以直接单元测试。
/// </summary>
public static class LogTextSelection
{
    /// <summary>行与行之间写进剪贴板的分隔符（与屏幕一致，用 CRLF）。</summary>
    public const string LineSeparator = "\r\n";

    /// <summary>把两个端点归一化成"从上到下、从左到右"。反向拖动（从下往上、从右往左）与正向是同一段文本。</summary>
    public static (RowCaret Start, RowCaret End) Normalize(RowCaret first, RowCaret second)
    {
        if (first.Row != second.Row)
        {
            return first.Row < second.Row ? (first, second) : (second, first);
        }

        return first.Char <= second.Char ? (first, second) : (second, first);
    }

    /// <summary>
    /// 把一段选择拼成剪贴板文本。
    /// </summary>
    /// <param name="rows">选择范围内的行文本，按屏幕顺序排好（第 0 项就是最上面那一行）。</param>
    /// <param name="startChar">第一行从第几个字符开始（含）。</param>
    /// <param name="endChar">最后一行到第几个字符为止（不含）。</param>
    /// <remarks>
    /// 首行之前、末行之后都不取；中间的行整行都取，并且用 <see cref="LineSeparator"/> 连接。
    /// 下标越界一律裁剪到行内（拖动时行会被虚拟化回收，端点可能落在已经不存在的行上）。
    /// </remarks>
    public static string Extract(IReadOnlyList<string> rows, int startChar, int endChar)
    {
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        for (var row = 0; row < rows.Count; row++)
        {
            var text = rows[row] ?? string.Empty;
            var from = Math.Clamp(row == 0 ? startChar : 0, 0, text.Length);
            var to = Math.Clamp(row == rows.Count - 1 ? endChar : text.Length, from, text.Length);

            if (row > 0)
            {
                builder.Append(LineSeparator);
            }

            builder.Append(text, from, to - from);
        }

        return builder.ToString();
    }
}
