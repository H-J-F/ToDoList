using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using ToDoList.Core;
using Wpf.Ui.Appearance;

namespace ToDoList.App.Services;
public static class ThemeService
{
    public sealed record AccentOption(string Name, Color Color)
    {
        public SolidColorBrush Brush { get; } = new(Color);
    }
    public static IReadOnlyList<AccentOption> Accents { get; } =
    [
        new("浅蓝", Color.FromRgb(0x33, 0x7E, 0xBB)),
        new("青绿", Color.FromRgb(0x13, 0x8A, 0x83)),
        new("橙色", Color.FromRgb(0xF4, 0xB5, 0x6A)),
        new("紫色", Color.FromRgb(0x80, 0x61, 0xCC))
    ];
    public static event Action? Changed;
    public static bool ReduceMotion { get; private set; }
    public static void Apply(AppSettings settings)
    {
        if (settings.SettingsVersion < 2 || settings.AccentPreset == null)
        {
            settings.AccentPreset = settings.Palette switch { "薄荷" => "青绿", "蜜桃" => "橙色", "薰衣草" => "紫色", _ => "浅蓝" };
            settings.SettingsVersion = 2;
        }
        var dark = settings.Mode == "深色" || settings.Mode == "跟随系统" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0;
        var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.None, false);
        var option = Accents.FirstOrDefault(a => a.Name == settings.AccentPreset) ?? Accents[0];
        settings.AccentPreset = option.Name;
        var accentColor = option.Color;
        var accent = accentColor.ToString();
        ApplicationAccentColorManager.Apply(accentColor, theme, false);
        if (option.Name == "橙色")
        {
            var orange = dark ? Color.FromRgb(0xFF, 0xC9, 0x8A) : accentColor;
            ApplicationAccentColorManager.Apply(accentColor, orange, orange, orange);
        }
        RefreshControlAccentBrushes();
        void Brush(string key, string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); Application.Current.Resources[key] = brush; }
        Brush("CanvasBrush", dark ? "#202020" : "#F3F5F8"); Brush("SidebarBrush", dark ? "#202020" : "#F3F5F8");
        Brush("PaperBrush", dark ? "#282828" : "#FFFFFF"); Brush("InputBrush", dark ? "#303030" : "#F8FAFC");
        Brush("InkBrush", dark ? "#F3F3F3" : "#202B3A"); Brush("MutedBrush", dark ? "#B5B9C0" : "#657387");
        Brush("LineBrush", dark ? "#404247" : "#E6EAF0"); Brush("HoverBrush", dark ? "#363A40" : "#EDF3F9");
        byte Mix(byte c) => (byte)(dark ? c * .25 + 32 * .75 : c * .13 + 255 * .87);
        var wash = $"#{Mix(accentColor.R):X2}{Mix(accentColor.G):X2}{Mix(accentColor.B):X2}";
        Brush("AccentBrush", wash); Brush("AccentInkBrush", accent);
        Brush("GreenBrush", dark ? "#70CF9C" : "#238653"); Brush("YellowBrush", dark ? "#F0C45C" : "#A97812"); Brush("RedBrush", dark ? "#FF8585" : "#CE3B45");
        Brush("TabViewItemHeaderBackgroundSelected", wash);
        Brush("ListBoxItemSelectedBackgroundThemeBrush", wash);
        Brush("ListBoxItemSelectedForegroundThemeBrush", dark ? "#F3F3F3" : "#202B3A");
        Brush("TabViewItemForegroundSelected", dark ? ApplicationAccentColorManager.SecondaryAccent.ToString() : accent);
        settings.FontSize = new double[] { 12, 14, 16, 18 }.Contains(settings.FontSize) ? settings.FontSize : 14;
        Application.Current.Resources["BodyFontSize"] = settings.FontSize;
        Application.Current.Resources["ControlContentThemeFontSize"] = settings.FontSize;
        Application.Current.Resources["TaskPadding"] = new Thickness(12, settings.Density == "紧凑" ? 9 : 15, 12, settings.Density == "紧凑" ? 9 : 15);
        ReduceMotion = settings.ReduceMotion;
        Application.Current.Resources["CheckBoxAnimationDuration"] = new Duration(TimeSpan.FromMilliseconds(ReduceMotion ? 0 : 180));
        Changed?.Invoke();
    }

    private static void RefreshControlAccentBrushes()
    {
        // WPF UI 4.3.0's theme-owned brushes can retain their first resolved Color.
        // Replace the control-facing resources too, so existing templates and open
        // popups update without rebuilding controls or losing keyboard focus.
        var resources = Application.Current.Resources;
        void Refresh(string colorKey, params string[] brushKeys)
        {
            var brush = new SolidColorBrush((Color)resources[colorKey]); brush.Freeze();
            foreach (var key in brushKeys) resources[key] = brush;
        }
        Refresh("AccentFillColorDefault", "AccentButtonBackground");
        Refresh("AccentFillColorSecondary", "AccentButtonBackgroundPointerOver", "CheckBoxCheckBackgroundFillCheckedPointerOver");
        Refresh("AccentFillColorTertiary", "AccentButtonBackgroundPressed", "CheckBoxCheckBackgroundFillCheckedPressed");
        Refresh("TextOnAccentFillColorPrimary", "AccentButtonForeground", "AccentButtonForegroundPointerOver", "TextOnAccentFillColorPrimaryBrush", "CheckBoxCheckGlyphForeground");
        Refresh("TextOnAccentFillColorSecondary", "AccentButtonForegroundPressed", "TextOnAccentFillColorSecondaryBrush");
        Refresh("SystemAccentColorPrimary", "CheckBoxCheckBackgroundFillChecked", "ComboBoxItemPillFillBrush",
            "TextControlFocusedBorderBrush", "ListViewItemPillFillBrush", "NavigationViewSelectionIndicatorForeground", "ProgressBarForeground", "ProgressRingForegroundThemeBrush");
        Refresh("SystemAccentColorSecondary", "ComboBoxBorderBrushFocused", "HyperlinkButtonForeground");
        Refresh("SystemAccentColorTertiary", "HyperlinkButtonForegroundPointerOver", "HyperlinkButtonForegroundPressed");
    }
}
