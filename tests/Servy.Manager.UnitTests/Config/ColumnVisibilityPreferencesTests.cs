using Servy.Manager.Config;

namespace Servy.Manager.UnitTests.Config;

public sealed class ColumnVisibilityPreferencesTests
{
    [Fact]
    public void SetVisibility_PersistsValuesAndPreservesOtherColumnKeys()
    {
        var path = NewPreferencesPath();
        try
        {
            var preferences = new ColumnVisibilityPreferences(path);
            preferences.SetVisibility("Name", false);
            preferences.SetVisibility("GpuMetrics[0].PowerDisplay", true);

            var reloaded = new ColumnVisibilityPreferences(path);

            Assert.True(reloaded.TryGetVisibility("Name", out var nameVisible));
            Assert.False(nameVisible);
            Assert.True(reloaded.TryGetVisibility("GpuMetrics[0].PowerDisplay", out var powerVisible));
            Assert.True(powerVisible);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MalformedPreferencesFile_FallsBackToDefaultsAndCanBeReplaced()
    {
        var path = NewPreferencesPath();
        try
        {
            File.WriteAllText(path, "{ not valid json }");

            var preferences = new ColumnVisibilityPreferences(path);

            Assert.False(preferences.TryGetVisibility("Name", out _));
            preferences.SetVisibility("Name", true);
            Assert.True(new ColumnVisibilityPreferences(path).TryGetVisibility("Name", out var visible));
            Assert.True(visible);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string NewPreferencesPath() =>
        Path.Combine(Path.GetTempPath(), $"servy-column-visibility-{Guid.NewGuid():N}.json");
}
