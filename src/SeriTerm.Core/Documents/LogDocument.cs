using SeriTerm.Core.Pipeline;
using SeriTerm.Core.Search;

namespace SeriTerm.Core.Documents;

/// <summary>一次日志缓冲更新，包含从头部淘汰的行数或整段重建标记。</summary>
public readonly record struct LogContentChange(int RemovedLines, bool IsReset = false);

/// <summary>
/// 日志文档：保存显示行、按行数与字节数双重预算淘汰旧数据，并承载 Ctrl+F 搜索。
///
/// 淘汰为什么要有字节预算：1 Mbps + 20 ms 自动断帧时每帧约 2.5 KB，
/// 只按行数限制的话 20 万行就是 500 MB 内存。
///
/// 本类不依赖 WPF，可直接单元测试。
/// </summary>
public sealed class LogDocument
{
    /// <summary>一次淘汰的行数下限：分块淘汰可避免每个刷新周期都重建集合。</summary>
    private const int MinEvictChunk = 20_000;

    private readonly List<DisplayLine> _matches = [];

    private long _totalBytes;
    private long _droppedLines;
    private string _searchText = string.Empty;
    private bool _caseSensitive;
    private int _currentMatchIndex = -1;
    private DisplayLine? _currentMatch;

    public BulkObservableCollection<DisplayLine> Lines { get; } = [];

    /// <summary>显示行数上限。</summary>
    public int MaxLines { get; set; } = 200_000;

    /// <summary>显示数据字节数上限（按原始字节计）。</summary>
    public long MaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>当前缓冲中的原始字节总数。</summary>
    public long TotalBytes => _totalBytes;

    /// <summary>因超出预算而被淘汰的行数。</summary>
    public long DroppedLines => _droppedLines;

    public IReadOnlyList<DisplayLine> Matches => _matches;

    public int MatchCount => _matches.Count;

    public int CurrentMatchIndex => _currentMatchIndex;

    public string SearchText => _searchText;

    public bool HasSearch => !string.IsNullOrEmpty(_searchText);

    /// <summary>搜索状态（命中集合 / 当前命中）发生变化。</summary>
    public event EventHandler? SearchChanged;

    /// <summary>缓冲完成一次追加、清空或重排后发出，供文本视图按批同步。</summary>
    public event EventHandler<LogContentChange>? ContentChanged;

    /// <summary>追加一批新行并处理淘汰；返回实际追加的行数。</summary>
    public int Append(IReadOnlyList<DisplayLine> newLines)
    {
        if (newLines.Count == 0)
        {
            return 0;
        }

        // 保留逐行 Add 的集合通知契约，文本视图通过 ContentChanged 一次处理整批。
        foreach (var line in newLines)
        {
            Lines.Add(line);
            _totalBytes += line.ByteLength;
        }

        var trim = TrimToLimits(batch: true);

        if (HasSearch)
        {
            MatchNewLines(newLines, Math.Max(0, newLines.Count - Lines.Count));
        }

        ContentChanged?.Invoke(this, new(trim));
        return newLines.Count;
    }

    /// <summary>调整显示缓冲的字节上限，立即淘汰超限的最旧记录。</summary>
    public void SetByteLimit(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        MaxBytes = maxBytes;
        var trim = TrimToLimits(batch: false);
        if (trim > 0)
            ContentChanged?.Invoke(this, new(trim));
    }

    public void Clear()
    {
        ClearMatchFlags();
        _matches.Clear();
        _currentMatch = null;
        _currentMatchIndex = -1;
        Lines.Clear();
        _totalBytes = 0;
        _droppedLines = 0;
        SearchChanged?.Invoke(this, EventArgs.Empty);
        ContentChanged?.Invoke(this, new(0, IsReset: true));
    }

    /// <summary>
    /// 用新的渲染方式重刷所有行（切换 HEX/编码后调用），只发一次 Reset。
    /// 系统提示行（没有原始字节）跳过，否则会被刷成空字符串。
    /// </summary>
    public void Reformat(Func<DisplayLine, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        using (Lines.Defer())
        {
            foreach (var line in Lines)
            {
                if (line.Direction == LineDirection.System)
                {
                    continue;
                }

                line.Text = formatter(line);
            }
        }

        if (HasSearch)
        {
            RescanMatches();
        }

        ContentChanged?.Invoke(this, new(0, IsReset: true));
    }

    // ---------- 搜索 ----------

    public void SetSearch(string? text, bool caseSensitive)
    {
        var normalized = text ?? string.Empty;

        if (string.Equals(normalized, _searchText, StringComparison.Ordinal) && caseSensitive == _caseSensitive)
        {
            return;
        }

        _searchText = normalized;
        _caseSensitive = caseSensitive;
        RescanMatches();
    }

    public void ClearSearch() => SetSearch(string.Empty, false);

    /// <summary>跳到下一处命中（末尾时回到第一处）。</summary>
    public DisplayLine? MoveNextMatch()
    {
        if (_matches.Count == 0)
        {
            return null;
        }

        SetCurrentMatch(_currentMatchIndex < 0 ? 0 : (_currentMatchIndex + 1) % _matches.Count);
        return _currentMatch;
    }

    /// <summary>跳到上一处命中（开头时回到最后一处）。</summary>
    public DisplayLine? MovePreviousMatch()
    {
        if (_matches.Count == 0)
        {
            return null;
        }

        SetCurrentMatch(_currentMatchIndex <= 0 ? _matches.Count - 1 : _currentMatchIndex - 1);
        return _currentMatch;
    }

    private void RescanMatches()
    {
        ClearMatchFlags();
        _matches.Clear();
        _currentMatch = null;
        _currentMatchIndex = -1;

        if (HasSearch)
        {
            foreach (var line in Lines)
            {
                if (!IsHit(line))
                {
                    continue;
                }

                line.IsMatch = true;
                _matches.Add(line);
            }

            if (_matches.Count > 0)
            {
                SetCurrentMatch(0, notify: false);
            }
        }

        SearchChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>新到达的行也要参与匹配，这样搜索才是"实时"的。</summary>
    private void MatchNewLines(IReadOnlyList<DisplayLine> newLines, int startIndex)
    {
        var changed = false;

        for (var i = startIndex; i < newLines.Count; i++)
        {
            var line = newLines[i];
            if (!IsHit(line))
            {
                continue;
            }

            line.IsMatch = true;
            _matches.Add(line);
            changed = true;

            if (_currentMatchIndex < 0)
            {
                SetCurrentMatch(_matches.Count - 1, notify: false);
            }
        }

        if (changed)
        {
            SearchChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool IsHit(DisplayLine line)
        => SearchMatchFinder.IsHit(line.Text, _searchText, _caseSensitive);

    /// <param name="notify">
    /// 是否在这里发 <see cref="SearchChanged"/>。整表重扫（<see cref="RescanMatches"/>）与
    /// 新行命中（<see cref="MatchNewLines"/>）后面本来就要发一次，传 false 免得一次搜索发两遍。
    /// 上下跳转没有别的通知点，必须传 true：界面要靠它重画"当前命中"的行底色与字符高亮。
    /// </param>
    private void SetCurrentMatch(int index, bool notify = true)
    {
        var next = index >= 0 && index < _matches.Count ? _matches[index] : null;

        if (ReferenceEquals(_currentMatch, next))
        {
            _currentMatchIndex = index;

            if (notify)
            {
                SearchChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        if (_currentMatch is not null)
        {
            _currentMatch.IsCurrentMatch = false;
        }

        _currentMatch = next;
        _currentMatchIndex = next is null ? -1 : index;

        if (_currentMatch is not null)
        {
            _currentMatch.IsCurrentMatch = true;
        }

        if (notify)
        {
            SearchChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ClearMatchFlags()
    {
        foreach (var line in _matches)
        {
            line.IsMatch = false;
            line.IsCurrentMatch = false;
        }
    }

    /// <summary>淘汰行之后，把随之一并消失的命中项从命中集合里剔除。</summary>
    private void PruneEvictedMatches(int removed)
    {
        if (removed == 0)
        {
            return;
        }

        _matches.RemoveRange(0, removed);

        if (_currentMatch?.IsMatch != true)
        {
            _currentMatch = null;

            if (_matches.Count > 0)
            {
                SetCurrentMatch(Math.Clamp(_currentMatchIndex - removed, 0, _matches.Count - 1));
            }
            else
            {
                _currentMatchIndex = -1;
            }
        }
        else
        {
            _currentMatchIndex = Math.Max(0, _currentMatchIndex - removed);
        }

        SearchChanged?.Invoke(this, EventArgs.Empty);
    }

    private int TrimToLimits(bool batch)
    {
        if (Lines.Count <= MaxLines && _totalBytes <= MaxBytes)
            return 0;

        var minimum = Math.Max(0, Lines.Count - MaxLines);
        if (batch)
        {
            var chunk = Math.Min(MinEvictChunk, Math.Max(1, MaxLines / 2));
            minimum = Math.Max(minimum, chunk);
        }

        var trim = 0;
        var removedMatches = 0;
        while (trim < Lines.Count && (trim < minimum || _totalBytes > MaxBytes))
        {
            var line = Lines[trim++];
            _totalBytes -= line.ByteLength;
            if (!line.IsMatch)
                continue;

            removedMatches++;
            line.IsMatch = false;
            line.IsCurrentMatch = false;
        }

        _droppedLines += trim;
        Lines.TrimFront(trim);
        PruneEvictedMatches(removedMatches);
        return trim;
    }
}
