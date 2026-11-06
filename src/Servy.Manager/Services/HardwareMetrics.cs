using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using Servy.Core.Logging;

namespace Servy.Manager.Services;

/// <summary>
/// Describes a GPU discovered from <c>nvidia-smi</c>, including device-level utilization and PCIe activity.
/// </summary>
/// <param name="RxMegabytesPerSecond">Device-wide PCIe receive throughput reported by <c>nvidia-smi dmon</c>.</param>
/// <param name="TxMegabytesPerSecond">Device-wide PCIe transmit throughput reported by <c>nvidia-smi dmon</c>.</param>
public sealed record GpuDeviceInfo(
    int Index,
    string Name,
    string Uuid,
    string DriverVersion,
    long? TotalMemoryBytes,
    double? DeviceUtilizationPercent,
    double? PowerDrawWatts,
    double? PowerLimitWatts,
    double? RxMegabytesPerSecond = null,
    double? TxMegabytesPerSecond = null);

/// <summary>
/// Describes one process's usage of one GPU.
/// </summary>
public sealed record ProcessGpuMetric(
    double? UtilizationPercent,
    long? UsedMemoryBytes,
    long? TotalMemoryBytes,
    double? DeviceUtilizationPercent,
    double? EstimatedPowerDrawWatts);

internal sealed record GpuEngineUtilizationSample(
    int ProcessId,
    string AdapterLuid,
    double? UtilizationPercent);

internal sealed record GpuProcessMemorySample(
    int ProcessId,
    string AdapterLuid,
    long? DedicatedBytes,
    long? LocalBytes);

/// <summary>
/// Describes capacity and read/write activity for a logical volume.
/// </summary>
public sealed record DiskUsageMetric(
    string Label,
    long TotalBytes,
    long FreeBytes,
    double UsagePercent,
    double? ReadBytesPerSecond = null,
    double? WriteBytesPerSecond = null)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

/// <summary>
/// Immutable telemetry captured for one refresh cycle.
/// </summary>
public sealed class HardwareMetricsSnapshot
{
    private readonly IReadOnlyDictionary<int, IReadOnlyDictionary<int, ProcessGpuMetric>> _gpuByProcess;

    /// <summary>
    /// Empty snapshot used when optional hardware telemetry has not been initialized or failed.
    /// </summary>
    public static HardwareMetricsSnapshot Empty { get; } = new(
        Array.Empty<GpuDeviceInfo>(),
        Array.Empty<DiskUsageMetric>(),
        new Dictionary<int, IReadOnlyDictionary<int, ProcessGpuMetric>>(),
        0,
        false);

    public HardwareMetricsSnapshot(
        IReadOnlyList<GpuDeviceInfo> gpus,
        IReadOnlyList<DiskUsageMetric> disks,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, ProcessGpuMetric>> gpuByProcess,
        long totalPhysicalMemoryBytes,
        bool nvidiaSmiAvailable)
    {
        Gpus = gpus;
        Disks = disks;
        _gpuByProcess = gpuByProcess;
        TotalPhysicalMemoryBytes = totalPhysicalMemoryBytes;
        NvidiaSmiAvailable = nvidiaSmiAvailable;
    }

    public IReadOnlyList<GpuDeviceInfo> Gpus { get; }
    public IReadOnlyList<DiskUsageMetric> Disks { get; }
    public long TotalPhysicalMemoryBytes { get; }
    public bool NvidiaSmiAvailable { get; }

    public IReadOnlyDictionary<int, ProcessGpuMetric> GetGpuMetrics(int? processId)
    {
        if (processId is null || !_gpuByProcess.TryGetValue(processId.Value, out var metrics))
            return EmptyGpuMetrics.Instance;

        return metrics;
    }

    private sealed class EmptyGpuMetrics : IReadOnlyDictionary<int, ProcessGpuMetric>
    {
        public static readonly EmptyGpuMetrics Instance = new();
        public IEnumerable<int> Keys => Array.Empty<int>();
        public IEnumerable<ProcessGpuMetric> Values => Array.Empty<ProcessGpuMetric>();
        public int Count => 0;
        public ProcessGpuMetric this[int key] => throw new KeyNotFoundException();
        public bool ContainsKey(int key) => false;
        public bool TryGetValue(int key, out ProcessGpuMetric value)
        {
            value = null!;
            return false;
        }
        public IEnumerator<KeyValuePair<int, ProcessGpuMetric>> GetEnumerator() => Enumerable.Empty<KeyValuePair<int, ProcessGpuMetric>>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>
/// Supplies optional machine telemetry used by the Services dashboard.
/// </summary>
public interface IHardwareMetricsProvider
{
    IReadOnlyList<GpuDeviceInfo> Gpus { get; }
    IReadOnlyList<DiskUsageMetric> Disks { get; }
    string Summary { get; }
    bool IsInitialized { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<HardwareMetricsSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Captures Windows storage information and NVIDIA process telemetry without making either
/// dependency mandatory. If <c>nvidia-smi</c> is not installed or access is denied, the UI
/// simply omits GPU columns and continues to show the rest of the dashboard.
/// </summary>
public sealed class HardwareMetricsProvider : IHardwareMetricsProvider
{
    private readonly object _sync = new();
    private IReadOnlyList<GpuDeviceInfo> _gpus = Array.Empty<GpuDeviceInfo>();
    private IReadOnlyList<DiskUsageMetric> _disks = Array.Empty<DiskUsageMetric>();
    private bool _initialized;
    private bool _nvidiaSmiAvailable;
    private Task? _initializationTask;

    public IReadOnlyList<GpuDeviceInfo> Gpus => _gpus;
    public IReadOnlyList<DiskUsageMetric> Disks => _disks;

    public bool IsInitialized
    {
        get
        {
            lock (_sync)
                return _initialized;
        }
    }

    public string Summary
    {
        get
        {
            if (!IsInitialized)
                return "Hardware telemetry deferred";

            var gpuText = _nvidiaSmiAvailable
                ? $"{_gpus.Count} NVIDIA GPU{(_gpus.Count == 1 ? string.Empty : "s")}"
                : "NVIDIA telemetry unavailable";
            return $"{gpuText} • {_disks.Count} volume{(_disks.Count == 1 ? string.Empty : "s")}";
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_initialized) return Task.CompletedTask;
            if (_initializationTask != null) return _initializationTask;

            _initializationTask = InitializeCoreAsync(cancellationToken);
            return _initializationTask;
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (GpuData: QueryGpuDevices(), Disks: QueryDisks());
            }, cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                _gpus = result.GpuData.Devices;
                _nvidiaSmiAvailable = result.GpuData.IsAvailable;
                _disks = result.Disks;
                _initialized = true;
            }

            Logger.Info(
                $"Optional hardware telemetry initialized in {stopwatch.ElapsedMilliseconds} ms. " +
                $"NVIDIA available: {_nvidiaSmiAvailable}; GPUs: {_gpus.Count}; disks: {_disks.Count}.");
        }
        catch (OperationCanceledException)
        {
            Logger.Debug("Optional hardware telemetry initialization was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            // Hardware telemetry is deliberately optional. A driver, WMI, or permission
            // problem must never prevent the service list from appearing.
            lock (_sync)
            {
                _gpus = Array.Empty<GpuDeviceInfo>();
                _disks = Array.Empty<DiskUsageMetric>();
                _nvidiaSmiAvailable = false;
                _initialized = true;
            }

            Logger.Error(
                "Optional hardware telemetry initialization failed; GPU and disk columns will be omitted.",
                ex);
        }
        finally
        {
            lock (_sync)
            {
                _initializationTask = null;
            }
        }
    }

    public async Task<HardwareMetricsSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var gpuData = QueryGpuDevices();
                var devices = gpuData.Devices;
                var disks = QueryDisks();
                var processMetrics = QueryProcessGpuMetrics(devices);
                var totalMemory = GetTotalPhysicalMemoryBytes();

                lock (_sync)
                {
                    // A driver update or GPU hot-plug can change the device list. Keep the
                    // dimensions in sync so the view can rebuild its optional columns.
                    _gpus = devices;
                    _disks = disks;
                    _nvidiaSmiAvailable = gpuData.IsAvailable;
                }

                return new HardwareMetricsSnapshot(
                    devices,
                    disks,
                    processMetrics,
                    totalMemory,
                    gpuData.IsAvailable);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(
                "Optional hardware telemetry capture failed; continuing without hardware metrics for this refresh.",
                ex);
            return HardwareMetricsSnapshot.Empty;
        }
    }

    private static (IReadOnlyList<GpuDeviceInfo> Devices, bool IsAvailable) QueryGpuDevices()
    {
        try
        {
            var result = RunNvidiaSmi(
                "--query-gpu=index,uuid,name,driver_version,memory.total,utilization.gpu,power.draw,power.limit " +
                "--format=csv,noheader,nounits");

            if (result is null)
                return (Array.Empty<GpuDeviceInfo>(), false);

            var devices = new List<GpuDeviceInfo>();
            foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = ParseCsvLine(line);
                if (fields.Count < 8 || !TryParseInt(fields[0], out var index)) continue;

                devices.Add(new GpuDeviceInfo(
                    index,
                    fields[2],
                    fields[1],
                    fields[3],
                    MiBToBytes(ParseNullableDouble(fields[4])),
                    ParseNullableDouble(fields[5]),
                    ParseNullableDouble(fields[6]),
                    ParseNullableDouble(fields[7])));
            }

            var engineUtilization = QueryGpuEngineUtilization();
            var pcieThroughput = QueryGpuPcieThroughput();
            var devicesWithTelemetry = devices
                .Select(device =>
                {
                    var utilizationRates = new List<double?> { device.DeviceUtilizationPercent };
                    if (engineUtilization.TryGetValue(device.Index, out var engineRate))
                        utilizationRates.Add(engineRate);
                    pcieThroughput.TryGetValue(device.Index, out var throughput);

                    return device with
                    {
                        DeviceUtilizationPercent = CombineGpuUtilization(utilizationRates),
                        RxMegabytesPerSecond = throughput.RxMegabytesPerSecond
                            ?? device.RxMegabytesPerSecond,
                        TxMegabytesPerSecond = throughput.TxMegabytesPerSecond
                            ?? device.TxMegabytesPerSecond
                    };
                })
                .OrderBy(g => g.Index)
                .ToArray();

            return (devicesWithTelemetry, true);
        }
        catch (Exception ex)
        {
            Logger.Debug("NVIDIA telemetry is unavailable.", ex);
            return (Array.Empty<GpuDeviceInfo>(), false);
        }
    }

    private static IReadOnlyList<DiskUsageMetric> QueryDisks()
    {
        var disks = new List<DiskUsageMetric>();
        var diskActivity = QueryDiskActivity();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.TotalSize <= 0) continue;
                    var free = Math.Max(0, drive.AvailableFreeSpace);
                    var usage = (drive.TotalSize - free) * 100d / drive.TotalSize;
                    var label = drive.Name.TrimEnd('\\');
                    var hasActivity = diskActivity.TryGetValue(label, out var activity);
                    disks.Add(new DiskUsageMetric(
                        label,
                        drive.TotalSize,
                        free,
                        Math.Clamp(usage, 0, 100),
                        hasActivity ? activity.ReadBytesPerSecond : null,
                        hasActivity ? activity.WriteBytesPerSecond : null));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug("Disk telemetry is unavailable.", ex);
        }

        return disks.OrderBy(d => d.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Dictionary<int, (double? RxMegabytesPerSecond, double? TxMegabytesPerSecond)> QueryGpuPcieThroughput()
    {
        var values = new Dictionary<int, (double? RxMegabytesPerSecond, double? TxMegabytesPerSecond)>();

        try
        {
            // dmon exposes PCIe transfer throughput in MB/s. The equivalent throughput
            // names are not accepted by --query-gpu on all NVIDIA driver versions.
            var output = RunNvidiaSmi("dmon -s t -c 1");
            if (string.IsNullOrWhiteSpace(output)) return values;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith('#')) continue;
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3 || !TryParseInt(fields[0], out var gpuIndex)) continue;

                values[gpuIndex] = (
                    ParseNullableDouble(fields[1]),
                    ParseNullableDouble(fields[2]));
            }
        }
        catch (Exception ex)
        {
            Logger.Debug("NVIDIA PCIe throughput telemetry is unavailable.", ex);
        }

        return values;
    }

    private static Dictionary<int, double> QueryGpuEngineUtilization()
    {
        var values = new Dictionary<int, double>();

        try
        {
            // NVIDIA reports SM, memory, encoder, decoder, JPEG, and OFA utilization
            // as separate engines. Keep the busiest engine as the device-wide 0-100% value.
            var output = RunNvidiaSmi("dmon -s u -c 1");
            if (string.IsNullOrWhiteSpace(output)) return values;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith('#') || line.StartsWith('-')) continue;
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2 || !TryParseInt(fields[0], out var gpuIndex)) continue;

                var utilization = CombineGpuUtilization(fields.Skip(1).Select(ParseNullableDouble));
                if (utilization.HasValue)
                    values[gpuIndex] = utilization.Value;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug("NVIDIA GPU engine utilization telemetry is unavailable.", ex);
        }

        return values;
    }

    internal static double? CombineGpuUtilization(IEnumerable<double?> rates)
    {
        var availableRates = rates
            .Where(rate => rate.HasValue && double.IsFinite(rate.Value))
            .Select(rate => Math.Clamp(rate!.Value, 0, 100))
            .ToArray();

        return availableRates.Length == 0 ? null : availableRates.Max();
    }

    private static Dictionary<string, (double ReadBytesPerSecond, double WriteBytesPerSecond)> QueryDiskActivity()
    {
        var values = new Dictionary<string, (double ReadBytesPerSecond, double WriteBytesPerSecond)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DiskReadBytesPersec, DiskWriteBytesPersec FROM Win32_PerfFormattedData_PerfDisk_LogicalDisk");
            using var results = searcher.Get();

            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var name = item["Name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name) || name.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var read = ParseNullableDouble(item["DiskReadBytesPersec"]?.ToString() ?? string.Empty);
                    var write = ParseNullableDouble(item["DiskWriteBytesPersec"]?.ToString() ?? string.Empty);
                    if (read.HasValue || write.HasValue)
                        values[name] = (read ?? 0, write ?? 0);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug("Disk read/write activity telemetry is unavailable.", ex);
        }

        return values;
    }

    private static IReadOnlyDictionary<int, IReadOnlyDictionary<int, ProcessGpuMetric>> QueryProcessGpuMetrics(
        IReadOnlyList<GpuDeviceInfo> devices)
    {
        if (devices.Count == 0) return new Dictionary<int, IReadOnlyDictionary<int, ProcessGpuMetric>>();

        var byProcess = new Dictionary<int, Dictionary<int, ProcessGpuMetric>>();
        var pmon = QueryProcessUtilization();
        var memory = QueryProcessMemory(devices);
        var windowsUtilization = QueryWindowsProcessUtilization(memory.ProcessIdsByGpu);
        var processUtilizationByGpu = new Dictionary<(int ProcessId, int GpuIndex), double>(windowsUtilization);
        foreach (var pair in pmon)
            processUtilizationByGpu.TryAdd(pair.Key, pair.Value);
        var estimatedPowerByProcessAndGpu = EstimateProcessPowerByGpu(processUtilizationByGpu, devices);

        foreach (var gpuProcessPair in memory.ProcessIdsByGpu)
        {
            var gpuIndex = gpuProcessPair.Key;
            var device = devices.FirstOrDefault(d => d.Index == gpuIndex);
            var processIds = gpuProcessPair.Value
                .Concat(memory.MemoryByProcessAndGpu.Keys
                    .Where(key => key.GpuIndex == gpuIndex)
                    .Select(key => key.ProcessId))
                .Concat(windowsUtilization.Keys
                    .Where(key => key.GpuIndex == gpuIndex)
                    .Select(key => key.ProcessId))
                .Distinct();

            foreach (var processId in processIds)
            {
                if (!byProcess.TryGetValue(processId, out var processMap))
                {
                    processMap = new Dictionary<int, ProcessGpuMetric>();
                    byProcess[processId] = processMap;
                }

                var processGpuKey = (processId, gpuIndex);
                double? utilization = windowsUtilization.TryGetValue(processGpuKey, out var windowsRate)
                    ? windowsRate
                    : pmon.TryGetValue(processGpuKey, out var pmonRate) ? pmonRate : null;
                var usedMemory = memory.MemoryByProcessAndGpu.TryGetValue(processGpuKey, out var memoryBytes)
                    ? memoryBytes
                    : (long?)null;
                processMap[gpuIndex] = new ProcessGpuMetric(
                    utilization,
                    usedMemory,
                    device?.TotalMemoryBytes,
                    device?.DeviceUtilizationPercent,
                    estimatedPowerByProcessAndGpu.TryGetValue(processGpuKey, out var estimatedPower)
                        ? estimatedPower
                        : null);
            }
        }

        foreach (var pair in pmon)
        {
            if (!byProcess.TryGetValue(pair.Key.ProcessId, out var processMap))
            {
                processMap = new Dictionary<int, ProcessGpuMetric>();
                byProcess[pair.Key.ProcessId] = processMap;
            }

            if (processMap.TryGetValue(pair.Key.GpuIndex, out var existing))
            {
                // Prefer Windows' per-process/per-engine reading when available, and use
                // nvidia-smi pmon only as a fallback on drivers that report it.
                if (existing.UtilizationPercent is null)
                    processMap[pair.Key.GpuIndex] = existing with { UtilizationPercent = pair.Value };
                continue;
            }

            var device = devices.FirstOrDefault(d => d.Index == pair.Key.GpuIndex);
            processMap[pair.Key.GpuIndex] = new ProcessGpuMetric(
                pair.Value,
                null,
                device?.TotalMemoryBytes,
                device?.DeviceUtilizationPercent,
                estimatedPowerByProcessAndGpu.TryGetValue(pair.Key, out var estimatedPower)
                    ? estimatedPower
                    : null);
        }

        return byProcess.ToDictionary(
            p => p.Key,
            p => (IReadOnlyDictionary<int, ProcessGpuMetric>)new ReadOnlyDictionary<int, ProcessGpuMetric>(p.Value));
    }

    private static Dictionary<(int ProcessId, int GpuIndex), double> QueryProcessUtilization()
    {
        var values = new Dictionary<(int, int), double>();
        var output = RunNvidiaSmi("pmon -c 1 -s u");
        if (output is null) return values;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseProcessUtilizationLine(line, out var gpu, out var pid, out var utilization) &&
                utilization.HasValue)
            {
                values[(pid, gpu)] = utilization.Value;
            }
        }

        return values;
    }

    internal static bool TryParseProcessUtilizationLine(
        string line,
        out int gpuIndex,
        out int processId,
        out double? utilizationPercent)
    {
        gpuIndex = default;
        processId = default;
        utilizationPercent = null;

        if (line.StartsWith('#') || line.StartsWith('-')) return false;

        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4 ||
            !TryParseInt(fields[0], out gpuIndex) ||
            !TryParseInt(fields[1], out processId))
        {
            return false;
        }

        // pmon -s u reports process-attributed SM, memory, encoder, decoder, JPEG and OFA
        // rates after the GPU/PID/type fields. Use the busiest available engine so a
        // service is not shown as idle when its work is on a non-SM engine.
        utilizationPercent = CombineGpuUtilization(fields.Skip(3).Select(ParseNullableDouble));
        return true;
    }

    internal static bool TryParseGpuAdapterInstanceName(
        string instanceName,
        out int processId,
        out string adapterLuid)
    {
        processId = default;
        adapterLuid = string.Empty;

        const string pidPrefix = "pid_";
        const string luidMarker = "_luid_";
        const string physicalMarker = "_phys_";
        if (!instanceName.StartsWith(pidPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var luidIndex = instanceName.IndexOf(luidMarker, StringComparison.OrdinalIgnoreCase);
        if (luidIndex <= pidPrefix.Length ||
            !TryParseInt(instanceName[pidPrefix.Length..luidIndex], out processId))
        {
            return false;
        }

        var luidStart = luidIndex + luidMarker.Length;
        var physicalIndex = instanceName.IndexOf(physicalMarker, luidStart, StringComparison.OrdinalIgnoreCase);
        if (physicalIndex <= luidStart) return false;

        adapterLuid = instanceName[luidStart..physicalIndex];
        return processId > 0 && !string.IsNullOrWhiteSpace(adapterLuid);
    }

    internal static IReadOnlyDictionary<(int ProcessId, int GpuIndex), double> MapGpuEngineUtilizationToNvidiaDevices(
        IReadOnlyList<GpuEngineUtilizationSample> engineSamples,
        IReadOnlyDictionary<int, HashSet<int>> processIdsByGpu)
    {
        var adapterToGpu = MapGpuAdapterLuidsToNvidiaDevices(
            engineSamples.Select(sample => (sample.ProcessId, sample.AdapterLuid)),
            processIdsByGpu);

        return engineSamples
            .Where(sample =>
                adapterToGpu.TryGetValue(sample.AdapterLuid, out var gpuIndex) &&
                processIdsByGpu.ContainsKey(gpuIndex) &&
                sample.UtilizationPercent.HasValue &&
                double.IsFinite(sample.UtilizationPercent.Value))
            .GroupBy(sample => (sample.ProcessId, GpuIndex: adapterToGpu[sample.AdapterLuid]))
            .ToDictionary(
                group => group.Key,
                group => CombineGpuUtilization(group.Select(sample => sample.UtilizationPercent))!.Value);
    }

    internal static IReadOnlyDictionary<(int ProcessId, int GpuIndex), long> MapGpuProcessMemoryToNvidiaDevices(
        IReadOnlyList<GpuProcessMemorySample> memorySamples,
        IReadOnlyDictionary<int, HashSet<int>> processIdsByGpu)
    {
        var adapterToGpu = MapGpuAdapterLuidsToNvidiaDevices(
            memorySamples.Select(sample => (sample.ProcessId, sample.AdapterLuid)),
            processIdsByGpu);

        return memorySamples
            .Where(sample =>
                adapterToGpu.TryGetValue(sample.AdapterLuid, out var gpuIndex) &&
                processIdsByGpu.ContainsKey(gpuIndex))
            .Select(sample => new
            {
                Key = (sample.ProcessId, GpuIndex: adapterToGpu[sample.AdapterLuid]),
                Bytes = sample.DedicatedBytes is > 0
                    ? sample.DedicatedBytes
                    : sample.LocalBytes ?? sample.DedicatedBytes
            })
            .Where(sample => sample.Bytes is >= 0)
            // One process can have multiple WDDM physical-node instances for an adapter.
            // Taking the largest counter avoids counting the same allocation repeatedly.
            .GroupBy(sample => sample.Key)
            .ToDictionary(group => group.Key, group => group.Max(sample => sample.Bytes!.Value));
    }

    internal static IReadOnlyDictionary<(int ProcessId, int GpuIndex), double> EstimateProcessPowerByGpu(
        IReadOnlyDictionary<(int ProcessId, int GpuIndex), double> processUtilizationByGpu,
        IReadOnlyList<GpuDeviceInfo> devices)
    {
        var estimatedPower = new Dictionary<(int ProcessId, int GpuIndex), double>();
        foreach (var device in devices)
        {
            if (device.PowerDrawWatts is not double devicePower || !double.IsFinite(devicePower) || devicePower < 0)
                continue;

            var processUsage = processUtilizationByGpu
                .Where(pair => pair.Key.GpuIndex == device.Index && double.IsFinite(pair.Value) && pair.Value > 0)
                .ToArray();
            foreach (var pair in processUsage)
                estimatedPower[pair.Key] = devicePower * Math.Clamp(pair.Value, 0, 100) / 100d;
        }

        return estimatedPower;
    }

    private static IReadOnlyDictionary<string, int> MapGpuAdapterLuidsToNvidiaDevices(
        IEnumerable<(int ProcessId, string AdapterLuid)> adapterInstances,
        IReadOnlyDictionary<int, HashSet<int>> processIdsByGpu)
    {
        var adapterProcessIds = adapterInstances
            .Where(instance => instance.ProcessId > 0 && !string.IsNullOrWhiteSpace(instance.AdapterLuid))
            .GroupBy(instance => instance.AdapterLuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(instance => instance.ProcessId).ToHashSet(),
                StringComparer.OrdinalIgnoreCase);

        var adapterToGpu = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapterProcessIds)
        {
            var bestGpuIndex = -1;
            var bestMatchCount = 0;
            var hasAmbiguousMatch = false;
            foreach (var gpu in processIdsByGpu)
            {
                var matchCount = adapter.Value.Count(gpu.Value.Contains);
                if (matchCount > bestMatchCount)
                {
                    bestGpuIndex = gpu.Key;
                    bestMatchCount = matchCount;
                    hasAmbiguousMatch = false;
                }
                else if (matchCount > 0 && matchCount == bestMatchCount)
                {
                    hasAmbiguousMatch = true;
                }
            }

            if (bestMatchCount > 0 && !hasAmbiguousMatch)
                adapterToGpu[adapter.Key] = bestGpuIndex;
        }

        return adapterToGpu;
    }

    private static IReadOnlyDictionary<(int ProcessId, int GpuIndex), double> QueryWindowsProcessUtilization(
        IReadOnlyDictionary<int, HashSet<int>> processIdsByGpu)
    {
        if (!processIdsByGpu.Values.Any(processIds => processIds.Count > 0))
            return new Dictionary<(int ProcessId, int GpuIndex), double>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            searcher.Options.Timeout = TimeSpan.FromSeconds(3);

            using var results = searcher.Get();
            var samples = new List<GpuEngineUtilizationSample>();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var instanceName = item["Name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(instanceName) ||
                        !TryParseGpuAdapterInstanceName(instanceName, out var processId, out var adapterLuid))
                    {
                        continue;
                    }

                    var utilization = ParseNullableDouble(item["UtilizationPercentage"]?.ToString() ?? string.Empty);
                    if (utilization.HasValue)
                        samples.Add(new GpuEngineUtilizationSample(processId, adapterLuid, utilization));
                }
            }

            return MapGpuEngineUtilizationToNvidiaDevices(samples, processIdsByGpu);
        }
        catch (Exception ex)
        {
            Logger.Debug("Windows per-process GPU-engine telemetry is unavailable.", ex);
            return new Dictionary<(int ProcessId, int GpuIndex), double>();
        }
    }

    private static (
        Dictionary<(int ProcessId, int GpuIndex), long> MemoryByProcessAndGpu,
        Dictionary<int, HashSet<int>> ProcessIdsByGpu) QueryProcessMemory(IReadOnlyList<GpuDeviceInfo> devices)
    {
        var memoryByProcessAndGpu = new Dictionary<(int ProcessId, int GpuIndex), long>();
        var processIdsByGpu = new Dictionary<int, HashSet<int>>();
        foreach (var device in devices)
        {
            var processIds = new HashSet<int>();
            processIdsByGpu[device.Index] = processIds;

            var output = RunNvidiaSmi(
                $"-i {device.Index} --query-compute-apps=pid,used_memory --format=csv,noheader,nounits");
            if (string.IsNullOrWhiteSpace(output))
            {
                // Driver versions use both field names. Keep the common query first and
                // fall back to the newer explicit GPU-memory name.
                output = RunNvidiaSmi(
                    $"-i {device.Index} --query-compute-apps=pid,used_gpu_memory --format=csv,noheader,nounits");
            }
            if (output is null) continue;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = ParseCsvLine(line);
                if (fields.Count == 0 || !TryParseInt(fields[0], out var pid)) continue;
                processIds.Add(pid);

                if (fields.Count < 2) continue;
                var used = MiBToBytes(ParseNullableDouble(fields[1]));
                if (!used.HasValue) continue;
                memoryByProcessAndGpu[(pid, device.Index)] = used.Value;
            }
        }

        // In WDDM mode NVIDIA's per-process query often returns a PID with [N/A] memory.
        // Windows' GPU Process Memory performance counter supplies per-process dedicated
        // allocations, and includes an adapter LUID so it can be matched to an NVIDIA GPU.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DedicatedUsage, LocalUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory");
            searcher.Options.Timeout = TimeSpan.FromSeconds(3);
            using var results = searcher.Get();
            var samples = new List<GpuProcessMemorySample>();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var instanceName = item["Name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(instanceName) ||
                        !TryParseGpuAdapterInstanceName(instanceName, out var processId, out var adapterLuid))
                    {
                        continue;
                    }

                    var dedicated = ParseNullableLong(item["DedicatedUsage"]?.ToString());
                    var local = ParseNullableLong(item["LocalUsage"]?.ToString());
                    if (dedicated.HasValue || local.HasValue)
                        samples.Add(new GpuProcessMemorySample(processId, adapterLuid, dedicated, local));
                }
            }

            foreach (var pair in MapGpuProcessMemoryToNvidiaDevices(samples, processIdsByGpu))
                memoryByProcessAndGpu[pair.Key] = pair.Value;
        }
        catch (Exception ex)
        {
            Logger.Debug("Windows per-process GPU-memory telemetry is unavailable.", ex);
        }

        return (memoryByProcessAndGpu, processIdsByGpu);
    }

    private static string? RunNvidiaSmi(string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            }
        };

        try
        {
            if (!process.Start()) return null;
            // Start draining both redirected streams before waiting so a large process list
            // cannot fill a pipe and deadlock the telemetry refresh.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            return process.ExitCode == 0 ? outputTask.GetAwaiter().GetResult() : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static long GetTotalPhysicalMemoryBytes()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (var item in searcher.Get())
            {
                if (long.TryParse(item["TotalPhysicalMemory"]?.ToString(), out var value) && value > 0)
                    return value;
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.Debug("Unable to read physical memory size from WMI.", ex);
        }

        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        foreach (var character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (character == ',' && !quoted)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }

    private static bool TryParseInt(string value, out int result) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static double? ParseNullableDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("N/A", StringComparison.OrdinalIgnoreCase)) return null;
        return double.TryParse(value.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static long? ParseNullableLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result
            : null;

    private static long? MiBToBytes(double? value) =>
        value.HasValue && value.Value >= 0 ? (long)(value.Value * 1024d * 1024d) : null;
}

/// <summary>
/// No-op provider used by design-time and unit-test view models.
/// </summary>
public sealed class NullHardwareMetricsProvider : IHardwareMetricsProvider
{
    public IReadOnlyList<GpuDeviceInfo> Gpus => Array.Empty<GpuDeviceInfo>();
    public IReadOnlyList<DiskUsageMetric> Disks => Array.Empty<DiskUsageMetric>();
    public string Summary => "Hardware telemetry disabled";
    public bool IsInitialized => false;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<HardwareMetricsSnapshot> CaptureAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(HardwareMetricsSnapshot.Empty);
}
