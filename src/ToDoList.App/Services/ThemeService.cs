using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using ToDoList.Core;
using Wpf.Ui.Appearance;

namespace ToDoList.App.Services;

public static class ThemeService
{
    public sealed class ThemeOption(ThemeDefinition definition)
    {
        public string Id => definition.Id;
        public string Name => definition.Name;
        public string Description => $"{Name} · {definition.Appearance}";
        public IReadOnlyList<SolidColorBrush> Swatches { get; } = new[] { definition.Colors.Sidebar, definition.Colors.Paper, definition.Colors.Accent }
            .Select(Brush).ToArray();
    }
    public static IReadOnlyList<ThemeOption> Themes { get; } = ThemeCatalog.All.Select(t => new ThemeOption(t)).ToArray();
    public static ThemeDefinition Current { get; private set; } = ThemeCatalog.Resolve(ThemeCatalog.DefaultId);
    public static event Action? Changed;
    internal static event Action? MotionChanged;
    private static string? _appliedTheme;
    private static double? _appliedFontSize;
    private static string? _appliedDensity;
    private static bool? _appliedMotion;
    public static void NotifyChanged() => Changed?.Invoke();
    public static bool ReduceMotion { get; private set; }
    private static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static SolidColorBrush Brush(string hex) { var brush = new SolidColorBrush(Color(hex)); brush.Freeze(); return brush; }

    public static void Apply(AppSettings settings)
    {
        ThemeCatalog.Normalize(settings);
        var systemDark = settings.ThemeId == "system" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0;
        var next = ThemeCatalog.Resolve(settings.ThemeId, systemDark);
        var resources = Application.Current.Resources;
        var paletteChanged = _appliedTheme != next.Id;
        if (paletteChanged)
        {
            var c = next.Colors;
            ApplicationThemeManager.Apply(c.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, Wpf.Ui.Controls.WindowBackdropType.None, false);
            ApplicationAccentColorManager.Apply(Color(c.Accent), Color(c.Accent), Color(c.Accent), Color(c.Accent));
            void Set(string hex, params string[] keys) { var brush = Brush(hex); foreach (var key in keys) resources[key] = brush; }
            void Colors(string hex, params string[] keys)
            {
                foreach (var key in keys)
                {
                    resources[key] = Color(hex);
                    resources[key + "Brush"] = Brush(hex);
                    if (ThemeBrushAliases.ByColor.TryGetValue(key, out var aliases)) Set(hex, aliases);
                }
            }
            // Replace static brush aliases too: existing templates and open popups must
            // update without rebuilding controls or losing the editor's selection.
            Colors(c.Canvas, "ApplicationBackgroundColor", "SolidBackgroundFillColorBase", "SolidBackgroundFillColorBaseAlt");
            Colors(c.Paper, "LayerFillColorDefault", "LayerFillColorAlt", "CardBackgroundFillColorDefault", "ControlSolidFillColorDefault", "ControlFillColorInputActive", "AcrylicBackgroundFillColorDefault");
            Colors(c.Input, "ControlFillColorDefault", "CardBackgroundFillColorSecondary", "SolidBackgroundFillColorSecondary", "ControlAltFillColorSecondary");
            Colors(c.Hover, "ControlFillColorSecondary", "SubtleFillColorSecondary", "ControlAltFillColorTertiary");
            Colors(c.Pressed, "ControlFillColorTertiary", "SubtleFillColorTertiary", "ControlAltFillColorQuarternary");
            Colors(c.Input, "ControlFillColorDisabled", "AccentFillColorDisabled");
            Colors(c.Selection, "SolidBackgroundFillColorTertiary");
            Colors(c.Ink, "TextFillColorPrimary", "TextFillColorInverse");
            Colors(c.Muted, "TextFillColorSecondary", "TextFillColorTertiary", "TextPlaceholderColor", "ControlStrongFillColorDefault");
            Colors(ThemeCatalog.Mix(c.Muted, c.Paper, .35), "TextFillColorDisabled", "AccentTextFillColorDisabled", "TextOnAccentFillColorDisabled");
            Colors(c.Border, "ControlStrokeColorDefault", "ControlStrokeColorSecondary", "ControlStrokeColorTertiary", "ControlStrongStrokeColorDefault", "SurfaceStrokeColorDefault", "SurfaceStrokeColorFlyout");
            Colors(c.Line, "CardStrokeColorDefault", "CardStrokeColorDefaultSolid", "DividerStrokeColorDefault", "ControlStrongStrokeColorDisabled");
            Colors(c.Accent, "KeyboardFocusBorderColor", "FocusStrokeColorOuter", "SystemAccentColorPrimary", "SystemAccentColorSecondary", "SystemAccentColorTertiary", "AccentFillColorDefault");
            Colors(c.AccentHover, "AccentFillColorSecondary"); Colors(c.AccentPressed, "AccentFillColorTertiary");
            Colors(c.Paper, "FocusStrokeColorInner");
            Colors(c.OnAccent, "TextOnAccentFillColorPrimary", "TextOnAccentFillColorSecondary", "TextOnAccentFillColorSelectedText");
            Colors(c.Completed, "SystemFillColorSuccess"); Colors(c.Verification, "SystemFillColorCaution"); Colors(c.Danger, "SystemFillColorCritical");

            Set(c.Canvas, "CanvasBrush"); Set(c.Sidebar, "SidebarBrush"); Set(c.Paper, "PaperBrush"); Set(c.Input, "InputBrush");
            Set(c.Ink, "InkBrush", "ListBoxItemSelectedForegroundThemeBrush");
            Set(c.Muted, "MutedBrush", "CompletedInkBrush"); Set(c.Line, "LineBrush"); Set(c.Border, "ControlBorderBrush");
            Set(c.Hover, "HoverBrush"); Set(c.Selection, "AccentBrush", "ListBoxItemSelectedBackgroundThemeBrush", "ComboBoxItemBackgroundSelected", "TabViewItemHeaderBackgroundSelected");
            Set(c.Accent, "AccentInkBrush", "TabViewItemForegroundSelected");
            Set(c.Open, "OpenBrush", "TaskOpenBrush"); Set(c.Completed, "GreenBrush", "TaskCompletedBrush");
            Set(c.Verification, "YellowBrush", "TaskVerificationBrush"); Set(c.Danger, "RedBrush");
            Set(c.Accent, "AccentControlElevationBorderBrush", "AccentButtonBorderBrush", "AccentButtonBorderBrushPointerOver", "AccentButtonBorderBrushPressed");
            Set(c.Border, "ControlElevationBorderBrush", "TextControlElevationBorderBrush", "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "ToggleButtonBorderBrush");
            Set(c.Accent, "TextControlBorderBrushFocused");
            Set(c.Ink, "ButtonForegroundPressed");
            Set(c.OnAccent, "CalendarViewSelectedForeground", "ToggleButtonForegroundCheckedPointerOver");
            Current = next;
            _appliedTheme = next.Id;
        }

        settings.FontSize = new double[] { 12, 14, 16, 18 }.Contains(settings.FontSize) ? settings.FontSize : 14;
        var layoutChanged = _appliedFontSize != settings.FontSize || _appliedDensity != settings.Density;
        if (_appliedFontSize != settings.FontSize)
        {
            resources["BodyFontSize"] = settings.FontSize;
            resources["ControlContentThemeFontSize"] = settings.FontSize;
            _appliedFontSize = settings.FontSize;
        }
        if (_appliedDensity != settings.Density)
        {
            resources["TaskPadding"] = new Thickness(12, settings.Density == "紧凑" ? 9 : 15, 12, settings.Density == "紧凑" ? 9 : 15);
            _appliedDensity = settings.Density;
        }
        if (_appliedMotion != settings.ReduceMotion)
        {
            ReduceMotion = settings.ReduceMotion;
            resources["ThemePopupAnimation"] = ReduceMotion ? System.Windows.Controls.Primitives.PopupAnimation.None : System.Windows.Controls.Primitives.PopupAnimation.Fade;
            resources["CheckBoxAnimationDuration"] = new Duration(TimeSpan.FromMilliseconds(ReduceMotion ? 0 : 180));
            _appliedMotion = settings.ReduceMotion;
            MotionChanged?.Invoke();
        }
        if (paletteChanged || layoutChanged) Changed?.Invoke();
    }
}
