namespace SeriTerm.Core.Search;

/// <summary>
/// 查找命中的判定与定位。
///
/// 判定（<see cref="IsHit"/>）和 <see cref="Documents.LogDocument"/> 的搜索是同一份实现：
/// 界面要按同一套规则把命中的字符高亮出来，两边各写一遍迟早会不一致
/// （出现"计数说有命中、行里却没有高亮"这种自相矛盾的界面）。
/// </summary>
public static class SearchMatchFinder
{
    /// <summary>查找使用的比较方式：区分大小写时按序数比较，否则忽略大小写。</summary>
    public static StringComparison Comparison(bool caseSensitive)
        => caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>这一行是否命中查找关键字。</summary>
    public static bool IsHit(string? text, string? query, bool caseSensitive)
        => !string.IsNullOrEmpty(text)
           && !string.IsNullOrEmpty(query)
           && text.Contains(query, Comparison(caseSensitive));

    /// <summary>
    /// 列出关键字在文本里出现的所有位置（不重叠，从左往右扫）。
    /// 界面拿这些区间去画高亮矩形。
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> FindRanges(string? text, string? query, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return [];
        }

        var comparison = Comparison(caseSensitive);
        var ranges = new List<(int Start, int Length)>();
        var index = 0;

        while (index <= text.Length - query.Length)
        {
            var found = text.IndexOf(query, index, comparison);

            if (found < 0)
            {
                break;
            }

            ranges.Add((found, query.Length));
            index = found + query.Length;
        }

        return ranges;
    }
}
