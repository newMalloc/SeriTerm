namespace SeriTerm.Core.Search;

/// <summary>
/// 查找关键字的收藏列表（左栏「查找收藏」）。
///
/// 和 <see cref="SeriTerm.Core.Presets.SerialPreset"/> 一样，增删都是"返回新列表、不改动入参"的纯函数：
/// 这样 ViewModel 只负责把结果灌回 <c>ObservableCollection</c>，逻辑本身可以单独测。
///
/// 去重按 <see cref="StringComparer.OrdinalIgnoreCase"/>：搜索本身有"区分大小写"开关，
/// 但收藏列表是给人看的，"OK" 和 "ok" 各存一份只会让人分不清该点哪个。
/// </summary>
public static class SearchFavoriteList
{
    /// <summary>去掉首尾空白；全空白视为没有内容。</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Trim();
    }

    /// <summary>是否已经收藏过（忽略大小写）。</summary>
    public static bool Contains(IReadOnlyList<string> favorites, string? text)
    {
        ArgumentNullException.ThrowIfNull(favorites);

        var normalized = Normalize(text);
        return normalized is not null && IndexOf(favorites, normalized) >= 0;
    }

    /// <summary>
    /// 追加一条收藏（已存在则不重复添加，保持原顺序）。空白输入不产生任何变化。
    /// </summary>
    /// <param name="added">是否真的新增了一条。</param>
    public static IReadOnlyList<string> Add(IReadOnlyList<string> favorites, string? text, out bool added)
    {
        ArgumentNullException.ThrowIfNull(favorites);

        var normalized = Normalize(text);
        added = false;

        if (normalized is null || IndexOf(favorites, normalized) >= 0)
        {
            return [.. favorites];
        }

        var result = new List<string>(favorites.Count + 1);
        result.AddRange(favorites);
        result.Add(normalized);
        added = true;
        return result;
    }

    /// <summary>按内容删除一条收藏；返回是否真的删掉了。</summary>
    public static IReadOnlyList<string> Remove(IReadOnlyList<string> favorites, string? text, out bool removed)
    {
        ArgumentNullException.ThrowIfNull(favorites);

        var normalized = Normalize(text);
        removed = false;

        if (normalized is null)
        {
            return [.. favorites];
        }

        var result = new List<string>(favorites.Count);
        foreach (var favorite in favorites)
        {
            if (!removed && string.Equals(favorite, normalized, StringComparison.OrdinalIgnoreCase))
            {
                removed = true;
                continue;
            }

            result.Add(favorite);
        }

        return removed ? result : [.. favorites];
    }

    /// <summary>清理配置里读出来的脏数据：去掉空白项与重复项，保持原顺序。</summary>
    public static IReadOnlyList<string> Sanitize(IEnumerable<string>? favorites)
    {
        if (favorites is null)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var favorite in favorites)
        {
            var normalized = Normalize(favorite);
            if (normalized is null || IndexOf(result, normalized) >= 0)
            {
                continue;
            }

            result.Add(normalized);
        }

        return result;
    }

    private static int IndexOf(IReadOnlyList<string> favorites, string value)
    {
        for (var i = 0; i < favorites.Count; i++)
        {
            if (string.Equals(favorites[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
