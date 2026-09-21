using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using ToDoList.App.Services;
using System.Windows.Threading;
using ToDoList.App.ViewModels;
using ToDoList.Core;

namespace ToDoList.App.Controls;
public sealed class StatusRequestEventArgs(bool longPress) : EventArgs { public bool LongPress { get; } = longPress; }
public partial class TaskCard : UserControl
{
    public event EventHandler<StatusRequestEventArgs>? StateRequested;
    public event EventHandler? EditRequested;
    public event EventHandler? DeleteRestoreRequested;
    public event EventHandler? SaveRequested;
    public event EventHandler? CancelRequested;
    public TaskViewModel? Row => DataContext as TaskViewModel;
    public RichEditor? ActiveEditor { get; private set; }
    private TaskViewModel? _subscribed;
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _pressed, _long;
    private TaskCompletionSource? _animation;
    private int _animationVersion;
    public TaskCard()
    {
        InitializeComponent();
        MouseEnter += (_, _) => AnimateHover(true); MouseLeave += (_, _) => AnimateHover(false);
        _hold.Tick += (_, _) => { _hold.Stop(); if (!_pressed) return; _long = true; StateRequested?.Invoke(this, new(true)); };
        DataContextChanged += (_, _) => BindRow(); Loaded += (_, _) => { BindRow(); ThemeService.Changed -= RenderText; ThemeService.Changed += RenderText; };
        Unloaded += (_, _) => { ThemeService.Changed -= RenderText; StopPress(); StopAnimation(); if (_subscribed != null) _subscribed.PropertyChanged -= OnRowChanged; _subscribed = null; };
    }
    private void BindRow()
    {
        if (_subscribed == Row) return;
        if (_subscribed != null) _subscribed.PropertyChanged -= OnRowChanged;
        _subscribed = Row; StopPress(); StopAnimation(); EndEdit(); RowBorder.Background = new SolidColorBrush(Colors.Transparent);
        if (_subscribed != null) _subscribed.PropertyChanged += OnRowChanged;
        RenderText();
    }
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(TaskViewModel.Item)) RenderText(); }
    private void RenderText()
    {
        BodyText.Inlines.Clear(); if (Row == null) return;
        var content = RichContent.Parse(Row.Item.ContentJson);
        bool first = true;
        foreach (var paragraph in content.Paragraphs)
        {
            if (!first) BodyText.Inlines.Add(new LineBreak()); first = false;
            foreach (var r in paragraph.Runs)
            {
                var inline = RichEditor.CreateInline(r);
                if (Row.IsCompleted || Row.IsDeleted)
                {
                    var decorations = new TextDecorationCollection(inline.TextDecorations);
                    var strike = TextDecorations.Strikethrough[0].Clone();
                    if (Row.IsDeleted) strike.Pen = new Pen((Brush)FindResource("RedBrush"), 1.2);
                    decorations.Add(strike); inline.TextDecorations = decorations;
                }
                if (inline is Hyperlink h) h.RequestNavigate += (_, e) => { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
                BodyText.Inlines.Add(inline);
            }
        }
        BodyText.Opacity = Row.IsCompleted ? .52 : 1;
        var stateBrush = (Brush)FindResource(Row.IsVerification ? "YellowBrush" : "GreenBrush");
        if (ThemeService.ReduceMotion)
            InteractionMotion.FinishCheckAnimation(CheckButton);
        foreach (var key in new[] { "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPressed" }) CheckButton.Resources[key] = stateBrush;
    }
    public void BeginEdit()
    {
        if (Row == null || Row.IsDeleted) return; ActiveEditor = new RichEditor { MinHeight = 110, MaxHeight = 240 };
        ActiveEditor.SetContent(RichContent.Parse(Row.Item.ContentJson));
        ActiveEditor.Submit += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
        ActiveEditor.Cancel += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        var panel = new StackPanel(); panel.Children.Add(ActiveEditor);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Wpf.Ui.Controls.Button { Content = "取消", Margin = new(0, 4, 8, 0), Padding = new(10, 5, 10, 5) }; cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        var save = new Wpf.Ui.Controls.Button { Content = "保存  Ctrl+Enter", Style = (Style)FindResource("PrimaryButton"), Margin = new(0, 4, 0, 0), Padding = new(10, 5, 10, 5) }; save.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions);
        EditorHost.Content = panel; EditorHost.Visibility = Visibility.Visible; BodyText.Visibility = Visibility.Collapsed;
        Row.IsEditing = true; ActiveEditor.FocusEditor(); Motion.Reveal(EditorHost, 4);
        if (!ThemeService.ReduceMotion)
        {
            EditorHost.Measure(new Size(Math.Max(100, ActualWidth - 60), double.PositiveInfinity));
            EditorHost.BeginAnimation(HeightProperty, new DoubleAnimation(0, EditorHost.DesiredSize.Height, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
    }
    public void EndEdit()
    {
        EditorHost.BeginAnimation(HeightProperty, null); EditorHost.BeginAnimation(OpacityProperty, null); EditorHost.Content = null; EditorHost.Visibility = Visibility.Collapsed; BodyText.Visibility = Visibility.Visible; ActiveEditor = null;
    }
    private void Body_Down(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2 && Row is { IsBusy: false, IsDeleted: false }) { e.Handled = true; EditRequested?.Invoke(this, EventArgs.Empty); } }
    private void Check_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; if (Row is not { IsBusy: false, IsEditing: false, IsDeleted: false }) return;
        CheckButton.Focus(); _pressed = true; _long = false; CheckButton.CaptureMouse();
        if (!Row.IsCompleted) _hold.Start();
    }
    private void Check_Up(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; var invoke = _pressed && !_long && CheckButton.IsMouseOver; StopPress();
        if (invoke) StateRequested?.Invoke(this, new(false));
    }
    private void Check_Leave(object sender, MouseEventArgs e) => StopPress();
    private void Check_LostCapture(object sender, MouseEventArgs e) { _hold.Stop(); _pressed = false; }
    private void StopPress() { _hold.Stop(); _pressed = false; if (CheckButton.IsMouseCaptured) CheckButton.ReleaseMouseCapture(); }
    private void Check_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && !e.IsRepeat) { e.Handled = true; StateRequested?.Invoke(this, new(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))); }
    }
    private void Check_Click(object sender, RoutedEventArgs e)
    {
        // UI Automation invokes Click; pointer and keyboard are handled before the native toggle.
        CheckButton.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
        if (Row is { IsBusy: false, IsEditing: false, IsDeleted: false }) StateRequested?.Invoke(this, new(false));
    }
    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        DeleteRestoreMenu.Header = Row?.IsDeleted == true ? "恢复任务" : "删除任务";
        DeleteRestoreMenu.Icon = new Wpf.Ui.Controls.SymbolIcon(Row?.IsDeleted == true ? Wpf.Ui.Controls.SymbolRegular.ArrowUndo20 : Wpf.Ui.Controls.SymbolRegular.Delete20);
        DeleteRestoreMenu.IsEnabled = Row is { IsBusy: false, IsEditing: false };
    }
    private void DeleteRestore_Click(object sender, RoutedEventArgs e) => DeleteRestoreRequested?.Invoke(this, EventArgs.Empty);
    private void AnimateHover(bool hover)
    {
        var target = hover ? ((SolidColorBrush)FindResource("HoverBrush")).Color : Colors.Transparent;
        if (RowBorder.Background is not SolidColorBrush brush || brush.IsFrozen) RowBorder.Background = brush = new SolidColorBrush(Colors.Transparent);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(ThemeService.ReduceMotion ? 0 : 140)));
    }
    public Task AnimateOutAsync(bool reduceMotion)
    {
        StopAnimation(); var version = _animationVersion; _animation = new(TaskCreationOptions.RunContinuationsAsynchronously); var result = _animation.Task;
        Height = ActualHeight;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(reduceMotion ? 90 : 160));
        fade.Completed += (_, _) =>
        {
            if (version != _animationVersion) return;
            if (reduceMotion) { _animation?.TrySetResult(); return; }
            var shrink = new DoubleAnimation(Height, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            shrink.Completed += (_, _) => { if (version == _animationVersion) _animation?.TrySetResult(); };
            BeginAnimation(HeightProperty, shrink);
        };
        BeginAnimation(OpacityProperty, fade); return result;
    }
    private void StopAnimation()
    {
        _animationVersion++; _animation?.TrySetResult(); _animation = null;
        BeginAnimation(OpacityProperty, null); BeginAnimation(HeightProperty, null); Opacity = 1; Height = double.NaN;
    }
}
