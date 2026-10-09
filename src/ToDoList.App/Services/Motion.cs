using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ToDoList.App.Services;

public static class Motion
{
    private sealed class Operation(Action finish) { public Action Finish { get; } = finish; }
    private static readonly ConditionalWeakTable<FrameworkElement, Operation> Active = new();

    // Completing an interrupted animation must also release its awaiters.
    public static void Finish(FrameworkElement element)
    {
        if (Active.TryGetValue(element, out var operation)) operation.Finish();
        element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1;
    }

    public static void Reveal(FrameworkElement element, double distance = 8, int milliseconds = 180)
    {
        Finish(element); element.Visibility = Visibility.Visible;
        var transform = new TranslateTransform(); element.RenderTransform = transform;
        if (ThemeService.ReduceMotion) return;
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        _ = FadeAsync(element, 0, 1, milliseconds, false);
    }

    public static Task HideAsync(FrameworkElement element) => FadeAsync(element, element.Opacity, 0, 160, true);
    internal static Task CrossFadeAsync(FrameworkElement snapshot) => FadeAsync(snapshot, 1, 0, 150, true);

    private static Task FadeAsync(FrameworkElement element, double from, double to, int milliseconds, bool collapse)
    {
        Finish(element);
        if (ThemeService.ReduceMotion || !element.IsLoaded)
        {
            if (collapse) element.Visibility = Visibility.Collapsed;
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        void Complete()
        {
            if (finished) return;
            finished = true;
            Active.Remove(element);
            ThemeService.MotionChanged -= Complete;
            element.Unloaded -= Unloaded;
            element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1;
            if (element.RenderTransform is TranslateTransform transform)
            { transform.BeginAnimation(TranslateTransform.XProperty, null); transform.X = 0; }
            if (collapse) element.Visibility = Visibility.Collapsed;
            completion.TrySetResult();
        }
        void Unloaded(object sender, RoutedEventArgs e) => Complete();
        Active.Add(element, new Operation(Complete));
        ThemeService.MotionChanged += Complete;
        element.Unloaded += Unloaded;
        var fade = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => Complete();
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        return completion.Task;
    }
}
