using Servy.Manager.Services;

namespace Servy.Manager.UnitTests.Services;

public sealed class HardwareMetricsProviderTests
{
    [Fact]
    public void CombineGpuUtilization_UsesTheHighestAvailableEngineRate()
    {
        var usage = HardwareMetricsProvider.CombineGpuUtilization([18, 74.5, null, 42]);

        Assert.Equal(74.5, usage!.Value);
    }

    [Fact]
    public void CombineGpuUtilization_ClampsRatesAndIgnoresNonFiniteOrUnavailableValues()
    {
        var usage = HardwareMetricsProvider.CombineGpuUtilization([-5, double.NaN, double.PositiveInfinity, 140]);

        Assert.Equal(100, usage!.Value);
    }

    [Fact]
    public void CombineGpuUtilization_WhenEveryEngineIsUnavailable_ReturnsNull()
    {
        var usage = HardwareMetricsProvider.CombineGpuUtilization([null, double.NaN]);

        Assert.Null(usage);
    }

    [Fact]
    public void TryParseProcessUtilizationLine_CombinesPerProcessEngineRates()
    {
        var parsed = HardwareMetricsProvider.TryParseProcessUtilizationLine(
            "0 1234 C 12 67 0 3 0 0",
            out var gpuIndex,
            out var processId,
            out var utilization);

        Assert.True(parsed);
        Assert.Equal(0, gpuIndex);
        Assert.Equal(1234, processId);
        Assert.Equal(67, utilization);
    }

    [Fact]
    public void TryParseProcessUtilizationLine_WhenOnlyUnavailableRatesAreReported_ReturnsNoUsage()
    {
        var parsed = HardwareMetricsProvider.TryParseProcessUtilizationLine(
            "1 4321 C - - N/A - - -",
            out var gpuIndex,
            out var processId,
            out var utilization);

        Assert.True(parsed);
        Assert.Equal(1, gpuIndex);
        Assert.Equal(4321, processId);
        Assert.Null(utilization);
    }

    [Fact]
    public void TryParseGpuAdapterInstanceName_ExtractsProcessIdAndAdapterLuid()
    {
        var parsed = HardwareMetricsProvider.TryParseGpuAdapterInstanceName(
            "pid_13196_luid_0x00000000_0x0000D495_phys_0_eng_0_engtype_3D",
            out var processId,
            out var adapterLuid);

        Assert.True(parsed);
        Assert.Equal(13196, processId);
        Assert.Equal("0x00000000_0x0000D495", adapterLuid);
    }

    [Fact]
    public void TryParseGpuAdapterInstanceName_AcceptsGpuProcessMemoryInstanceName()
    {
        var parsed = HardwareMetricsProvider.TryParseGpuAdapterInstanceName(
            "pid_66748_luid_0x00000000_0x0000D495_phys_0",
            out var processId,
            out var adapterLuid);

        Assert.True(parsed);
        Assert.Equal(66748, processId);
        Assert.Equal("0x00000000_0x0000D495", adapterLuid);
    }

    [Fact]
    public void MapGpuEngineUtilizationToNvidiaDevices_KeepsUsageSpecificToEachProcess()
    {
        var samples = new[]
        {
            new GpuEngineUtilizationSample(101, "nvidia-luid", 12),
            new GpuEngineUtilizationSample(101, "nvidia-luid", 43),
            new GpuEngineUtilizationSample(202, "nvidia-luid", 7),
            new GpuEngineUtilizationSample(404, "nvidia-luid", 31),
            new GpuEngineUtilizationSample(202, "nvidia-luid", double.NaN),
            new GpuEngineUtilizationSample(999, "other-luid", 95)
        };
        var nvidiaProcesses = new Dictionary<int, HashSet<int>>
        {
            [0] = [101, 202],
            [1] = [303]
        };

        var mapped = HardwareMetricsProvider.MapGpuEngineUtilizationToNvidiaDevices(samples, nvidiaProcesses);

        Assert.Equal(43, mapped[(101, 0)]);
        Assert.Equal(7, mapped[(202, 0)]);
        Assert.Equal(31, mapped[(404, 0)]);
        Assert.DoesNotContain((999, 0), mapped.Keys);
        Assert.DoesNotContain((101, 1), mapped.Keys);
    }

    [Fact]
    public void MapGpuEngineUtilizationToNvidiaDevices_DoesNotGuessWhenAdapterMatchIsAmbiguous()
    {
        var samples = new[] { new GpuEngineUtilizationSample(101, "shared-luid", 55) };
        var nvidiaProcesses = new Dictionary<int, HashSet<int>>
        {
            [0] = [101],
            [1] = [101]
        };

        var mapped = HardwareMetricsProvider.MapGpuEngineUtilizationToNvidiaDevices(samples, nvidiaProcesses);

        Assert.Empty(mapped);
    }

    [Fact]
    public void MapGpuProcessMemoryToNvidiaDevices_UsesDedicatedBytesAndKeepsProcessesSeparate()
    {
        var samples = new[]
        {
            new GpuProcessMemorySample(101, "nvidia-luid", 2_000, 2_500),
            new GpuProcessMemorySample(101, "nvidia-luid", 1_500, 2_000),
            new GpuProcessMemorySample(202, "nvidia-luid", null, 700),
            new GpuProcessMemorySample(505, "nvidia-luid", 0, 900),
            new GpuProcessMemorySample(404, "nvidia-luid", 1_100, 1_200),
            new GpuProcessMemorySample(303, "other-luid", 900, 1_000)
        };
        var nvidiaProcesses = new Dictionary<int, HashSet<int>>
        {
            [0] = [101, 202],
            [1] = [404]
        };

        var mapped = HardwareMetricsProvider.MapGpuProcessMemoryToNvidiaDevices(samples, nvidiaProcesses);

        Assert.Equal(2_000, mapped[(101, 0)]);
        Assert.Equal(700, mapped[(202, 0)]);
        Assert.Equal(900, mapped[(505, 0)]);
        Assert.Equal(1_100, mapped[(404, 0)]);
        Assert.DoesNotContain((303, 1), mapped.Keys);
        Assert.DoesNotContain((303, 0), mapped.Keys);
    }

    [Fact]
    public void EstimateProcessPowerByGpu_UsesEachProcessesOwnGpuActivity()
    {
        var device = new GpuDeviceInfo(
            0,
            "NVIDIA Test GPU",
            "GPU-test",
            "test-driver",
            8L * 1024 * 1024 * 1024,
            60,
            120,
            250);
        var processUsage = new Dictionary<(int ProcessId, int GpuIndex), double>
        {
            [(101, 0)] = 40,
            [(202, 0)] = 20,
            [(303, 1)] = 95
        };

        var estimates = HardwareMetricsProvider.EstimateProcessPowerByGpu(processUsage, [device]);

        Assert.Equal(48, estimates[(101, 0)], precision: 6);
        Assert.Equal(24, estimates[(202, 0)], precision: 6);
        Assert.DoesNotContain((303, 1), estimates.Keys);
    }

    [Fact]
    public void EstimateProcessPowerByGpu_DoesNotAssignWholeGpuPowerWhenNoProcessIsActive()
    {
        var device = new GpuDeviceInfo(0, "NVIDIA Test GPU", "GPU-test", "test-driver", null, 0, 120, 250);

        var estimates = HardwareMetricsProvider.EstimateProcessPowerByGpu(
            new Dictionary<(int ProcessId, int GpuIndex), double> { [(101, 0)] = 0 },
            [device]);

        Assert.Empty(estimates);
    }
}
