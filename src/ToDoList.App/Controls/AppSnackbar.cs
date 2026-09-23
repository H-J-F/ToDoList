using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ToDoList.App.Services;

namespace ToDoList.App.Controls;

internal sealed class AppSnackbar(Wpf.Ui.Controls.SnackbarPresenter presenter) : Wpf.Ui.Controls.Snackbar(presenter)
{
    private readonly TranslateTransform _slide = new();

    protected override void OnOpened()
    {
        RenderTransform = _slide;
        var duration = TimeSpan.FromMilliseconds(ThemeService.ReduceMotion ? 90 : 180);
        _slide.BeginAnimation(TranslateTransform.XProperty, null); Opacity = 0; _slide.X = ThemeService.ReduceMotion ? 0 : 48;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        if (!ThemeService.ReduceMotion) _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(48, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        base.OnOpened();
    }

    protected override void OnClosed()
    {
        var duration = TimeSpan.FromMilliseconds(ThemeService.ReduceMotion ? 90 : 180);
        BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 0, duration));
        if (!ThemeService.ReduceMotion) _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_slide.X, 48, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        base.OnClosed();
    }
}
