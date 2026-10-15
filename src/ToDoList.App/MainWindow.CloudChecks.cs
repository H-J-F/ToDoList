using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToDoList.App.Services;
using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.App;

public partial class MainWindow
{
    private async Task RunCloudUiChecksAsync()
    {
        var args = Environment.GetCommandLineArgs(); var inputIndex = Array.IndexOf(args, "--cloud-ui-input");
        var output = Path.Combine(_guestLibrary.DataDirectory, "..", "ui-results"); Directory.CreateDirectory(output);
        var checks = new List<object>(); var passed = false; string? failure = null;
        void Check(bool ok, string name) { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); }
        async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(220); UpdateLayout(); }
        void Capture(string name)
        {
            var dpi = VisualTreeHelper.GetDpi(this); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(this); bitmap.Freeze(); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        }
        async Task SyncButton()
        {
            CloudSyncButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var clock = Stopwatch.StartNew(); while (_cloudBusy && clock.Elapsed < TimeSpan.FromMinutes(3)) await Task.Delay(100);
            if (_cloudBusy) throw new TimeoutException("WPF cloud button did not complete");
            if (_cloudAutoPaused || _cloudFailures > 0) throw new InvalidOperationException(_cloudText);
            await Settle();
        }
        try
        {
            var allowedRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "cloud-live")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_guestLibrary.DataDirectory).StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) || CloudTestNamespace == null || inputIndex < 0 || inputIndex + 1 >= args.Length)
                throw new InvalidOperationException("Cloud UI checks require the isolated live-check fixture");
            using var input = JsonDocument.Parse(File.ReadAllText(args[inputIndex + 1]));
            var first = input.RootElement.GetProperty("firstAccount").GetString()!; var second = input.RootElement.GetProperty("secondAccount").GetString()!;
            Check(Path.GetFullPath(input.RootElement.GetProperty("dataDirectory").GetString()!) == Path.GetFullPath(_guestLibrary.DataDirectory), "WPF uses the exact isolated data directory");
            _cloudTimer.Stop(); await StopCloudAsync(); CloudAutoSync.IsChecked = false;
            Check(_cloudAccount == first && _cloudCoordinator != null, "Encrypted active-account credentials restore into WPF");
            var guest = await _guestLibrary.CreateAsync("Guest", "本地空间验证");
            await new TaskRepository(guest.Path).AddTaskAsync(RichContent.FromText("本地空间笔记不能自动上传"), null);
            Account_Click(AccountButton, new()); await Settle(); await SyncButton();
            Check(Model.HasBook && Model.Library.DataDirectory == WindowsCloudCredentials.Workspace(_guestLibrary.DataDirectory, first), "WPF synchronization button downloads the first account into its own directory");
            Check(Model.Tasks.Any(t => t.Item.PlainText is "合成版本 A" or "合成版本 B"), "Real task cards render synchronized synthetic content");
            Check(Descendants<TextBlock>(AccountPanel).Any(t => t.Text.Contains("扫码授权即同意使用当前微云空间")), "WeChat login consent notice is present in the rendered account panel");
            Check(AccountName.Text == "已授权" && SettingsPanel.Visibility == Visibility.Collapsed, "Authorized account entry opens the independent account panel");
            Capture("01-first-account");
            await Model.SelectFilterAsync(TaskFilter.All); await Settle();
            Model.Settings.LastBooksByWorkspace["guest"] = guest.Name;
            _cloudSwitching = true;
            try { await StopCloudAsync(); await AdoptCloudSessionAsync(_cloudCredentials.Load(second)!); }
            finally { _cloudSwitching = false; UpdateCloudControls(); }
            Check(_cloudAccount == second && !Model.HasBook && Model.Tasks.Count == 0, "Switching to the second account hides the first account's notes");
            var ownBook = await Model.Library.CreateAsync("Second", "第二账号验证");
            var own = await new TaskRepository(ownBook.Path).AddTaskAsync(RichContent.FromText("仅属于第二账号的合成任务"), null);
            await Model.SwitchLibraryAsync(Model.Library); SyncSelectors(); await Settle(); await SyncButton();
            Check(Model.Tasks.Any(t => t.Id == own.Id), "Second-account save and real upload complete through WPF"); Capture("02-second-account");
            _cloudSwitching = true;
            try { await StopCloudAsync(); await AdoptCloudSessionAsync(_cloudCredentials.Load(first)!); }
            finally { _cloudSwitching = false; UpdateCloudControls(); }
            Check(!Model.Tasks.Any(t => t.Id == own.Id), "Returning to the first account never displays second-account tasks");
            var before = await _cloudBooks!.CaptureAsync();
            DraftEditor.SetContent(RichContent.FromText("未提交草稿，暂缓云端应用"));
            Check(await ApplyCloudAsync(before, before, CancellationToken.None), "No-change synchronization does not interrupt a pending draft");
            var incoming = before.Copy(); var firstTaskKey = incoming.Values.Keys.First(k => CloudSnapshot.Kind(k) == "task");
            var fixtureTask = incoming.Values[firstTaskKey][0].Read<TodoItem>();
            incoming.Values[firstTaskKey] = [CloudValue.From(fixtureTask with { ContentJson = RichContent.FromText("远端修改").ToJson(), PlainText = "远端修改" })];
            Check(!await ApplyCloudAsync(before, incoming, CancellationToken.None), "Pending draft defers actual remote changes");
            DraftEditor.SetContent(RichContent.FromText(""));
            var current = Model.Tasks.First(); var changed = await Model.Repository!.UpdateContentAsync(current.Id, RichContent.FromText("界面冲突预览的合成版本"), current.Item.Revision);
            _cloudSwitching = true;
            try
            {
                var versions = new[] { CloudValue.From(current.Item), CloudValue.From(changed) };
                var key = CloudSnapshot.Key(CloudSnapshot.Book(before.Values.Keys.First()), "task", current.Id);
                var show = Dialogs.CloudConflict(this, key, versions, versions[0]); await Settle();
                var dialog = Descendants<Wpf.Ui.Controls.ContentDialog>(this).Single(d => d.IsVisible);
                Check(dialog.PrimaryButtonText == "保留所选版本" && dialog.SecondaryButtonText == "保留双方" && dialog.CloseButtonText == "稍后处理", "Conflict dialog offers explicit version, both, and defer actions");
                Capture("03-conflict-dialog"); dialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Close);
                Check(await show == null, "Deferring conflict preserves both versions without a resolution");
            }
            finally { _cloudSwitching = false; UpdateCloudControls(); }
            await RunCloudSyncAsync(SyncDirection.Both, true);
            CloudLogoutButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var logoutClock = Stopwatch.StartNew(); while (_cloudSwitching && logoutClock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
            Check(_cloudAccount == null && Model.Library.DataDirectory == _guestLibrary.DataDirectory && Model.CurrentBook?.Name == guest.Name, "Logout returns to guest notes and retains both account caches");
            Check(File.Exists(ownBook.Path) && _cloudCredentials.ActiveAccount == null, "Logout retains account database and removes automatic sign-in selection");
            await Settle(); Capture("04-guest-after-logout"); passed = true;
        }
        catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            CloudBookStorage.AtomicWrite(Path.Combine(output, "result.json"), CloudJson.Serialize(new { Completed = true, Passed = passed, checks, failure }));
            RequestExit();
        }
    }
}
