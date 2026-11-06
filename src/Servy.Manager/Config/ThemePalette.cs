using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Servy.Manager.Config;

/// <summary>
/// Resolves Windows' appearance and updates the manager's shared, dynamic color resources.
/// </summary>
internal static class ThemePalette
{
    private static readonly IReadOnlyDictionary<string, string> LightColors = new Dictionary<string, string>
    {
        ["AppBackgroundBrush"] = "#F5F7FB",
        ["AppSurfaceBrush"] = "#FFFFFF",
        ["AppSurfaceMutedBrush"] = "#EEF2F7",
        ["AppBorderBrush"] = "#DCE3EC",
        ["AppTextBrush"] = "#182230",
        ["AppMutedTextBrush"] = "#667085",
        ["AppAccentBrush"] = "#1769E0",
        ["AppAccentHoverBrush"] = "#0D57C7",
        ["AppAccentPressedBrush"] = "#0A45A0",
        ["RowHoverBrush"] = "#F0F6FF",
        ["RowSelectedBrush"] = "#DCEBFF",
        ["TabHoverBrush"] = "#E8F1FF",
        ["ButtonHoverBrush"] = "#E8F1FF",
        ["ButtonPressedBrush"] = "#D4E5FF",
        ["GridLinesBrush"] = "#EEF1F5",
        ["AlternateRowBrush"] = "#FBFCFE",
        ["GraphStrokeBrush"] = "#1B7F9F",
        ["GraphFillBrush"] = "#80C3ECFA",
        ["GraphBorderBrush"] = "#AAB4C2",
        ["GraphGridPenBrush"] = "#EBEBEB",
        ["PidBadgeBackgroundBrush"] = "#F0F7FA",
        ["PidBadgeForegroundBrush"] = "#1B7F9F",
        ["SuccessBrush"] = "#16845B",
        ["DangerBrush"] = "#C43D45"
    };

    private static readonly IReadOnlyDictionary<string, string> DarkColors = new Dictionary<string, string>
    {
        ["AppBackgroundBrush"] = "#171A21",
        ["AppSurfaceBrush"] = "#20242C",
        ["AppSurfaceMutedBrush"] = "#282E38",
        ["AppBorderBrush"] = "#39414E",
        ["AppTextBrush"] = "#E6EAF0",
        ["AppMutedTextBrush"] = "#A4ADBA",
        ["AppAccentBrush"] = "#78ADFF",
        ["AppAccentHoverBrush"] = "#95BDFF",
        ["AppAccentPressedBrush"] = "#5D98F0",
        ["RowHoverBrush"] = "#272F3A",
        ["RowSelectedBrush"] = "#283D5A",
        ["TabHoverBrush"] = "#2A323E",
        ["ButtonHoverBrush"] = "#303844",
        ["ButtonPressedBrush"] = "#394555",
        ["GridLinesBrush"] = "#343B46",
        ["AlternateRowBrush"] = "#242932",
        ["GraphStrokeBrush"] = "#65C9E7",
        ["GraphFillBrush"] = "#5525A9C5",
        ["GraphBorderBrush"] = "#4B5665",
        ["GraphGridPenBrush"] = "#3B4350",
        ["PidBadgeBackgroundBrush"] = "#24343B",
        ["PidBadgeForegroundBrush"] = "#74CDE4",
        ["SuccessBrush"] = "#55C88A",
        ["DangerBrush"] = "#F0787E"
    };

    public static ApplicationTheme Resolve(ThemePreference preference) =>
        Resolve(preference, ApplicationThemeManager.GetSystemTheme());

    internal static ApplicationTheme Resolve(ThemePreference preference, SystemTheme systemTheme)
    {
        if (preference == ThemePreference.Dark) return ApplicationTheme.Dark;
        if (preference == ThemePreference.Light) return ApplicationTheme.Light;

        if (systemTheme is SystemTheme.HCWhite or SystemTheme.HCBlack or SystemTheme.HC1 or SystemTheme.HC2)
            return ApplicationTheme.HighContrast;

        return systemTheme is SystemTheme.Dark or SystemTheme.CapturedMotion or SystemTheme.Glow
            ? ApplicationTheme.Dark
            : ApplicationTheme.Light;
    }

    public static bool IsDark(ApplicationTheme theme)
    {
        if (theme == ApplicationTheme.Dark) return true;
        if (theme != ApplicationTheme.HighContrast) return false;

        return ApplicationThemeManager.GetSystemTheme() is not SystemTheme.HCWhite;
    }

    public static void Apply(Application application, ApplicationTheme theme)
    {
        Apply(application.Resources, theme);
    }

    public static void Apply(ResourceDictionary resources, ApplicationTheme theme)
    {
        var isDark = IsDark(theme);
        var colors = isDark ? DarkColors : LightColors;

        foreach (var pair in colors)
        {
            var color = (Color)ColorConverter.ConvertFromString(pair.Value)!;
            resources[pair.Key] = new SolidColorBrush(color);
        }

        var fadeColor = (Color)ColorConverter.ConvertFromString(colors["AppSurfaceBrush"])!;
        var fadeBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };
        fadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, fadeColor.R, fadeColor.G, fadeColor.B), 0));
        fadeBrush.GradientStops.Add(new GradientStop(fadeColor, 1));
        resources["ActionsColumnFadeBrush"] = fadeBrush;
    }
}
