using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ToDoList.Core;

namespace ToDoList.Storage.Cloud;

public sealed record CachedCloudCommit(CloudCommit Commit, CloudSnapshot Snapshot);
public sealed class CloudSyncState
{
    public int Format { get; set; } = 1;
    public string AccountId { get; set; } = "";
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public CloudSnapshot Baseline { get; set; } = new();
    public Dictionary<string, List<CloudValue>> Unresolved { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<CloudValue>> Overrides { get; set; } = new(StringComparer.Ordinal);
    public CachedCloudCommit? Pending { get; set; }
    public long? LastSyncedAt { get; set; }
    public CloudDbSyncState? Db { get; set; }
    public string? LocalCheckpoint { get; set; }
    public string? RemoteInventory { get; set; }
    public bool NeedsUpload { get; set; }
    public string? ImportFingerprint { get; set; }
}
public enum CloudSyncOutcome { Synced, Unchanged, Deferred }
public sealed record CloudSyncResult(bool Deferred, int Conflicts, bool Uploaded, string? CleanupWarning = null, CloudSyncOutcome Outcome = CloudSyncOutcome.Synced);
public sealed partial class SyncCoordinator
{
    private readonly ICloudFileStore _files;
    private readonly CloudBookStorage _books;
    private readonly string _account;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CachedCloudCommit> _commits = new(StringComparer.Ordinal);
    private CloudSyncState _state;
    private string StatePath => Path.Combine(_books.StateDirectory, "state.json");
    public IReadOnlyDictionary<string, List<CloudValue>> Conflicts
    {
        get
        {
            var values = _state.Unresolved.ToDictionary(p => p.Key, p => p.Value.ToList());
            foreach (var (key, imported) in _books.ImportVersions().Values)
                values[key] = CloudMerge.Union(key, (values.GetValueOrDefault(key) ?? []).Concat(imported));
            return values;
        }
    }
    public bool HasPending => _state.Pending != null || _state.Db?.PendingName != null || _state.Overrides.Count > 0;
    public long? LastSyncedAt => _state.LastSyncedAt;
    public SyncCoordinator(ICloudFileStore files, CloudBookStorage books, string account)
    {
        _files = files; _books = books; _account = account;
        _state = File.Exists(StatePath) ? CloudJson.Parse<CloudSyncState>(File.ReadAllText(StatePath)) : new() { AccountId = account };
        if (_state.Format != 1 || _state.AccountId != account || !Guid.TryParseExact(_state.DeviceId, "N", out _)) throw new InvalidDataException("同步缓存与当前账号不一致，未同步任何数据。");
        _state.Baseline.Validate();
        if (_state.Db != null)
        {
            var db = _state.Db;
            db.Document.Validate();
            if (!CloudDbCodec.ProtocolName.IsMatch(db.ProtocolName) || db.Counter < 0 || db.Generation < 0 ||
                db.Document.Records.Values.Any(r => r.Context.GetValueOrDefault(_state.DeviceId) > db.Counter))
                throw new InvalidDataException("本地云 DB 因果缓存无效。");
        }
        var cache = Path.Combine(_books.StateDirectory, "commits");
        if (_state.Db == null && Directory.Exists(cache)) foreach (var path in Directory.EnumerateFiles(cache, "*.json"))
        {
            var cached = CloudJson.Parse<CachedCloudCommit>(File.ReadAllText(path)); ValidateCommit(cached.Commit); cached.Snapshot.Validate();
            if (Path.GetFileName(path) != cached.Commit.Id + ".json") throw new InvalidDataException("同步提交缓存标识不一致。");
            _commits.Add(cached.Commit.Id, cached);
        }
        if (_state.Pending != null) { ValidateCommit(_state.Pending.Commit); _state.Pending.Snapshot.Validate(); }
    }
    private Dictionary<string, List<CloudValue>> EffectiveOverrides()
    {
        var values = _state.Overrides.ToDictionary(p => p.Key, p => p.Value.ToList());
        foreach (var (key, imported) in _books.ImportVersions().Values)
            if (values.TryGetValue(key, out var existing)) values[key] = CloudMerge.Union(key, existing.Concat(imported));
        return values;
    }
    private void SaveState() => CloudBookStorage.AtomicWrite(StatePath, CloudJson.Serialize(_state));
    private void Cache(CachedCloudCommit entry)
    {
        ValidateCommit(entry.Commit); entry.Snapshot.Validate();
        CloudBookStorage.AtomicWrite(Path.Combine(_books.StateDirectory, "commits", entry.Commit.Id + ".json"), CloudJson.Serialize(entry));
        _commits[entry.Commit.Id] = entry;
    }
    private void ValidateCommit(CloudCommit commit)
    {
        if (commit.Format != 1 || commit.AccountId != _account || !Guid.TryParseExact(commit.Id, "N", out _) || !Guid.TryParseExact(commit.DeviceId, "N", out _) ||
            commit.Parents == null || commit.Parents.Count > 1000 || commit.Parents.Distinct().Count() != commit.Parents.Count ||
            commit.Parents.Any(p => !Guid.TryParseExact(p, "N", out _) || p == commit.Id) ||
            !Regex.IsMatch(commit.Sha256 ?? "", "\\A[a-f0-9]{64}\\z") || commit.SnapshotName != commit.Sha256 + ".snapshot.gz")
            throw new InvalidDataException("云端提交标识、账号或格式无效。");
    }
    public static byte[] Encode(CloudSnapshot snapshot)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
        { var bytes = Encoding.UTF8.GetBytes(CloudJson.Serialize(snapshot)); if (bytes.Length > 128 * 1024 * 1024) throw new InvalidDataException("云存档超过支持的大小，本地数据仍可使用。"); gzip.Write(bytes); }
        return output.ToArray();
    }
    public static async Task<CloudSnapshot> DecodeAsync(byte[] bytes, CancellationToken ct)
    {
        using var input = new MemoryStream(bytes); using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var plain = await WeiyunFileStore.ReadBoundedAsync(gzip, 128 * 1024 * 1024, ct);
        var snapshot = CloudJson.Parse<CloudSnapshot>(Encoding.UTF8.GetString(plain)); snapshot.Validate(); return snapshot;
    }
    private async Task LoadRemoteAsync(CancellationToken ct, IProgress<CloudSyncProgress>? progress = null)
    {
        var commitFiles = (await _files.ListAsync(CloudArea.Sync, ct)).Where(f => Regex.IsMatch(f.Name, "\\Acommit-[a-f0-9]{32}\\.json\\z")).ToArray();
        if (commitFiles.GroupBy(f => f.Name).Any(g => g.Count() != 1)) throw new InvalidDataException("云端存在同名提交冲突。");
        IReadOnlyList<CloudFile>? dataFiles = null;
        var completed = 0;
        foreach (var file in commitFiles)
        {
            ct.ThrowIfCancellationRequested(); var id = file.Name.Substring(7, 32);
            progress?.Report(new("正在迁移旧云存档", completed++, commitFiles.Length));
            if (_commits.ContainsKey(id)) continue;
            if (file.Size > 1024 * 1024) throw new InvalidDataException("云端提交记录过大。");
            var commit = CloudJson.Parse<CloudCommit>(Encoding.UTF8.GetString(await _files.ReadAsync(CloudArea.Sync, file, ct))); ValidateCommit(commit);
            if (commit.Id != id) throw new InvalidDataException("云端提交文件名与标识不一致。");
            dataFiles ??= await _files.ListAsync(CloudArea.Data, ct);
            var snapshots = dataFiles.Where(f => f.Name == commit.SnapshotName).ToArray();
            if (snapshots.Length != 1) throw new InvalidDataException("云端提交引用的存档缺失或重名，未修改本地数据。");
            var bytes = await _files.ReadAsync(CloudArea.Data, snapshots[0], ct);
            if (CloudJson.Hash(bytes) != commit.Sha256) throw new InvalidDataException("云端存档校验失败，未修改本地数据。");
            var snapshot = await Task.Run(() => DecodeAsync(bytes, ct), ct); Cache(new(commit, snapshot));
        }
    }
    private List<string> Heads()
    {
        var parentIds = new HashSet<string>();
        foreach (var entry in _commits.Values)
            foreach (var parent in entry.Commit.Parents)
            { if (!_commits.ContainsKey(parent)) throw new InvalidDataException("云端提交历史暂不完整，请稍后重试。"); parentIds.Add(parent); }
        // Kahn's algorithm rejects cycles without recursively walking a long history.
        var degree = _commits.ToDictionary(p => p.Key, p => p.Value.Commit.Parents.Count);
        var children = _commits.Keys.ToDictionary(k => k, _ => new List<string>());
        foreach (var entry in _commits.Values) foreach (var parent in entry.Commit.Parents) children[parent].Add(entry.Commit.Id);
        var queue = new Queue<string>(degree.Where(p => p.Value == 0).Select(p => p.Key)); var visited = 0;
        while (queue.TryDequeue(out var id)) { visited++; foreach (var child in children[id]) if (--degree[child] == 0) queue.Enqueue(child); }
        if (visited != _commits.Count) throw new InvalidDataException("云端提交历史包含循环。");
        return _commits.Keys.Where(k => !parentIds.Contains(k)).Order(StringComparer.Ordinal).ToList();
    }
    private Dictionary<string, int> Ancestors(string head)
    {
        var found = new Dictionary<string, int> { [head] = 0 }; var queue = new Queue<string>(); queue.Enqueue(head);
        while (queue.TryDequeue(out var id)) foreach (var parent in _commits[id].Commit.Parents)
            if (found.TryAdd(parent, found[id] + 1)) queue.Enqueue(parent);
        return found;
    }
    private CloudSnapshot RemoteSnapshot(List<string> heads)
    {
        if (heads.Count == 0) return new();
        var merged = _commits[heads[0]].Snapshot.Copy(); var ancestors = Ancestors(heads[0]);
        foreach (var head in heads.Skip(1))
        {
            var other = Ancestors(head);
            var shared = ancestors.Keys.Intersect(other.Keys).OrderBy(k => ancestors[k] + other[k]).ThenBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            merged = CloudMerge.Merge(shared == null ? new() : _commits[shared].Snapshot, merged, _commits[head].Snapshot);
            ancestors = ancestors.Where(p => other.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value + other[p.Key]);
        }
        return merged;
    }
    private async Task FlushPendingAsync(CancellationToken ct)
    {
        var pending = _state.Pending; if (pending == null) return;
        var outboxPath = Path.Combine(_books.StateDirectory, "outbox", pending.Commit.Id + ".snapshot.gz");
        if (!File.Exists(outboxPath)) throw new InvalidDataException("待上传存档文件缺失，本地数据仍保留，请先备份数据再修复同步缓存。");
        var bytes = await File.ReadAllBytesAsync(outboxPath, ct);
        if (CloudJson.Hash(bytes) != pending.Commit.Sha256) throw new InvalidDataException("待上传存档的校验值不一致。");
        await _files.WriteImmutableAsync(CloudArea.Data, pending.Commit.SnapshotName, bytes, ct);
        await _files.WriteImmutableAsync(CloudArea.Sync, "commit-" + pending.Commit.Id + ".json", Encoding.UTF8.GetBytes(CloudJson.Serialize(pending.Commit)), ct);
        Cache(pending); _state.Baseline = pending.Snapshot;
        foreach (var key in _state.Overrides.Keys.ToArray())
            if (CloudMerge.Equal(key, _state.Overrides[key], pending.Snapshot.Values.GetValueOrDefault(key))) _state.Overrides.Remove(key);
        _state.Unresolved = pending.Snapshot.Conflicts.ToDictionary(p => p.Key, p => p.Value.ToList());
        _state.Pending = null; SaveState();
        File.Delete(outboxPath);
    }
    public async Task ResolveAsync(string key, int version, bool keepBoth, Func<CloudSnapshot, CloudSnapshot, CancellationToken, Task<bool>> apply, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!Conflicts.TryGetValue(key, out var versions) || version < 0 || version >= versions.Count) throw new InvalidOperationException("冲突版本已变化，请刷新后重试。");
            var expected = await _books.CaptureAsync(ct); var desired = CloudMerge.PrepareLocal(expected.Copy(), new() { Values = new(Conflicts.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal) }, EffectiveOverrides());
            var chosen = versions[version]; desired.Values[key] = [chosen];
            var book = CloudSnapshot.Book(key); var kind = CloudSnapshot.Kind(key);
            var updates = new Dictionary<string, List<CloudValue>> { [key] = [chosen] };
            if (keepBoth)
            {
                if (kind is not ("task" or "project" or "book")) throw new InvalidOperationException("此类冲突请选择一个有效版本。");
                foreach (var other in versions.Where(v => !v.Deleted && v.Semantic(key) != chosen.Semantic(key)))
                {
                    var id = Guid.NewGuid().ToString("N");
                    if (kind == "task")
                    {
                        var task = other.Read<TodoItem>() with { Id = id, Revision = 1 };
                        var cloneKey = CloudSnapshot.Key(book, kind, id); updates[cloneKey] = desired.Values[cloneKey] = [CloudValue.From(task)];
                        foreach (var (eventKey, evVersions) in desired.Values.Where(p => CloudSnapshot.Kind(p.Key) == "event").ToArray())
                            foreach (var evValue in evVersions.Where(v => !v.Deleted && v.Read<CloudStateEvent>().TaskId == CloudSnapshot.Id(key)))
                            {
                                var ev = evValue.Read<CloudStateEvent>() with { Id = Guid.NewGuid().ToString("N"), TaskId = id };
                                var cloneEventKey = CloudSnapshot.Key(book, "event", ev.Id); updates[cloneEventKey] = desired.Values[cloneEventKey] = [CloudValue.From(ev)];
                            }
                    }
                    else if (kind == "project")
                    {
                        var p = other.Read<ProjectInfo>(); var copy = p with { Id = id, Name = p.Name[..Math.Min(80, p.Name.Length)] + "（副本 " + id[..6] + "）" };
                        var cloneKey = CloudSnapshot.Key(book, kind, id); updates[cloneKey] = desired.Values[cloneKey] = [CloudValue.From(copy)];
                    }
                    else
                    {
                        foreach (var (childKey, values) in desired.Values.Where(p => CloudSnapshot.Book(p.Key) == book).ToArray())
                        {
                            var cloneKey = CloudSnapshot.Key(id, CloudSnapshot.Kind(childKey), CloudSnapshot.Kind(childKey) == "book" ? id : CloudSnapshot.Id(childKey));
                            var payload = CloudSnapshot.Kind(childKey) == "book" ? other : values.FirstOrDefault(v => !v.Deleted) ?? values[0];
                            if (CloudSnapshot.Kind(childKey) == "book") { var meta = payload.Read<CloudBookMeta>(); payload = CloudValue.From(meta with { Name = meta.Name[..Math.Min(48, meta.Name.Length)] + id[..8], Title = meta.Title[..Math.Min(80, meta.Title.Length)] + "（副本）" }); }
                            updates[cloneKey] = desired.Values[cloneKey] = CloudSnapshot.Kind(childKey) == "book" ? [payload] : values.ToList();
                        }
                    }
                }
            }
            if (chosen.Deleted && kind == "book")
                foreach (var childKey in desired.Values.Keys.Where(k => CloudSnapshot.Book(k) == book).ToArray()) updates[childKey] = desired.Values[childKey] = [new(null)];
            if (chosen.Deleted && kind == "task")
                foreach (var (eventKey, evValues) in desired.Values.Where(p => CloudSnapshot.Book(p.Key) == book && CloudSnapshot.Kind(p.Key) == "event").ToArray())
                    if (evValues.Any(v => !v.Deleted && v.Read<CloudStateEvent>().TaskId == CloudSnapshot.Id(key))) updates[eventKey] = desired.Values[eventKey] = [new(null)];
            if (chosen.Deleted && kind == "project")
                foreach (var (taskKey, values) in desired.Values.Where(p => CloudSnapshot.Book(p.Key) == book && CloudSnapshot.Kind(p.Key) == "task").ToArray())
                {
                    var moved = values.Select(v => v.Deleted || v.Read<TodoItem>().ProjectId != CloudSnapshot.Id(key) ? v : MoveToUnassigned(v)).ToList();
                    updates[taskKey] = desired.Values[taskKey] = CloudMerge.Union(taskKey, moved);
                }
            desired.Validate(); CloudMerge.ProtectDependencies(desired, expected);
            if (!await apply(expected, desired, ct)) throw new InvalidOperationException("请先保存当前编辑，再处理冲突。");
            foreach (var (updatedKey, values) in updates) _state.Overrides[updatedKey] = values;
            _state.Unresolved = desired.Conflicts.ToDictionary(p => p.Key, p => p.Value.ToList()); SaveState();
            var imported = _books.ImportVersions();
            foreach (var updatedKey in updates.Keys) imported.Values.Remove(updatedKey);
            _books.SaveImportVersions(imported);
        }
        finally { _gate.Release(); }
    }
    private static CloudValue MoveToUnassigned(CloudValue value)
    {
        var task = value.Read<TodoItem>(); var now = Math.Max(task.UpdatedAt + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return CloudValue.From(task with { ProjectId = null, UpdatedAt = now, EditedAt = now, Revision = task.Revision + 1 });
    }
}
