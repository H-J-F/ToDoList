using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Runtime.CompilerServices;

namespace ToDoList.App.Services;

public static class InteractionMotion
{
    private static readonly ConditionalWeakTable<ComboBox, object> WiredCombos = new();
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => Motion.Reveal((ContextMenu)s, 0, 140)));
        EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.CheckedEvent, new RoutedEventHandler((s, _) =>
        {
            if (!ThemeService.ReduceMotion) return;
            var check = (CheckBox)s;
            check.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (ThemeService.ReduceMotion && check.IsLoaded) FinishCheckAnimation(check); }));
        }));
        EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            var combo = (ComboBox)s; if (WiredCombos.TryGetValue(combo, out var existing)) return; WiredCombos.Add(combo, new());
            combo.IsEnabledChanged += (_, _) =>
            {
                if (!combo.IsEnabled) combo.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
            };
            combo.DropDownOpened += (_, _) =>
            {
                if (!ThemeService.ReduceMotion) return;
                combo.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (!ThemeService.ReduceMotion || !combo.IsDropDownOpen) return;
                    // Template storyboards use a separate animation layer. Replace their target
                    // transforms so an old clock cannot override the reduced-motion final values.
                    if (combo.Template.FindName("DropDownBorder", combo) is FrameworkElement border)
                        border.RenderTransform = new TranslateTransform();
                    if (combo.Template.FindName("ChevronIcon", combo) is FrameworkElement chevron)
                        chevron.RenderTransform = new RotateTransform(180);
                }));
            };
            combo.DropDownClosed += (_, _) =>
            {
                if (!ThemeService.ReduceMotion) return;
                combo.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (!ThemeService.ReduceMotion || combo.IsDropDownOpen) return;
                    if (combo.Template.FindName("ChevronIcon", combo) is FrameworkElement chevron)
                        chevron.RenderTransform = new RotateTransform();
                }));
            };
        }));
    }
    internal static void FinishCheckAnimation(CheckBox check)
    {
        check.ApplyTemplate();
        foreach (var trigger in check.Template.Triggers.OfType<Trigger>())
            foreach (var action in trigger.EnterActions.OfType<BeginStoryboard>())
                if (action.Name == "SlideIn") action.Storyboard.Remove(check);
        if (check.Template.FindName("ControlIcon", check) is FrameworkElement glyph)
        { glyph.BeginAnimation(FrameworkElement.TagProperty, null); glyph.Tag = 1d; }
    }
}
