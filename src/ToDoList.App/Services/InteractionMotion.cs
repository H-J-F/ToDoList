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
        EventManager.RegisterClassHandler(typeof(Button), UIElement.MouseEnterEvent, new MouseEventHandler(Hover));
        EventManager.RegisterClassHandler(typeof(Button), UIElement.MouseLeaveEvent, new MouseEventHandler(Hover));
        EventManager.RegisterClassHandler(typeof(Button), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Press));
        EventManager.RegisterClassHandler(typeof(Button), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Press));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => Motion.Reveal((ContextMenu)s, 0, 140)));
        EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.CheckedEvent, new RoutedEventHandler((s, _) =>
        {
            if (!ThemeService.ReduceMotion) return;
            var check = (CheckBox)s;
            check.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (check.Template.FindName("ControlIcon", check) is FrameworkElement glyph)
                { glyph.BeginAnimation(FrameworkElement.TagProperty, null); glyph.Tag = 1d; }
            }));
        }));
        EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            var combo = (ComboBox)s; if (WiredCombos.TryGetValue(combo, out var existing)) return; WiredCombos.Add(combo, new());
            combo.DropDownOpened += (_, _) =>
            {
                if (!ThemeService.ReduceMotion) return;
                combo.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (combo.Template.FindName("DropDownBorder", combo) is FrameworkElement border && border.RenderTransform is TranslateTransform transform)
                    { transform.BeginAnimation(TranslateTransform.YProperty, null); transform.Y = 0; }
                }));
            };
        }));
    }
    private static void Hover(object sender, MouseEventArgs e)
    {
        var button = (Button)sender;
        button.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(button.IsMouseOver ? .85 : 1,
            TimeSpan.FromMilliseconds(ThemeService.ReduceMotion ? 0 : 140)));
        if (!button.IsMouseOver && button.RenderTransform is ScaleTransform transform) AnimateScale(transform, 1);
    }
    private static void Press(object sender, MouseButtonEventArgs e)
    {
        if (ThemeService.ReduceMotion) return;
        var button = (Button)sender;
        if (button.RenderTransform is not ScaleTransform transform)
        { button.RenderTransformOrigin = new Point(.5, .5); button.RenderTransform = transform = new ScaleTransform(1, 1); }
        AnimateScale(transform, e.ButtonState == MouseButtonState.Pressed ? .97 : 1);
    }
    private static void AnimateScale(ScaleTransform transform, double target)
    {
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation); transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}
