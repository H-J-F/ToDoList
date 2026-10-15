using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using ToDoList.App.Services;
using ToDoList.Storage.Cloud;

namespace ToDoList.App;

public partial class MainWindow
{
    private async Task RunLiveLoginChecksAsync()
    {
        var output = Path.GetFullPath(Path.Combine(_guestLibrary.DataDirectory, "..", "ui-results"));
        var allowed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "account-ui")) + Path.DirectorySeparatorChar;
        var accounts = new List<object>(); var identities = new HashSet<string>(); string? failure = null; var allProfilesVerified = true;
        try
        {
            if (!_guestLibrary.DataDirectory.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || CloudTestNamespace == null)
                throw new InvalidOperationException("Live login checks require artifacts/account-ui/<run>/Data and an isolated CloudCheck namespace.");
            Directory.CreateDirectory(output); _cloudTimer.Stop(); await StopCloudAsync();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                _cloudSwitching = _cloudAuthenticating = true;
                _cloudCancellation?.Dispose(); _cloudCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                SetCloudStatus(attempt == 0 ? "真实内嵌验收：请扫码第一个微信账号" : "真实内嵌验收：请扫码另一个微信账号");
                var operation = LoginCloudSessionAsync(_cloudCancellation.Token); _cloudOperation = operation; await operation;
                identities.Add(_cloudAccount ?? "");
                var verified = _accountProfile?.DisplayName != null && _accountImage != null;
                allProfilesVerified &= verified;
                accounts.Add(new { Account = _cloudAccount, ProfileVerified = verified, NicknamePresent = _accountProfile?.DisplayName != null, AvatarPresent = _accountImage != null });
                CloudBookStorage.AtomicWrite(Path.Combine(output, "live-progress.json"), CloudJson.Serialize(new { Accounts = accounts, Completed = false }));
                if (_cloudAccount == null) throw new InvalidOperationException("授权没有完成账号切换。");
            }
            if (identities.Count != 2) throw new InvalidOperationException("两次扫码必须使用不同微信账号。");
            if (!allProfilesVerified) throw new InvalidOperationException("真实授权成功，但头像或昵称没有完成验证。");
        }
        catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            _cloudSwitching = _cloudAuthenticating = false;
            Directory.CreateDirectory(output);
            CloudBookStorage.AtomicWrite(Path.Combine(output, "live-result.json"), CloudJson.Serialize(new { Completed = true, Passed = failure == null, Accounts = accounts, Failure = failure, CloudNamespace = CloudTestNamespace }));
            RequestExit();
        }
    }
    private async Task RunLoginBrowserChecksAsync(string output, Action<bool, string> check, bool routed)
    {
        check(!ActivationProtocol.EnsureRegistered(), "Isolated checks do not replace protocol association");
        check(ActivationProtocol.IsActivation("todolist-desktop://activate") && !ActivationProtocol.IsActivation("todolist-desktop://activate?code=x") &&
            !ActivationProtocol.IsActivation("todolist-desktop://activate/other"), "Activation only accepts exact credential-free URI");
        var cancelCount = 0;
        var unavailable = new CloudLoginWindow(this, () => cancelCount++, _ => Task.FromException<CoreWebView2Environment>(new InvalidOperationException("Synthetic missing Runtime")));
        try
        {
            await unavailable.OpenAsync("https://www.weiyun.com/authorize/oauth?redirect_uri=http%3A%2F%2F127.0.0.1%3A54321%2Fcallback", CancellationToken.None);
            await UiSettingsNavigationChecks.Settle();
            check(unavailable.Notice.IsOpen && unavailable.RetryButton.IsEnabled && unavailable.ExternalButton.IsEnabled, "Missing Runtime offers retry and explicit external browser");
            check(!IsEnabled, "Login window prevents editing behind embedded authorization");
            if (routed) unavailable.RetryButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            else await UiSettingsNavigationChecks.Click(unavailable.RetryButton);
            await UiSettingsNavigationChecks.Settle();
            check(unavailable.Notice.IsOpen && unavailable.RetryButton.IsEnabled, "Unavailable Runtime retry stays usable");
            if (routed) unavailable.Close();
            else await UiSettingsNavigationChecks.Click(Descendants<Wpf.Ui.Controls.Button>(unavailable).Single(b => b.Content?.ToString() == "取消登录"));
            check(cancelCount == 1 && IsEnabled, "Closing failed login cancels once and restores owner");
        }
        finally { unavailable.Complete(); }

        var fixture = new CloudSession("fixture", "synthetic-access", "synthetic-refresh", long.MaxValue, "browser-fixture-space");
        var streams = new List<MemoryStream>();
        foreach (var theme in new[] { "classic-light", "classic-dark" })
        {
            Model.Settings.ThemeId = theme; ThemeService.Apply(Model.Settings); SyncSettings();
            var window = new CloudLoginWindow(this, () => cancelCount++, configureBrowser: core =>
            {
                core.AddWebResourceRequestedFilter("https://www.weiyun.com/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) =>
                {
                    var uri = new Uri(e.Request.Uri);
                    var html = uri.AbsolutePath == "/act/openclaw"
                        ? "<html><meta charset='utf-8'><script>window.syncData={data:{userInfo:{nick_name:'内嵌合成昵称',main_dir_key:'browser-fixture-space'}}};</script><body>合成账号资料页面</body></html>"
                        : "<html><meta charset='utf-8'><body><h2>合成官方登录页面 · 不是真实微信授权</h2><input id='name' style='position:absolute;left:25%;top:45%;width:50%;height:10%;box-sizing:border-box' value='keyboard-test'></body></html>";
                    var stream = new MemoryStream(Encoding.UTF8.GetBytes(html)); streams.Add(stream);
                    e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: text/html; charset=utf-8");
                };
            });
            try
            {
                await window.OpenAsync("https://www.weiyun.com/authorize/oauth?redirect_uri=http%3A%2F%2F127.0.0.1%3A54321%2Fcallback", CancellationToken.None);
                var core = window.Browser.CoreWebView2;
                check(core != null, "Published WebView2 initializes: " + theme);
                check(window.CacheDirectory.StartsWith(Path.Combine(_guestLibrary.DataDirectory, "Auth", "Browser") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "WebView2 cache stays in isolated Auth/Browser: " + theme);
                check(core!.Profile.IsInPrivateModeEnabled && !core.Settings.AreHostObjectsAllowed && !core.Settings.IsWebMessageEnabled, "Browser session is private and exposes no host APIs: " + theme);
                check(!window.AllowedNavigation("https://weiyun.com.attacker.invalid/") && !window.AllowedNavigation("file:///C:/Windows/win.ini") &&
                    !window.AllowedNavigation("http://127.0.0.1:54322/callback"), "Browser rejects foreign sites files and unrelated loopback ports: " + theme);
                await Task.Delay(300);
                if (!routed) { window.Activate(); await UiSettingsNavigationChecks.Click(window.Browser); }
                await core.ExecuteScriptAsync("document.getElementById('name').focus(); document.getElementById('name').setSelectionRange(8,8)");
                if (!routed) { keybd_event(0x25, 0, 0, 0); keybd_event(0x25, 0, 2, 0); await Task.Delay(100); }
                var selected = await core.ExecuteScriptAsync("document.getElementById('name').selectionStart");
                check(selected == (routed ? "8" : "7"), "Browser input focus and arrow key selection: " + theme);
                await using (var preview = File.Create(Path.Combine(output, "login-browser-" + theme + ".png")))
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
                var frame = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); frame.Render(window);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(frame));
                using (var file = File.Create(Path.Combine(output, "login-shell-" + theme + ".png"))) png.Save(file);
                var profile = await window.ReadProfileAsync(fixture, CancellationToken.None);
                check(profile?.DisplayName == "内嵌合成昵称" && profile.AccountId == fixture.AccountId, "Same embedded session reads only identity-matched profile: " + theme);
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await window.ReadProfileAsync(fixture, cancelled.Token); check(false, "Cancelled profile must throw"); }
                catch (OperationCanceledException) { check(true, "Cancelled profile is not returned to caller: " + theme); }
                if (theme == "classic-light")
                {
                    var mismatch = await window.ReadProfileAsync(fixture with { RootKey = "different-space" }, CancellationToken.None);
                    check(mismatch == null, "Embedded mismatched identity times out to default instead of using foreign profile");
                }
                window.Complete();
                check(IsEnabled && cancelCount == 1 && !window.IsVisible, "Completion closes embedded window without cancelling successful login: " + theme);
            }
            finally { window.Complete(); }
        }
        foreach (var stream in streams) stream.Dispose();
        ActivateAccountCheck();
    }
}
