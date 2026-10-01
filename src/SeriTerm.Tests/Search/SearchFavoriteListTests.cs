using SeriTerm.Core.Search;

namespace SeriTerm.Tests.Search;

public sealed class SearchFavoriteListTests
{
    [Fact]
    public void Add_AppendsTrimmedEntry()
    {
        var result = SearchFavoriteList.Add(["AT"], "  0x55  ", out var added);

        Assert.True(added);
        Assert.Equal(["AT", "0x55"], result);
    }

    [Fact]
    public void Add_EmptyOrWhitespaceChangesNothing()
    {
        var result = SearchFavoriteList.Add(["AT"], "   ", out var added);

        Assert.False(added);
        Assert.Equal(["AT"], result);
    }

    [Fact]
    public void Add_IsCaseInsensitiveDuplicate()
    {
        var result = SearchFavoriteList.Add(["OK"], "ok", out var added);

        Assert.False(added);
        Assert.Equal(["OK"], result);
    }

    [Fact]
    public void Add_DoesNotMutateInput()
    {
        var source = new List<string> { "AT" };

        var result = SearchFavoriteList.Add(source, "ERR", out _);

        Assert.Equal(["AT"], source);
        Assert.Equal(["AT", "ERR"], result);
    }

    [Fact]
    public void Remove_DeletesMatchingEntryOnly()
    {
        var result = SearchFavoriteList.Remove(["AT", "ERR", "AT2"], "err", out var removed);

        Assert.True(removed);
        Assert.Equal(["AT", "AT2"], result);
    }

    [Fact]
    public void Remove_MissingEntryReportsNotRemoved()
    {
        var result = SearchFavoriteList.Remove(["AT"], "ERR", out var removed);

        Assert.False(removed);
        Assert.Equal(["AT"], result);
    }

    [Fact]
    public void Contains_IgnoresCaseAndSurroundingWhitespace()
    {
        Assert.True(SearchFavoriteList.Contains(["OK"], " ok "));
        Assert.False(SearchFavoriteList.Contains(["OK"], "no"));
        Assert.False(SearchFavoriteList.Contains([], "OK"));
    }

    [Fact]
    public void Sanitize_DropsBlanksAndDuplicates()
    {
        var result = SearchFavoriteList.Sanitize([" AT ", "", "at", "ERR", null!]);

        Assert.Equal(["AT", "ERR"], result);
    }

    [Fact]
    public void Sanitize_NullYieldsEmptyList()
    {
        Assert.Empty(SearchFavoriteList.Sanitize(null));
    }
}
