using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ToDoList.App.ViewModels;
using ToDoList.Core;

namespace ToDoList.App.Controls;
public sealed class StatusRequestEventArgs(bool longPress) : EventArgs { public bool LongPress { get; } = longPress; }
public partial class TaskCard : UserControl
{
    public event EventHandler<StatusRequestEventArgs>? StateRequested;
    public event EventHandler? EditRequested;
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
        _hold.Tick += (_, _) => { _hold.Stop(); if (!_pressed) return; _long = true; StateRequested?.Invoke(this, new(true)); };
        DataContextChanged += (_, _) => BindRow(); Loaded += (_, _) => BindRow();
        Unloaded += (_, _) => { StopPress(); StopAnimation(); if (_subscribed != null) _subscribed.PropertyChanged -= OnRowChanged; _subscribed = null; };
    }
    private void BindRow()
    {
        if (_subscribed == Row) return;
        if (_subscribed != null) _subscribed.PropertyChanged -= OnRowChanged;
        _subscribed = Row; StopPress(); StopAnimation(); EndEdit();
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
                if (Row.IsCompleted) { var decorations = new TextDecorationCollection(inline.TextDecorations); decorations.Add(TextDecorations.Strikethrough[0]); inline.TextDecorations = decorations; }
                if (inline is Hyperlink h) h.RequestNavigate += (_, e) => { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
                BodyText.Inlines.Add(inline);
            }
        }
        BodyText.Opacity = Row.IsCompleted ? .52 : 1;
    }
    public void BeginEdit()
    {
        if (Row == null) return; ActiveEditor = new RichEditor { MinHeight = 110, MaxHeight = 240 };
        ActiveEditor.SetContent(RichContent.Parse(Row.Item.ContentJson));
        ActiveEditor.Submit += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
        ActiveEditor.Cancel += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        var panel = new StackPanel(); panel.Children.Add(ActiveEditor);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Margin = new(0, 4, 8, 0), Padding = new(10, 5, 10, 5) }; cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        var save = new Button { Content = "保存  Ctrl+Enter", Style = (Style)FindResource("PrimaryButton"), Margin = new(0, 4, 0, 0), Padding = new(10, 5, 10, 5) }; save.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions);
        EditorHost.Content = panel; EditorHost.Visibility = Visibility.Visible; BodyText.Visibility = Visibility.Collapsed;
        Row.IsEditing = true; ActiveEditor.FocusEditor();
    }
    public void EndEdit()
    {
        EditorHost.Content = null; EditorHost.Visibility = Visibility.Collapsed; BodyText.Visibility = Visibility.Visible; ActiveEditor = null;
    }
    private void Body_Down(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2 && Row is { IsBusy: false }) { e.Handled = true; EditRequested?.Invoke(this, EventArgs.Empty); } }
    private void Check_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; if (Row is not { IsBusy: false, IsEditing: false }) return;
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
