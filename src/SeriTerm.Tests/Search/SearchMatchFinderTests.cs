using SeriTerm.Core.Search;

namespace SeriTerm.Tests.Search;

public sealed class SearchMatchFinderTests
{
    [Fact]
    public void FindRanges_列出所有出现位置()
    {
        var ranges = SearchMatchFinder.FindRanges("loopback loopback", "loop", caseSensitive: false);

        Assert.Equal([(0, 4), (9, 4)], ranges);
    }

    [Fact]
    public void FindRanges_不区分大小写时忽略大小写()
    {
        var ranges = SearchMatchFinder.FindRanges("LoopBACK", "loopback", caseSensitive: false);

        Assert.Equal([(0, 8)], ranges);
    }

    [Fact]
    public void FindRanges_区分大小写时只认完全一致()
    {
        Assert.Empty(SearchMatchFinder.FindRanges("LoopBACK", "loopback", caseSensitive: true));
        Assert.Equal([(0, 8)], SearchMatchFinder.FindRanges("loopback", "loopback", caseSensitive: true));
    }

    [Fact]
    public void FindRanges_不重叠()
    {
        var ranges = SearchMatchFinder.FindRanges("aaaa", "aa", caseSensitive: false);

        Assert.Equal([(0, 2), (2, 2)], ranges);
    }

    [Theory]
    [InlineData(null, "a")]
    [InlineData("abc", null)]
    [InlineData("", "a")]
    [InlineData("abc", "")]
    [InlineData("abc", "zzz")]
    public void FindRanges_无效输入返回空(string? text, string? query)
    {
        Assert.Empty(SearchMatchFinder.FindRanges(text, query, caseSensitive: false));
    }

    [Fact]
    public void IsHit_与范围查找一致()
    {
        Assert.True(SearchMatchFinder.IsHit("SeriTerm loopback", "LOOP", caseSensitive: false));
        Assert.False(SearchMatchFinder.IsHit("SeriTerm loopback", "LOOP", caseSensitive: true));
        Assert.False(SearchMatchFinder.IsHit(null, "loop", caseSensitive: false));
        Assert.False(SearchMatchFinder.IsHit("loop", null, caseSensitive: false));
    }
}
