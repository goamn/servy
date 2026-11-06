using System.IO;
using System.Security;
using System.Text.Json;

namespace Servy.Manager.Config;

/// <summary>
/// Stores optional service-grid column visibility without coupling the view to a settings framework.
/// </summary>
internal sealed class ColumnVisibilityPreferences
{
    private readonly string _filePath;
    private readonly Dictionary<string, bool> _visibility = new(StringComparer.Ordinal);

    public ColumnVisibilityPreferences()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Servy",
            "Manager",
            "column-visibility.json"))
    {
    }

    internal ColumnVisibilityPreferences(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public bool TryGetVisibility(string key, out bool isVisible) => _visibility.TryGetValue(key, out isVisible);

    public void SetVisibility(string key, bool isVisible)
    {
        _visibility[key] = isVisible;
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            var values = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_filePath));
            if (values is null) return;

            foreach (var pair in values)
                _visibility[pair.Key] = pair.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException)
        {
            // A broken or inaccessible optional settings file must never prevent the manager from opening.
        }
    }

    private void Save()
    {
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_visibility));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException)
        {
            // Column preferences are best-effort and must not interrupt service management.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    // Ignore temporary-file cleanup failures for this non-essential setting.
                }
            }
        }
    }
}
