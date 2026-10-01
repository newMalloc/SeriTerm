using SeriTerm.Core.Pipeline;

namespace SeriTerm.Tests.Pipeline;

public class DisplayLineTests
{
    private static DisplayLine Line(LineDirection direction = LineDirection.Rx, string text = "SeriTerm loopback test")
        => new(1, new DateTime(2026, 9, 30, 14, 27, 52, 276), direction, [1, 2, 3], text);

    [Fact]
    public void ToDisplayText_带时间戳时应按屏幕上的列序拼接()
    {
        Assert.Equal("14:27:52.276 Rx SeriTerm loopback test", Line().ToDisplayText(withTimestamp: true));
    }

    [Fact]
    public void ToDisplayText_关掉时间戳列时不应带时间()
    {
        Assert.Equal("Tx SeriTerm loopback test", Line(LineDirection.Tx).ToDisplayText(withTimestamp: false));
    }
}
