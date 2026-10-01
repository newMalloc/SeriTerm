using SeriTerm.Core.Pipeline;
using SeriTerm.Core.Search;

namespace SeriTerm.Core.Documents;

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

    /// <summary>追加一批新行并处理淘汰；返回实际追加的行数。</summary>
    public int Append(IReadOnlyList<DisplayLine> newLines)
    {
        if (newLines.Count == 0)
        {
            return 0;
        }

        // 追加要逐行走 Add 通知，**不能**攒成一个 Reset。
        //
        // 原来这里是 using (Lines.Defer())：一批只发一次 Reset，看着很省。实测恰恰相反——
        // Reset 会让 WPF 的虚拟化列表把已经生成的行容器**全部丢掉重建**（每行一个只读文本框、
        // 一层高亮 Canvas、时间/方向两列），每次追加都要重排整个可视区。10 次/秒的定时发送
        // （每次 Tx+Rx 两行）就够把界面线程跑满：进程 CPU 占空比 ~100%，点一下要等 0.1~2 秒，
        // 表现就是"数据照发，界面像卡死"。
        //
        // 换成 Add 之后，WPF 只为新行生成容器，同一帧里的多次失效会合并成一次布局，
        // 开销与"新增多少行"成正比，而不是与"屏幕上显示多少行"成正比。
        // 真正需要 Reset 的只有整段变化：淘汰旧行（下面 TrimFront）和切换 HEX/编码重刷（Reformat）。
        foreach (var line in newLines)
        {
            Lines.Add(line);
            _totalBytes += line.ByteLength;
        }

        var trim = ComputeTrimCount();

        if (trim > 0)
        {
            for (var i = 0; i < trim && i < Lines.Count; i++)
            {
                _totalBytes -= Lines[i].ByteLength;
            }

            _droppedLines += trim;
            Lines.TrimFront(trim);

            // 淘汰后需要把已经不在缓冲里的命中项清掉
            PruneEvictedMatches();
        }

        if (HasSearch)
        {
            MatchNewLines(newLines);
        }

        return newLines.Count;
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
    private void MatchNewLines(IReadOnlyList<DisplayLine> newLines)
    {
        var changed = false;

        foreach (var line in newLines)
        {
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
    private void PruneEvictedMatches()
    {
        if (_matches.Count == 0)
        {
            return;
        }

        var firstLiveSequence = Lines.Count > 0 ? Lines[0].Sequence : long.MaxValue;
        var removed = 0;

        while (removed < _matches.Count && _matches[removed].Sequence < firstLiveSequence)
        {
            _matches[removed].IsMatch = false;
            _matches[removed].IsCurrentMatch = false;
            removed++;
        }

        if (removed == 0)
        {
            return;
        }

        _matches.RemoveRange(0, removed);

        if (_currentMatch is null || _currentMatch.Sequence < firstLiveSequence)
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

    private int ComputeTrimCount()
    {
        var overLines = Lines.Count - MaxLines;
        var overBytes = _totalBytes - MaxBytes;

        if (overLines <= 0 && overBytes <= 0)
        {
            return 0;
        }

        var needed = Math.Max(overLines, 0);

        if (overBytes > 0 && Lines.Count > 0)
        {
            var averageBytes = Math.Max(1, _totalBytes / Lines.Count);
            needed = Math.Max(needed, (int)Math.Min(Lines.Count, (overBytes / averageBytes) + 1));
        }

        // 分块淘汰：一次多淘汰一些，避免每个刷新周期都重建集合。
        // 分块大小还要受 MaxLines 约束，否则 MaxLines 配得很小时会把整个缓冲清空。
        var chunk = Math.Min(MinEvictChunk, Math.Max(1, MaxLines / 2));

        return (int)Math.Min(Lines.Count, Math.Max(needed, chunk));
    }
}
