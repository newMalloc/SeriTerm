using System.Diagnostics;

namespace SeriTerm.Core.Mcp;

/// <summary>
/// 完全权限下的动作限速（令牌桶）。默认 3 次/秒、允许 6 次的突发：
/// 够一个"看日志 → 发一条命令 → 再看日志"的闭环用，又不至于让一个跑偏的模型
/// 用每秒几百条命令把设备刷死。时钟可注入，便于单测不靠 sleep。
/// </summary>
public sealed class CallRateLimiter
{
    public const double DefaultTokensPerSecond = 3.0;
    public const double DefaultBurst = 6.0;

    private readonly double _tokensPerSecond;
    private readonly double _burst;
    private readonly Func<double> _clock;
    private readonly object _gate = new();

    private double _tokens;
    private double _lastRefill;

    public CallRateLimiter(
        double tokensPerSecond = DefaultTokensPerSecond,
        double burst = DefaultBurst,
        Func<double>? clock = null)
    {
        _tokensPerSecond = Math.Max(0.01, tokensPerSecond);
        _burst = Math.Max(1, burst);
        _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _tokens = _burst;
        _lastRefill = _clock();
    }

    /// <summary>当前可用令牌数（向下取整），用于在返回结果里告诉 AI 还剩多少余量。</summary>
    public int Available
    {
        get
        {
            lock (_gate)
            {
                Refill();
                return (int)Math.Floor(_tokens);
            }
        }
    }

    public bool TryAcquire(out int remaining)
    {
        lock (_gate)
        {
            Refill();

            if (_tokens < 1)
            {
                remaining = 0;
                return false;
            }

            _tokens -= 1;
            remaining = (int)Math.Floor(_tokens);
            return true;
        }
    }

    private void Refill()
    {
        var now = _clock();
        var elapsed = now - _lastRefill;

        if (elapsed <= 0)
        {
            return;
        }

        _lastRefill = now;
        _tokens = Math.Min(_burst, _tokens + (elapsed * _tokensPerSecond));
    }
}
