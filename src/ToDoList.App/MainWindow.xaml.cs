using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ToDoList.App.Controls;
using ToDoList.App.Services;
using ToDoList.App.ViewModels;
using ToDoList.Core;
using ToDoList.Storage;

namespace ToDoList.App;
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    public MainViewModel Model { get; }
    internal Action<string> OpenExternal { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    public Wpf.Ui.Controls.ContentDialogHost DialogPresenter => DialogHost;
    private bool _sync = true, _initialized, _navigating, _closingApproved, _restoring;
    private TaskCard? _editingCard;
    private RichEditor? _taskEditor;
    private string? _draftProjectId, _draftBook;
    internal RichEditor TaskEditor => _taskEditor ??= new RichEditor { MinHeight = 92, MaxHeight = 240 };
    private (string Id, double Y)? _anchor;
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _calendar = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _settingsSave = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private DateTime _today = DateTime.Today;
    private string _zone = TimeZoneInfo.Local.Id;
    private int _pendingMutations;
    private PageDirection? _scrollIntent;
    private bool _scrollbarGesture;
    private bool _editRevealPending;
    private bool _savingRow, _closePending;
    private WindowState _stateBeforeTray = WindowState.Normal;
    public MainWindow(BookLibrary library, AppSettings settings)
    {
        Model = new(library, settings); InitializeComponent(); DataContext = Model;
        InitializeTaskManagement();
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) _stateBeforeTray = WindowState; };
        Width = Math.Clamp(settings.Width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        Height = Math.Clamp(settings.Height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        if (settings.Left.HasValue && settings.Top.HasValue && settings.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 && settings.Left + Width > SystemParameters.VirtualScreenLeft + 100 && settings.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50 && settings.Top >= SystemParameters.VirtualScreenTop)
        { Left = settings.Left.Value; Top = settings.Top.Value; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (settings.Maximized) WindowState = WindowState.Maximized;
        var bitmap = new BitmapImage(new Uri("pack://application:,,,/Assets/app-icon.png")); bitmap.Freeze();
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
        WelcomeIcon.Source = AboutIcon.Source = bitmap;
        ThemeSetting.ItemsSource = ThemeService.Themes;
        FontSetting.ItemsSource = new double[] { 12, 14, 16, 18 };
        DensitySetting.ItemsSource = new[] { "紧凑", "舒适" }; MotionSetting.ItemsSource = new[] { "标准", "减少动态效果" };
        SyncSettings();
        PreviewMouseDown += Window_PreviewMouseDown;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _datePopup?.IsOpen == true) { _datePopup.IsOpen = false; e.Handled = true; } };
        Deactivated += (_, _) => { if (_datePopup != null) _datePopup.IsOpen = false; };
        Model.LayoutChanging += CaptureAnchor; Model.LayoutChanged += RestoreAnchor;
        Model.LatestRequested += ScrollToLatest;
        Model.IsEditing = () => _editingCard != null;
        Model.AnimateRemoval = row => FindCard(row)?.AnimateOutAsync(Model.Settings.ReduceMotion) ?? Task.CompletedTask;
        TaskList.PreviewMouseWheel += (_, e) => { _editRevealPending = false; _scrollIntent = e.Delta > 0 ? PageDirection.Older : PageDirection.Newer; };
        TaskList.PreviewKeyDown += (_, e) =>
        {
            if (_editingCard?.IsKeyboardFocusWithin == true) return;
            if (e.Key is Key.Up or Key.PageUp or Key.Home) _scrollIntent = PageDirection.Older;
            else if (e.Key is Key.Down or Key.PageDown or Key.End) _scrollIntent = PageDirection.Newer;
        };
        TaskList.PreviewMouseDown += (_, e) =>
        {
            for (var d = e.OriginalSource as DependencyObject; d is Visual; d = VisualTreeHelper.GetParent(d))
                if (d is ScrollBar) { _editRevealPending = false; _scrollbarGesture = true; break; }
        };
        TaskList.PreviewMouseUp += (_, _) => _scrollbarGesture = false;
        _settingsSave.Tick += (_, _) => { _settingsSave.Stop(); try { Model.SaveSettings(); } catch (Exception ex) { ShowError(ex); } };
        _calendar.Tick += async (_, _) =>
        {
            TimeZoneInfo.ClearCachedData();
            if (_today == DateTime.Today && _zone == TimeZoneInfo.Local.Id || _navigating || _editingCard != null || _pendingMutations > 0) return;
            _today = DateTime.Today; _zone = TimeZoneInfo.Local.Id;
            await SafeAsync(() => Model.ReloadAsync());
        };
        SystemEvents.UserPreferenceChanged += OnPreferencesChanged;
        SizeChanged += (_, _) =>
        {
            var compact = ActualHeight < 650;
            PageSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            PageHeading.FontSize = compact ? 22 : 26;
            BookmarkArea.Margin = new Thickness(0, compact ? 8 : 16, 0, 0);
            DraftEditor.Height = compact ? 76 : 80;
            MainPage.Margin = new Thickness(24, compact ? 12 : 20, 24, compact ? 12 : 20);
        };
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Model.Message) && _initialized) { _feedbackTimer.Stop(); _feedbackTimer.Start(); }
        };
        _feedbackTimer.Tick += (_, _) =>
        {
            if (Notifications.Content is { IsShown: false }) return;
            _feedbackTimer.Stop();
            if (Notifications.Content is { } current) current.Content = Model.Message;
            else new AppSnackbar(Notifications) { Content = Model.Message, MinWidth = 280, MaxWidth = 460, Timeout = TimeSpan.FromSeconds(2), Style = (Style)FindResource("HorizontalSnackbar") }.Show();
        };
        Closed += (_, _) => { _feedbackTimer.Stop(); _calendar.Stop(); _settingsSave.Stop(); SystemEvents.UserPreferenceChanged -= OnPreferencesChanged; };
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return; // Recreating the tray window handle can raise Loaded again.
        await SafeAsync(async () => { Model.IsBusy = true; await Model.InitializeAsync(); SyncSelectors(); SyncSettings(); });
        Model.IsBusy = false; _sync = false; _initialized = true; _calendar.Start();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!IsLoaded) return;
            if (TaskEditor.Parent != null) return;
            TaskEditor.ApplyTemplate();
            TaskEditor.Measure(new Size(Math.Max(100, TaskList.ActualWidth - 60), double.PositiveInfinity));
        }));
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--ui-themes")) { await Services.UiThemePreview.RunAsync(this); return; }
        if (args.Contains("--ui-revision")) { await Services.UiRevisionChecks.RunAsync(this); return; }
        if (args.Contains("--ui-drag-boundaries")) { await Services.UiDragBoundaryChecks.RunAsync(this); return; }
        if (args.Contains("--ui-interaction")) { await Services.UiInteractionChecks.RunAsync(this); return; }
        if (args.Contains("--ui-taskfixes")) { await Services.UiTaskFixes.RunAsync(this); return; }
        if (args.Contains("--ui-smoke")) await Services.UiSmoke.RunAsync(this);
        else if (args.Contains("--ui-features")) await Services.UiFeatures.RunAsync(this);
        else if (args.Contains("--ui-typography")) await Services.UiTypography.RunAsync(this);
        else if (args.Contains("--ui-demo")) await Services.UiDemo.RunAsync(this);
        else if (args.Contains("--ui-perf")) await Services.UiPerformance.RunAsync(this);
        else if (args.Contains("--ui-lifecycle")) await Services.UiLifecycle.RunAsync(this);
    }
    private void SyncSelectors(bool resetDraft = false)
    {
        var previous = _sync; _sync = true;
        BookSelector.SelectedItem = Model.Books.FirstOrDefault(b => b.Name == Model.CurrentBook?.Name);
        ProjectList.SelectedItem = Model.Projects.FirstOrDefault(p => p.Id == Model.CurrentProjectId);
        var draftId = resetDraft || _draftBook != Model.CurrentBook?.Name ? Model.CurrentProjectId : _draftProjectId;
        _draftBook = Model.CurrentBook?.Name;
        DraftProject.SelectedItem = Model.DraftProjects.FirstOrDefault(p => p.Id == draftId) ?? Model.DraftProjects.FirstOrDefault();
        foreach (TabItem tab in Tabs.Items) tab.IsSelected = (string)tab.Tag == Model.Filter.ToString();
        SortSelector.SelectedIndex = Model.Sort == TaskSort.Created ? 0 : 1;
        NewProjectName.Visibility = (DraftProject.SelectedItem as ProjectInfo)?.Id == "__new" ? Visibility.Visible : Visibility.Collapsed; _sync = previous;
        AppTitleBar.Title = Title = "ToDoList " + ApplicationLinks.BuildLabel + " · " + Model.BookTitle;
    }
    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { } catch (Exception ex) { ShowError(ex); }
    }
    private void ShowError(Exception ex) { if (!IsVisible) RestoreFromTray(); Model.Message = "未能完成：" + ex.Message; _ = Dialogs.Info(this, "操作未完成", ex.Message); }
    private async Task NavigateAsync(Func<Task> action, bool bookOperation = false, bool preserveDraft = false, bool bookSwitch = false)
    {
        if (_navigating) { SyncSelectors(); return; }
        _navigating = true;
        try
        {
            if (!(preserveDraft ? await TryLeaveRowEditAsync() : await TryLeaveEditsAsync())) return;
            if (bookOperation)
            {
                Model.IsSwitchingBook = bookSwitch;
                Model.IsBusy = true;
                while (_pendingMutations > 0) await Task.Delay(20);
            }
            _sync = true; ClearDeletedSelection();
            if (bookSwitch && !ThemeService.ReduceMotion) CaptureBookSnapshot();
            await action();
            if (bookSwitch)
            {
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                MainPage.UpdateLayout();
                if (BookTransitionSnapshot.Visibility == Visibility.Visible) await Motion.CrossFadeAsync(BookTransitionSnapshot);
            }
            else Motion.Reveal(TaskContent);
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            Motion.Finish(BookTransitionSnapshot); BookTransitionSnapshot.Visibility = Visibility.Collapsed; BookTransitionSnapshot.Source = null;
            _sync = false; _navigating = false; Model.IsBusy = false; Model.IsSwitchingBook = false; SyncSelectors();
        }
    }
    private void CaptureBookSnapshot()
    {
        if (!MainPage.IsVisible || MainPage.ActualWidth <= 0 || MainPage.ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(MainPage);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(MainPage.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(MainPage.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, MainPage.ActualWidth, MainPage.ActualHeight);
            context.DrawRectangle((Brush)FindResource("PaperBrush"), null, bounds);
            context.DrawRectangle(new VisualBrush(MainPage), null, bounds);
        }
        bitmap.Render(drawing); bitmap.Freeze();
        BookTransitionSnapshot.Source = bitmap; BookTransitionSnapshot.Opacity = 1; BookTransitionSnapshot.Visibility = Visibility.Visible;
    }
    private async void Book_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || !_initialized || BookSelector.SelectedItem is not BookInfo book || book.Name == Model.CurrentBook?.Name) return;
        await NavigateAsync(async () => { await Model.OpenBookAsync(book); SyncSelectors(true); }, true, bookSwitch: true);
    }
    private async void Project_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || !_initialized || ProjectList.SelectedItem is not ProjectInfo project || project.Id == Model.CurrentProjectId) return;
        await NavigateAsync(async () =>
        {
            await Model.SelectProjectAsync(project.Id);
            if (project.Id != null) DraftProject.SelectedItem = Model.DraftProjects.FirstOrDefault(p => p.Id == project.Id);
        }, preserveDraft: true);
    }
    private async void Tab_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || !_initialized || !ReferenceEquals(e.Source, Tabs) || Tabs.SelectedItem is not TabItem tab) return;
        var filter = Enum.Parse<TaskFilter>((string)tab.Tag);
        if (filter == Model.Filter) return;
        await NavigateAsync(() => Model.SelectFilterAsync(filter), preserveDraft: true);
    }
    private async void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || !_initialized || SortSelector.SelectedIndex < 0) return;
        var sort = SortSelector.SelectedIndex == 0 ? TaskSort.Created : TaskSort.Completed;
        if (sort != Model.Sort) await NavigateAsync(() => Model.SelectSortAsync(sort));
    }
    private void DraftProject_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DraftProject.SelectedItem is ProjectInfo selected) _draftProjectId = selected.Id;
        if (NewProjectName != null) NewProjectName.Visibility = DraftProject.SelectedItem is ProjectInfo { Id: "__new" } ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void NewBook_Click(object sender, RoutedEventArgs e)
    {
        var input = await Dialogs.CreateBook(this); if (input == null) return;
        await NavigateAsync(async () => { var book = await Model.Library.CreateAsync(input.Value.Name, input.Value.Title); Dialogs.ClearBookDraft(); await Model.RefreshBooksAsync(); await Model.OpenBookAsync(book); }, true);
    }
    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Repository == null) return;
        var input = await Dialogs.Input(this, "加一个新的模块", "模块名称"); if (input == null) return;
        await SafeAsync(async () => { await Model.Repository.AddProjectAsync(input); _sync = true; await Model.RefreshProjectsAsync(); SyncSelectors(); _sync = false; Model.Message = "新模块已加入目录。"; });
        _sync = false;
    }
    private ProjectInfo? SelectedRealProject() => ProjectList.SelectedItem as ProjectInfo is { Id: not null } project ? project : null;
    private async void RenameProject_Click(object sender, RoutedEventArgs e)
    {
        var project = SelectedRealProject(); if (project == null) return;
        var input = await Dialogs.Input(this, "重命名模块", "新的模块名称", project.Name); if (input == null) return;
        await SafeAsync(async () => { await Model.RenameProjectAsync(project, input); SyncSelectors(); });
    }
    private async void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        var project = SelectedRealProject(); if (project == null) return;
        var choice = await Dialogs.Choose(this, "删除模块", $"确定删除模块“{project.Name}”吗？\n\n模块内任务不会删除，将改为未分配模块。", "取消", "删除");
        if (choice != 1) return;
        await NavigateAsync(() => Model.DeleteProjectAsync(project));
    }
    private async void MoveProjectUp_Click(object sender, RoutedEventArgs e) => await MoveProjectAsync(-1);
    private async void MoveProjectDown_Click(object sender, RoutedEventArgs e) => await MoveProjectAsync(1);
    private async Task MoveProjectAsync(int offset)
    {
        var project = SelectedRealProject(); if (project == null) return;
        await SafeAsync(() => Model.ReorderProjectAsync(project, offset));
    }
    private void BookMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu(); var import = new MenuItem { Header = "导入待办笔记…", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.ArrowDownload20) }; import.Click += Import_Click; menu.Items.Add(import);
        var export = new MenuItem { Header = "导出当前待办笔记…", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.ArrowUpload20), IsEnabled = Model.HasBook }; export.Click += Export_Click; menu.Items.Add(export);
        var delete = new MenuItem { Header = "删除当前待办笔记…", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Delete20), IsEnabled = Model.HasBook }; delete.Click += DeleteBook_Click; menu.Items.Add(new Separator()); menu.Items.Add(delete);
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private async void DeleteBook_Click(object sender, RoutedEventArgs e)
    {
        var book = Model.CurrentBook; if (book == null) return;
        var choice = await Dialogs.Choose(this, "删除待办笔记", $"确定删除“{book.Title}”吗？\n\n删除前会自动备份整个数据库到：\n{Model.Library.BackupDirectory}", "取消", "删除");
        if (choice != 1) return;
        await NavigateAsync(async () => { await Model.DeleteCurrentBookAsync(); }, true);
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ToDoList 待办笔记 (*.db)|*.db", Title = "导入一本待办笔记" };
        if (dialog.ShowDialog(this) != true) return;
        await NavigateAsync(async () =>
        {
            using var prepared = await Model.Library.PrepareImportAsync(dialog.FileName);
            var existing = Model.Books.FirstOrDefault(b => b.Name.Equals(prepared.Info.Name, StringComparison.OrdinalIgnoreCase));
            var mode = ImportMode.Merge;
            if (existing != null)
            {
                var choice = await Dialogs.Choose(this, "已有相同的待办笔记", $"标识名：{existing.Name}\n\n合并：保留二者任务，同一任务采用最后修改的版本。\n覆盖：使用导入笔记替换现有笔记。\n\n原笔记会先备份到：\n{Model.Library.BackupDirectory}", "取消", "合并", "覆盖");
                if (choice < 1) return; mode = choice == 1 ? ImportMode.Merge : ImportMode.Replace;
            }
            var imported = await Model.Library.ImportAsync(prepared, existing, mode);
            await Model.RefreshBooksAsync(); await Model.OpenBookAsync(imported);
            Model.Message = existing == null ? "待办笔记已导入。" : "导入完成，原笔记已保存到 Data/Backup。";
        }, true);
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Model.CurrentBook == null) return;
        var dialog = new SaveFileDialog { Filter = "ToDoList 待办笔记 (*.db)|*.db", FileName = Model.CurrentBook.Name + ".db", Title = "导出整本待办笔记" };
        if (dialog.ShowDialog(this) != true) return;
        await NavigateAsync(async () => { await Model.Library.ExportAsync(Model.CurrentBook, dialog.FileName); Model.Message = "待办笔记已导出到 " + dialog.FileName; }, true);
    }
    private async void AddTask_Click(object sender, RoutedEventArgs e) => await SafeAsync(AddDraftAsync);
    private async void Draft_Submit(object? sender, EventArgs e) => await SafeAsync(AddDraftAsync);
    private bool _adding;
    private async Task AddDraftAsync()
    {
        if (_adding || Model.Repository == null) return;
        if (_editingCard != null && !await TryLeaveRowEditAsync()) return;
        var project = DraftProject.SelectedItem as ProjectInfo; var content = DraftEditor.GetContent();
        _adding = true; DraftEditor.IsEnabled = false; _pendingMutations++;
        try
        {
            await Model.AddAsync(content, project?.Id == "__new" ? null : project?.Id, project?.Id == "__new" ? NewProjectName.Text : null);
            if (project?.Id == "__new") _draftProjectId = Model.DraftProjects.FirstOrDefault(p => p.Name == NewProjectName.Text.Trim())?.Id;
            DraftEditor.SetContent(RichContent.FromText("")); NewProjectName.Clear(); SyncSelectors(); DraftEditor.FocusEditor(); Motion.Reveal(TaskContent, 4);
        }
        finally { _adding = false; DraftEditor.IsEnabled = true; _pendingMutations--; }
    }
    private async void Task_StateRequested(object? sender, StatusRequestEventArgs e)
    {
        if (sender is not TaskCard { Row: { } row } || Model.IsBusy) return;
        _pendingMutations++;
        try { await SafeAsync(() => Model.ChangeStatusAsync(row, e.LongPress)); }
        finally { _pendingMutations--; }
    }
    private async void Task_DeleteRestoreRequested(object? sender, EventArgs e)
    {
        if (sender is not TaskCard { Row: { } row } || Model.IsBusy) return;
        _pendingMutations++;
        try { await SafeAsync(() => Model.DeleteRestoreAsync(row)); if (!row.IsDeleted) { row.SelectedForDeletion = false; row.SelectionMode = false; UpdateDeletedSelection(row); } }
        finally { _pendingMutations--; }
    }
    private async void Task_EditRequested(object? sender, EventArgs e)
    {
        if (sender is not TaskCard card || card.Row is not { IsBusy: false, IsDeleted: false } || card == _editingCard) return;
        if (!await TryLeaveRowEditAsync()) return;
        _editingCard = card;
        _editRevealPending = true;
        _scrollIntent = null;
        _scrollbarGesture = false;
        // Replace ScrollToEnd's infinity target with a finite offset before the
        // virtualizing panel's extent changes on every animation frame.
        if (Descendants<ScrollViewer>(TaskList).FirstOrDefault() is { } scroll)
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset);
        if (TaskList.ItemContainerGenerator.ContainerFromItem(card.Row) is DependencyObject container) VirtualizingPanel.SetIsContainerVirtualizable(container, false);
        card.BeginEdit();
    }
    internal void RevealTaskEditor(TaskCard card, FrameworkElement editorHost)
    {
        if (!_editRevealPending || !ReferenceEquals(_editingCard, card) || Descendants<ScrollViewer>(TaskList).FirstOrDefault() is not { } scroll) return;
        _editRevealPending = false;
        var top = editorHost.TransformToAncestor(scroll).Transform(new Point()).Y;
        var bottom = top + editorHost.ActualHeight;
        var delta = editorHost.ActualHeight > scroll.ViewportHeight ? top : top < 0 ? top : Math.Max(0, bottom - scroll.ViewportHeight);
        if (Math.Abs(delta) > 1) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + delta);
    }
    private async void Task_SaveRequested(object? sender, EventArgs e) => await SafeAsync(SaveRowEditAsync);
    private void Task_CancelRequested(object? sender, EventArgs e) => EndRowEdit();
    private async Task SaveRowEditAsync()
    {
        if (_savingRow || _editingCard?.Row is not { } row || _editingCard.ActiveEditor == null) return;
        var card = _editingCard; var content = card.ActiveEditor.GetContent();
        _savingRow = true; card.IsEnabled = false; _pendingMutations++;
        try { await Model.SaveTaskAsync(row, content, card.EditedProjectId); EndRowEdit(); }
        finally { _pendingMutations--; _savingRow = false; card.IsEnabled = true; }
    }
    private void EndRowEdit()
    {
        _editRevealPending = false;
        if (_editingCard == null) return;
        if (_editingCard.Row is { } row)
        {
            row.IsEditing = false;
            if (TaskList.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container) VirtualizingPanel.SetIsContainerVirtualizable(container, true);
        }
        _editingCard.EndEdit(); _editingCard = null;
    }
    private async Task<bool> TryLeaveRowEditAsync()
    {
        while (_savingRow) await Task.Delay(20);
        if (_editingCard is not { HasPendingChanges: true }) { EndRowEdit(); return true; }
        if (!IsVisible) RestoreFromTray();
        int choice = await Dialogs.Choose(this, "还有未保存的修改", "离开前，要保存这条任务的修改吗？", "取消", "放弃修改", "保存");
        if (choice <= 0) return false;
        if (choice == 2) { try { await SaveRowEditAsync(); } catch (Exception ex) { ShowError(ex); return false; } }
        else EndRowEdit(); return true;
    }
    private async Task<bool> TryLeaveEditsAsync()
    {
        if (!await TryLeaveRowEditAsync()) return false;
        if (!string.IsNullOrWhiteSpace(DraftEditor.GetContent().PlainText))
        {
            if (!IsVisible) RestoreFromTray();
            var choice = await Dialogs.Choose(this, "还有一件事没记下来", "输入栏中有未提交的任务，要先保存吗？", "取消", "放弃草稿", "保存");
            if (choice <= 0) return false;
            if (choice == 2) { try { await AddDraftAsync(); } catch (Exception ex) { ShowError(ex); return false; } }
            else DraftEditor.SetContent(RichContent.FromText(""));
        }
        return true;
    }
    private async void TaskList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_restoring || _editingCard?.IsExpandingEdit == true || !_initialized || e.OriginalSource is not ScrollViewer scroll || e.VerticalChange == 0 ||
            !ReferenceEquals(scroll, Descendants<ScrollViewer>(TaskList).FirstOrDefault()) || (!_scrollbarGesture && _scrollIntent == null)) return;
        if (scroll.VerticalOffset < 150 && e.VerticalChange < 0 && (_scrollbarGesture || _scrollIntent == PageDirection.Older))
        { _scrollIntent = null; await SafeAsync(() => Model.LoadPageAsync(PageDirection.Older)); }
        else if (scroll.ScrollableHeight - scroll.VerticalOffset < 300 && e.VerticalChange > 0 && (_scrollbarGesture || _scrollIntent == PageDirection.Newer))
        { _scrollIntent = null; await SafeAsync(() => Model.LoadPageAsync(PageDirection.Newer)); }
    }
    private void ScrollToLatest()
    {
        var epoch = Model.QueryEpoch;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (epoch != Model.QueryEpoch || Model.Tasks.Count == 0) return;
            _restoring = true;
            TaskList.ScrollIntoView(Model.Tasks[^1]); TaskList.UpdateLayout();
            Descendants<ScrollViewer>(TaskList).FirstOrDefault()?.ScrollToEnd();
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => _restoring = false));
        }));
    }
    private void OpenRepository_Click(object sender, RoutedEventArgs e)
    {
        try { OpenExternal(ApplicationLinks.RepositoryUrl); }
        catch (Exception) { Model.Message = "无法打开浏览器，请访问 " + ApplicationLinks.RepositoryUrl; }
    }
    private void CaptureAnchor()
    {
        _anchor = null;
        foreach (var card in Descendants<TaskCard>(TaskList))
        {
            var y = card.TransformToAncestor(TaskList).Transform(new Point()).Y;
            if (card.Row != null && y + card.ActualHeight > 0) { _anchor = (card.Row.Id, y); break; }
        }
        _restoring = true;
    }
    private void RestoreAnchor()
    {
        var anchor = _anchor; var epoch = Model.QueryEpoch;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                if (epoch != Model.QueryEpoch || anchor == null) return;
                var row = Model.Tasks.FirstOrDefault(t => t.Id == anchor.Value.Id); if (row == null) return;
                TaskList.ScrollIntoView(row); TaskList.UpdateLayout();
                var card = FindCard(row); var scroll = Descendants<ScrollViewer>(TaskList).FirstOrDefault();
                if (card != null && scroll != null) { var y = card.TransformToAncestor(TaskList).Transform(new Point()).Y; scroll.ScrollToVerticalOffset(scroll.VerticalOffset + y - anchor.Value.Y); }
            }
            finally { Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => _restoring = false)); }
        }));
    }
    public TaskCard? FindCard(TaskViewModel row) => Descendants<TaskCard>(TaskList).FirstOrDefault(c => ReferenceEquals(c.Row, row));
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private bool _settingsClosing;
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsClosing) return;
        if (SettingsPanel.Visibility == Visibility.Visible) { CloseSettings_Click(sender, e); return; }
        SettingsPanel.Visibility = Visibility.Visible; Motion.Reveal(SettingsPanel, 32, 200);
    }
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Popup children live in a separate visual root and do not reach this handler.
        bool Within(DependencyObject root)
        {
            var pending = new Queue<DependencyObject>(); var visited = new HashSet<DependencyObject>();
            if (e.OriginalSource is DependencyObject source) pending.Enqueue(source);
            while (pending.TryDequeue(out var node))
            {
                if (!visited.Add(node)) continue;
                if (ReferenceEquals(node, root)) return true;
                void Add(DependencyObject? parent) { if (parent != null) pending.Enqueue(parent); }
                if (node is Visual) Add(VisualTreeHelper.GetParent(node));
                Add(LogicalTreeHelper.GetParent(node));
                if (node is FrameworkElement element) Add(element.TemplatedParent);
                if (node is Popup popup) Add(popup.PlacementTarget);
                if (node is ContextMenu menu) Add(menu.PlacementTarget);
            }
            return false;
        }
        if (_datePopup?.IsOpen == true && !Within(CalendarButton)) _datePopup.IsOpen = false;
        if (SettingsPanel.Visibility == Visibility.Visible && !Within(SettingsPanel) && !Within(SettingsButton))
            CloseSettings_Click(sender, e);
    }
    private async void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsClosing) return;
        _settingsClosing = true;
        try { await Motion.HideAsync(SettingsPanel); }
        finally { _settingsClosing = false; }
    }
    private bool _settingsSync, _settingsApplyQueued;
    private Appearance? _pendingAppearance;
    private sealed record Appearance(string? Theme, double FontSize, string Density, bool ReduceMotion)
    {
        public static Appearance Read(AppSettings s) => new(s.ThemeId, s.FontSize, s.Density, s.ReduceMotion);
        public void Write(AppSettings s) { s.ThemeId = Theme; s.FontSize = FontSize; s.Density = Density; s.ReduceMotion = ReduceMotion; }
    }
    internal void SyncSettings()
    {
        var prior = _settingsSync; _settingsSync = true; var s = Model.Settings;
        try
        {
            ThemeSetting.SelectedValue = s.ThemeId; FontSetting.SelectedItem = s.FontSize;
            DensitySetting.SelectedItem = s.Density; MotionSetting.SelectedIndex = s.ReduceMotion ? 1 : 0;
        }
        finally { _settingsSync = prior; }
        if (OpenColorSetting != null) { OpenColorSetting.Text = Model.ReportOpenColor ?? TaskReport.Color(ReportColor.Open); VerificationColorSetting.Text = Model.ReportVerificationColor ?? TaskReport.Color(ReportColor.Verification); CompletedColorSetting.Text = Model.ReportCompletedColor ?? TaskReport.Color(ReportColor.Completed); }
    }
    private void StatusColor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box || !System.Text.RegularExpressions.Regex.IsMatch(box.Text.Trim(), "^#[0-9a-fA-F]{6}$")) return;
        var swatch = box == OpenColorSetting ? OpenColorPreview : box == VerificationColorSetting ? VerificationColorPreview : CompletedColorPreview;
        if (swatch != null) swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(box.Text.Trim()));
    }
    private readonly HashSet<TextBox> _colorMenus = [];
    private readonly Dictionary<TextBox, int> _colorValidationVersions = [];
    private void StatusColor_MenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is TextBox box) _colorMenus.Add(box);
    }
    private void StatusColor_MenuClosing(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TextBox box) return;
        _colorMenus.Remove(box);
        QueueStatusColorValidation(box);
    }
    private void StatusColor_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) QueueStatusColorValidation(box);
    }
    private void QueueStatusColorValidation(TextBox box)
    {
        if (!_initialized || Model.Repository == null) return;
        var repository = Model.Repository;
        var value = box.Text.Trim();
        var version = _colorValidationVersions.GetValueOrDefault(box) + 1;
        _colorValidationVersions[box] = version;
        // Opening/closing a native TextBox menu temporarily moves keyboard focus.
        // Let that transition finish before deciding whether editing actually ended.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(async () =>
        {
            if (_colorValidationVersions[box] != version || _colorMenus.Contains(box) || box.IsKeyboardFocusWithin ||
                !ReferenceEquals(repository, Model.Repository) || value != box.Text.Trim()) return;
            var key = box == OpenColorSetting ? "Open" : box == VerificationColorSetting ? "Verification" : "Completed";
            var saved = box == OpenColorSetting ? Model.ReportOpenColor : box == VerificationColorSetting ? Model.ReportVerificationColor : Model.ReportCompletedColor;
            if (string.Equals(value, saved, StringComparison.OrdinalIgnoreCase)) return;
            await SafeAsync(() => Model.SetStatusColorAsync(key, value));
        }));
    }
    private void Setting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsSync || !_initialized || sender is not ComboBox { SelectedItem: not null } selector || e.OriginalSource != sender) return;
        var next = _pendingAppearance ?? Appearance.Read(Model.Settings);
        if (selector == ThemeSetting) next = next with { Theme = ((ThemeService.ThemeOption)selector.SelectedItem).Id };
        else if (selector == FontSetting) next = next with { FontSize = (double)selector.SelectedItem };
        else if (selector == DensitySetting) next = next with { Density = (string)selector.SelectedItem };
        else if (selector == MotionSetting) next = next with { ReduceMotion = selector.SelectedIndex == 1 };
        QueueAppearance(next);
    }
    private void QueueAppearance(Appearance next)
    {
        _pendingAppearance = next;
        if (_settingsApplyQueued) return;
        _settingsApplyQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { _settingsApplyQueued = false; CommitAppearance(); }));
    }
    private bool CommitAppearance()
    {
        if (_pendingAppearance is not { } next) return true;
        _pendingAppearance = null;
        var previous = Appearance.Read(Model.Settings);
        var prior = _settingsSync; _settingsSync = true;
        try
        {
            next.Write(Model.Settings);
            ThemeService.Apply(Model.Settings);
            Model.SaveSettings();
            SyncSettings();
            return true;
        }
        catch (Exception ex)
        {
            previous.Write(Model.Settings);
            try { ThemeService.Apply(Model.Settings); SyncSettings(); }
            catch (Exception restoreError) { ex = new AggregateException(ex, restoreError); }
            ShowError(ex); return false;
        }
        finally { _settingsSync = prior; }
    }
    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        QueueAppearance(new(ThemeCatalog.DefaultId, 14, "舒适", !SystemParameters.ClientAreaAnimation));
    }
    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenDirectory(Model.Library.DataDirectory);
    private void OpenBackup_Click(object sender, RoutedEventArgs e) => OpenDirectory(Model.Library.BackupDirectory);
    private void OpenDirectory(string path) { try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { ShowError(ex); } }
    private void OnPreferencesChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => ThemeService.Apply(Model.Settings));
    internal void RestoreFromTray()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RestoreFromTray); return; }
        if (_closingApproved || Dispatcher.HasShutdownStarted) return;
        ShowInTaskbar = true; Show();
        WindowState = _stateBeforeTray == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        var handle = new WindowInteropHelper(this).Handle;
        if (!SetForegroundWindow(handle))
        {
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Hwnd = handle, Flags = 2 | 12, Count = 3 };
            FlashWindowEx(ref info);
        }
    }
    internal void RequestExit()
    {
        if (_closePending || _closingApproved) return;
        (Application.Current as App)?.BeginExit();
        Close();
    }
    private void SaveWindowPlacement()
    {
        var bounds = RestoreBounds; var s = Model.Settings;
        s.Width = bounds.Width; s.Height = bounds.Height; s.Left = bounds.Left; s.Top = bounds.Top;
        s.Maximized = WindowState == WindowState.Maximized || _stateBeforeTray == WindowState.Maximized && !IsVisible;
        Model.SaveSettings();
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingApproved) return;
        var app = (App)Application.Current;
        var automated = Environment.GetCommandLineArgs().Any(a => a is "--ui-themes" or "--ui-smoke" or "--ui-perf" or "--ui-demo" or "--ui-typography" or "--ui-features");
        if (!app.ExitRequested && !automated)
        {
            e.Cancel = true;
            try
            {
                _stateBeforeTray = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
                SaveWindowPlacement(); ClosePopupsForExit(); ShowInTaskbar = false; Hide();
            }
            catch (Exception ex) { ShowInTaskbar = true; Show(); ShowError(ex); }
            return;
        }
        e.Cancel = true;
        if (_closePending) return;
        _closePending = true;
        try
        {
            // Let WPF finish cancelling this Close before a pending-edit dialog
            // restores the hidden window (Show is forbidden inside Closing).
            await Dispatcher.Yield(DispatcherPriority.Normal);
            while (_navigating || Model.IsBusy || _pendingMutations > 0) await Task.Delay(20);
            if (!await TryLeaveEditsAsync()) return;
            while (_pendingMutations > 0) await Task.Delay(20);
            if (!CommitAppearance()) return;
            SaveWindowPlacement();
            ClosePopupsForExit();
            app.PrepareExit();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            _closingApproved = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _closePending = false; if (!_closingApproved) app.CancelExit(); }
    }
    private void ClosePopupsForExit()
    {
        if (_datePopup != null) { _datePopup.PopupAnimation = PopupAnimation.None; _datePopup.IsOpen = false; }
        foreach (var combo in Descendants<ComboBox>(this)) combo.IsDropDownOpen = false;
        foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>().ToArray())
        {
            if (source.RootVisual == null) continue;
            foreach (var menu in Descendants<ContextMenu>(source.RootVisual).ToArray())
            {
                if (menu.Parent is Popup popup) popup.PopupAnimation = PopupAnimation.None;
                Motion.Finish(menu); menu.IsOpen = false;
            }
        }
        Motion.Finish(SettingsPanel);
        Mouse.Capture(null); Keyboard.ClearFocus();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Hwnd; public uint Flags; public uint Count; public uint Timeout; }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
}
