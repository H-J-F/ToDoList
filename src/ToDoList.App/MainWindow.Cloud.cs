using System.IO;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using ToDoList.App.Services;
using ToDoList.Core;
using ToDoList.Storage;
using ToDoList.Storage.Cloud;

namespace ToDoList.App;

public partial class MainWindow
{
    private BookLibrary _guestLibrary = null!;
    private WindowsCloudCredentials _cloudCredentials = null!;
    private WeiyunAuthProvider? _cloudAuth;
    private WeiyunFileStore? _cloudFiles;
    private CloudBookStorage? _cloudBooks;
    private SyncCoordinator? _cloudCoordinator;
    private string? _cloudAccount;
    private CloudAccountProfile? _accountProfile;
    private BitmapImage? _accountImage;
    private CancellationTokenSource? _cloudCancellation;
    private Task _cloudOperation = Task.CompletedTask;
    private readonly DispatcherTimer _cloudTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _cloudBusy, _cloudAuthenticating, _cloudSwitching, _cloudApplying, _cloudAutoPaused;
    private int _cloudEpoch, _cloudFailures;
    private readonly CloudSyncSchedule _cloudSchedule = new();
    private SyncCoordinator? _localConflicts;
    private string? _localConflictDirectory;
    private bool _cloudWasOnline = true;
    private string _cloudText = "本地空间 · 登录后支持云同步";
    private TimeSpan? _lastSyncDuration;
    private string? CloudTestNamespace
    {
        get
        {
            var args = Environment.GetCommandLineArgs(); var i = Array.IndexOf(args, "--cloud-test-space");
            return i >= 0 && i + 1 < args.Length && args.Any(a => a is "--ui-cloud" or "--ui-login-live") ? args[i + 1] : null;
        }
    }
    private void InitializeCloud(BookLibrary guest)
    {
        _guestLibrary = guest; _cloudCredentials = new(guest.DataDirectory, AuthPaths.Root);
        string? migrationWarning = null;
        if (!AuthPaths.IsIsolated)
        {
            try { _cloudCredentials.MigrateLegacy(); }
            catch (Exception ex) { migrationWarning = "凭证迁移未完成：" + ex.Message; RuntimeLog.Write("cloud.credentials.migration-failed", error: ex); }
        }
        try
        {
            _cloudAccount = _cloudCredentials.ActiveAccount ?? (!AuthPaths.IsIsolated ? _cloudCredentials.LegacyActiveAccount : null);
            if (_cloudAccount != null)
            {
                Model.SetInitialLibrary(new(WindowsCloudCredentials.Workspace(guest.DataDirectory, _cloudAccount)));
                if (Model.Settings.LastBooksByWorkspace.TryGetValue(_cloudAccount, out var lastBook)) Model.Settings.LastBook = lastBook;
                ReloadAccountProfile(_cloudAccount);
                var session = _cloudCredentials.Load(_cloudAccount);
                if (session != null) { ConfigureCloud(session); if (!session.IsAuthorizationValid(DateTimeOffset.UtcNow)) { _cloudAutoPaused = true; _cloudText = "14天授权已到期，请重新扫码；账号本地笔记仍可使用。"; } }
                else _cloudText = "账号本地空间 · 请重新微信登录以恢复同步";
            }
        }
        catch (Exception ex) { _cloudAutoPaused = true; _cloudText = "保存的微云授权未能读取，请重新登录；账号本地笔记仍可使用。"; RuntimeLog.Write("cloud.credentials.restore-failed", error: ex); }
        if (migrationWarning != null) _cloudText = migrationWarning + "；当前本地笔记仍可使用。";
        CloudAutoSync.IsChecked = Model.Settings.CloudAutomaticSync;
        Model.Settings.CloudSyncIntervalMinutes = CloudSyncSchedule.Normalize(Model.Settings.CloudSyncIntervalMinutes);
        CloudInterval.Value = Array.IndexOf(CloudSyncSchedule.Intervals.ToArray(), Model.Settings.CloudSyncIntervalMinutes);
        UpdateCloudIntervalLabel();
        Database.Changed += CloudDataChanged;
        _cloudTimer.Tick += async (_, _) =>
        {
            if (_cloudAuth?.Session is { } active && !active.IsAuthorizationValid(DateTimeOffset.UtcNow))
            { _cloudAutoPaused = true; SetCloudStatus("14天授权已到期，请重新扫码；账号本地笔记仍可使用。"); }
            if (!_initialized || _cloudAccount == null || _cloudCoordinator == null || !Model.Settings.CloudAutomaticSync || _cloudAutoPaused ||
                _cloudBusy || _cloudSwitching || _closePending || _closingApproved) return;
            var online = NetworkInterface.GetIsNetworkAvailable();
            if (!online) { _cloudWasOnline = false; SetCloudStatus("离线 · 当前账号的本地数据可继续使用"); return; }
            var now = DateTime.UtcNow;
            if (!_cloudWasOnline) _cloudWasOnline = true;
            if (_cloudSchedule.Due(now, Model.Settings.CloudAutomaticSync)) await RunCloudSyncAsync(SyncDirection.Both, false);
        };
        UpdateCloudControls();
    }
    private void ConfigureCloud(CloudSession session)
    {
        CommitCloud(BuildCloud(session, Model.Library), session);
    }
    private sealed record CloudConnection(WeiyunAuthProvider Auth, WeiyunFileStore Files, CloudBookStorage Books, SyncCoordinator Coordinator);
    private CloudConnection BuildCloud(CloudSession session, BookLibrary library)
    {
        var auth = new WeiyunAuthProvider(url => OpenExternal(url), _cloudCredentials.Save, session);
        var files = new WeiyunFileStore(auth, CloudTestNamespace);
        try { var books = new CloudBookStorage(library); return new(auth, files, books, new(files, books, session.AccountId)); }
        catch { auth.Dispose(); files.Dispose(); throw; }
    }
    private void CommitCloud(CloudConnection connection, CloudSession session)
    {
        if (_cloudCoordinator?.LastSyncedAt == null || _cloudAuth?.Session?.AccountId != session.AccountId) _lastSyncDuration = null;
        Notifications.Dismiss();
        _cloudAuth?.Dispose(); _cloudFiles?.Dispose();
        _cloudAuth = connection.Auth; _cloudFiles = connection.Files; _cloudBooks = connection.Books; _cloudCoordinator = connection.Coordinator;
        ReloadAccountProfile(session.AccountId);
        _cloudText = "微云个人空间 · 已连接账号 " + session.AccountId[..6];
    }
    private async Task AdoptCloudSessionAsync(CloudSession session, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (session.AuthorizedAt == null) { session = session with { AuthorizedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }; _cloudCredentials.Save(session); }
        var target = new BookLibrary(WindowsCloudCredentials.Workspace(_guestLibrary.DataDirectory, session.AccountId));
        var prepared = BuildCloud(session, target); var committed = false;
        var previousLibrary = Model.Library; var previousLastBook = Model.Settings.LastBook;
        Model.Settings.LastBooksByWorkspace[_cloudAccount ?? "guest"] = Model.CurrentBook?.BookId;
        Model.Settings.LastBook = Model.Settings.LastBooksByWorkspace.GetValueOrDefault(session.AccountId);
        Model.IsBusy = true; _sync = true;
        try
        {
            await Model.SwitchLibraryAsync(target);
            try { ct.ThrowIfCancellationRequested(); _cloudCredentials.Activate(session.AccountId); }
            catch { Model.Settings.LastBook = previousLastBook; await Model.SwitchLibraryAsync(previousLibrary); throw; }
            _cloudAccount = session.AccountId; CommitCloud(prepared, session); committed = true;
            prepared.Coordinator.AuthorizationRenewed();
            _cloudAutoPaused = false; _cloudFailures = 0; _cloudSchedule.Reset();
            DraftEditor.SetContent(RichContent.FromText("")); SyncSelectors(true); SyncSettings();
            try { Model.SaveSettings(); } catch (Exception ex) { SetCloudStatus("微云已连接，但本地偏好未保存：" + ex.Message); }
        }
        catch { if (!committed) Model.Settings.LastBook = previousLastBook; throw; }
        finally { if (!committed) { prepared.Auth.Dispose(); prepared.Files.Dispose(); } Model.IsBusy = false; _sync = false; }
    }
    private void StartCloud() { _cloudTimer.Start(); _cloudSchedule.Request(DateTime.UtcNow); }
    private async Task AdoptAndInitializeCloudAsync(CloudSession session, Func<CancellationToken, Task> initialize, CancellationToken ct = default)
    {
        await AdoptCloudSessionAsync(session, ct);
        try { await initialize(ct); }
        catch { _cloudAutoPaused = true; throw; }
    }
    private void CloudDataChanged(string path)
    {
        if (_cloudApplying && Path.GetFileName(path) == ".cloud-applied") return;
        if (!string.Equals(Path.GetDirectoryName(path), Model.Library.DataDirectory, StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_undoInProgress) return;
            Notifications.Dismiss();
            if (_closingApproved || _cloudAccount == null) return;
            _cloudSchedule.Changed(DateTime.UtcNow); SetCloudStatus("本地变更已保存 · 等待同步");
        }));
    }
    private void SetCloudStatus(string text) { _cloudText = text; UpdateCloudControls(); }
    private void ReloadAccountProfile(string accountId)
    {
        _accountProfile = null; _accountImage = null;
        try
        {
            _accountProfile = _cloudCredentials.LoadProfile(accountId);
            if (_accountProfile?.AvatarPng is { Length: > 0 } bytes)
            {
                using var stream = new MemoryStream(bytes);
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 64; image.StreamSource = stream; image.EndInit(); image.Freeze(); _accountImage = image;
            }
        }
        catch (Exception) { _accountImage = null; }
    }
    private void UpdateCloudControls()
    {
        if (CloudStatus == null) return;
        var authorized = _cloudAuth?.Session?.IsAuthorizationValid(DateTimeOffset.UtcNow) == true;
        var profile = authorized && _accountProfile?.AccountId == _cloudAccount ? _accountProfile : null;
        AccountName.Text = authorized ? profile?.DisplayName ?? "已授权" : "未登录";
        AccountName.ToolTip = AccountName.Text;
        AccountName.SetResourceReference(TextBlock.ForegroundProperty, authorized ? "InkBrush" : "MutedBrush");
        AccountAvatar.SetResourceReference(Control.ForegroundProperty, authorized ? "AccentInkBrush" : "MutedBrush");
        AccountAvatarImage.Source = profile == null ? null : _accountImage;
        AccountAvatarImage.Visibility = AccountAvatarImage.Source == null ? Visibility.Collapsed : Visibility.Visible;
        AccountAvatar.Visibility = AccountAvatarImage.Source == null ? Visibility.Visible : Visibility.Collapsed;
        AccountIdentity.Text = _cloudAccount == null ? "本地空间" : "微云账号 · " + _cloudAccount[..6];
        CloudStatus.Text = _cloudText;
        CloudLastSync.Text = _cloudCoordinator?.LastSyncedAt is { } last ? "上次同步：" + DateTimeOffset.FromUnixTimeMilliseconds(last).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "尚未同步";
        CloudSyncDuration.Text = _lastSyncDuration is { } duration ? $" | 耗时: {duration.TotalSeconds:F2} 秒" : "";
        CloudSyncDuration.Visibility = _lastSyncDuration.HasValue ? Visibility.Visible : Visibility.Collapsed;
        CloudLoginButton.Content = _cloudAuthenticating ? "取消本次登录" : _cloudAccount == null ? "微信登录微云" : "重新登录 / 切换微信账号";
        CloudLoginButton.IsEnabled = !_cloudApplying;
        var ready = authorized && _cloudCoordinator != null && !_cloudBusy && !_cloudSwitching && !_cloudApplying;
        CloudSyncButton.IsEnabled = CloudUploadButton.IsEnabled = CloudDownloadButton.IsEnabled = ready;
        CloudLogoutButton.IsEnabled = _cloudAccount != null && !_cloudApplying && !_cloudSwitching;
        CloudCopyButton.IsEnabled = ready;
        var count = ConflictCoordinator.Conflicts.Count;
        CloudConflictsButton.IsEnabled = !_cloudBusy && !_cloudSwitching && !_cloudApplying && count > 0;
        CloudConflictsButton.Content = count > 0 ? $"处理同步冲突（{count}）" : "处理同步冲突";
    }
    private void CancelCloudOperation() => _cloudCancellation?.Cancel();
    private async Task StopCloudAsync()
    {
        CancelCloudOperation();
        // Each caller already reports operation errors; a previous failure must not block retry.
        try { await _cloudOperation; } catch (Exception) { }
        _cloudEpoch++;
    }
    private async void CloudLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudAuthenticating) { CancelCloudOperation(); return; }
        if (_cloudSwitching || _cloudApplying || !await TryLeaveEditsAsync()) return;
        _cloudSwitching = true;
        var loggedIn = false;
        try
        {
            await StopCloudAsync();
            _cloudCancellation?.Dispose(); _cloudCancellation = new();
            _cloudAuthenticating = true; SetCloudStatus("请在应用内的官方微云页面用微信扫码并确认授权");
            var operation = LoginCloudSessionAsync(_cloudCancellation.Token); _cloudOperation = operation;
            await operation;
            loggedIn = true;
        }
        catch (OperationCanceledException) { SetCloudStatus("本次微信登录已取消或超时，可重新登录。"); }
        catch (Exception ex) { SetCloudStatus("微信登录／云连接未完成：" + ex.Message + (_cloudAccount == null ? "" : "；当前账号本地笔记仍可使用。")); }
        finally { _cloudAuthenticating = false; _cloudSwitching = false; UpdateCloudControls(); }
        if (loggedIn && _cloudCoordinator != null && _cloudAccount != null) await RunCloudSyncAsync(SyncDirection.Both, true);
    }
    private async Task LoginCloudSessionAsync(CancellationToken ct)
    {
        var browser = new CloudLoginWindow(this, CancelCloudOperation);
        try
        {
            using var candidate = new WeiyunAuthProvider(browser.OpenAsync, _cloudCredentials.Save,
                canActivateFromBrowser: () => browser.CanActivateFromBrowser);
            await candidate.LoginAsync(ct);
            if (browser.UsesExternalBrowser)
            {
                if (IsVisible && WindowState != WindowState.Minimized) _stateBeforeTray = WindowState;
                RestoreFromTray();
            }
            using var files = new WeiyunFileStore(candidate, CloudTestNamespace);
            // Verify identity separately: directory setup is never a prerequisite
            // for opening this account's existing local notes.
            await files.CheckAccessAsync(ct); var session = candidate.Session!;
            // External login leaves the main window usable; protect any edits made meanwhile.
            if (!await TryLeaveEditsAsync()) return;
            ct.ThrowIfCancellationRequested();
            await AdoptAndInitializeCloudAsync(session, async token => { await files.InitializeAsync(token); }, ct);
            var profile = await browser.ReadProfileAsync(session, ct);
            ct.ThrowIfCancellationRequested();
            browser.Complete();
            try
            {
                _cloudCredentials.SaveProfile(profile ?? new(session.AccountId, null));
                ReloadAccountProfile(session.AccountId); UpdateCloudControls();
            }
            catch (Exception) { SetCloudStatus("微云已授权；账号显示资料未能缓存，笔记同步仍可使用。"); }
        }
        finally { browser.Complete(); }
    }
    private async void CloudLogout_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudSwitching || _cloudApplying || !await TryLeaveEditsAsync()) return;
        _cloudSwitching = true;
        try
        {
            await StopCloudAsync(); Model.IsBusy = true; _sync = true;
            var previousLibrary = Model.Library; var previousLast = Model.Settings.LastBook;
            Model.Settings.LastBooksByWorkspace[_cloudAccount!] = Model.CurrentBook?.BookId;
            Model.Settings.LastBook = Model.Settings.LastBooksByWorkspace.GetValueOrDefault("guest");
            try { await Model.SwitchLibraryAsync(_guestLibrary); _cloudCredentials.Logout(); }
            catch { Model.Settings.LastBook = previousLast; await Model.SwitchLibraryAsync(previousLibrary); throw; }
            _cloudAccount = null; _cloudAuth?.Dispose(); _cloudFiles?.Dispose();
            _lastSyncDuration = null; Notifications.Dismiss();
            _accountProfile = null; _accountImage = null;
            _cloudAuth = null; _cloudFiles = null; _cloudBooks = null; _cloudCoordinator = null; _cloudSchedule.Reset();
            Model.SaveSettings(); SyncSelectors(true); SyncSettings(); SetCloudStatus("本地空间 · 已退出微云，账号本地缓存与云端存档仍保留");
        }
        catch (Exception ex) { SetCloudStatus("退出微云未完成：" + ex.Message); }
        finally { Model.IsBusy = false; _sync = false; _cloudSwitching = false; UpdateCloudControls(); }
    }
    private Task<bool> ApplyCloudAsync(CloudSnapshot expected, CloudSnapshot desired, CancellationToken ct) =>
        _cloudBooks == null ? Task.FromResult(false) : ApplySnapshotAsync(_cloudBooks, expected, desired, ct);
    private async Task<bool> ApplySnapshotAsync(CloudBookStorage storage, CloudSnapshot expected, CloudSnapshot desired, CancellationToken ct)
    {
        var changes = CloudBookStorage.PreviewChanges(expected, desired);
        if (changes.HasChanges && (_navigating || Model.IsBusy || _editingCard != null || _savingRow || _pendingMutations > 0 ||
            DraftEditor.IsComposing || !string.IsNullOrWhiteSpace(DraftEditor.GetContent().PlainText) || _closingApproved ||
            (Application.Current as App)?.ExitRequested == true)) return false;
        ct.ThrowIfCancellationRequested(); _cloudApplying = true;
        try
        {
            var applied = await storage.ApplyWithChangesAsync(expected, desired, ct);
            if (applied.HasChanges)
            {
                var previous = _sync; _sync = true;
                try { await Model.RefreshAfterCloudAsync(applied); SyncSelectors(); if (applied.HasKind(Model.CurrentBook?.BookId ?? "", "setting")) SyncStatusColors(); }
                finally { _sync = previous; }
            }
            return true;
        }
        finally { _cloudApplying = false; UpdateCloudControls(); }
    }
    private async Task RunCloudSyncAsync(SyncDirection direction, bool manual)
    {
        if (_cloudBusy || _cloudSwitching || _undoInProgress || _cloudCoordinator == null || _closePending || _closingApproved) return;
        _cloudBusy = true; _cloudCancellation?.Dispose(); _cloudCancellation = new();
        var epoch = _cloudEpoch;
        var initialLoad = _cloudCoordinator.LastSyncedAt == null;
        if (initialLoad) { Model.LoadingMessage = "正在连接云空间…"; Model.LoadingIndeterminate = true; Model.CanCancelInitialLoad = true; Model.IsInitialLoading = true; }
        var progress = new Progress<CloudSyncProgress>(p =>
        {
            if (!initialLoad || !Model.IsInitialLoading || epoch != _cloudEpoch) return;
            Model.LoadingIndeterminate = p.Total is null or <= 0;
            Model.LoadingProgress = p.Total is > 0 ? (double)p.Completed / p.Total.Value : 0;
            Model.LoadingMessage = p.Total is > 0 ? $"{p.Stage}（{p.Completed}/{p.Total}）" : p.Stage;
        });
        if (manual) _cloudAutoPaused = false;
        var generationAtStart = _cloudSchedule.Generation;
        Notifications.Clock.Synced(); var stopwatch = Stopwatch.StartNew(); SetCloudStatus("正在同步当前微云账号的数据…");
        try
        {
            var task = _cloudCoordinator.SyncAsync(direction, (a, b, c) => epoch == _cloudEpoch ? ApplyCloudAsync(a, b, c) : Task.FromResult(false), _cloudCancellation.Token, progress);
            _cloudOperation = task; var result = await task;
            if (epoch != _cloudEpoch) return;
            _cloudFailures = 0; _cloudSchedule.Completed(DateTime.UtcNow, Model.Settings.CloudSyncIntervalMinutes, generationAtStart, result.Deferred);
            if (!result.Deferred && result.Outcome != CloudSyncOutcome.Unchanged) _lastSyncDuration = stopwatch.Elapsed;
            SetCloudStatus(result.Outcome == CloudSyncOutcome.Unchanged ? "已检查，无变化" : result.Deferred ? "当前正在编辑 · 等待下个周期或手动同步" : result.Conflicts > 0 ? $"其他数据已同步 · {result.Conflicts} 项冲突待处理" : "微云空间 · " + DateTime.Now.ToString("HH:mm:ss") + (direction == SyncDirection.Download ? " 已下载变更" : " 已同步"));
            RuntimeLog.Write("sync.ui-result", new { result.Outcome, result.Uploaded, result.Conflicts, elapsedMs = stopwatch.Elapsed.TotalMilliseconds });
            if (result.CleanupWarning != null) SetCloudStatus(result.CleanupWarning);
            if (manual && result.Deferred) Model.Message = "请先保存或结束当前编辑，再同步云端数据。";
        }
        catch (OperationCanceledException)
        {
            if (epoch == _cloudEpoch) { _cloudSchedule.Failed(DateTime.UtcNow, Model.Settings.CloudSyncIntervalMinutes); SetCloudStatus("同步已暂停 · 本地变更与待上传记录已保留"); }
        }
        catch (CloudLocalChangedException)
        {
            if (epoch != _cloudEpoch) return;
            if (_cloudSchedule.Generation == generationAtStart) _cloudSchedule.Changed(DateTime.UtcNow);
            _cloudSchedule.Failed(DateTime.UtcNow, Model.Settings.CloudSyncIntervalMinutes);
            SetCloudStatus("本地数据已更新 · 等待同步最新变更");
        }
        catch (Exception ex)
        {
            if (epoch != _cloudEpoch) return;
            RuntimeLog.Write("sync.ui-failed", error: ex);
            _cloudFailures++; _cloudSchedule.Failed(DateTime.UtcNow, Model.Settings.CloudSyncIntervalMinutes);
            if (ex is CloudAuthenticationException || ex is CloudTransportException { Kind: CloudFailureKind.Disabled or CloudFailureKind.StorageQuota } || ex is CloudTransportException { Code: 117402 }) _cloudAutoPaused = true;
            var text = ex.Message + (ex is CloudTransportException { RetryAt: { } retry } ? " 恢复时间：" + retry.ToLocalTime().ToString("MM-dd HH:mm:ss") : "");
            SetCloudStatus(text); if (manual) Model.Message = "云同步未完成：" + text;
        }
        finally { if (initialLoad) { Model.IsInitialLoading = false; Model.CanCancelInitialLoad = false; } _cloudBusy = false; UpdateCloudControls(); }
    }
    private void CancelInitialCloudLoad_Click(object sender, RoutedEventArgs e) => CancelCloudOperation();
    private void OpenRunLog_Click(object sender, RoutedEventArgs e)
    {
        try { var path = Path.Combine(AppContext.BaseDirectory, "Log"); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void CloudSync_Click(object sender, RoutedEventArgs e) => await RunCloudSyncAsync(SyncDirection.Both, true);
    private async void CloudUpload_Click(object sender, RoutedEventArgs e) => await RunCloudSyncAsync(SyncDirection.Upload, true);
    private async void CloudDownload_Click(object sender, RoutedEventArgs e) => await RunCloudSyncAsync(SyncDirection.Download, true);
    private void CloudAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _sync || _cloudCredentials == null) return;
        var previous = Model.Settings.CloudAutomaticSync;
        Model.Settings.CloudAutomaticSync = CloudAutoSync.IsChecked == true;
        try { Model.SaveSettings(); _cloudSchedule.Request(DateTime.UtcNow); }
        catch (Exception ex)
        {
            Model.Settings.CloudAutomaticSync = previous;
            var sync = _sync; _sync = true; CloudAutoSync.IsChecked = previous; _sync = sync;
            SetCloudStatus("自动同步设置未保存：" + ex.Message);
        }
    }
    private async void CloudCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudBusy || _cloudSwitching || _cloudAccount == null || !await TryLeaveEditsAsync()) return;
        var choice = await Dialogs.Choose(this, "导入本地笔记", "将本地空间的所有笔记导入当前微云账号。同一 ID 的笔记合并，不同版本保留供处理冲突；源笔记保留。随后同步到云端。", "取消", "导入笔记");
        if (choice != 1) return;
        _cloudSwitching = true; Model.IsBusy = true; _sync = true;
        try
        {
            foreach (var book in await _guestLibrary.ListAsync())
            {
                await Model.Library.CopyBookAsync(_guestLibrary, book);
            }
            await Model.SwitchLibraryAsync(Model.Library); SyncSelectors(true); _cloudSchedule.Changed(DateTime.UtcNow);
            SetCloudStatus("本地笔记已导入当前账号 · 等待上传");
        }
        catch (Exception ex) { SetCloudStatus("导入笔记未完成：" + ex.Message); }
        finally { Model.IsBusy = false; _sync = false; _cloudSwitching = false; UpdateCloudControls(); }
        await RunCloudSyncAsync(SyncDirection.Both, true);
    }
    private async void CloudConflicts_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudBusy || _cloudSwitching || !await TryLeaveEditsAsync()) return;
        _cloudSwitching = true;
        try
        {
            var coordinator = ConflictCoordinator; var storage = new CloudBookStorage(Model.Library);
            var conflicts = coordinator.Conflicts.ToArray();
            foreach (var (key, versions) in conflicts)
            {
                var current = await storage.CaptureAsync();
                var selection = await Dialogs.CloudConflict(this, key, versions, current.Values.GetValueOrDefault(key)?.FirstOrDefault());
                if (selection == null) break;
                _cloudBusy = true; UpdateCloudControls();
                try
                {
                    await coordinator.ResolveAsync(key, selection.Version, selection.KeepBoth, (a, b, c) => ApplySnapshotAsync(storage, a, b, c));
                    _cloudSchedule.Changed(DateTime.UtcNow); SetCloudStatus("冲突选择已保存 · 等待同步");
                }
                catch (Exception ex) { SetCloudStatus("冲突未解决：" + ex.Message); break; }
                finally { _cloudBusy = false; UpdateCloudControls(); }
            }
        }
        finally { _cloudSwitching = false; UpdateCloudControls(); }
        await RunCloudSyncAsync(SyncDirection.Both, true);
    }
    private SyncCoordinator ConflictCoordinator
    {
        get
        {
            if (_cloudCoordinator != null) return _cloudCoordinator;
            if (_localConflicts == null || _localConflictDirectory != Model.Library.DataDirectory)
            {
                _localConflicts = new(new LocalCloudFiles(), new(Model.Library), _cloudAccount ?? new string('0', 64));
                _localConflictDirectory = Model.Library.DataDirectory;
            }
            return _localConflicts;
        }
    }
    private sealed class LocalCloudFiles : ICloudFileStore
    {
        public Task<CloudSpace> InitializeAsync(CancellationToken ct = default) => throw new InvalidOperationException("本地空间不发起网络同步。");
        public Task<IReadOnlyList<CloudFile>> ListAsync(CloudArea area, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> ReadAsync(CloudArea area, CloudFile file, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CloudFile> WriteImmutableAsync(CloudArea area, string name, byte[] bytes, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private void CloudInterval_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || _sync || CloudIntervalLabel == null) return;
        var index = Math.Clamp((int)Math.Round(e.NewValue), 0, CloudSyncSchedule.Intervals.Count - 1);
        var previous = Model.Settings.CloudSyncIntervalMinutes;
        Model.Settings.CloudSyncIntervalMinutes = CloudSyncSchedule.Intervals[index];
        try
        {
            Model.SaveSettings();
            _cloudSchedule.Completed(DateTime.UtcNow, Model.Settings.CloudSyncIntervalMinutes, null, false);
        }
        catch (Exception ex) { Model.Settings.CloudSyncIntervalMinutes = previous; CloudInterval.Value = Array.IndexOf(CloudSyncSchedule.Intervals.ToArray(), previous); SetCloudStatus("同步周期未保存：" + ex.Message); }
        UpdateCloudIntervalLabel();
    }
    private void UpdateCloudIntervalLabel() => CloudIntervalLabel.Text = Model.Settings.CloudSyncIntervalMinutes == 60
        ? "同步周期：1 小时" : $"同步周期：{Model.Settings.CloudSyncIntervalMinutes} 分钟";
    private void DisposeCloud()
    {
        _cloudTimer.Stop(); CancelCloudOperation(); Database.Changed -= CloudDataChanged;
        _cloudAuth?.Dispose(); _cloudFiles?.Dispose();
    }
}
