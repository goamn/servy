using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Servy.Manager.Models;

/// <summary>
/// Bindable GPU telemetry for one service row and one GPU.
/// </summary>
public sealed class GpuMetric : INotifyPropertyChanged
{
    private double? _utilizationPercent;
    private long? _usedMemoryBytes;
    private long? _totalMemoryBytes;
    private double? _deviceUtilizationPercent;
    private double? _powerDrawWatts;
    private double? _rxMegabytesPerSecond;
    private double? _txMegabytesPerSecond;

    public double? UtilizationPercent
    {
        get => _utilizationPercent;
        private set => Set(ref _utilizationPercent, value);
    }

    public long? UsedMemoryBytes
    {
        get => _usedMemoryBytes;
        private set => Set(ref _usedMemoryBytes, value);
    }

    public long? TotalMemoryBytes
    {
        get => _totalMemoryBytes;
        private set => Set(ref _totalMemoryBytes, value);
    }

    public double? DeviceUtilizationPercent
    {
        get => _deviceUtilizationPercent;
        private set => Set(ref _deviceUtilizationPercent, value);
    }

    public double? PowerDrawWatts
    {
        get => _powerDrawWatts;
        private set => Set(ref _powerDrawWatts, value);
    }

    public double? RxMegabytesPerSecond
    {
        get => _rxMegabytesPerSecond;
        private set => Set(ref _rxMegabytesPerSecond, value);
    }

    public double? TxMegabytesPerSecond
    {
        get => _txMegabytesPerSecond;
        private set => Set(ref _txMegabytesPerSecond, value);
    }

    /// <summary>
    /// Device-wide utilization reported by NVIDIA, kept separately from this process's utilization.
    /// </summary>
    public string UsageDisplay => UtilizationPercent.HasValue
        ? $"{Math.Clamp(UtilizationPercent.Value, 0, 100).ToString("0.0", CultureInfo.InvariantCulture)}%"
        : "—";

    public string UsageDetailsDisplay => UtilizationPercent.HasValue
        ? $"Highest activity across this service process's available GPU compute/graphics, memory, encoder, decoder, JPEG, and OFA engines: {UsageDisplay}"
        : "Per-process GPU usage is unavailable for this service.";

    public string PowerDisplay => PowerDrawWatts.HasValue
        ? $"{Math.Max(0, PowerDrawWatts.Value).ToString("0.0", CultureInfo.InvariantCulture)} W"
        : "—";

    public string VramDisplay => UsedMemoryBytes.HasValue
        ? $"{FormatBytes(UsedMemoryBytes.Value)} / {FormatBytes(TotalMemoryBytes)}"
        : "—";

    public string DetailsDisplay
    {
        get
        {
            var details = new List<string>();
            if (RxMegabytesPerSecond.HasValue || TxMegabytesPerSecond.HasValue)
                details.Add($"PCIe RX {FormatRate(RxMegabytesPerSecond)} / TX {FormatRate(TxMegabytesPerSecond)} MB/s");
            if (DeviceUtilizationPercent.HasValue) details.Add($"GPU {DeviceUtilizationPercent.Value:0.0}%");
            if (UtilizationPercent.HasValue && UtilizationPercent != DeviceUtilizationPercent)
                details.Add($"process {UtilizationPercent.Value:0.0}%");
            return details.Count == 0 ? "NVIDIA telemetry is unavailable for this process." : string.Join(" • ", details);
        }
    }

    public string PowerDetailsDisplay => PowerDrawWatts.HasValue
        ? $"Estimated {PowerDisplay} from this process's GPU-engine activity and the GPU's board power. Power is reported for the whole GPU, not measured per process."
        : "Per-process GPU power is unavailable because no attributable GPU-engine activity was reported.";

    public void Apply(Servy.Manager.Services.ProcessGpuMetric? metric, Servy.Manager.Services.GpuDeviceInfo? device)
    {
        UtilizationPercent = metric?.UtilizationPercent;
        UsedMemoryBytes = metric?.UsedMemoryBytes;
        TotalMemoryBytes = metric?.TotalMemoryBytes ?? device?.TotalMemoryBytes;
        DeviceUtilizationPercent = metric?.DeviceUtilizationPercent ?? device?.DeviceUtilizationPercent;
        PowerDrawWatts = metric?.EstimatedPowerDrawWatts;
        RxMegabytesPerSecond = device?.RxMegabytesPerSecond;
        TxMegabytesPerSecond = device?.TxMegabytesPerSecond;
        OnPropertyChanged(nameof(UsageDisplay));
        OnPropertyChanged(nameof(UsageDetailsDisplay));
        OnPropertyChanged(nameof(PowerDisplay));
        OnPropertyChanged(nameof(PowerDetailsDisplay));
        OnPropertyChanged(nameof(VramDisplay));
        OnPropertyChanged(nameof(DetailsDisplay));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null) return "—";
        var value = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        var display = (double)value;
        while (unit < units.Length - 1 && display >= 1024)
        {
            display /= 1024;
            unit++;
        }

        return $"{display:0.0} {units[unit]}";
    }

    private static string FormatRate(double? megabytesPerSecond) =>
        megabytesPerSecond.HasValue
            ? Math.Max(0, megabytesPerSecond.Value).ToString("0.00", CultureInfo.InvariantCulture)
            : "—";
}
