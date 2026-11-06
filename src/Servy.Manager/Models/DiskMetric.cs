using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Servy.Manager.Models;

/// <summary>
/// Bindable disk activity telemetry for one service row.
/// </summary>
public sealed class DiskMetric : INotifyPropertyChanged
{
    private string _label = string.Empty;
    private long _totalBytes;
    private long _freeBytes;
    private double? _readBytesPerSecond;
    private double? _writeBytesPerSecond;

    public string Label
    {
        get => _label;
        private set => Set(ref _label, value);
    }

    public long TotalBytes
    {
        get => _totalBytes;
        private set => Set(ref _totalBytes, value);
    }

    public long FreeBytes
    {
        get => _freeBytes;
        private set => Set(ref _freeBytes, value);
    }

    public double? ReadBytesPerSecond
    {
        get => _readBytesPerSecond;
        private set => Set(ref _readBytesPerSecond, value);
    }

    public double? WriteBytesPerSecond
    {
        get => _writeBytesPerSecond;
        private set => Set(ref _writeBytesPerSecond, value);
    }

    public double? OverallBytesPerSecond => ReadBytesPerSecond.HasValue || WriteBytesPerSecond.HasValue
        ? (ReadBytesPerSecond ?? 0) + (WriteBytesPerSecond ?? 0)
        : null;

    public string UsageDisplay => FormatRateWithUnit(OverallBytesPerSecond);
    public string ReadDisplay => FormatRateWithUnit(ReadBytesPerSecond);
    public string WriteDisplay => FormatRateWithUnit(WriteBytesPerSecond);

    public string DetailsDisplay => OverallBytesPerSecond.HasValue
        ? $"Total disk activity (read + write): {FormatRateWithUnit(OverallBytesPerSecond)}"
        : "Total disk activity is unavailable.";
    public string ReadDetailsDisplay => ReadBytesPerSecond.HasValue
        ? $"Disk read activity: {FormatRateWithUnit(ReadBytesPerSecond)}"
        : "Disk read activity is unavailable.";
    public string WriteDetailsDisplay => WriteBytesPerSecond.HasValue
        ? $"Disk write activity: {FormatRateWithUnit(WriteBytesPerSecond)}"
        : "Disk write activity is unavailable.";

    public void Apply(Servy.Manager.Services.DiskUsageMetric metric)
    {
        Label = metric.Label;
        TotalBytes = metric.TotalBytes;
        FreeBytes = metric.FreeBytes;
        ReadBytesPerSecond = metric.ReadBytesPerSecond;
        WriteBytesPerSecond = metric.WriteBytesPerSecond;
        OnPropertyChanged(nameof(OverallBytesPerSecond));
        OnPropertyChanged(nameof(UsageDisplay));
        OnPropertyChanged(nameof(ReadDisplay));
        OnPropertyChanged(nameof(WriteDisplay));
        OnPropertyChanged(nameof(DetailsDisplay));
        OnPropertyChanged(nameof(ReadDetailsDisplay));
        OnPropertyChanged(nameof(WriteDetailsDisplay));
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

    private static string FormatRate(double? bytesPerSecond) =>
        bytesPerSecond.HasValue
            ? (Math.Max(0, bytesPerSecond.Value) / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture)
            : "—";

    private static string FormatRateWithUnit(double? bytesPerSecond) =>
        bytesPerSecond.HasValue ? $"{FormatRate(bytesPerSecond)} MB/s" : "—";
}
