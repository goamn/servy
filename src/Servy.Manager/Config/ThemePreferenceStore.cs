using System.IO;
using System.Security;

namespace Servy.Manager.Config;

internal enum ThemePreference
{
    System,
    Light,
    Dark
}

/// <summary>
/// Persists the user's theme choice. System is the default so new installs follow Windows.
/// </summary>
internal sealed class ThemePreferenceStore
{
    private readonly string _filePath;

    public ThemePreferenceStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Servy",
            "Manager",
            "theme-preference.txt"))
    {
    }

    internal ThemePreferenceStore(string filePath)
    {
        _filePath = filePath;
        Preference = Load();
    }

    public ThemePreference Preference { get; private set; }

    public void SetPreference(ThemePreference preference)
    {
        Preference = Enum.IsDefined(preference) ? preference : ThemePreference.System;
        Save();
    }

    private ThemePreference Load()
    {
        try
        {
            if (File.Exists(_filePath) &&
                Enum.TryParse<ThemePreference>(File.ReadAllText(_filePath).Trim(), ignoreCase: true, out var preference) &&
                Enum.IsDefined(preference))
            {
                return preference;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Theme preference is optional; fall back to following Windows if it cannot be read.
        }

        return ThemePreference.System;
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
            File.WriteAllText(temporaryPath, Preference.ToString());
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // A setting write failure must not interfere with using the manager.
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
                    // Ignore temporary-file cleanup failures for this non-essential preference.
                }
            }
        }
    }
}
