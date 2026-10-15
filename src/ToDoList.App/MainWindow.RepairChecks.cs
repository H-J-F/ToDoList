using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToDoList.App.Services;
using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.App;

public partial class MainWindow
{
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")] private static extern bool RepairSetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void RepairMouseEvent(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int size, [Out] nint[] layouts);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern nint ActivateKeyboardLayout(nint layout, uint flags);
    private async Task RunRepairUiChecksAsync()
    {
        var output = Path.GetFullPath(Path.Combine(Model.Library.DataDirectory, "..", "ui-results"));
        var checks = new List<string>(); var limitations = new List<string>(); string? failure = null;
        Window? auxiliary = null;
        var oldLayout = GetKeyboardLayout(0);
        async Task Settle() { await UiSettingsNavigationChecks.Settle(); UpdateLayout(); }
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        async Task Click(FrameworkElement element) { await UiSettingsNavigationChecks.Click(element); await Settle(); }
        async Task Press(byte key) { keybd_event(key, 0, 0, 0); keybd_event(key, 0, 2, 0); await Task.Delay(80); }
        async Task Edit(ViewModels.TaskViewModel row)
        {
            TaskList.ScrollIntoView(row); await Settle();
            Task_EditRequested(FindCard(row), EventArgs.Empty); await Task.Delay(400); await Settle();
            Check(_editingCard?.ActiveEditor != null, "Existing task enters editing");
        }
        try
        {
            var allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts")) + Path.DirectorySeparatorChar;
            if (!Model.Library.DataDirectory.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Model.HasBook)
                throw new InvalidOperationException("Repair checks require an empty artifacts/.../Data workspace.");
            Directory.CreateDirectory(output); _cloudTimer.Stop(); _calendar.Stop();
            ActivateAccountCheck(); await Settle();
            Check(GetForegroundWindow() == new WindowInteropHelper(this).Handle, "Test owns foreground before native input");
            var book = await Model.Library.CreateAsync("Repairs", "修复验收");
            await Model.RefreshBooksAsync(); await Model.OpenBookAsync(book); SyncSelectors(true);
            await Click(AccountButton);
            foreach (var (minutes, i) in CloudSyncSchedule.Intervals.Select((v, i) => (v, i)))
            {
                CloudInterval.Value = i; await Settle();
                Check(Model.Settings.CloudSyncIntervalMinutes == minutes && _guestLibrary.LoadSettings().CloudSyncIntervalMinutes == minutes, "Interval persists: " + minutes);
            }
            CloudInterval.Value = 0; await Click(CloseAccountButton);
            var input = (RichTextBox)DraftEditor.FindName("Editor");
            for (var i = 0; i < 3; i++)
            {
                DraftEditor.SetContent(RichContent.FromText("连续发送 " + i)); await Click(input); await Press(0x0D); await Settle();
                Check(DraftEditor.GetContent().PlainText == "" && input.IsKeyboardFocused && input.IsEnabled, "Send restores actual keyboard focus: " + i);
            }
            var duplicate = await Model.Repository!.AddProjectAsync("已有模块");
            await Model.RefreshProjectsAsync(); SyncSelectors();
            DraftProject.SelectedItem = Model.DraftProjects.Single(p => p.Id == "__new"); NewProjectName.Text = duplicate.Name;
            DraftEditor.SetContent(RichContent.FromText("发送失败应保留的草稿"));
            var sendFailed = false;
            try { await AddDraftAsync(); } catch (InvalidOperationException) { sendFailed = true; }
            Check(sendFailed && DraftEditor.IsEnabled && DraftEditor.GetContent().PlainText == "发送失败应保留的草稿", "Failed submission keeps the complete draft and re-enables input");
            DraftEditor.SetContent(RichContent.FromText("")); DraftProject.SelectedItem = Model.DraftProjects.First(); NewProjectName.Clear();
            var layouts = new nint[32]; var layoutCount = GetKeyboardLayoutList(layouts.Length, layouts);
            var chinese = layouts.Take(Math.Max(0, layoutCount)).FirstOrDefault(l => ((long)l & 0xffff) == 0x0804);
            if (chinese != 0)
            {
                ActivateKeyboardLayout(chinese, 0); InputMethod.SetPreferredImeState(input, InputMethodState.On);
                InputMethod.Current.ImeState = InputMethodState.On;
                for (var i = 0; i < 2; i++)
                {
                    await Click(input);
                    foreach (var imeKey in new byte[] { 0x4E, 0x49, 0x48, 0x41, 0x4F }) await Press(imeKey);
                    await Press(0x20); await Settle();
                    var text = DraftEditor.GetContent().PlainText;
                    if (!text.Any(c => c >= '\u4e00' && c <= '\u9fff'))
                    { limitations.Add("Installed Chinese layout did not produce Chinese composition; real IME acceptance is incomplete."); DraftEditor.SetContent(RichContent.FromText("")); break; }
                    Check(true, "Native Chinese IME inserts visible content after send: " + i);
                    await Press(0x0D); await Settle();
                    Check(input.IsKeyboardFocused && DraftEditor.GetContent().PlainText == "", "Chinese IME send retains keyboard focus: " + i);
                }
            }
            else limitations.Add("No Simplified Chinese keyboard layout was available; real IME acceptance is incomplete.");
            ActivateKeyboardLayout(oldLayout, 0);
            await Model.SelectFilterAsync(TaskFilter.All); SyncSelectors();
            var row = Model.Tasks.Last(); await Edit(row);
            _editingCard!.ActiveEditor!.SetContent(RichContent.FromText("自动失焦保存"));
            ((RichTextBox)_editingCard.ActiveEditor.FindName("Editor")).AppendText("。");
            await Click(input);
            Check(_editingCard == null && row.Item.PlainText == "自动失焦保存。", "Native outside click saves existing task and ends edit");
            await Edit(row);
            await Click((FrameworkElement)_editingCard!.ActiveEditor!.FindName("ColorButton"));
            Check(_editingCard.ActiveEditor != null && _editingCard.ActiveEditor.HasToolInteraction, "Color popup remains inside editing scope");
            _editingCard!.ActiveEditor!.CloseTool(); _editingCard.ActiveEditor.FocusEditor(); await Settle();
            Check(_editingCard != null, "Closing editor popup retains editing");
            await Click(_editingCard!.ActiveProjectSelector!);
            Check(_editingCard.ActiveEditor != null, "Module dropdown does not save edit");
            _editingCard.ActiveProjectSelector!.IsDropDownOpen = false; _editingCard.ActiveEditor!.FocusEditor();
            _editingCard.ActiveEditor.SetContent(RichContent.FromText("切换窗口保存"));
            ((RichTextBox)_editingCard.ActiveEditor.FindName("Editor")).AppendText("。");
            auxiliary = new Window { Title = "隔离焦点验收", Style = null, Width = 240, Height = 120, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            auxiliary.Show(); auxiliary.Activate(); SetForegroundWindow(new WindowInteropHelper(auxiliary).Handle); await Settle();
            Check(_editingCard == null && row.Item.PlainText == "切换窗口保存。", "Switching to another window saves and ends edit");
            auxiliary.Close(); auxiliary = null; ActivateAccountCheck(); await Settle(); await Edit(row);
            _editingCard!.ActiveEditor!.SetContent(RichContent.FromText("最小化保存"));
            ((RichTextBox)_editingCard.ActiveEditor.FindName("Editor")).AppendText("。");
            WindowState = WindowState.Minimized; await Settle();
            Check(_editingCard == null && row.Item.PlainText == "最小化保存。", "Minimizing saves and ends edit");
            RestoreFromTray(); ActivateAccountCheck(); await Settle();
            for (var i = 0; i < 220; i++) await Model.Repository!.AddTaskAsync(RichContent.FromText("分页测试 " + i), null);
            await Model.ReloadAsync(true); await Model.LoadPageAsync(PageDirection.Older); SyncSelectors(); await Settle();
            Check(Model.Tasks.Count > 200, "Multi-page window loaded before sync");
            var count = Model.Tasks.Count; var retained = Model.Tasks.Last();
            var storage = new CloudBookStorage(Model.Library); var before = await storage.CaptureAsync();
            var resets = 0; NotifyCollectionChangedEventHandler observed = (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
            Model.Tasks.CollectionChanged += observed; Model.Projects.CollectionChanged += observed; Model.Books.CollectionChanged += observed;
            DraftEditor.SetContent(RichContent.FromText("同步过程中保留草稿")); await Click(input);
            Check(await ApplySnapshotAsync(storage, before, before, CancellationToken.None), "No-op apply succeeds while drafting");
            Check(!Model.ShowBlockingOverlay && resets == 0 && input.IsKeyboardFocused, "No-op sync has no overlay, collection reset or focus loss");
            var desired = before.Copy(); var key = CloudSnapshot.Key(book.BookId, "task", retained.Id);
            desired.Values[key] = [CloudValue.From(retained.Item with { ContentJson = RichContent.FromText("远端修改").ToJson(), PlainText = "远端修改" })];
            Check(!await ApplySnapshotAsync(storage, before, desired, CancellationToken.None), "Actual changes defer while a draft is pending");
            DraftEditor.SetContent(RichContent.FromText("")); input.Focus(); await Settle();
            Check(await ApplySnapshotAsync(storage, before, desired, CancellationToken.None), "Remote changes apply after draft completes"); await Settle();
            Check(ReferenceEquals(retained, Model.Tasks.Single(t => t.Id == retained.Id)) && retained.Item.PlainText == "远端修改", "Changed task retains view-model identity");
            Check(Model.Tasks.Count >= count && resets == 0 && input.IsKeyboardFocused, "Incremental sync preserves pagination and focus without Reset");
            before = await storage.CaptureAsync(); desired = before.Copy();
            var metaKey = CloudSnapshot.Key(book.BookId, "book", book.BookId);
            desired.Values[metaKey] = [CloudValue.From(new CloudBookMeta(book.Name, "云端重命名"))];
            Check(await ApplySnapshotAsync(storage, before, desired, CancellationToken.None), "Rename applies incrementally");
            Check(Model.CurrentBook!.BookId == book.BookId && Model.BookTitle == "云端重命名" && input.IsKeyboardFocused && resets == 0, "Rename preserves ID, input and lists");
            Model.Tasks.CollectionChanged -= observed; Model.Projects.CollectionChanged -= observed; Model.Books.CollectionChanged -= observed;
            await Edit(Model.Tasks.Last());
            _editingCard!.ActiveEditor!.SetContent(RichContent.FromText(""));
            ((RichTextBox)_editingCard.ActiveEditor.FindName("Editor")).AppendText(" ");
            Check(!await TryLeaveRowEditAsync() && _editingCard != null, "Invalid auto-save retains editing and blocks navigation");
            await Settle();
            var saveError = Descendants<Wpf.Ui.Controls.ContentDialog>(this).FirstOrDefault(d => d.IsVisible);
            saveError?.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Close); await Settle();
            EndRowEdit();
            before = await storage.CaptureAsync(); await Edit(Model.Tasks.Last());
            desired = before.Copy(); desired.Values[metaKey] = [CloudValue.From(new CloudBookMeta(book.Name, "编辑期间远端变化"))];
            Check(!await ApplySnapshotAsync(storage, before, desired, CancellationToken.None) && _editingCard != null, "Synchronization does not end an active existing-task edit");
            EndRowEdit();
            var targetBook = await Model.Library.CreateAsync("MigrationTarget", "迁移目标");
            await new TaskRepository(targetBook.Path).AddProjectAsync(duplicate.Name);
            await Model.RefreshBooksAsync(); SyncSelectors(); await Settle();
            Check(Model.CurrentProjectId == null, "All remains selected before module right click");
            ProjectList.ScrollIntoView(Model.Projects.Single(p => p.Id == duplicate.Id)); await Settle();
            var projectContainer = (FrameworkElement)ProjectList.ItemContainerGenerator.ContainerFromItem(Model.Projects.Single(p => p.Id == duplicate.Id));
            var point = projectContainer.PointToScreen(new Point(projectContainer.ActualWidth / 2, projectContainer.ActualHeight / 2));
            RepairSetCursorPos((int)point.X, (int)point.Y); RepairMouseEvent(8, 0, 0, 0, 0); RepairMouseEvent(16, 0, 0, 0, 0); await Settle();
            var migrationMenu = ProjectList.ContextMenu.Items.OfType<MenuItem>().Single(i => ((string)i.Header).StartsWith("迁移"));
            Check(ProjectList.ContextMenu.IsOpen && migrationMenu.IsEnabled && SelectedRealProject()?.Id == duplicate.Id, "Native right click targets the clicked module while All is selected");
            await Click(migrationMenu);
            var targetDialog = Descendants<Wpf.Ui.Controls.ContentDialog>(this).Single(d => d.IsVisible);
            Check(targetDialog.Title?.ToString() == "迁移模块", "Migration menu opens target selector");
            targetDialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Primary); await Settle();
            var collisionDialog = Descendants<Wpf.Ui.Controls.ContentDialog>(this).Single(d => d.IsVisible);
            Check(collisionDialog.Title?.ToString() == "目标已有同名模块", "Same-name migration offers a warning and explicit choices");
            collisionDialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Secondary); await Settle(); await Task.Delay(350);
            Check(!(await Model.Repository!.GetProjectsAsync()).Any(p => p.Id == duplicate.Id) && (await new TaskRepository(targetBook.Path).GetProjectsAsync()).Count == 1, "Menu-driven merge migration completes without a duplicate module");
            Check(Model.CurrentBook?.BookId == book.BookId && Model.CurrentProjectId == null, "Migration retains source notebook navigation");
            await Click(AccountButton); await Settle();
            var dpi = VisualTreeHelper.GetDpi(this); var bmp = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var file = File.Create(Path.Combine(output, "repairs.png"))) encoder.Save(file);
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            auxiliary?.Close(); ActivateKeyboardLayout(oldLayout, 0);
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "result.json"), System.Text.Json.JsonSerializer.Serialize(new { Completed = true, Passed = failure == null, Checks = checks, Limitations = limitations, Failure = failure, InputMode = "OS keyboard and mouse with WPF assertions" }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            EndRowEdit(); DraftEditor.SetContent(RichContent.FromText("")); RequestExit();
        }
    }
}
