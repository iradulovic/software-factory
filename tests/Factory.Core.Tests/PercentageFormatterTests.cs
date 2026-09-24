namespace Factory.Core.Tests;

public sealed class PercentageFormatterTests
{
    [Theory]
    [InlineData(0, "0%")]
    [InlineData(0.5, "50%")]
    [InlineData(0.333, "33.3%")]
    [InlineData(1, "100%")]
    public void Formats_a_ratio_as_a_compact_percentage(double ratio, string expected)
    {
        var result = PercentageFormatter.Format(ratio);

        Assert.Equal(expected, result);
    }
}
