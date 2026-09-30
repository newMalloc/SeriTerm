using System.IO.Ports;
using SeriTerm.Core.Serial;

namespace SeriTerm.Tests.Serial;

public class PortEnumeratorTests
{
    [Theory]
    [InlineData("COM3", 3)]
    [InlineData("com10", 10)]
    [InlineData("COM5", 5)]
    [InlineData("COM", int.MaxValue)]
    [InlineData("ABC", int.MaxValue)]
    [InlineData("", int.MaxValue)]
    [InlineData(null, int.MaxValue)]
    public void ParsePortNumber_应解析出端口号(string? portName, int expected)
        => Assert.Equal(expected, PortEnumerator.ParsePortNumber(portName));

    [Fact]
    public void ParsePortNumber_排序时COM10应排在COM9之后()
        => Assert.True(PortEnumerator.ParsePortNumber("COM10") > PortEnumerator.ParsePortNumber("COM9"));

    [Fact]
    public void GetPortNames_不应抛出异常且元素不含空白()
    {
        var ports = PortEnumerator.GetPortNames();

        Assert.NotNull(ports);
        Assert.DoesNotContain(ports, string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void IsPortPresent_对空字符串应返回false()
    {
        Assert.False(PortEnumerator.IsPortPresent(string.Empty));
        Assert.False(PortEnumerator.IsPortPresent("   "));
    }
}
