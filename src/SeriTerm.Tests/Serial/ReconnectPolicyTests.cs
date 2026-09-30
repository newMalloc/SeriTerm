using SeriTerm.Core.Serial;

namespace SeriTerm.Tests.Serial;

public class ReconnectPolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 10)]
    [InlineData(6, 10)]
    [InlineData(50, 10)]
    public void 退避时长应按指数增长并封顶(int attempt, double expectedSeconds)
    {
        var policy = ReconnectPolicy.Default;

        Assert.Equal(expectedSeconds, policy.GetDelay(attempt).TotalSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void 非法次数应等同于第一次(int attempt)
    {
        var policy = ReconnectPolicy.Default;

        Assert.Equal(1, policy.GetDelay(attempt).TotalSeconds);
    }

    [Fact]
    public void 应支持自定义退避参数()
    {
        var policy = new ReconnectPolicy { BaseSeconds = 0.5, MaxSeconds = 1 };

        Assert.Equal(0.5, policy.GetDelay(1).TotalSeconds);
        Assert.Equal(1, policy.GetDelay(2).TotalSeconds);
        Assert.Equal(1, policy.GetDelay(9).TotalSeconds);
    }

    [Fact]
    public void 默认策略应一直重试()
        => Assert.Equal(0, ReconnectPolicy.Default.MaxAttempts);
}
