using System.Globalization;
using SeriTerm.Core.Send;

namespace SeriTerm.Tests.Send;

public class SendIntervalTests
{
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e100")]
    [InlineData("0")]
    [InlineData("0.049")]
    [InlineData("86401")]
    [InlineData("")]
    public void InvalidIntervalsAreRejected(string input) => Assert.False(SendInterval.TryParse(input, out _));

    [Theory]
    [InlineData("0.05", 0.05)]
    [InlineData("1.5", 1.5)]
    [InlineData("86400", 86400)]
    public void DecimalSyntaxIsIndependentOfWindowsCulture(string input, double expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.True(SendInterval.TryParse(input, out var result));
            Assert.Equal(expected, result);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
