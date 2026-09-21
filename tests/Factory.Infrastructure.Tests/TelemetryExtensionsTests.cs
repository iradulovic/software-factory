namespace Factory.Infrastructure.Tests;

public sealed class TelemetryExtensionsTests
{
    [Fact]
    public void Configured_service_name_overrides_the_default() =>
        Assert.Equal("custom-service", TelemetryExtensions.ResolveServiceName(new TelemetryOptions { ServiceName = "custom-service" }, "Factory.Api"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_service_name_falls_back_to_the_hosts_default(string? configured) =>
        Assert.Equal("Factory.Api", TelemetryExtensions.ResolveServiceName(new TelemetryOptions { ServiceName = configured }, "Factory.Api"));
}
