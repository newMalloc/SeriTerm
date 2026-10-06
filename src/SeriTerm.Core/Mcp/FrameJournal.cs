using SeriTerm.Core.Pipeline;

namespace SeriTerm.Core.Mcp;

/// <summary>帧缓冲里的一帧。<see cref="Raw"/> 直接复用显示行持有的数组（只读、不复制）。</summary>
public readonly record struct JournalFrame(long Seq, DateTime Time, LineDirection Direction, byte[] Raw, int DisplayLength)
{
    /// <summary>有效载荷（分隔符断帧时不带行尾分隔符）。</summary>
    public ReadOnlySpan<byte> Payload => Raw.AsSpan(0, Math.Clamp(DisplayLength, 0, Raw.Length));
}

/// <summary>
/// 给 AI 读的帧缓冲。为什么不让 AI 直接读日志控件那份数据：
/// <list type="bullet">
/// <item>日志区有"暂停显示"——那是给人看的功能，不该让 AI 跟着瞎；</item>
/// <item>日志区按行数/字节数淘汰，而 AI 是轮询的，游标必须能自己解释"中间丢了哪些"；</item>
/// <item>日志控件绑在界面上，从管道线程读它需要抢界面线程。</item>
/// </list>
/// 所以这里另存一份有界缓冲（默认 2 万帧 / 16 MB），游标与淘汰数一并返回，
/// 让 AI 能明确说出"第 500 帧之前的已经滚掉了"。
/// </summary>
public sealed class FrameJournal
{
    public const int DefaultMaxFrames = 20_000;
    public const long DefaultMaxBytes = 16L * 1024 * 1024;

    /// <summary>一次多淘汰一些，避免每来一帧就搬一次数组。</summary>
    private const int MinEvictChunk = 2_000;

    private readonly object _gate = new();
    private readonly Queue<JournalFrame> _frames = new();

    private TaskCompletionSource<bool>? _signal;
    private long _totalBytes;
    private long _lastSeq;
    private long _totalFrames;
    private long _evictedFrames;

    public FrameJournal(int maxFrames = DefaultMaxFrames, long maxBytes = DefaultMaxBytes)
    {
        MaxFrames = Math.Max(1, maxFrames);
        MaxBytes = Math.Max(1, maxBytes);
    }

    public int MaxFrames { get; }

    public long MaxBytes { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>缓冲里最旧一帧的序号；空缓冲为 0。</summary>
    public long OldestSeq
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count == 0 ? 0 : _frames.Peek().Seq;
            }
        }
    }

    /// <summary>已经收到的最后一帧序号（单调，清空也不回退）；什么都没收到时为 0。</summary>
    public long LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _lastSeq;
            }
        }
    }

    public long TotalFrames
    {
        get
        {
            lock (_gate)
            {
                return _totalFrames;
            }
        }
    }

    /// <summary>因为超出预算（或用户清空日志）而消失的帧数。</summary>
    public long EvictedFrames
    {
        get
        {
            lock (_gate)
            {
                return _evictedFrames;
            }
        }
    }

    /// <summary>
    /// 把一批显示行收进缓冲。在界面线程调用（与日志区同一处），
    /// 但本方法自身加锁，读侧可以随便在别的线程上读。
    /// </summary>
    public void Append(IReadOnlyList<DisplayLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var line in lines)
            {
                // 系统提示行没有原始字节，用 UTF-8 存一份文本，这样 AI 读到的内容与界面一致
                var raw = line.Direction == LineDirection.System && line.Raw.Length == 0
                    ? System.Text.Encoding.UTF8.GetBytes(line.Text)
                    : line.Raw;

                var frame = new JournalFrame(line.Sequence, line.Timestamp, line.Direction, raw, raw.Length);

                _frames.Enqueue(frame);
                _totalBytes += raw.Length;
                _totalFrames++;
                _lastSeq = Math.Max(_lastSeq, line.Sequence);
            }

            Evict();

            _signal?.TrySetResult(true);
            _signal = null;
        }
    }

    /// <summary>清空缓冲（用户点"清空显示"时跟着清），已淘汰计数继续累加，游标不回退。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _evictedFrames += _frames.Count;
            _frames.Clear();
            _totalBytes = 0;
        }
    }

    /// <summary>
    /// 从 <paramref name="since"/>（不含）之后按方向过滤读一页。
    /// 返回的 <c>cursor</c> 是"已经处理到哪儿"：即使某些帧被方向过滤掉了，
    /// 游标也会越过它们，这样同一个客户端反复读不会重复扫同一段。
    /// </summary>
    public (List<JournalFrame> Frames, long Cursor, bool HasMore) Read(
        long since,
        int limit,
        McpFrameDirection direction)
    {
        var result = new List<JournalFrame>(Math.Min(Math.Max(limit, 1), 64));
        var effectiveLimit = Math.Max(1, limit);
        var cursor = since;
        var hasMore = false;

        lock (_gate)
        {
            foreach (var frame in _frames)
            {
                if (frame.Seq <= since)
                {
                    continue;
                }

                if (Matches(frame.Direction, direction))
                {
                    if (result.Count >= effectiveLimit)
                    {
                        hasMore = true;
                        break;
                    }

                    result.Add(frame);
                }

                cursor = frame.Seq;
            }
        }

        return (result, cursor, hasMore);
    }

    /// <summary>等"有新帧"或超时。返回是否真的等到了新帧。</summary>
    public async Task<bool> WaitForNewAsync(long since, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task<bool> waiter;

        lock (_gate)
        {
            if (_lastSeq > since)
            {
                return true;
            }

            _signal ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiter = _signal.Task;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (timeout > TimeSpan.Zero)
        {
            timeoutSource.CancelAfter(timeout);
        }

        try
        {
            await waiter.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 超时或被取消：下面用"有没有新帧"统一给答案
        }

        lock (_gate)
        {
            return _lastSeq > since;
        }
    }

    public static bool Matches(LineDirection direction, McpFrameDirection filter) => filter switch
    {
        McpFrameDirection.Rx => direction == LineDirection.Rx,
        McpFrameDirection.Tx => direction == LineDirection.Tx,
        McpFrameDirection.System => direction == LineDirection.System,
        _ => true,
    };

    public static string ToProtocolText(LineDirection direction) => direction switch
    {
        LineDirection.Rx => "rx",
        LineDirection.Tx => "tx",
        _ => "system",
    };

    private void Evict()
    {
        if (_frames.Count <= MaxFrames && _totalBytes <= MaxBytes)
        {
            return;
        }

        var target = Math.Max(1, _frames.Count - MaxFrames);

        // 字节预算：从最旧的开始累计，直到剩下的字节数落回上限之内
        var byBytes = 0;
        var runningBytes = _totalBytes;

        foreach (var frame in _frames)
        {
            if (runningBytes <= MaxBytes)
            {
                break;
            }

            runningBytes -= frame.Raw.Length;
            byBytes++;
        }

        // 分块淘汰：一帧一帧丢的话，每来一帧都要搬一次队列。
        // 分块大小还要受 MaxFrames 约束，否则预算配得很小时会把整个缓冲清空。
        var chunk = Math.Min(MinEvictChunk, Math.Max(1, MaxFrames / 2));
        var count = Math.Min(_frames.Count, Math.Max(Math.Max(target, byBytes), chunk));

        for (var i = 0; i < count; i++)
        {
            var frame = _frames.Dequeue();
            _totalBytes -= frame.Raw.Length;
            _evictedFrames++;
        }
    }
}
