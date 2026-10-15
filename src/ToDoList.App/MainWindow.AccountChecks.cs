using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToDoList.App.Services;
using ToDoList.Core;
using ToDoList.Storage.Cloud;

namespace ToDoList.App;

public partial class MainWindow
{
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    private void ActivateAccountCheck()
    {
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _); var thread = GetCurrentThreadId();
        var attached = foregroundThread != thread && AttachThreadInput(thread, foregroundThread, true);
        try { Show(); Activate(); SetForegroundWindow(new WindowInteropHelper(this).Handle); }
        finally { if (attached) AttachThreadInput(thread, foregroundThread, false); }
    }
    private async Task RunAccountUiChecksAsync()
    {
        var output = Path.GetFullPath(Path.Combine(_guestLibrary.DataDirectory, "..", "ui-results"));
        var routedInput = Environment.GetCommandLineArgs().Contains("--account-routed-input");
        var checks = new List<string>(); string? failure = null;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        void Capture(string name)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        async Task Settle() { await UiSettingsNavigationChecks.Settle(); UpdateLayout(); }
        async Task Click(FrameworkElement element)
        {
            element.BringIntoView(); await Settle();
            if (!routedInput) { await UiSettingsNavigationChecks.Click(element); return; }
            if (element == AccountName) AccountButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            else if (element is ButtonBase) element.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            else element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            await Settle();
        }
        async Task Logout()
        {
            await Click(CloudLogoutButton);
            var watch = Stopwatch.StartNew();
            while (_cloudSwitching && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(30);
            Check(!_cloudSwitching && _cloudAccount == null, "Logout completes and clears active account");
            await Settle();
        }
        try
        {
            var allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "account-ui")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_guestLibrary.DataDirectory).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Account checks require artifacts/account-ui/<run>/Data");
            Directory.CreateDirectory(output); _cloudTimer.Stop(); await StopCloudAsync();
            Topmost = true; ActivateAccountCheck(); await Settle();
            var guest = await _guestLibrary.CreateAsync("Guest", "原样导入的本地笔记");
            var task = await new ToDoList.Storage.TaskRepository(guest.Path).AddTaskAsync(RichContent.FromText("账号面板合成验证内容"), null);
            await Model.SwitchLibraryAsync(_guestLibrary); SyncSelectors(true);
            Check(AccountName.Text == "未登录" && AccountAvatar.Symbol == Wpf.Ui.Controls.SymbolRegular.PersonCircle20, "Guest entry uses circled person icon and 未登录");
            Check(AccountButton.ActualWidth == SettingsButton.ActualWidth && AccountButton.ActualHeight == 40 && SettingsButton.ActualHeight == 40, "Account and settings have identical row dimensions");
            Check(AccountEntry.TranslatePoint(new Point(), this).Y < SettingsButton.TranslatePoint(new Point(), this).Y, "Account entry sits directly above settings");
            foreach (var theme in new[] { "classic-light", "classic-dark" })
            foreach (var reduced in new[] { false, true })
            {
                Model.Settings.ThemeId = theme; Model.Settings.ReduceMotion = reduced; ThemeService.Apply(Model.Settings); SyncSettings();
                Width = MinWidth; Height = MinHeight; await Settle();
                Check(Math.Abs(AccountName.TranslatePoint(new Point(), this).X - SettingsLabel.TranslatePoint(new Point(), this).X) < 0.1, "Account and settings text align: " + theme + "/" + reduced);
                Check(AccountAvatar.ActualWidth == 20 && AccountAvatar.ActualHeight == 20 && SettingsIcon.ActualWidth == 20 && SettingsIcon.ActualHeight == 20, "Account and settings icons are twenty DIP: " + theme + "/" + reduced);
                Check(Math.Abs(AccountAvatar.TranslatePoint(new Point(10, 10), this).X - SettingsIcon.TranslatePoint(new Point(10, 10), this).X) < 0.1, "Account and settings icon centers align: " + theme + "/" + reduced);
                Check(AccountAvatar.Foreground == FindResource("MutedBrush") && AccountName.Foreground == FindResource("MutedBrush"), "Guest icon and text use theme gray: " + theme + "/" + reduced);
                await Click(AccountButton);
                Check(AccountPanel.Visibility == Visibility.Visible && SettingsPanel.Visibility == Visibility.Collapsed, "Avatar opens account panel: " + theme + "/" + reduced);
                Check(CloudLoginButton.IsVisible && !CloudSyncButton.IsEnabled && !CloudLogoutButton.IsEnabled, "Guest account actions are gated: " + theme + "/" + reduced);
                Check(AccountPanel.ActualWidth == 440 && AccountPanel.ActualHeight > 400 && AccountName.ActualWidth > 0, "Minimum window has usable account layout: " + theme + "/" + reduced);
                Capture(theme + (reduced ? "-reduced" : "-standard"));
                await Click(AccountName); Check(AccountPanel.Visibility == Visibility.Collapsed, "Name click closes account panel: " + theme + "/" + reduced);
                await Click(SettingsButton); await Click(AccountButton);
                Check(SettingsPanel.Visibility == Visibility.Collapsed && AccountPanel.Visibility == Visibility.Visible, "Opening account closes settings: " + theme + "/" + reduced);
                await Click(SettingsButton);
                Check(AccountPanel.Visibility == Visibility.Collapsed && SettingsPanel.Visibility == Visibility.Visible, "Opening settings closes account: " + theme + "/" + reduced);
                Check(!Descendants<FrameworkElement>(SettingsPanel).Contains(CloudLoginButton), "Cloud controls are absent from settings: " + theme + "/" + reduced);
                await Click(AccountName); await Click(CloseAccountButton);
                Check(AccountPanel.Visibility == Visibility.Collapsed, "Account close button works: " + theme + "/" + reduced);
                // The heading is covered by the flyout at minimum width. Use
                // the sidebar, which remains physically reachable in all sizes.
                await Click(AccountButton); await Click(ProjectList);
                Check(AccountPanel.Visibility == Visibility.Collapsed, "Outside mouse click closes account: " + theme + "/" + reduced);
                await Click(AccountButton);
                if (routedInput) RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                else { keybd_event(0x1B, 0, 0, 0); keybd_event(0x1B, 0, 2, 0); }
                await Settle();
                Check(AccountPanel.Visibility == Visibility.Collapsed, "Escape closes account: " + theme + "/" + reduced);
                // Burst routed clicks exercise animation interruption before a frame completes.
                for (var i = 0; i < 12; i++)
                {
                    AccountButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    SettingsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                await Settle();
                Check(SettingsPanel.Visibility == Visibility.Visible && AccountPanel.Visibility == Visibility.Collapsed && SettingsPanel.Opacity == 1, "Rapid switching reaches stable final state: " + theme + "/" + reduced);
                await Click(SettingsButton); await Click(AccountButton);
                Close(); await Settle();
                Check(!IsVisible && AccountPanel.Visibility == Visibility.Collapsed, "Hide ends account animation: " + theme + "/" + reduced);
                RestoreFromTray(); ActivateAccountCheck(); await Settle();
            }
            var first = new CloudSession("fixture", "synthetic-access", "synthetic-refresh", long.MaxValue, "synthetic-first");
            var second = first with { RootKey = "synthetic-second" };
            _cloudCredentials.Save(first); _cloudCredentials.Save(second);
            await AdoptCloudSessionAsync(first); UpdateCloudControls(); await Settle(); await Click(AccountButton);
            Check(AccountName.Text == "已授权" && CloudCopyButton.IsEnabled && CloudAutoSync.IsEnabled, "Authorized fixture shows fallback label and enables account actions");
            var copy = Model.Library.CopyBookAsync(_guestLibrary, guest); await copy;
            await Model.SwitchLibraryAsync(Model.Library); SyncSelectors(true); await Settle();
            Check(Model.CurrentBook?.Title == guest.Title && Model.Tasks.Any(t => t.Id == task.Id), "Imported guest title and task render unchanged in authorized workspace");
            await Click(CloudCopyButton);
            var dialog = Descendants<Wpf.Ui.Controls.ContentDialog>(this).Single(d => d.IsVisible);
            dialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Close); await Settle();
            Check((await Model.Library.ListAsync()).Count == 1, "Cancelling real import dialog leaves account data unchanged");
            if (AccountPanel.Visibility == Visibility.Collapsed) await Click(AccountButton);
            Capture("authorized-first");
            await AdoptCloudSessionAsync(second); UpdateCloudControls(); await Settle();
            Check(Model.Tasks.Count == 0 && AccountIdentity.Text.EndsWith(second.AccountId[..6]), "Switching account clears first account data and updates identity");
            await AdoptCloudSessionAsync(_cloudCredentials.Load(first.AccountId)!); UpdateCloudControls(); await Settle();
            Check(AccountName.Text == "已授权" && Model.Tasks.Any(t => t.Id == task.Id), "Encrypted credential restore preserves authorized state and isolated cache");
            var avatarBitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 240, 120, 30, 255, 240, 120, 30, 255, 240, 120, 30, 255, 240, 120, 30, 255 }, 8);
            var avatarEncoder = new PngBitmapEncoder(); avatarEncoder.Frames.Add(BitmapFrame.Create(avatarBitmap));
            using var avatarStream = new MemoryStream(); avatarEncoder.Save(avatarStream);
            _cloudCredentials.SaveProfile(new(first.AccountId, "合成昵称 · " + new string('长', 70), avatarStream.ToArray()));
            ReloadAccountProfile(first.AccountId); UpdateCloudControls(); await Settle();
            Check(AccountName.Text.StartsWith("合成昵称") && AccountName.ToolTip?.ToString() == AccountName.Text && AccountName.TextTrimming == TextTrimming.CharacterEllipsis, "Verified cached long nickname has ellipsis and complete tooltip");
            Check(AccountAvatarImage.Visibility == Visibility.Visible && AccountAvatar.Visibility == Visibility.Collapsed && AccountAvatarImage.ActualWidth == 20, "Cached profile image replaces icon at twenty DIP");
            Capture("cached-profile");
            await AdoptCloudSessionAsync(second); UpdateCloudControls(); await Settle();
            Check(AccountName.Text == "已授权" && AccountAvatarImage.Source == null, "Switching to account without profile clears nickname and image");
            await AdoptCloudSessionAsync(first); UpdateCloudControls(); await Settle();
            Check(AccountName.Text.StartsWith("合成昵称") && AccountAvatarImage.Source != null, "Restoring first account reloads only its encrypted profile");
            _cloudCredentials.SaveProfile(new(first.AccountId, "保留昵称", [1, 2, 3]));
            ReloadAccountProfile(first.AccountId); UpdateCloudControls(); await Settle();
            Check(AccountName.Text == "保留昵称" && AccountAvatar.Visibility == Visibility.Visible, "Malformed cached image falls back without losing valid nickname");
            if (AccountPanel.Visibility == Visibility.Collapsed) await Click(AccountButton);
            await Logout();
            Check(AccountName.Text == "未登录" && Model.Library.DataDirectory == _guestLibrary.DataDirectory && Model.Tasks.Any(t => t.Id == task.Id), "Logout returns to original guest data and default entry");
            Capture("guest-after-logout");
            if (Environment.GetCommandLineArgs().Contains("--login-browser-checks"))
                await RunLoginBrowserChecksAsync(output, Check, routedInput);
        }
        catch (Exception ex) { failure = ex.ToString(); if (Directory.Exists(output)) Capture("failure"); }
        finally
        {
            Directory.CreateDirectory(output);
            CloudBookStorage.AtomicWrite(Path.Combine(output, "result.json"), CloudJson.Serialize(new { Completed = true, Passed = failure == null, checks, failure, InputMode = routedInput ? "WPF routed events" : "OS mouse and keyboard; bursts use routed events", RealProfileVerified = false, Authorization = "synthetic offline fixture; no network synchronization" }));
            RequestExit();
        }
    }
}
