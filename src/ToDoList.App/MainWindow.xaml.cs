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
    internal RichEditor TaskEditor => _taskEditor ??= new RichEditor { MinHeight = 110, MaxHeight = 240 };
    private (string Id, double Y)? _anchor;
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _calendar = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _settingsSave = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private DateTime _today = DateTime.Today;
    private string _zone = TimeZoneInfo.Local.Id;
    private int _pendingMutations;
    private PageDirection? _scrollIntent;
    private bool _scrollbarGesture;
    private bool _savingRow, _closePending;
    public MainWindow(BookLibrary library, AppSettings settings)
    {
        Model = new(library, settings); InitializeComponent(); DataContext = Model;
        Width = Math.Clamp(settings.Width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        Height = Math.Clamp(settings.Height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        if (settings.Left.HasValue && settings.Top.HasValue && settings.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 && settings.Left + Width > SystemParameters.VirtualScreenLeft + 100 && settings.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50 && settings.Top >= SystemParameters.VirtualScreenTop)
        { Left = settings.Left.Value; Top = settings.Top.Value; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (settings.Maximized) WindowState = WindowState.Maximized;
        var bitmap = new BitmapImage(new Uri("pack://application:,,,/Assets/app-icon.png")); bitmap.Freeze();
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
        WelcomeIcon.Source = AboutIcon.Source = bitmap;
        ModeSetting.ItemsSource = new[] { "浅色", "深色", "跟随系统" };
        PaletteSetting.ItemsSource = ThemeService.Accents;
        FontSetting.ItemsSource = new double[] { 12, 14, 16, 18 };
        DensitySetting.ItemsSource = new[] { "紧凑", "舒适" }; MotionSetting.ItemsSource = new[] { "标准", "减少动态效果" };
        SyncSettings();
        Model.LayoutChanging += CaptureAnchor; Model.LayoutChanged += RestoreAnchor;
        Model.LatestRequested += ScrollToLatest;
        Model.IsEditing = () => _editingCard != null;
        Model.AnimateRemoval = row => FindCard(row)?.AnimateOutAsync(Model.Settings.ReduceMotion) ?? Task.CompletedTask;
        TaskList.PreviewMouseWheel += (_, e) => _scrollIntent = e.Delta > 0 ? PageDirection.Older : PageDirection.Newer;
        TaskList.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.PageUp or Key.Home) _scrollIntent = PageDirection.Older;
            else if (e.Key is Key.Down or Key.PageDown or Key.End) _scrollIntent = PageDirection.Newer;
        };
        TaskList.PreviewMouseDown += (_, e) =>
        {
            for (var d = e.OriginalSource as DependencyObject; d is Visual; d = VisualTreeHelper.GetParent(d))
                if (d is ScrollBar) { _scrollbarGesture = true; break; }
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
            DraftEditor.Height = compact ? 80 : 92;
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
            else new Wpf.Ui.Controls.Snackbar(Notifications) { Content = Model.Message, MinWidth = 280, MaxWidth = 460, Timeout = TimeSpan.FromSeconds(2) }.Show();
        };
        Closed += (_, _) => { _feedbackTimer.Stop(); _calendar.Stop(); _settingsSave.Stop(); SystemEvents.UserPreferenceChanged -= OnPreferencesChanged; };
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await SafeAsync(async () => { Model.IsBusy = true; await Model.InitializeAsync(); SyncSelectors(); });
        Model.IsBusy = false; _sync = false; _initialized = true; _calendar.Start();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!IsLoaded) return;
            if (TaskEditor.Parent != null) return;
            TaskEditor.ApplyTemplate();
            TaskEditor.Measure(new Size(Math.Max(100, TaskList.ActualWidth - 60), double.PositiveInfinity));
        }));
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--ui-smoke")) await Services.UiSmoke.RunAsync(this);
        else if (args.Contains("--ui-typography")) await Services.UiTypography.RunAsync(this);
        else if (args.Contains("--ui-demo")) await Services.UiDemo.RunAsync(this);
        else if (args.Contains("--ui-perf")) await Services.UiPerformance.RunAsync(this);
    }
    private void SyncSelectors()
    {
        var previous = _sync; _sync = true;
        BookSelector.SelectedItem = Model.Books.FirstOrDefault(b => b.Name == Model.CurrentBook?.Name);
        ProjectList.SelectedItem = Model.Projects.FirstOrDefault(p => p.Id == Model.CurrentProjectId);
        DraftProject.SelectedItem = Model.DraftProjects.FirstOrDefault(p => p.Id == Model.CurrentProjectId) ?? Model.DraftProjects.FirstOrDefault();
        foreach (TabItem tab in Tabs.Items) tab.IsSelected = (string)tab.Tag == Model.Filter.ToString();
        SortSelector.SelectedIndex = Model.Sort == TaskSort.Created ? 0 : 1;
        NewProjectName.Visibility = Visibility.Collapsed; _sync = previous;
        Title = "ToDoList · " + Model.BookTitle;
    }
    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { } catch (Exception ex) { ShowError(ex); }
    }
    private void ShowError(Exception ex) { Model.Message = "未能完成：" + ex.Message; _ = Dialogs.Info(this, "操作未完成", ex.Message); }
    private async Task NavigateAsync(Func<Task> action, bool bookOperation = false)
    {
        if (_navigating) { SyncSelectors(); return; }
        _navigating = true;
        try
        {
            if (!await TryLeaveEditsAsync()) return;
            if (bookOperation)
            {
                Model.IsBusy = true;
                while (_pendingMutations > 0) await Task.Delay(20);
            }
            _sync = true; await action(); Motion.Reveal(TaskContent);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _sync = false; _navigating = false; Model.IsBusy = false; SyncSelectors(); }
    }
    private async void Book_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || !_initialized || BookSelector.SelectedItem is not BookInfo book || book.Name == Model.CurrentBook?.Name) return;
        await NavigateAsync(() => Model.OpenBookAsync(book), true);
    }
    private async void Project_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || !_initialized || ProjectList.SelectedItem is not ProjectInfo project || project.Id == Model.CurrentProjectId) return;
        await NavigateAsync(() => Model.SelectProjectAsync(project.Id));
    }
    private async void Tab_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || !_initialized || !ReferenceEquals(e.Source, Tabs) || Tabs.SelectedItem is not TabItem tab) return;
        var filter = Enum.Parse<TaskFilter>((string)tab.Tag);
        if (filter == Model.Filter) return;
        await NavigateAsync(() => Model.SelectFilterAsync(filter));
    }
    private async void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || !_initialized || SortSelector.SelectedIndex < 0) return;
        var sort = SortSelector.SelectedIndex == 0 ? TaskSort.Created : TaskSort.Completed;
        if (sort != Model.Sort) await NavigateAsync(() => Model.SelectSortAsync(sort));
    }
    private void DraftProject_Changed(object sender, SelectionChangedEventArgs e)
    {
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
    private void BookMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu(); var import = new MenuItem { Header = "导入待办书…", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.ArrowDownload20) }; import.Click += Import_Click; menu.Items.Add(import);
        var export = new MenuItem { Header = "导出当前待办书…", Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.ArrowUpload20), IsEnabled = Model.HasBook }; export.Click += Export_Click; menu.Items.Add(export);
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ToDoList 待办书 (*.db)|*.db", Title = "导入一本待办书" };
        if (dialog.ShowDialog(this) != true) return;
        await NavigateAsync(async () =>
        {
            using var prepared = await Model.Library.PrepareImportAsync(dialog.FileName);
            var existing = Model.Books.FirstOrDefault(b => b.Name.Equals(prepared.Info.Name, StringComparison.OrdinalIgnoreCase));
            var mode = ImportMode.Merge;
            if (existing != null)
            {
                var choice = await Dialogs.Choose(this, "已有相同的待办书", $"标识名：{existing.Name}\n\n合并：保留二者任务，同一任务采用最后修改的版本。\n覆盖：使用导入书替换现有书。\n\n原书会先备份到：\n{Model.Library.BackupDirectory}", "取消", "合并", "覆盖");
                if (choice < 1) return; mode = choice == 1 ? ImportMode.Merge : ImportMode.Replace;
            }
            var imported = await Model.Library.ImportAsync(prepared, existing, mode);
            await Model.RefreshBooksAsync(); await Model.OpenBookAsync(imported);
            Model.Message = existing == null ? "待办书已导入。" : "导入完成，原书已保存到 Data/Backup。";
        }, true);
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Model.CurrentBook == null) return;
        var dialog = new SaveFileDialog { Filter = "ToDoList 待办书 (*.db)|*.db", FileName = Model.CurrentBook.Name + ".db", Title = "导出整本待办书" };
        if (dialog.ShowDialog(this) != true) return;
        await NavigateAsync(async () => { await Model.Library.ExportAsync(Model.CurrentBook, dialog.FileName); Model.Message = "待办书已导出到 " + dialog.FileName; }, true);
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
        try { await SafeAsync(() => Model.DeleteRestoreAsync(row)); }
        finally { _pendingMutations--; }
    }
    private async void Task_EditRequested(object? sender, EventArgs e)
    {
        if (sender is not TaskCard card || card.Row is not { IsBusy: false, IsDeleted: false } || card == _editingCard) return;
        if (!await TryLeaveRowEditAsync()) return;
        _editingCard = card;
        if (TaskList.ItemContainerGenerator.ContainerFromItem(card.Row) is DependencyObject container) VirtualizingPanel.SetIsContainerVirtualizable(container, false);
        card.BeginEdit();
    }
    private async void Task_SaveRequested(object? sender, EventArgs e) => await SafeAsync(SaveRowEditAsync);
    private void Task_CancelRequested(object? sender, EventArgs e) => EndRowEdit();
    private async Task SaveRowEditAsync()
    {
        if (_savingRow || _editingCard?.Row is not { } row || _editingCard.ActiveEditor == null) return;
        var card = _editingCard; var content = card.ActiveEditor.GetContent();
        _savingRow = true; card.IsEnabled = false; _pendingMutations++;
        try { await Model.SaveContentAsync(row, content); EndRowEdit(); }
        finally { _pendingMutations--; _savingRow = false; card.IsEnabled = true; }
    }
    private void EndRowEdit()
    {
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
        if (_editingCard?.ActiveEditor is not { Dirty: true }) { EndRowEdit(); return true; }
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
            var choice = await Dialogs.Choose(this, "还有一件事没记下来", "输入栏中有未提交的任务，要先保存吗？", "取消", "放弃草稿", "保存");
            if (choice <= 0) return false;
            if (choice == 2) { try { await AddDraftAsync(); } catch (Exception ex) { ShowError(ex); return false; } }
            else DraftEditor.SetContent(RichContent.FromText(""));
        }
        return true;
    }
    private async void TaskList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_restoring || !_initialized || e.OriginalSource is not ScrollViewer scroll || e.VerticalChange == 0 ||
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
    private void Settings_Click(object sender, RoutedEventArgs e) { if (_settingsClosing) return; SettingsPanel.Visibility = Visibility.Visible; Motion.Reveal(SettingsPanel, 32, 200); }
    private async void CloseSettings_Click(object sender, RoutedEventArgs e) { if (_settingsClosing) return; _settingsClosing = true; await Motion.HideAsync(SettingsPanel); _settingsClosing = false; }
    internal void SyncSettings()
    {
        var prior = _sync; _sync = true; var s = Model.Settings;
        ModeSetting.SelectedItem = s.Mode; PaletteSetting.SelectedValue = s.AccentPreset; FontSetting.SelectedItem = s.FontSize;
        DensitySetting.SelectedItem = s.Density; MotionSetting.SelectedIndex = s.ReduceMotion ? 1 : 0; _sync = prior;
    }
    private void Setting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || !_initialized || sender is not ComboBox { SelectedItem: not null } selector || e.OriginalSource != sender) return;
        var s = Model.Settings;
        if (selector == ModeSetting) s.Mode = (string)selector.SelectedItem;
        else if (selector == PaletteSetting) s.AccentPreset = ((ThemeService.AccentOption)selector.SelectedItem).Name;
        else if (selector == FontSetting) s.FontSize = (double)selector.SelectedItem;
        else if (selector == DensitySetting) s.Density = (string)selector.SelectedItem;
        else if (selector == MotionSetting) s.ReduceMotion = selector.SelectedIndex == 1;
        // Theme replacement can raise selection events while templates are rebuilt.
        _sync = true;
        try { ThemeService.Apply(s); }
        finally { _sync = false; }
        _settingsSave.Stop(); _settingsSave.Start();
    }
    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = Model.Settings; s.Mode = "浅色"; s.AccentPreset = "浅蓝"; s.FontSize = 14; s.Density = "舒适"; s.ReduceMotion = !SystemParameters.ClientAreaAnimation;
        SyncSettings(); ThemeService.Apply(s); _settingsSave.Start();
    }
    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenDirectory(Model.Library.DataDirectory);
    private void OpenBackup_Click(object sender, RoutedEventArgs e) => OpenDirectory(Model.Library.BackupDirectory);
    private void OpenDirectory(string path) { try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { ShowError(ex); } }
    private void OnPreferencesChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => ThemeService.Apply(Model.Settings));
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingApproved) return; e.Cancel = true;
        if (_closePending) return;
        if (_navigating || Model.IsBusy) { Model.Message = "正在保存待办书，请稍候再关闭。"; return; }
        _closePending = true;
        try
        {
            if (!await TryLeaveEditsAsync()) return;
            while (_pendingMutations > 0) await Task.Delay(20);
            var bounds = RestoreBounds; var s = Model.Settings;
            s.Width = bounds.Width; s.Height = bounds.Height; s.Left = bounds.Left; s.Top = bounds.Top; s.Maximized = WindowState == WindowState.Maximized;
            Model.SaveSettings(); _closingApproved = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _closePending = false; }
    }
}
