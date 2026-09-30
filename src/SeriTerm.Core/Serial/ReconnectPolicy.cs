namespace SeriTerm.Core.Serial;

/// <summary>
/// 自动重连的退避策略：默认 1 → 2 → 4 → 8 → 10 → 10…（上限 10 秒）。
/// 纯函数，便于单测。
/// </summary>
public sealed record ReconnectPolicy
{
    public static ReconnectPolicy Default { get; } = new();

    /// <summary>首次重连等待秒数。</summary>
    public double BaseSeconds { get; init; } = 1;

    /// <summary>单次等待上限秒数。</summary>
    public double MaxSeconds { get; init; } = 10;

    /// <summary>最大尝试次数；0 表示一直重试。</summary>
    public int MaxAttempts { get; init; }

    /// <summary>取第 <paramref name="attempt"/> 次（从 1 开始）重连前的等待时长。</summary>
    public TimeSpan GetDelay(int attempt)
    {
        if (attempt <= 0)
        {
            attempt = 1;
        }

        var seconds = Math.Min(BaseSeconds * Math.Pow(2, attempt - 1), MaxSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
