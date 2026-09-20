using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using ToDoList.Core;
using Wpf.Ui.Appearance;

namespace ToDoList.App.Services;
public static class ThemeService
{
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
        var accent = settings.AccentPreset switch { "青绿" => "#138A83", "橙色" => "#C56A24", "紫色" => "#8061CC", _ => "#337EBB" };
        ApplicationAccentColorManager.Apply((Color)ColorConverter.ConvertFromString(accent), theme, false);
        void Brush(string key, string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); Application.Current.Resources[key] = brush; }
        Brush("CanvasBrush", dark ? "#202020" : "#F3F5F8"); Brush("SidebarBrush", dark ? "#202020" : "#F3F5F8");
        Brush("PaperBrush", dark ? "#282828" : "#FFFFFF"); Brush("InputBrush", dark ? "#303030" : "#F8FAFC");
        Brush("InkBrush", dark ? "#F3F3F3" : "#202B3A"); Brush("MutedBrush", dark ? "#B5B9C0" : "#657387");
        Brush("LineBrush", dark ? "#404247" : "#E6EAF0"); Brush("HoverBrush", dark ? "#363A40" : "#EDF3F9");
        var accentColor = (Color)ColorConverter.ConvertFromString(accent);
        byte Mix(byte c) => (byte)(dark ? c * .25 + 32 * .75 : c * .13 + 255 * .87);
        var wash = $"#{Mix(accentColor.R):X2}{Mix(accentColor.G):X2}{Mix(accentColor.B):X2}";
        Brush("AccentBrush", wash); Brush("AccentInkBrush", accent);
        Brush("GreenBrush", dark ? "#70CF9C" : "#238653"); Brush("YellowBrush", dark ? "#F0C45C" : "#A97812"); Brush("RedBrush", dark ? "#FF8585" : "#CE3B45");
        Brush("TabViewItemHeaderBackgroundSelected", wash);
        Brush("ListBoxItemSelectedBackgroundThemeBrush", wash);
        Brush("ListBoxItemSelectedForegroundThemeBrush", dark ? "#F3F3F3" : "#202B3A");
        Brush("TabViewItemForegroundSelected", dark ? "#B8DEFA" : accent);
        settings.FontSize = new double[] { 12, 14, 16, 18 }.Contains(settings.FontSize) ? settings.FontSize : 14;
        Application.Current.Resources["BodyFontSize"] = settings.FontSize;
        Application.Current.Resources["ControlContentThemeFontSize"] = settings.FontSize;
        Application.Current.Resources["TaskPadding"] = new Thickness(12, settings.Density == "紧凑" ? 9 : 15, 12, settings.Density == "紧凑" ? 9 : 15);
        ReduceMotion = settings.ReduceMotion;
        Application.Current.Resources["CheckBoxAnimationDuration"] = new Duration(TimeSpan.FromMilliseconds(ReduceMotion ? 0 : 180));
        Changed?.Invoke();
    }
}
