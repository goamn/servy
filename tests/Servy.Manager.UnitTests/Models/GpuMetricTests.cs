using Servy.Manager.Models;
using Servy.Manager.Services;

namespace Servy.Manager.UnitTests.Models;

public sealed class GpuMetricTests
{
    [Fact]
    public void Apply_FormatsPerProcessUtilizationPowerAndVram()
    {
        var metric = new GpuMetric();
        var device = new GpuDeviceInfo(
            0,
            "NVIDIA Test GPU",
            "GPU-test",
            "test-driver",
            8L * 1024 * 1024 * 1024,
            37,
            120,
            250,
            1.235,
            12.344);

        metric.Apply(
            new ProcessGpuMetric(82.5, 2L * 1024 * 1024, 8L * 1024 * 1024, 37, 45),
            device);

        Assert.Equal("82.5%", metric.UsageDisplay);
        Assert.Equal("45.0 W", metric.PowerDisplay);
        Assert.Contains("this service process's available GPU compute/graphics", metric.UsageDetailsDisplay);
        Assert.Contains("Estimated 45.0 W", metric.PowerDetailsDisplay);
        Assert.Contains("not measured per process", metric.PowerDetailsDisplay);
        Assert.Contains("PCIe RX 1.24 / TX 12.34 MB/s", metric.DetailsDisplay);
        Assert.Contains("GPU 37.0%", metric.DetailsDisplay);
        Assert.Contains("process 82.5%", metric.DetailsDisplay);
    }

    [Fact]
    public void Apply_WhenGpuTelemetryIsUnavailable_ShowsUnavailableMarkers()
    {
        var metric = new GpuMetric();
        var device = new GpuDeviceInfo(
            0,
            "NVIDIA Test GPU",
            "GPU-test",
            "test-driver",
            null,
            null,
            null,
            null,
            null);

        metric.Apply(null, device);

        Assert.Equal("—", metric.UsageDisplay);
        Assert.Equal("—", metric.PowerDisplay);
    }

    [Fact]
    public void Apply_DoesNotSubstituteDeviceWideUtilizationForMissingProcessUsage()
    {
        var metric = new GpuMetric();
        var device = new GpuDeviceInfo(
            0,
            "NVIDIA Test GPU",
            "GPU-test",
            "test-driver",
            8L * 1024 * 1024 * 1024,
            93,
            100,
            250);

        metric.Apply(
            new ProcessGpuMetric(null, 2L * 1024 * 1024, 8L * 1024 * 1024, 93, null),
            device);

        Assert.Equal("—", metric.UsageDisplay);
        Assert.Contains("Per-process GPU usage is unavailable", metric.UsageDetailsDisplay);
        Assert.Equal("—", metric.PowerDisplay);
        Assert.Contains("Per-process GPU power is unavailable", metric.PowerDetailsDisplay);
    }
}
