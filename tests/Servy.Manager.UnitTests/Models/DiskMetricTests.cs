using Servy.Manager.Models;
using Servy.Manager.Services;

namespace Servy.Manager.UnitTests.Models;

public sealed class DiskMetricTests
{
    [Fact]
    public void Apply_FormatsOverallReadPlusWriteAndSeparateRatesWithTwoDecimals()
    {
        var metric = new DiskMetric();

        metric.Apply(new DiskUsageMetric(
            "C:",
            500_000_000_000,
            250_000_000_000,
            50,
            1_234_567,
            12_345_678));

        Assert.Equal(13_580_245d, metric.OverallBytesPerSecond!.Value);
        Assert.Equal("13.58 MB/s", metric.UsageDisplay);
        Assert.Equal("1.23 MB/s", metric.ReadDisplay);
        Assert.Equal("12.35 MB/s", metric.WriteDisplay);
        Assert.Equal("Total disk activity (read + write): 13.58 MB/s", metric.DetailsDisplay);
        Assert.Equal("Disk read activity: 1.23 MB/s", metric.ReadDetailsDisplay);
        Assert.Equal("Disk write activity: 12.35 MB/s", metric.WriteDetailsDisplay);
    }

    [Fact]
    public void Apply_WhenOnlyOneRateIsAvailable_UsesAvailableRateForOverallActivity()
    {
        var metric = new DiskMetric();

        metric.Apply(new DiskUsageMetric("C:", 500_000_000_000, 250_000_000_000, 50, 1_234_567));

        Assert.Equal("1.23 MB/s", metric.UsageDisplay);
        Assert.Equal("1.23 MB/s", metric.ReadDisplay);
        Assert.Equal("—", metric.WriteDisplay);
    }

    [Fact]
    public void Apply_WhenActivityIsUnavailable_ShowsUnavailableMarker()
    {
        var metric = new DiskMetric();

        metric.Apply(new DiskUsageMetric("C:", 500_000_000_000, 250_000_000_000, 50));

        Assert.Equal("—", metric.UsageDisplay);
        Assert.Equal("—", metric.ReadDisplay);
        Assert.Equal("—", metric.WriteDisplay);
        Assert.Equal("Total disk activity is unavailable.", metric.DetailsDisplay);
        Assert.Equal("Disk read activity is unavailable.", metric.ReadDetailsDisplay);
        Assert.Equal("Disk write activity is unavailable.", metric.WriteDetailsDisplay);
    }
}
