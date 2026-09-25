using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
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
    public ComboBox? ActiveProjectSelector { get; private set; }
    public string? EditedProjectId => (ActiveProjectSelector?.SelectedItem as ProjectInfo)?.Id;
    public bool HasPendingChanges => ActiveEditor?.Dirty == true || (Row != null && EditedProjectId != Row.Item.ProjectId);
    private TaskViewModel? _subscribed;
    private TaskViewModel? _boundRow;
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _pressed, _long;
    private TaskCompletionSource? _animation;
    private int _animationVersion;
    private int _editVersion;
    internal bool IsExpandingEdit { get; private set; }
    public TaskCard()
    {
        InitializeComponent();
        RequestBringIntoView += (_, e) => { if (IsExpandingEdit) e.Handled = true; };
        MouseEnter += (_, _) => AnimateHover(true); MouseLeave += (_, _) => AnimateHover(false);
        _hold.Tick += (_, _) => { _hold.Stop(); if (!_pressed) return; _long = true; StateRequested?.Invoke(this, new(true)); };
        DataContextChanged += (_, _) => BindRow(); Loaded += (_, _) => { BindRow(); ThemeService.Changed -= RenderText; ThemeService.Changed += RenderText; };
        Unloaded += (_, _) => { ThemeService.Changed -= RenderText; StopPress(); StopAnimation(); _editVersion++; IsExpandingEdit = false; ResetEditLayout(); if (_subscribed != null) _subscribed.PropertyChanged -= OnRowChanged; _subscribed = null; };
    }
    private void BindRow()
    {
        // A tray handle recreation unloads/reloads the same row. Retain its unsaved
        // editor; only a different data item may release the shared editor.
        if (_boundRow != Row)
        {
            StopPress(); StopAnimation(); EndEdit(); _boundRow = Row;
            RowBorder.Background = new SolidColorBrush(Colors.Transparent);
        }
        if (_subscribed == Row) return;
        if (_subscribed != null) _subscribed.PropertyChanged -= OnRowChanged;
        _subscribed = Row;
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
                // Display-only inlines are rebuilt on theme changes. Use concrete values
                // here; editable inlines inherit from their FlowDocument instead.
                inline.FontSize = (double)FindResource("BodyFontSize");
                inline.FontFamily = (FontFamily)FindResource("BodyFontFamily");
                if (r.Color == null && inline is not Hyperlink) inline.SetResourceReference(TextElement.ForegroundProperty, "InkBrush");
                if (Row.IsCompleted || Row.IsDeleted)
                {
                    var decorations = new TextDecorationCollection(inline.TextDecorations);
                    var strike = TextDecorations.Strikethrough[0].Clone();
                    if (Row.IsDeleted) strike.Pen = new Pen((Brush)FindResource("RedBrush"), 1.2);
                    decorations.Add(strike); inline.TextDecorations = decorations;
                }
                if (Row.IsDeleted) inline.SetResourceReference(TextElement.ForegroundProperty, "RedBrush");
                if (inline is Hyperlink h) h.RequestNavigate += (_, e) => { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
                BodyText.Inlines.Add(inline);
            }
        }
        BodyText.Opacity = Row.IsCompleted ? .52 : 1;
        AnimateHover(IsMouseOver);
        var stateBrush = (Brush)FindResource(Row.IsVerification ? "TaskVerificationBrush" : Row.IsCompleted ? "TaskCompletedBrush" : "TaskOpenBrush");
        CheckButton.BorderBrush = stateBrush;
        CheckButton.Resources["CheckBoxCheckBorderBrush"] = stateBrush;
        if (ThemeService.ReduceMotion)
            InteractionMotion.FinishCheckAnimation(CheckButton);
        foreach (var key in new[] { "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPressed", "CheckBoxCheckBackgroundStrokeUnchecked", "CheckBoxCheckBackgroundStrokeUncheckedPointerOver", "CheckBoxCheckBackgroundStrokeUncheckedPressed" }) CheckButton.Resources[key] = stateBrush;
    }
    public void BeginEdit()
    {
        if (Row == null || Row.IsDeleted || ActiveEditor != null) return;
        IsExpandingEdit = true;
        ActiveEditor = (Window.GetWindow(this) as MainWindow)?.TaskEditor ?? new RichEditor { MinHeight = 92, MaxHeight = 240 };
        ActiveEditor.SubmitLabel = "保存";
        ActiveEditor.SetContent(RichContent.Parse(Row.Item.ContentJson));
        ActiveEditor.Submit += EditorSubmit;
        ActiveEditor.Cancel += EditorCancel;
        var panel = new StackPanel();
        var owner = Window.GetWindow(this) as MainWindow;
        var projects = owner?.Model.Projects.Select(p => p.Id == null ? new ProjectInfo(null, "未分配模块") : p).ToArray() ?? [new ProjectInfo(null, "未分配模块")];
        ActiveProjectSelector = new ComboBox { ItemsSource = projects, DisplayMemberPath = "Name", SelectedValuePath = "Id", Margin = new(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        ActiveProjectSelector.SelectedItem = projects.FirstOrDefault(p => p.Id == Row.Item.ProjectId) ?? projects[0];
        AutomationProperties.SetAutomationId(ActiveProjectSelector, "EditTaskProject");
        panel.Children.Add(new TextBlock { Text = "所属模块", FontSize = 11, Margin = new(0, 0, 0, 5) }); panel.Children.Add(ActiveProjectSelector); panel.Children.Add(ActiveEditor);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Wpf.Ui.Controls.Button { Content = "取消", Margin = new(0, 4, 8, 0), Padding = new(10, 5, 10, 5) }; cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        var save = new Wpf.Ui.Controls.Button { Content = "保存  Enter", Style = (Style)FindResource("PrimaryButton"), Margin = new(0, 4, 0, 0), Padding = new(10, 5, 10, 5) }; save.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions);
        var initialHeight = BodyText.ActualHeight;
        EditorHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        EditorHost.VerticalContentAlignment = VerticalAlignment.Top;
        EditorHost.ClipToBounds = true;
        panel.VerticalAlignment = VerticalAlignment.Top;
        EditorHost.Height = initialHeight;
        EditorHost.Opacity = 0;
        EditorHost.Content = panel; EditorHost.Visibility = Visibility.Visible; BodyText.Visibility = Visibility.Collapsed;
        Row.IsEditing = true;
        var version = ++_editVersion;
        // Wait for inherited styles/templates and the real column width. A detached
        // RichTextBox can report a different height before its first arrange.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_editVersion != version || ActiveEditor == null || !IsLoaded) return;
            var width = EditorHost.ActualWidth;
            if (width <= 0) { FinishEditExpansion(version); return; }
            panel.Width = width;
            panel.Measure(new Size(width, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
            panel.UpdateLayout();
            panel.Measure(new Size(width, double.PositiveInfinity));
            var target = panel.DesiredSize.Height;
            panel.Height = target;
            if (ThemeService.ReduceMotion) { FinishEditExpansion(version); return; }
            var expand = new DoubleAnimation(initialHeight, target, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            expand.Completed += (_, _) => FinishEditExpansion(version);
            EditorHost.BeginAnimation(HeightProperty, expand);
            EditorHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }));
    }
    private void FinishEditExpansion(int version)
    {
        if (_editVersion != version) return;
        ResetEditLayout();
        // Natural sizing must be arranged before focus/caret visibility requests.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_editVersion != version || !IsLoaded || ActiveEditor == null) return;
            ActiveEditor.FocusEditor();
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (_editVersion != version || !IsLoaded || ActiveEditor == null) return;
                IsExpandingEdit = false;
                if (Window.GetWindow(this) is MainWindow owner) owner.RevealTaskEditor(this, EditorHost);
            }));
        }));
    }
    private void ResetEditLayout()
    {
        EditorHost.BeginAnimation(HeightProperty, null);
        EditorHost.BeginAnimation(OpacityProperty, null);
        if (EditorHost.Content is Panel panel) panel.Height = panel.Width = double.NaN;
        EditorHost.Height = double.NaN; EditorHost.Opacity = 1;
    }
    private void EditorSubmit(object? sender, EventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);
    private void EditorCancel(object? sender, EventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
    public void EndEdit()
    {
        _editVersion++;
        IsExpandingEdit = false;
        ResetEditLayout();
        if (ActiveEditor != null)
        {
            ActiveEditor.Submit -= EditorSubmit; ActiveEditor.Cancel -= EditorCancel;
            ActiveEditor.CloseTool();
            if (EditorHost.Content is Panel panel) panel.Children.Remove(ActiveEditor);
        }
        EditorHost.Content = null; EditorHost.Visibility = Visibility.Collapsed; BodyText.Visibility = Visibility.Visible; ActiveEditor = null; ActiveProjectSelector = null;
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
        var owner = Window.GetWindow(this) as MainWindow;
        PermanentDeleteMenu.Visibility = MultiSelectMenu.Visibility = Row?.IsDeleted == true && owner?.Model.Filter == TaskFilter.Deleted ? Visibility.Visible : Visibility.Collapsed;
        PermanentDeleteMenu.IsEnabled = MultiSelectMenu.IsEnabled = owner?.Model.IsBusy == false;
        DeleteRestoreMenu.Header = Row?.IsDeleted == true ? "恢复任务" : "删除任务";
        DeleteRestoreMenu.Icon = new Wpf.Ui.Controls.SymbolIcon(Row?.IsDeleted == true ? Wpf.Ui.Controls.SymbolRegular.ArrowUndo20 : Wpf.Ui.Controls.SymbolRegular.Delete20);
        DeleteRestoreMenu.IsEnabled = Row is { IsBusy: false, IsEditing: false };
    }
    private async void PermanentDelete_Click(object sender, RoutedEventArgs e)
    { if (Row is { } row && Window.GetWindow(this) is MainWindow owner) await owner.DeletePermanentlyAsync(new Dictionary<string, long> { [row.Id] = row.Item.Revision }); }
    private void MultiSelect_Click(object sender, RoutedEventArgs e)
    { if (Row is { } row && Window.GetWindow(this) is MainWindow owner) owner.StartDeletedSelection(row); }
    private void DeleteSelection_Click(object sender, RoutedEventArgs e)
    { if (Row is { } row && Window.GetWindow(this) is MainWindow owner) owner.UpdateDeletedSelection(row); }
    private void DeleteRestore_Click(object sender, RoutedEventArgs e) => DeleteRestoreRequested?.Invoke(this, EventArgs.Empty);
    private void AnimateHover(bool hover)
    {
        var color = ((SolidColorBrush)FindResource("HoverBrush")).Color;
        if (RowBorder.Background is not SolidColorBrush brush || brush.IsFrozen || brush.Color == Colors.Transparent)
            RowBorder.Background = brush = new SolidColorBrush(color) { Opacity = 0 };
        brush.Color = color;
        brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(hover ? 1 : 0,
            TimeSpan.FromMilliseconds(ThemeService.ReduceMotion ? 0 : 140)) { EasingFunction = new QuadraticEase() });
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
        if (RowBorder.Background is SolidColorBrush brush && !brush.IsFrozen)
        { brush.BeginAnimation(Brush.OpacityProperty, null); brush.Opacity = 0; }
        BeginAnimation(OpacityProperty, null); BeginAnimation(HeightProperty, null); Opacity = 1; Height = double.NaN;
    }
}
