using Servy.Manager.Config;
using Wpf.Ui.Appearance;

namespace Servy.Manager.UnitTests.Config;

public sealed class ThemePreferenceStoreTests
{
    [Fact]
    public void ThemePreferenceStore_PersistsUserChoice()
    {
        var path = NewPath();
        try
        {
            new ThemePreferenceStore(path).SetPreference(ThemePreference.Dark);

            Assert.Equal(ThemePreference.Dark, new ThemePreferenceStore(path).Preference);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ThemePreferenceStore_UsesSystemModeForMissingOrInvalidPreference()
    {
        var path = NewPath();
        try
        {
            Assert.Equal(ThemePreference.System, new ThemePreferenceStore(path).Preference);
            File.WriteAllText(path, "unknown-theme");
            Assert.Equal(ThemePreference.System, new ThemePreferenceStore(path).Preference);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(SystemTheme.Dark, ApplicationTheme.Dark)]
    [InlineData(SystemTheme.CapturedMotion, ApplicationTheme.Dark)]
    [InlineData(SystemTheme.Glow, ApplicationTheme.Dark)]
    [InlineData(SystemTheme.Light, ApplicationTheme.Light)]
    [InlineData(SystemTheme.Flow, ApplicationTheme.Light)]
    [InlineData(SystemTheme.HCWhite, ApplicationTheme.HighContrast)]
    [InlineData(SystemTheme.HCBlack, ApplicationTheme.HighContrast)]
    [InlineData(SystemTheme.HC1, ApplicationTheme.HighContrast)]
    [InlineData(SystemTheme.HC2, ApplicationTheme.HighContrast)]
    public void ThemePalette_ResolvesSystemMode(SystemTheme systemTheme, ApplicationTheme expected)
    {
        Assert.Equal(expected, ThemePalette.Resolve(ThemePreference.System, systemTheme));
    }

    [Theory]
    [InlineData("Dark", SystemTheme.Light, ApplicationTheme.Dark)]
    [InlineData("Light", SystemTheme.Dark, ApplicationTheme.Light)]
    public void ThemePalette_ExplicitChoiceOverridesSystem(
        string preference,
        SystemTheme systemTheme,
        ApplicationTheme expected)
    {
        Assert.Equal(expected, ThemePalette.Resolve(Enum.Parse<ThemePreference>(preference), systemTheme));
    }

    private static string NewPath() =>
        Path.Combine(Path.GetTempPath(), $"servy-theme-{Guid.NewGuid():N}.txt");
}
