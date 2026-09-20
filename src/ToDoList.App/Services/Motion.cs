using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ToDoList.App.Services;

public static class Motion
{
    public static void Reveal(FrameworkElement element, double distance = 8, int milliseconds = 180)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1,
            TimeSpan.FromMilliseconds(ThemeService.ReduceMotion ? 80 : milliseconds)) { FillBehavior = FillBehavior.Stop });
        var transform = new TranslateTransform(); element.RenderTransform = transform;
        if (!ThemeService.ReduceMotion)
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(milliseconds))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    public static async Task HideAsync(FrameworkElement element)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(ThemeService.ReduceMotion ? 80 : 160));
        fade.Completed += (_, _) => completion.TrySetResult(); element.BeginAnimation(UIElement.OpacityProperty, fade);
        await completion.Task; element.Visibility = Visibility.Collapsed;
        element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1;
    }
}
