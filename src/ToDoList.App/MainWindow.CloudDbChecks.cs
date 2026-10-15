using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToDoList.App.Services;
using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.App;

public partial class MainWindow
{
    private async Task RunCloudDbUiChecksAsync()
    {
        var checks = new List<string>(); string? failure = null;
        var output = Path.GetFullPath(Path.Combine(Model.Library.DataDirectory, "..", "ui-results"));
        var routed = Environment.GetCommandLineArgs().Contains("--cloud-db-routed-input");
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        async Task Settle(int ms = 80) { await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle); await Task.Delay(ms); UpdateLayout(); }
        void Capture(string name)
        {
            UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(this); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, name + ".png")); png.Save(stream);
        }
        try
        {
            var allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts")) + Path.DirectorySeparatorChar;
            if (!Model.Library.DataDirectory.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Model.HasBook) throw new InvalidOperationException("Cloud DB UI checks require an isolated empty artifacts workspace.");
            Directory.CreateDirectory(output); _cloudTimer.Stop(); _calendar.Stop(); ActivateAccountCheck();
            Width = 1100; Height = 760; Model.Settings.ReduceMotion = false; ThemeService.Apply(Model.Settings); await Settle();
            var cloud = new UndoCheckCloud();
            var seedLibrary = new BookLibrary(Path.Combine(output, "Seed", "Data")); var seedStorage = new CloudBookStorage(seedLibrary);
            var book = await seedLibrary.CreateAsync("CloudUi", "云 DB 界面验证"); var seedRepo = new TaskRepository(book.Path);
            await seedRepo.AddProjectAsync("保持不变的模块");
            for (var i = 0; i < 24; i++) await seedRepo.AddTaskAsync(RichContent.FromText("合成任务 " + i), null);
            await new SyncCoordinator(cloud, seedStorage, UndoCheckCloud.Account).SyncAsync(SyncDirection.Both, async (a, b, ct) => { await seedStorage.ApplyAsync(a, b, ct); return true; });
            _cloudAccount = UndoCheckCloud.Account; _cloudBooks = new(Model.Library); _cloudCoordinator = new(cloud, _cloudBooks, _cloudAccount);
            cloud.ReadReached = new(); cloud.ReleaseRead = new();
            var loading = RunCloudSyncAsync(SyncDirection.Both, true);
            await cloud.ReadReached.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Settle();
            Check(Model.ShowBlockingOverlay && BlockingOverlay.IsVisible && !Model.LoadingIndeterminate && Model.LoadingMessage.Contains("下载") && Model.LoadingMessage.Contains("0/1"), "Slow initial download displays actual file progress in startup loader"); Capture("initial-download");
            cloud.ReleaseRead.TrySetResult(); await loading; await Settle();
            Check(!Model.ShowBlockingOverlay && !BlockingOverlay.IsVisible && Model.Tasks.Count == 24, "Successful initialization dismisses loader and displays all seed tasks");
            Check(CloudSyncDuration.Text.StartsWith(" | 耗时: ") && CloudSyncDuration.Parent == CloudLastSync.Parent, "Timestamp and separated duration occupy the same horizontal row");
            AccountPanel.Visibility = Visibility.Visible; await Settle();
            Check(Math.Abs(CloudLastSync.TranslatePoint(new Point(), this).Y - CloudSyncDuration.TranslatePoint(new Point(), this).Y) < 1 && CloudSyncDuration.ActualWidth > 0 && CloudSyncDuration.TranslatePoint(new Point(CloudSyncDuration.ActualWidth, 0), AccountPanel).X < AccountPanel.ActualWidth, "One-line timestamp and duration fit account panel"); Capture("account-sync-line"); AccountPanel.Visibility = Visibility.Collapsed;
            var frames = new List<(double Opacity, double X)>();
            void Frame(object? sender, EventArgs e) => frames.Add((Notifications.Opacity, ((TranslateTransform)Notifications.RenderTransform).X));
            System.Windows.Media.CompositionTarget.Rendering += Frame;
            Notifications.Show("动画验收"); await Settle(240);
            Check(frames.Any(v => v.X > 0 && v.Opacity is > 0 and < 1) && Notifications.Opacity > 0.99 && Math.Abs(((TranslateTransform)Notifications.RenderTransform).X) < 0.1, "Toast flies in from right while fading in"); Capture("toast-visible");
            frames.Clear(); Notifications.CloseAnimated(); await Settle(240);
            Check(frames.Any(v => v.X > 0 && v.Opacity is > 0 and < 1) && Notifications.Visibility == Visibility.Collapsed, "Toast flies out right while fading out before collapse");
            Notifications.Show("旧提示"); await Settle(70); Notifications.CloseAnimated(); await Settle(50); Notifications.Show("新提示"); await Settle(260);
            Check(Notifications.Visibility == Visibility.Visible && Notifications.MessageLabel.Text == "新提示" && Notifications.Opacity > 0.99, "Rapid replacement survives completion of an older close animation");
            Model.Settings.ReduceMotion = true; ThemeService.Apply(Model.Settings); frames.Clear(); Notifications.Show("减少动态效果"); await Settle(140);
            Check(frames.All(v => Math.Abs(v.X) < 0.1) && Notifications.Opacity > 0.99, "Reduced motion uses fade without horizontal travel");
            Notifications.CloseAnimated(); await Settle(140); Check(Notifications.Visibility == Visibility.Collapsed, "Reduced close animation reaches collapsed state");
            CompositionTarget.Rendering -= Frame; Model.Settings.ReduceMotion = false; ThemeService.Apply(Model.Settings);
            var kept = Model.Tasks[0]; var original = kept.Item.PlainText; var reset = 0; var overlayFrames = 0;
            var unrelated = Model.Tasks[1]; var unrelatedChanges = 0; unrelated.PropertyChanged += (_, _) => unrelatedChanges++;
            var disabledFrames = 0; var modulePixelsChanged = 0; var undoFrames = 0; byte[]? modulePixels = null;
            byte[] RenderModules()
            {
                var width = Math.Max(1, (int)Math.Ceiling(ProjectList.ActualWidth)); var height = Math.Max(1, (int)Math.Ceiling(ProjectList.ActualHeight));
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(ProjectList);
                var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0); return pixels;
            }
            Model.Tasks.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) reset++; };
            void UndoFrame(object? sender, EventArgs e)
            {
                undoFrames++; if (BlockingOverlay.IsVisible) overlayFrames++; if (!WorkArea.IsEnabled || !ProjectList.IsEnabled) disabledFrames++;
                var pixels = RenderModules(); if (modulePixels != null && !modulePixels.SequenceEqual(pixels)) modulePixelsChanged++;
            }
            var scroll = Descendants<ScrollViewer>(TaskList).First(); scroll.ScrollToVerticalOffset(120); await Settle(); var offset = scroll.VerticalOffset;
            DraftEditor.FocusEditor(); var focus = Keyboard.FocusedElement;
            modulePixels = RenderModules(); CompositionTarget.Rendering += UndoFrame;
            await Model.SaveTaskAsync(kept, RichContent.FromText("局部修改"), null); await Settle(260);
            if (routed) Notifications.UndoButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            else await UiSettingsNavigationChecks.Click(Notifications.UndoButton);
            await Settle(260); CompositionTarget.Rendering -= UndoFrame;
            Check(kept.Item.PlainText == original && ReferenceEquals(kept, Model.Tasks.First(t => t.Id == kept.Id)) && reset == 0 && overlayFrames == 0 && Math.Abs(scroll.VerticalOffset - offset) < 1 && ReferenceEquals(focus, Keyboard.FocusedElement), "Undo preserves row identity, focus, scroll and collection without any full-screen overlay frame");
            Check(undoFrames > 0 && disabledFrames == 0 && modulePixelsChanged == 0 && unrelatedChanges == 0, "Every undo frame keeps unchanged modules pixel-identical, enabled, and unrelated rows unnotified");
            var statusCard = FindCard(kept) ?? throw new InvalidOperationException("Visible task card missing."); var body = (TextBlock)statusCard.FindName("BodyText"); var inline = body.Inlines.FirstInline;
            kept.Item = kept.Item with { Status = TodoStatus.Completed };
            Check(ReferenceEquals(inline, body.Inlines.FirstInline), "Status update preserves existing rich-text inline objects");
            kept.Item = kept.Item with { Status = TodoStatus.Open };
            var renamed = await BookOperations.RenameAsync(Model.Library, Model.CurrentBook!, "当前导出标题");
            await Model.RefreshAfterCloudAsync(new(new HashSet<string> { CloudSnapshot.Key(renamed.BookId, "book", renamed.BookId) }));
            SyncSelectors();
            Check(ExportNames.FromTitle(Model.CurrentBook!.Title) == "当前导出标题" && Model.CurrentBook.Name == "CloudUi", "Export filename follows renamed title while internal name stays stable");
            Check(RuntimeLog.Writer is FileRuntimeLog logger && logger.DirectoryPath == Path.Combine(AppContext.BaseDirectory, "Log") && Directory.GetFiles(logger.DirectoryPath, "run-*.jsonl").Length > 0, "Release runtime writes logs beside its executable");
            var stableRow = Model.Tasks[1]; var resetsBeforeAdd = reset;
            await Model.AddAsync(RichContent.FromText("差量新增"), null, null); await Settle();
            Check(ReferenceEquals(stableRow, Model.Tasks.Single(t => t.Id == stableRow.Id)) && reset == resetsBeforeAdd, "Ordinary add refresh keeps existing row instances without collection reset");
            var menu = ProjectList.ContextMenu.Items.OfType<MenuItem>().Single(i => ((string)i.Header).StartsWith("迁移")); Check((string)menu.Header == "迁移到其他笔记", "Migration menu text exactly matches requested label");
            var cancelled = new UndoCheckCloud { InitializeReached = new(), ReleaseInitialize = new() };
            _cloudBooks = new(new BookLibrary(Path.Combine(output, "Cancel", "Data"))); _cloudCoordinator = new(cancelled, _cloudBooks, _cloudAccount);
            _cloudSchedule.Request(DateTime.UtcNow);
            var cancel = RunCloudSyncAsync(SyncDirection.Both, true); await cancelled.InitializeReached.Task; await Settle();
            Check(Model.ShowBlockingOverlay && Model.LoadingIndeterminate, "Unknown initial total uses indeterminate progress");
            if (routed) CancelInitialCloudLoad.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            else await UiSettingsNavigationChecks.Click(CancelInitialCloudLoad);
            await cancel; await Settle(); Check(!Model.ShowBlockingOverlay, "Actual cancel button dismisses initialization loader");
            Check(!_cloudSchedule.Due(DateTime.UtcNow.AddSeconds(6), true), "Cancelled automatic sync does not immediately retry before the next cycle");
            cancelled.InitializeReached = null; cancelled.Fail = true; await RunCloudSyncAsync(SyncDirection.Both, false); await Settle();
            Check(!Model.ShowBlockingOverlay && _cloudCoordinator.LastSyncedAt == null && _cloudText.Contains("合成同步失败"), "Failed initialization dismisses loader without recording successful sync");
            _lastSyncDuration = null; UpdateCloudControls(); Check(CloudSyncDuration.Visibility == Visibility.Collapsed && CloudSyncDuration.Text == "", "Missing duration removes separator as well");
            Capture("final");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            Directory.CreateDirectory(output);
            CloudBookStorage.AtomicWrite(Path.Combine(output, "result.json"), CloudJson.Serialize(new { Completed = true, Passed = failure == null, checks, failure, inputMode = routed ? "WPF routed events" : "OS mouse input with WPF assertions", limitations = new[] { "Synthetic cloud; real scan not repeated. Host DPI only." } }));
            DraftEditor.SetContent(RichContent.FromText("")); RequestExit();
        }
    }
}
