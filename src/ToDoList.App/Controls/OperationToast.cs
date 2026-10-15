using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ToDoList.App.Services;
using ToDoList.Core;
using Button = Wpf.Ui.Controls.Button;

namespace ToDoList.App.Controls;

public sealed class OperationToast : Border
{
    public UndoWindow Clock { get; } = new();
    public TextBlock MessageLabel { get; } = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    public Button UndoButton { get; } = new() { Content = "撤回", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(12, 0, 0, 0), MinWidth = 52, MinHeight = 28, Focusable = false };
    public ProgressBar Countdown { get; } = new() { Minimum = 0, Maximum = 1, Height = 3, Margin = new Thickness(0, 10, 0, 0), Focusable = false };
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _fading;
    private readonly TranslateTransform _slide = new();
    public Func<bool>? IsUndoAvailable { get; set; }
    public event EventHandler? UndoRequested;
    public event EventHandler? Dismissed;
    public OperationToast()
    {
        RenderTransform = _slide;
        Visibility = Visibility.Collapsed; MinWidth = 280; MaxWidth = 460; Padding = new Thickness(12); CornerRadius = new CornerRadius(4); BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "SnackBarBackground"); SetResourceReference(BorderBrushProperty, "SnackBarBorderBrush");
        MessageLabel.SetResourceReference(TextBlock.ForegroundProperty, "SnackBarForeground");
        UndoButton.SetResourceReference(Control.ForegroundProperty, "SnackBarForeground");
        Countdown.SetResourceReference(Control.ForegroundProperty, "AccentInkBrush");
        Countdown.SetResourceReference(StyleProperty, "ToastCountdown");
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var close = new Button { Content = "×", Padding = new Thickness(6), Margin = new Thickness(8, 0, 0, 0), MinHeight = 28, Focusable = false };
        close.SetResourceReference(Control.ForegroundProperty, "SnackBarForeground");
        System.Windows.Automation.AutomationProperties.SetName(UndoButton, "撤回本次操作");
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭提示");
        Grid.SetColumn(UndoButton, 1); Grid.SetColumn(close, 2); Grid.SetRow(Countdown, 1); Grid.SetColumnSpan(Countdown, 3);
        grid.Children.Add(MessageLabel); grid.Children.Add(UndoButton); grid.Children.Add(close); grid.Children.Add(Countdown); Child = grid;
        UndoButton.Click += (_, e) => { e.Handled = true; UndoRequested?.Invoke(this, EventArgs.Empty); };
        close.Click += (_, _) => CloseAnimated();
        _timer.Tick += (_, _) => UpdateClock();
        Unloaded += (_, _) => Dismiss();
    }
    public void Show(string message, bool undo = false, bool syncing = false)
    {
        StopAnimations(); _fading = false;
        Clock.Start(undo, syncing); MessageLabel.Text = message;
        UndoButton.Visibility = Countdown.Visibility = undo ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.IsEnabled = undo && IsUndoAvailable?.Invoke() != false; Countdown.Value = 1; Visibility = Visibility.Visible; Opacity = 1;
        var duration = TimeSpan.FromMilliseconds(ThemeService.ReduceMotion ? 90 : 180);
        _slide.X = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        if (!ThemeService.ReduceMotion) _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(48, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _timer.Start();
    }
    public bool AcceptUndo()
    {
        if (IsUndoAvailable?.Invoke() == false || !Clock.Accept()) { UpdateClock(); return false; }
        UndoButton.IsEnabled = false; return true;
    }
    private void UpdateClock()
    {
        Countdown.Value = Clock.Remaining; UndoButton.IsEnabled = Clock.CanUndo && IsUndoAvailable?.Invoke() != false;
        if (!Clock.ShouldFade || _fading) return;
        CloseAnimated();
    }
    private void StopAnimations()
    {
        _timer.Stop(); BeginAnimation(OpacityProperty, null); _slide.BeginAnimation(TranslateTransform.XProperty, null);
    }
    public void CloseAnimated()
    {
        if (_fading || Visibility != Visibility.Visible) return;
        var opacity = Opacity; var x = _slide.X;
        StopAnimations(); Clock.Clear(); UndoButton.IsEnabled = false; Dismissed?.Invoke(this, EventArgs.Empty);
        _fading = true; var generation = Clock.Generation;
        var duration = TimeSpan.FromMilliseconds(ThemeService.ReduceMotion ? 90 : 180);
        var fade = new DoubleAnimation(opacity, 0, duration);
        fade.Completed += (_, _) => { if (Clock.Generation == generation && _fading) Dismiss(); };
        BeginAnimation(OpacityProperty, fade);
        if (!ThemeService.ReduceMotion) _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, 48, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }
    public void Dismiss()
    {
        StopAnimations(); Clock.Clear(); _fading = false; Opacity = 1; _slide.X = 0; Visibility = Visibility.Collapsed;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}
