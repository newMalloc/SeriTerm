using SeriTerm.Core.Mcp;

namespace SeriTerm.Tests.Mcp;

public class CallRateLimiterTests
{
    [Fact]
    public void 突发额度用完之后拒绝_并给出剩余为零()
    {
        var limiter = new CallRateLimiter(tokensPerSecond: 1, burst: 3, clock: () => 0);

        Assert.True(limiter.TryAcquire(out var remainingAfterFirst));
        Assert.Equal(2, remainingAfterFirst);
        Assert.True(limiter.TryAcquire(out _));
        Assert.True(limiter.TryAcquire(out var remainingAfterThird));
        Assert.Equal(0, remainingAfterThird);

        Assert.False(limiter.TryAcquire(out var remainingWhenDenied));
        Assert.Equal(0, remainingWhenDenied);
    }

    [Fact]
    public void 时间推进之后_按速率补充令牌()
    {
        var now = 0.0;
        var limiter = new CallRateLimiter(tokensPerSecond: 2, burst: 2, clock: () => now);

        Assert.True(limiter.TryAcquire(out _));
        Assert.True(limiter.TryAcquire(out _));
        Assert.False(limiter.TryAcquire(out _));

        // 0.5 秒按 2 个/秒补充 1 个
        now = 0.5;

        Assert.True(limiter.TryAcquire(out var remaining));
        Assert.Equal(0, remaining);
    }

    [Fact]
    public void 补充不会超过突发上限()
    {
        var now = 0.0;
        var limiter = new CallRateLimiter(tokensPerSecond: 100, burst: 2, clock: () => now);

        Assert.True(limiter.TryAcquire(out _));
        Assert.True(limiter.TryAcquire(out _));

        now = 60;

        Assert.True(limiter.TryAcquire(out var remaining));
        Assert.Equal(1, remaining);
        Assert.True(limiter.TryAcquire(out _));
        Assert.False(limiter.TryAcquire(out _));
    }
}
