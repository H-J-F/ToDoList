using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using ToDoList.Core;

namespace ToDoList.App.Services;
public static class ThemeService
{
    public static void Apply(AppSettings settings)
    {
        var dark = settings.Mode == "深色" || settings.Mode == "跟随系统" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0;
        var colors = settings.Palette switch
        {
            "薄荷" => new[] { "#DCEDE3", "#E8F2E8", "#B8DCC7", "#315D43" },
            "蜜桃" => new[] { "#F1E1D9", "#F4EAE1", "#EFC9B6", "#784D3C" },
            "薰衣草" => new[] { "#E7E1EF", "#EEE9F2", "#D7C9E7", "#635075" },
            _ => new[] { "#E7EEE5", "#F2EDDF", "#C6DFC9", "#315D43" }
        };
        void Brush(string key, string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); Application.Current.Resources[key] = brush; }
        Brush("CanvasBrush", dark ? "#232E2A" : colors[0]); Brush("SidebarBrush", dark ? "#303A34" : colors[1]);
        Brush("PaperBrush", dark ? "#29332F" : "#FFFCF4"); Brush("InputBrush", dark ? "#354039" : "#F8F2E4");
        Brush("InkBrush", dark ? "#EEEBDD" : "#35483F"); Brush("MutedBrush", dark ? "#B7B9AA" : "#797D70");
        Brush("LineBrush", dark ? "#48534A" : "#E2DFCF"); Brush("HoverBrush", dark ? "#3C4940" : "#ECEEDF");
        Brush("AccentBrush", dark ? settings.Palette switch { "蜜桃" => "#765648", "薰衣草" => "#60546F", _ => "#40664F" } : colors[2]);
        Brush("AccentInkBrush", dark ? "#DAEDD6" : colors[3]); Brush("GreenBrush", dark ? "#83C59A" : "#489468"); Brush("YellowBrush", dark ? "#E8C065" : "#AE7B16");
        var tabs = dark ? new[] { "#49604D", "#6C5944", "#60566C", "#425C66", "#715642" } : new[] { "#CFE1CF", "#F1D7B9", "#E0D8ED", "#CCE0E7", "#F0D4BC" };
        string[] keys = ["TabDoneBrush", "TabOpenBrush", "TabMonthBrush", "TabWeekBrush", "TabTodayBrush"];
        for (int i = 0; i < keys.Length; i++) Brush(keys[i], tabs[i]);
        settings.FontSize = new double[] { 12, 14, 16, 18 }.Contains(settings.FontSize) ? settings.FontSize : 14;
        Application.Current.Resources["BodyFontSize"] = settings.FontSize;
        Application.Current.Resources["TaskPadding"] = new Thickness(8, settings.Density == "紧凑" ? 8 : 15, 8, settings.Density == "紧凑" ? 8 : 15);
    }
}
