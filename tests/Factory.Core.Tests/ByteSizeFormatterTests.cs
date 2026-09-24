namespace Factory.Core.Tests;

public sealed class ByteSizeFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(2355, "2.3 KB")]
    [InlineData(1153434, "1.1 MB")]
    [InlineData(4294967296, "4.0 GB")]
    public void Formats_byte_counts_with_the_appropriate_binary_unit(long byteCount, string expected) =>
        Assert.Equal(expected, ByteSizeFormatter.Format(byteCount));

    [Fact]
    public void Formats_the_largest_long_without_overflowing() =>
        Assert.Equal("8.0 EB", ByteSizeFormatter.Format(long.MaxValue));

    [Fact]
    public void Formats_negative_values_as_bytes() =>
        Assert.Equal("-1 B", ByteSizeFormatter.Format(-1));
}
