using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToDoList.App.Services;
using ToDoList.App.ViewModels;
using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.App;

public partial class MainWindow
{
    private async Task RunBookVisibilityChecksAsync()
    {
        var output = Path.GetFullPath(Path.Combine(_guestLibrary.DataDirectory, "..", "ui-results"));
        var checks = new List<string>(); string? failure = null;
        var args = Environment.GetCommandLineArgs();
        var stage = args.Contains("--book-corrupt-check") ? "corrupt" : args.Contains("--book-restart-check") ? "restart" : "initial";
        void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }
        async Task Settle() { await UiSettingsNavigationChecks.Settle(); UpdateLayout(); }
        void Capture(string name)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        async Task Menu(string header)
        {
            var owner = Descendants<Button>(this).Single(b => (string?)b.ToolTip == "笔记操作");
            owner.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Settle();
            var menu = owner.ContextMenu; Check(menu is { IsOpen: true }, "Actual notebook menu opens");
            var item = menu!.Items.OfType<MenuItem>().Single(i => (string)i.Header == header);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
            for (var i = 0; i < 300 && _navigating; i++) await Task.Delay(20);
            await Settle(); Check(!_navigating, "Notebook action completes: " + header);
        }
        try
        {
            var allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "book-visibility")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_guestLibrary.DataDirectory).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Visibility checks require artifacts/book-visibility/<run>/Data");
            Directory.CreateDirectory(output); _cloudTimer.Stop(); await StopCloudAsync();
            Check(_cloudCredentials.DirectoryPath == Path.Combine(_guestLibrary.DataDirectory, "Auth"), "Credentials remain inside isolated data directory");
            if (stage != "initial")
            {
                Check(_cloudAccount != null && Model.Library.DataDirectory != _guestLibrary.DataDirectory, "Startup restores account workspace without network authorization");
                Check(Model.CurrentBook?.Name == "Offline" && Model.Tasks.Any(t => t.Item.PlainText == "离线账号内容"), "Restart loads local account notes");
                Check(!_cloudBusy && !CloudSyncButton.IsEnabled, "Expired or unreadable credentials leave cloud requests disabled");
                Check(!Model.ShowHiddenBooks && Model.VisibleBooks.All(b => !b.IsHidden), "Restart defaults to visible notes only"); Capture(stage);
                if (stage == "restart") File.WriteAllBytes(Path.Combine(_cloudCredentials.DirectoryPath, "account-" + _cloudAccount + ".dpapi"), [1, 2, 3]);
            }
            else
            {
                var first = await Model.Library.CreateAsync("First", "第一本笔记");
                var second = await Model.Library.CreateAsync("Second", "第二本笔记");
                await new TaskRepository(first.Path).AddTaskAsync(RichContent.FromText("隐藏笔记的内容"), null);
                await Model.RefreshBooksAsync(); await Model.OpenBookAsync(first); SyncSelectors(true); await Settle();
                var blockedSettings = Path.Combine(_guestLibrary.DataDirectory, "settings.json.tmp");
                Directory.CreateDirectory(blockedSettings);
                try
                {
                    try { await Model.SetCurrentBookHiddenAsync(true); Check(false, "Settings failure must reject hide navigation"); }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                    Check(Model.CurrentBook?.BookId == first.BookId && !Model.CurrentBook.IsHidden && !Model.ShowHiddenBooks, "Failed settings save preserves current note and visibility");
                    Check(!(await Model.Library.ListAsync()).Single(b => b.BookId == first.BookId).IsHidden, "Failed preparation does not persist hiding");
                }
                finally { Directory.Delete(blockedSettings); }
                await Menu("隐藏当前笔记");
                Check(Model.CurrentBook?.BookId == second.BookId && Model.Books.Count == 2 && Model.VisibleBooks.Count == 1, "Hide switches to visible book while retaining complete collection");
                Check((await Model.Library.ListAsync()).Single(b => b.BookId == first.BookId).IsHidden, "Hide persists to notebook database");
                await Menu("隐藏当前笔记");
                Check(Model.CurrentBook == null && Model.Tasks.Count == 0 && Model.VisibleBooks.Count == 0, "All hidden clears current content");
                Check(Model.WelcomeTitle == "全部笔记已隐藏" && ShowHiddenWelcome.IsVisible, "All-hidden empty state offers recovery"); Capture("all-hidden");
                await Menu("显示隐藏笔记");
                Check(Model.ShowHiddenBooks && BookSelector.Items.Count == 2 && Model.VisibleBooks.All(b => b.DisplayTitle.EndsWith("（已隐藏）")), "Toggle exposes and labels hidden books");
                BookSelector.SelectedItem = Model.VisibleBooks.First(b => b.BookId == first.BookId);
                for (var i = 0; i < 300 && _navigating; i++) await Task.Delay(20);
                await Settle(); Check(Model.Tasks.Any(t => t.Item.PlainText == "隐藏笔记的内容"), "Hidden book still displays complete content"); Capture("hidden-open");
                Model.Settings.ThemeId = "classic-dark"; ThemeService.Apply(Model.Settings); SyncSettings(); await Settle(); Capture("hidden-open-dark");
                Model.Settings.ThemeId = "classic-light"; ThemeService.Apply(Model.Settings); SyncSettings();
                await Menu("取消隐藏当前笔记"); await Menu("显示隐藏笔记");
                Check(!Model.ShowHiddenBooks && Model.CurrentBook?.BookId == first.BookId && Model.VisibleBooks.Count == 1, "Restore and disable toggle retain current visible book");
                var storage = new CloudBookStorage(Model.Library); var before = await storage.CaptureAsync(); var desired = before.Copy();
                desired.Set(first.BookId, "setting", BookInfo.HiddenSetting, "true");
                var changes = await storage.ApplyWithChangesAsync(before, desired);
                _sync = true; await Model.RefreshAfterCloudAsync(changes); _sync = false; SyncSelectors(); await Settle();
                Check(Model.CurrentBook == null && Model.VisibleBooks.Count == 0, "Cloud-only setting change refreshes visibility and clears current book");
                var freshModel = new MainViewModel(Model.Library, new AppSettings { LastBook = first.BookId }); await freshModel.InitializeAsync();
                Check(freshModel.CurrentBook == null && !freshModel.ShowHiddenBooks && freshModel.Books.Count == 2, "Fresh model does not reopen remembered hidden book");
                await Model.ShowHiddenBooksAsync(true); await Model.OpenBookAsync(Model.Books.First(b => b.BookId == first.BookId)); SyncSelectors();
                DraftEditor.SetContent(RichContent.FromText("未提交草稿"));
                ShowHiddenBooks_Click(this, new RoutedEventArgs()); await Settle();
                var dialog = Descendants<Wpf.Ui.Controls.ContentDialog>(this).Single(d => d.IsVisible);
                dialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Close);
                for (var i = 0; i < 300 && _navigating; i++) await Task.Delay(20);
                Check(Model.ShowHiddenBooks && Model.CurrentBook?.BookId == first.BookId && Model.CurrentBook.IsHidden, "Cancelling draft navigation preserves hidden note and toggle");
                DraftEditor.SetContent(RichContent.FromText(""));
                var session = new CloudSession("fixture", "synthetic-access", "synthetic-refresh", long.MaxValue, "offline-visibility-space", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var accountLibrary = new BookLibrary(WindowsCloudCredentials.Workspace(_guestLibrary.DataDirectory, session.AccountId));
                var offline = await accountLibrary.CreateAsync("Offline", "离线笔记"); await new TaskRepository(offline.Path).AddTaskAsync(RichContent.FromText("离线账号内容"), null);
                try { await AdoptAndInitializeCloudAsync(session, _ => Task.FromException(new IOException("synthetic directory initialization failure"))); }
                catch (IOException) { }
                SyncSelectors(true); UpdateCloudControls(); await Settle();
                Check(_cloudAccount == session.AccountId && Model.CurrentBook?.BookId == offline.BookId && Model.Tasks.Count == 1, "Directory initialization failure preserves confirmed account local notes");
                Check(_cloudAutoPaused && _cloudCredentials.ActiveAccount == session.AccountId, "Failed cloud setup pauses automatic requests but preserves selection");
                Check(!Model.ShowHiddenBooks, "Account switching resets visibility toggle");
                _cloudCredentials.Save(session with { AuthorizedAt = DateTimeOffset.UtcNow.AddDays(-15).ToUnixTimeSeconds() }); Capture("offline-account");
            }
        }
        catch (Exception ex) { failure = ex.ToString(); if (Directory.Exists(output)) Capture("failure-" + stage); }
        finally
        {
            Directory.CreateDirectory(output);
            CloudBookStorage.AtomicWrite(Path.Combine(output, "result-" + stage + ".json"), CloudJson.Serialize(new { Completed = true, Passed = failure == null, checks, failure, InputMode = "WPF routed events on real controls", Limitations = "No real WeChat authorization or OS mouse/keyboard validation" }));
            RequestExit();
        }
    }
}
