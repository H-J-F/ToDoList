using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using ToDoList.Storage.Cloud;
using Wpf.Ui.Controls;

namespace ToDoList.App.Services;

internal partial class CloudLoginWindow : FluentWindow
{
    private readonly Action _cancel;
    private readonly string _cacheRoot = Path.Combine(AuthPaths.Root, "Browser");
    private readonly string _cache;
    private readonly CancellationTokenSource _closed = new();
    private bool _completed, _initializing, _external, _readingProfile, _ownerDisabled;
    private string? _authorizationUrl;
    private Uri? _callback;
    private CoreWebView2Environment? _environment;
    internal bool CanActivateFromBrowser { get; private set; }
    private readonly Func<string, Task<CoreWebView2Environment>> _createEnvironment;
    private readonly Action<CoreWebView2>? _configureBrowser;
    internal bool UsesExternalBrowser => _external;
    internal string CacheDirectory => _cache;

    public CloudLoginWindow(Window owner, Action cancel, Func<string, Task<CoreWebView2Environment>>? createEnvironment = null,
        Action<CoreWebView2>? configureBrowser = null)
    {
        InitializeComponent(); Owner = owner; _cancel = cancel;
        _createEnvironment = createEnvironment ?? (cache => CoreWebView2Environment.CreateAsync(userDataFolder: cache));
        _configureBrowser = configureBrowser;
        _cache = Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N"));
        Closed += (_, _) =>
        {
            _closed.Cancel(); if (!_completed) _cancel();
            EnableOwner(); Browser.Dispose();
        };
    }

    public async Task OpenAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); _authorizationUrl = url;
        var args = new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2));
        _callback = new Uri(Uri.UnescapeDataString(args.Single(p => p[0] == "redirect_uri")[1]));
        Show(); Owner.IsEnabled = false; _ownerDisabled = true;
        await InitializeBrowserAsync(ct);
    }

    private void EnableOwner() { if (_ownerDisabled) { Owner.IsEnabled = true; _ownerDisabled = false; } }
    private async Task InitializeBrowserAsync(CancellationToken ct)
    {
        if (_initializing || _external || _closed.IsCancellationRequested) return;
        _initializing = true; RetryButton.IsEnabled = false; Notice.IsOpen = false;
        Loading.Visibility = Visibility.Visible; Spinner.Visibility = Visibility.Visible;
        LoadingText.Text = "正在打开官方登录页面…";
        try
        {
            if (Browser.CoreWebView2 == null)
            {
                _environment ??= await _createEnvironment(_cache).WaitAsync(ct);
                _environment.BrowserProcessExited += (_, _) => CleanupCache();
                var options = _environment.CreateCoreWebView2ControllerOptions(); options.IsInPrivateModeEnabled = true;
                await Browser.EnsureCoreWebView2Async(_environment, options).WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (_external || _closed.IsCancellationRequested) return;
                var core = Browser.CoreWebView2 ?? throw new InvalidOperationException("浏览器未完成初始化。");
                core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
                core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, e) => e.Cancel = true;
                core.NavigationStarting += (_, e) =>
                {
                    if (!AllowedNavigation(e.Uri)) { e.Cancel = true; ShowNotice("官方页面请求了不支持的跳转，可改用系统浏览器。"); }
                };
                core.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    if (AllowedNavigation(e.Uri)) core.Navigate(e.Uri);
                    else ShowNotice("官方页面请求了不支持的跳转，可改用系统浏览器。");
                };
                core.NavigationCompleted += (_, e) =>
                {
                    if (_external || _closed.IsCancellationRequested) return;
                    Loading.Visibility = Visibility.Collapsed;
                    if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                        ShowNotice("官方页面未能加载，请重试或使用系统浏览器。");
                };
                _configureBrowser?.Invoke(core);
            }
            if (_external || _closed.IsCancellationRequested) return;
            Browser.Visibility = Visibility.Visible; Browser.CoreWebView2!.Navigate(_authorizationUrl!);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            Browser.Visibility = Visibility.Collapsed; Spinner.Visibility = Visibility.Collapsed;
            LoadingText.Text = "内嵌登录暂时不可用。可以重试，或使用系统浏览器完成授权。";
            ShowNotice("请确认已安装 Microsoft Edge WebView2 Runtime；程序不会自动安装运行时。");
        }
        finally { _initializing = false; RetryButton.IsEnabled = !_readingProfile && !_external; }
    }

    internal bool AllowedNavigation(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0) return false;
        if (value == "about:blank") return true;
        if (_callback != null && uri.Scheme == _callback.Scheme && uri.Host == _callback.Host &&
            uri.Port == _callback.Port && uri.AbsolutePath == _callback.AbsolutePath) return true;
        return uri.Scheme == "https" && uri.IsDefaultPort &&
            new[] { "weiyun.com", "qq.com", "weixin.qq.com" }.Any(host =>
                uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.IdnHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowNotice(string text) { Notice.Message = text; Notice.IsOpen = true; }
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        try { await InitializeBrowserAsync(_closed.Token); }
        catch (OperationCanceledException) { }
    }
    private void External_Click(object sender, RoutedEventArgs e)
    {
        if (_authorizationUrl == null || _readingProfile || _external || _closed.IsCancellationRequested) return;
        try
        {
            var registered = ActivationProtocol.EnsureRegistered();
            Process.Start(new ProcessStartInfo(_authorizationUrl) { UseShellExecute = true });
            _external = true; CanActivateFromBrowser = registered;
            Browser.CoreWebView2?.Stop(); Browser.Visibility = Visibility.Collapsed;
            RetryButton.IsEnabled = ExternalButton.IsEnabled = false; Spinner.Visibility = Visibility.Collapsed;
            Loading.Visibility = Visibility.Visible; LoadingText.Text = "请在系统浏览器中完成微信授权。完成后会返回 ToDoList。";
            Notice.IsOpen = !registered; if (!registered) Notice.Message = "此环境无法注册返回链接，授权后将尝试恢复 ToDoList 窗口。";
            EnableOwner(); Hide();
        }
        catch (Exception) { ShowNotice("系统浏览器未能打开，请重试内嵌登录。"); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    public void Complete() { _completed = true; if (!_closed.IsCancellationRequested) Close(); }

    public async Task<CloudAccountProfile?> ReadProfileAsync(CloudSession session, CancellationToken ct)
    {
        if (_external || _closed.IsCancellationRequested || Browser.CoreWebView2 == null) return null;
        _readingProfile = true; RetryButton.IsEnabled = ExternalButton.IsEnabled = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        try
        {
            LoadingText.Text = "授权已完成，正在读取当前账号资料…"; Notice.IsOpen = false;
            Browser.CoreWebView2.Navigate("https://www.weiyun.com/act/openclaw");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (IsProfilePage(Browser.CoreWebView2.Source))
                {
                    const string script = "(() => { if(location.origin !== 'https://www.weiyun.com' || location.pathname !== '/act/openclaw') return null; const u = window.syncData?.data?.userInfo ?? window.syncData?.userInfo; if(!u) return null; return {nick_name:u.nick_name ?? u.nickname, https_head_img_url:u.https_head_img_url ?? u.avatar, main_dir_key:u.main_dir_key ?? u.mainDirKey, root_dir_key:u.root_dir_key ?? u.rootDirKey}; })()";
                    var json = await Browser.CoreWebView2.ExecuteScriptAsync(script).WaitAsync(token);
                    // Navigation may have changed while the script was running.
                    if (IsProfilePage(Browser.CoreWebView2.Source))
                    {
                        using var doc = JsonDocument.Parse(json);
                        var verified = CloudAccountProfile.ReadVerified(doc.RootElement, session);
                        if (verified.HasValue)
                        {
                            var (profile, avatar) = verified.Value;
                            if (avatar == null) return profile;
                            try { return profile with { AvatarPng = await DownloadAvatarAsync(avatar, token) }; }
                            catch (Exception) when (!ct.IsCancellationRequested && !_closed.IsCancellationRequested) { return profile; }
                        }
                    }
                }
                await Task.Delay(200, token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_closed.IsCancellationRequested) { return null; }
        catch (Exception) when (!ct.IsCancellationRequested && !_closed.IsCancellationRequested) { return null; }
    }
    private static bool IsProfilePage(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        uri.GetLeftPart(UriPartial.Authority) == "https://www.weiyun.com" && uri.AbsolutePath == "/act/openclaw";

    internal static async Task<byte[]> DownloadAvatarAsync(Uri uri, CancellationToken ct)
    {
        if (!CloudAccountProfile.IsAvatarUri(uri)) throw new InvalidDataException("头像地址无效。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > CloudAccountProfile.MaxAvatarBytes) throw new InvalidDataException("头像过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var bytes = await WeiyunFileStore.ReadBoundedAsync(stream, CloudAccountProfile.MaxAvatarBytes, timeout.Token);
        using var input = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth is < 1 or > 4096 || frame.PixelHeight is < 1 or > 4096) throw new InvalidDataException("头像尺寸无效。");
        input.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 64; image.StreamSource = input; image.EndInit(); image.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }

    private void CleanupCache()
    {
        // Only the fresh GUID directory created by this window may be removed.
        var target = Path.GetFullPath(_cache); var root = Path.GetFullPath(_cacheRoot) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        try { if (Directory.Exists(target)) Directory.Delete(target, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
