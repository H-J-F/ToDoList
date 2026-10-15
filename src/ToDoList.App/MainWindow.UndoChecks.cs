using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private sealed class UndoCheckCloud : ICloudFileStore
    {
        public static string Account => new('0', 64);
        private readonly Dictionary<(CloudArea, string), byte[]> _files = [];
        public TaskCompletionSource? UploadReached, ReleaseUpload;
        public TaskCompletionSource? ReadReached, ReleaseRead, InitializeReached, ReleaseInitialize;
        public bool Fail;
        public async Task<CloudSpace> InitializeAsync(CancellationToken ct = default)
        { if (InitializeReached != null) { InitializeReached.TrySetResult(); await ReleaseInitialize!.Task.WaitAsync(ct); } if (Fail) throw new IOException("合成同步失败"); return new(Account, "root", "Data", "Sync"); }
        public Task<IReadOnlyList<CloudFile>> ListAsync(CloudArea area, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CloudFile>>(_files.Where(p => p.Key.Item1 == area).Select(p => new CloudFile(p.Key.Item2, p.Key.Item2, p.Value.Length)).ToArray());
        public async Task<byte[]> ReadAsync(CloudArea area, CloudFile file, CancellationToken ct = default)
        { if (area == CloudArea.Data && ReadReached != null) { ReadReached.TrySetResult(); await ReleaseRead!.Task.WaitAsync(ct); } return _files[(area, file.Name)]; }
        public Task RecycleAsync(CloudArea area, IReadOnlyList<CloudFile> files, CancellationToken ct = default)
        { foreach (var file in files) _files.Remove((area, file.Name)); return Task.CompletedTask; }
        public async Task<CloudFile> WriteImmutableAsync(CloudArea area, string name, byte[] bytes, CancellationToken ct = default)
        {
            if (UploadReached != null) { UploadReached.TrySetResult(); await ReleaseUpload!.Task.WaitAsync(ct); }
            if (_files.TryGetValue((area, name), out var existing) && !existing.SequenceEqual(bytes)) throw new InvalidDataException("不可变提交被改写");
            _files[(area, name)] = bytes.ToArray(); return new(name, name, bytes.Length);
        }
    }
    private async Task RunUndoUiChecksAsync()
    {
        var args = Environment.GetCommandLineArgs(); var routed = args.Contains("--undo-routed-input");
        var output = Path.GetFullPath(Path.Combine(Model.Library.DataDirectory, "..", "ui-results"));
        var checks = new List<string>(); string? failure = null; var limitations = new List<string>();
        async Task Settle() { await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle); await Task.Delay(100); UpdateLayout(); }
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        async Task Click(ButtonBase button)
        {
            if (routed)
            {
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                // RaiseEvent alone does not run ButtonBase.OnClick's command dispatch.
                if (button.Command is RoutedCommand command && command.CanExecute(button.CommandParameter, button.CommandTarget ?? button)) command.Execute(button.CommandParameter, button.CommandTarget ?? button);
                else if (button.Command is { } simple && simple.CanExecute(button.CommandParameter)) simple.Execute(button.CommandParameter);
                await Settle();
            }
            else await UiSettingsNavigationChecks.Click(button);
        }
        async Task WaitUntil(double seconds)
        { var delay = seconds - Notifications.Clock.Elapsed.TotalSeconds; if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay)); await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle); }
        void Capture(string name)
        {
            UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        try
        {
            var allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts")) + Path.DirectorySeparatorChar;
            if (!Model.Library.DataDirectory.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Model.HasBook && !args.Contains("--undo-restart")) throw new InvalidOperationException("Undo checks require an isolated artifacts/.../Data workspace.");
            Directory.CreateDirectory(output); _cloudTimer.Stop(); _calendar.Stop();
            ActivateAccountCheck(); await Settle();
            if (!routed) Check(GetForegroundWindow() == new WindowInteropHelper(this).Handle, "Native input owns foreground");
            else limitations.Add("WPF routed input; no native mouse/keyboard acceptance.");
            _cloudAccount = UndoCheckCloud.Account; var cloud = new UndoCheckCloud(); _cloudBooks = new(Model.Library); _cloudCoordinator = new(cloud, _cloudBooks, _cloudAccount);
            UpdateCloudControls();
            Check(_lastSyncDuration == null && CloudSyncDuration.Visibility == Visibility.Collapsed, "Session starts without duration");
            if (args.Contains("--undo-restart"))
            {
                Check(_cloudCoordinator.LastSyncedAt != null && CloudLastSync.Text.Contains("上次同步"), "Restart retains sync timestamp without duration");
                Check(Notifications.Visibility == Visibility.Collapsed && _undo == null, "Restart has no undo opportunity");
                return;
            }
            var book = await Model.Library.CreateAsync("UndoUi", "撤回交互验收"); await Model.RefreshBooksAsync(); await Model.OpenBookAsync(book); await Model.SelectFilterAsync(TaskFilter.All); SyncSelectors();
            await Model.AddAsync(RichContent.FromText("保留任务"), null, null); await Settle(); var first = Model.Tasks.Single(); var firstReceipt = _undo; var generation = Notifications.Clock.Generation;
            await Model.AddAsync(RichContent.FromText("本次任务"), null, "新增模块"); await Settle();
            Check(_undo != firstReceipt && Notifications.Clock.Generation > generation && Notifications.Countdown.Value > 0.9, "Repeated success text replaces receipt and resets countdown");
            await Click(Notifications.UndoButton); await Settle();
            Check(Model.Tasks.Count == 1 && Model.Tasks[0].Id == first.Id && Model.Projects.Count == 1, "Actual undo button reverses only latest combined add");
            first = Model.Tasks[0];
            _cloudSchedule.Completed(DateTime.UtcNow, 5, _cloudSchedule.Generation, false);
            Check(!_cloudSchedule.Due(DateTime.UtcNow.AddSeconds(6), true), "Undo never schedules a five-second sync");
            await Model.SaveTaskAsync(first, RichContent.FromText("已保存修改"), null); await Settle();
            Task_EditRequested(FindCard(first), EventArgs.Empty); await Task.Delay(400); await Settle();
            _editingCard!.ActiveEditor!.SetContent(RichContent.FromText("尚未提交的编辑"));
            await Click(Notifications.UndoButton); await Settle();
            Check(first.Item.PlainText == "保留任务" && _editingCard?.ActiveEditor?.GetContent().PlainText == "尚未提交的编辑", "Undo click does not autosave or discard active editor");
            EndRowEdit();
            await RunCloudSyncAsync(SyncDirection.Both, true); Check(_lastSyncDuration.HasValue && CloudSyncDuration.Visibility == Visibility.Visible, "Successful sync displays ephemeral duration");
            var duration = _lastSyncDuration; cloud.Fail = true; await RunCloudSyncAsync(SyncDirection.Both, false); cloud.Fail = false;
            Check(_lastSyncDuration == duration, "Failed sync retains last successful duration");
            await Model.AddAsync(RichContent.FromText("同步中撤回"), null, null); await Settle();
            cloud.UploadReached = new(TaskCreationOptions.RunContinuationsAsynchronously); cloud.ReleaseUpload = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var sync = RunCloudSyncAsync(SyncDirection.Both, true); await cloud.UploadReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Notifications.Clock.CanUndo && Notifications.Clock.EnteredSync, "Sync start preserves undo deadline");
            await Click(Notifications.UndoButton); await Settle(); var dirty = _cloudSchedule.DirtyAt; var undoGeneration = _cloudSchedule.Generation;
            Check(dirty.HasValue, "Undo during upload preserves an unsynced change");
            cloud.ReleaseUpload.TrySetResult(); await sync; cloud.UploadReached = cloud.ReleaseUpload = null;
            Check(_cloudSchedule.DirtyAt == dirty && _cloudSchedule.Generation == undoGeneration, "Old sync cannot clear compensating change");
            Check(!_cloudSchedule.Due(dirty!.Value.AddSeconds(6), true) && _cloudSchedule.Due(_cloudSchedule.NextPoll, true), "Compensating change waits for manual sync or the regular cycle");
            await RunCloudSyncAsync(SyncDirection.Both, true);
            first = Model.Tasks.Single(row => row.Id == first.Id);
            await Model.SaveTaskAsync(first, RichContent.FromText("同步后撤回"), null); await Settle(); await RunCloudSyncAsync(SyncDirection.Both, true);
            Check(Notifications.Clock.CanUndo, "Completed sync preserves undo opportunity"); await Click(Notifications.UndoButton); await Settle();
            Check(first.Item.PlainText == "保留任务" && _cloudSchedule.DirtyAt.HasValue, "Undo after sync is a new modification");
            await RunCloudSyncAsync(SyncDirection.Both, true);
            await Model.AddAsync(RichContent.FromText("计时测试"), null, null); await Settle();
            await WaitUntil(2); Check(Notifications.Countdown.Value is > 0.4 and < 0.65, "Countdown advances visually");
            await WaitUntil(4.6); Check(!Notifications.UndoButton.IsEnabled && Notifications.Visibility == Visibility.Visible, "4.5 second deadline disables undo before fade");
            await WaitUntil(4.95); Check(Notifications.Visibility == Visibility.Visible, "Toast waits another half second");
            await WaitUntil(5.04); generation = Notifications.Clock.Generation;
            await Model.AddAsync(RichContent.FromText("淡出中更新"), null, null); await Settle(); await Task.Delay(250);
            Check(Notifications.Clock.Generation > generation && Notifications.Visibility == Visibility.Visible && Notifications.Opacity > 0.99 && Notifications.Clock.CanUndo, "New operation cancels old fade and resets deadline");
            await WaitUntil(5.3); Check(Notifications.Visibility == Visibility.Collapsed && _undo == null, "Finished fade releases undo receipt");
            AccountPanel.Visibility = Visibility.Visible;
            foreach (var theme in new[] { "classic-light", "classic-dark" })
            foreach (var reduced in new[] { false, true })
            foreach (var width in new[] { 800d, 1100d })
            {
                Width = width; Height = width == 800 ? 560 : 760; Model.Settings.ThemeId = theme; Model.Settings.ReduceMotion = reduced; ThemeService.Apply(Model.Settings);
                Motion.Finish(AccountPanel); AccountPanel.Visibility = Visibility.Visible;
                CloudInterval.BringIntoView(); await Settle();
                var track = (Track)CloudInterval.Template.FindName("PART_Track", CloudInterval); var labels = (Canvas)CloudInterval.Template.FindName("PART_Labels", CloudInterval);
                Check(track.Thumb.Width == 14 && track.Thumb.Height == 14 && Math.Abs(track.Thumb.ActualWidth - 14) <= 1 && Math.Abs(track.Thumb.ActualHeight - 14) <= 1,
                    $"Square handle: {theme}/{reduced}/{width}: {track.Thumb.ActualWidth}x{track.Thumb.ActualHeight} DIP");
                Check(labels.Children.Count == CloudSyncSchedule.Intervals.Count && CloudInterval.Maximum == CloudSyncSchedule.Intervals.Count - 1, "Slider range follows intervals");
                foreach (var i in Enumerable.Range(0, CloudSyncSchedule.Intervals.Count))
                {
                    CloudInterval.Value = i; UpdateLayout(); var label = (TextBlock)labels.Children[i];
                    Check(label.Text == CloudSyncSchedule.Intervals[i].ToString(), "Tick shows allowed interval: " + i);
                    var center = label.TranslatePoint(new Point(label.ActualWidth / 2, 0), CloudInterval).X;
                    var thumb = track.Thumb.TranslatePoint(new Point(7, 0), CloudInterval).X;
                    Check(Math.Abs(center - thumb) <= 1, "Label aligns to tick/handle: " + theme + reduced + width + "/" + i);
                }
                Check(CloudIntervalLabel.Text == "同步周期：1 小时" && _guestLibrary.LoadSettings().CloudSyncIntervalMinutes == 60, "Hour label and persisted minutes");
                CloudInterval.Value = 0; CloudInterval.Focus();
                if (routed) CloudInterval.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(CloudInterval), Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
                else { keybd_event(0x27, 0, 0, 0); keybd_event(0x27, 0, 2, 0); }
                await Settle(); Check(CloudInterval.Value == 1 && Model.Settings.CloudSyncIntervalMinutes == 10, "Keyboard changes discrete interval: " + theme + reduced + width);
                if (theme == "classic-light" && !reduced && width == 1100)
                {
                    CloudInterval.Value = 0; await Settle(); await Click(track.IncreaseRepeatButton);
                    Check(CloudInterval.Value == 1, "Track click advances one discrete interval");
                    var from = track.Thumb.PointToScreen(new Point(7, 7)); var to = track.PointToScreen(new Point(track.ActualWidth - 7, track.ActualHeight / 2));
                    if (routed)
                    {
                        track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0));
                        track.Thumb.RaiseEvent(new DragDeltaEventArgs(track.ActualWidth, 0));
                        track.Thumb.RaiseEvent(new DragCompletedEventArgs(track.ActualWidth, 0, false));
                    }
                    else
                    {
                        Check(GetForegroundWindow() == new WindowInteropHelper(this).Handle, "Native drag owns foreground");
                        RepairSetCursorPos((int)from.X, (int)from.Y); RepairMouseEvent(2, 0, 0, 0, 0);
                        try { for (var n = 1; n <= 5; n++) { RepairSetCursorPos((int)(from.X + (to.X - from.X) * n / 5), (int)to.Y); await Task.Delay(35); } }
                        finally { RepairMouseEvent(4, 0, 0, 0, 0); }
                    }
                    await Settle(); Check(CloudInterval.Value == CloudSyncSchedule.Intervals.Count - 1 && Model.Settings.CloudSyncIntervalMinutes == 60, "Handle drag reaches last tick and persists interval");
                }
                CloudInterval.Value = CloudSyncSchedule.Intervals.Count - 1; await Settle();
                await Model.AddAsync(RichContent.FromText("主题和布局 " + checks.Count), null, null); await Settle(); Capture(theme + "-" + reduced + "-" + width);
                Notifications.Dismiss();
            }
            var dpi = VisualTreeHelper.GetDpi(this); limitations.Add($"Rendered and native input at host DPI {dpi.PixelsPerInchX:F0}; other OS DPI settings were not exercised.");
            await Model.AddAsync(RichContent.FromText("隐藏释放机会"), null, null); await Settle(); Hide(); await Settle();
            Check(_undo == null && Notifications.Visibility == Visibility.Collapsed, "Hiding window releases undo opportunity");
            Show(); ActivateAccountCheck(); await Settle(); await RunCloudSyncAsync(SyncDirection.Both, true);
            CloudLogout_Click(this, new RoutedEventArgs()); while (_cloudSwitching) await Task.Delay(20);
            Check(_lastSyncDuration == null && CloudSyncDuration.Visibility == Visibility.Collapsed, "Logout clears session duration");
            var next = new UndoCheckCloud(); _cloudBooks = new(Model.Library); _cloudCoordinator = new(next, _cloudBooks, UndoCheckCloud.Account); _lastSyncDuration = null; UpdateCloudControls();
            Check(CloudSyncDuration.Visibility == Visibility.Collapsed, "New session state hides duration");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, args.Contains("--undo-restart") ? "restart.json" : "result.json"), JsonSerializer.Serialize(new { Completed = true, Passed = failure == null, Checks = checks, Limitations = limitations, Failure = failure, InputMode = routed ? "WPF routed events" : "OS mouse and keyboard" }, new JsonSerializerOptions { WriteIndented = true }));
            _cloudAccount = null; _cloudCoordinator = null; _cloudBooks = null; EndRowEdit(); DraftEditor.SetContent(RichContent.FromText("")); RequestExit();
        }
    }
}
