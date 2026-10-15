using System.Text;
using System.Text.RegularExpressions;
using ToDoList.Core;

namespace ToDoList.Storage.Cloud;

public sealed partial class SyncCoordinator
{
    private static bool LegacyCommit(CloudFile file) => Regex.IsMatch(file.Name, "\\Acommit-[a-f0-9]{32}\\.json\\z");
    private string DbCache(string name) => Path.Combine(_books.StateDirectory, "db-cache", name);
    private async Task<CloudDbEnvelope> ReadDbAsync(CloudArea area, CloudFile file, CancellationToken ct)
    {
        var match = area == CloudArea.Sync ? CloudDbCodec.ProtocolName.Match(file.Name) : CloudDbCodec.DeviceName.Match(file.Name);
        if (!match.Success || file.Size is < 100 or > WeiyunFileStore.MaxObjectBytes) throw new InvalidDataException("云 DB 文件名或大小无效。");
        var hash = match.Groups[area == CloudArea.Sync ? 1 : 3].Value;
        var cache = DbCache(file.Name);
        var bytes = File.Exists(cache) ? await File.ReadAllBytesAsync(cache, ct) : await _files.ReadAsync(area, file, ct);
        if (bytes.Length != file.Size || CloudJson.Hash(bytes) != hash) throw new InvalidDataException("云 DB 字节校验失败，未修改本地数据。");
        var envelope = await Task.Run(() => CloudDbCodec.DecodeAsync(bytes, _account, ct), ct);
        if (area == CloudArea.Sync ? envelope.Generation != 0 : envelope.DeviceId != match.Groups[1].Value || !long.TryParse(match.Groups[2].Value, out var generation) || envelope.Generation != generation || generation < 1 || envelope.Legacy.Count != 0)
            throw new InvalidDataException("云 DB 文件名与内部身份不一致。");
        if (!File.Exists(cache)) CloudDbCodec.AtomicBytes(cache, bytes);
        return envelope;
    }
    private async Task<CloudDbEnvelope> ProtocolAsync(CancellationToken ct, IProgress<CloudSyncProgress>? progress, IReadOnlyList<CloudFile>? initialFiles = null)
    {
        var syncFiles = initialFiles ?? await _files.ListAsync(CloudArea.Sync, ct);
        var protocols = syncFiles.Where(f => CloudDbCodec.ProtocolName.IsMatch(f.Name)).ToArray();
        if (protocols.Length > 1) throw new InvalidDataException("检测到并发迁移产生的不同协议存档，请先处理，未清理任何数据。");
        if (protocols.Length == 1)
        {
            if (_state.Db != null && _state.Db.ProtocolName != protocols[0].Name) throw new InvalidDataException("云同步协议存档发生变化。");
            var seed = await ReadDbAsync(CloudArea.Sync, protocols[0], ct);
            CheckLegacyWriters(syncFiles, seed);
            return seed;
        }
        if (_state.Db != null) throw new InvalidDataException("云端协议初始化 DB 缺失，已停止同步。");
        // Finish a v1 durable outbox before retiring the v1 protocol.
        await FlushPendingAsync(ct);
        await LoadRemoteAsync(ct, progress);
        var remote = RemoteSnapshot(Heads());
        syncFiles = await _files.ListAsync(CloudArea.Sync, ct);
        var dataFiles = await _files.ListAsync(CloudArea.Data, ct);
        var refs = _commits.Values.Select(v => v.Commit.SnapshotName).ToHashSet();
        var sources = syncFiles.Where(LegacyCommit).Select(f => (Area: CloudArea.Sync, File: f))
            .Concat(dataFiles.Where(f => refs.Contains(f.Name)).Select(f => (Area: CloudArea.Data, File: f)))
            .OrderBy(f => f.Area).ThenBy(f => f.File.Name, StringComparer.Ordinal).ToArray();
        var inventory = CloudJson.Hash(Encoding.UTF8.GetBytes(CloudJson.Serialize(sources.Select(v => new { v.Area, v.File }))));
        var backup = Path.Combine(_books.StateDirectory, "legacy-backup", inventory);
        var legacy = new List<CloudLegacyFile>();
        for (var i = 0; i < sources.Length; i++)
        {
            progress?.Report(new("正在备份旧云存档", i, sources.Length));
            var source = sources[i]; var bytes = await _files.ReadAsync(source.Area, source.File, ct);
            CloudDbCodec.AtomicBytes(Path.Combine(backup, source.Area.ToString(), source.File.Name), bytes);
            legacy.Add(new(source.Area, source.File, CloudJson.Hash(bytes)));
        }
        var after = await _files.ListAsync(CloudArea.Sync, ct);
        if (!SameFiles(syncFiles.Where(LegacyCommit), after.Where(LegacyCommit))) throw new InvalidDataException("迁移期间旧客户端仍在同步，请全部升级后重试。");
        if (after.Any(f => CloudDbCodec.ProtocolName.IsMatch(f.Name))) return await ProtocolAsync(ct, progress);
        var seedId = inventory[..32]; long counter = 0; var document = new CloudDocument();
        foreach (var (key, values) in remote.Values.OrderBy(p => p.Key, StringComparer.Ordinal)) document.Set(key, values, seedId, ref counter);
        var envelope = new CloudDbEnvelope { AccountId = _account, DeviceId = seedId, Document = document, Legacy = legacy };
        var encoded = await Task.Run(() => CloudDbCodec.Encode(envelope), ct);
        var name = "protocol-v2-" + CloudJson.Hash(encoded) + ".db";
        CloudDbCodec.AtomicBytes(DbCache(name), encoded);
        progress?.Report(new("正在发布云 DB 协议"));
        var written = await _files.WriteImmutableAsync(CloudArea.Sync, name, encoded, ct);
        await ReadDbAsync(CloudArea.Sync, written, ct);
        // Re-list to detect concurrent migrators before allowing any cleanup.
        after = await _files.ListAsync(CloudArea.Sync, ct);
        if (after.Count(f => CloudDbCodec.ProtocolName.IsMatch(f.Name)) != 1) throw new InvalidDataException("发生并发迁移，旧存档已保留。");
        CheckLegacyWriters(after, envelope);
        return envelope;
    }
    private static bool SameFiles(IEnumerable<CloudFile> a, IEnumerable<CloudFile> b) =>
        a.OrderBy(f => f.Name, StringComparer.Ordinal).SequenceEqual(b.OrderBy(f => f.Name, StringComparer.Ordinal));
    private static void CheckLegacyWriters(IReadOnlyList<CloudFile> files, CloudDbEnvelope seed)
    {
        var known = seed.Legacy.Where(v => v.Area == CloudArea.Sync).Select(v => v.File).ToHashSet();
        if (files.Where(LegacyCommit).Any(f => !known.Contains(f))) throw new InvalidDataException("旧客户端仍在写入云存档，请全部升级后再同步；未清理新数据。");
    }
    private CloudDocument InitialDocument(CloudDbEnvelope seed, CloudSnapshot expected, ref long counter)
    {
        var overlay = _state.Baseline.Copy(); foreach (var (key, values) in Conflicts) overlay.Values[key] = values;
        var local = CloudMerge.PrepareLocal(expected.Copy(), overlay, EffectiveOverrides());
        var seedSnapshot = seed.Document.Snapshot(); var delta = new CloudDocument();
        foreach (var (key, values) in local.Values)
        {
            var baseline = _state.Baseline.Values.GetValueOrDefault(key);
            if (CloudMerge.Equal(key, values, baseline) || CloudMerge.Equal(key, values, seedSnapshot.Values.GetValueOrDefault(key))) continue;
            // Only dominate the seed if it is exactly the version this old device observed.
            if (CloudMerge.Equal(key, baseline, seedSnapshot.Values.GetValueOrDefault(key)) && seed.Document.Records.TryGetValue(key, out var observed))
                delta.Records[key] = CloudJson.Parse<CloudRegister>(CloudJson.Serialize(observed));
            delta.Set(key, values, _state.DeviceId, ref counter);
        }
        return CloudDocument.Merge(seed.Document, delta);
    }
    private async Task<bool> FlushDbPendingAsync(CancellationToken ct, IProgress<CloudSyncProgress>? progress)
    {
        var db = _state.Db!; if (db.PendingName == null) return false;
        var name = db.PendingName; var path = Path.Combine(_books.StateDirectory, "outbox", name);
        if (!File.Exists(path)) throw new InvalidDataException("待上传云 DB 缺失，本地数据仍保留。");
        var bytes = await File.ReadAllBytesAsync(path, ct); var match = CloudDbCodec.DeviceName.Match(name);
        if (!match.Success || match.Groups[1].Value != _state.DeviceId || CloudJson.Hash(bytes) != match.Groups[3].Value) throw new InvalidDataException("待上传云 DB 校验失败。");
        progress?.Report(new("正在上传云 DB"));
        var file = await _files.WriteImmutableAsync(CloudArea.Data, name, bytes, ct);
        // WriteImmutableAsync guarantees download/byte verification before success.
        _lastUploadedFile = file;
        CloudDbCodec.AtomicBytes(DbCache(name), bytes);
        db.PendingName = null; SaveState(); File.Delete(path); return true;
    }
    public async Task<CloudSyncResult> SyncAsync(SyncDirection direction, Func<CloudSnapshot, CloudSnapshot, CancellationToken, Task<bool>> apply,
        CancellationToken ct = default, IProgress<CloudSyncProgress>? progress = null)
    {
        await _gate.WaitAsync(ct);
        using var operation = RuntimeLog.Begin("sync", new { direction, account = _account[..8] });
        try
        {
            EnsureRequestAllowed(); _lastUploadedFile = null;
            CloudSnapshot checkedLocal;
            byte[] localBytes;
            using (RuntimeLog.Begin("sync.local-check")) { checkedLocal = await _books.CaptureAsync(ct); localBytes = LocalCheckpointBytes(checkedLocal); }
            var localUnchanged = MatchesCheckpoint(localBytes);
            if (direction == SyncDirection.Upload && localUnchanged && !HasComparisonWork)
                return new(false, Conflicts.Count, false, Outcome: CloudSyncOutcome.Unchanged);
            progress?.Report(new("正在连接云空间"));
            var space = await _files.InitializeAsync(ct);
            if (space.Id != _account) throw new InvalidDataException("当前云空间与账号工作区不一致。");
            IReadOnlyList<CloudFile> syncFiles; IReadOnlyList<CloudFile> files;
            using (RuntimeLog.Begin("sync.remote-check"))
            { syncFiles = await _files.ListAsync(CloudArea.Sync, ct); files = await _files.ListAsync(CloudArea.Data, ct); }
            var protocols = syncFiles.Where(f => CloudDbCodec.ProtocolName.IsMatch(f.Name)).ToArray();
            if (protocols.Length > 1) throw new InvalidDataException("云同步协议存档发生冲突。");
            if (_state.Db != null && (protocols.Length != 1 || protocols[0].Name != _state.Db.ProtocolName)) throw new InvalidDataException("云端协议初始化 DB 缺失或发生变化。");
            var inventory = Inventory(syncFiles, files);
            var newest = NewestFiles(files);
            var excess = files.Count(f => CloudDbCodec.DeviceName.IsMatch(f.Name) && CloudDbCodec.DeviceName.Match(f.Name).Groups[1].Value == _state.DeviceId) > 2;
            if (localUnchanged && !HasComparisonWork && !excess && protocols.Length == 1 && inventory == _state.RemoteInventory && !syncFiles.Any(LegacyCommit))
            {
                RuntimeLog.Write("sync.unchanged", new { localHash = CloudJson.Hash(localBytes), inventory });
                ClearExpiredLimit();
                return new(false, Conflicts.Count, false, Outcome: CloudSyncOutcome.Unchanged);
            }
            using var full = RuntimeLog.Begin("sync.full");
            var seed = await ProtocolAsync(ct, progress, syncFiles);
            if (protocols.Length == 0)
            {
                syncFiles = await _files.ListAsync(CloudArea.Sync, ct); files = await _files.ListAsync(CloudArea.Data, ct);
                newest = NewestFiles(files);
            }
            var protocolName = syncFiles.Single(f => CloudDbCodec.ProtocolName.IsMatch(f.Name)).Name;
            var uploaded = _state.Db != null && await FlushDbPendingAsync(ct, progress);
            if (_lastUploadedFile != null) { files = files.Where(f => f.Name != _lastUploadedFile.Name).Append(_lastUploadedFile).ToArray(); newest = NewestFiles(files); }
            var remote = seed.Document.Copy();
            for (var i = 0; i < newest.Length; i++)
            {
                progress?.Report(new("正在下载云存档", i, newest.Length));
                var envelope = await ReadDbAsync(CloudArea.Data, newest[i], ct);
                remote = CloudDocument.Merge(remote, envelope.Document);
            }
            progress?.Report(new("正在合并笔记"));
            var importedAtStart = ImportFingerprint();
            var expected = await _books.CaptureAsync(ct);
            var db = _state.Db == null ? new CloudDbSyncState { ProtocolName = protocolName } : CloudJson.Parse<CloudDbSyncState>(CloudJson.Serialize(_state.Db));
            var counter = db.Counter;
            var local = _state.Db == null ? InitialDocument(seed, expected, ref counter) : db.Document.Copy();
            if (_state.Db != null)
            {
                var overlay = local.Snapshot();
                foreach (var (key, values) in Conflicts) overlay.Values[key] = values;
                var desiredLocal = CloudMerge.PrepareLocal(expected.Copy(), overlay, EffectiveOverrides());
                var existing = local.Snapshot();
                foreach (var (key, values) in desiredLocal.Values)
                    if (!CloudMerge.Equal(key, values, existing.Values.GetValueOrDefault(key))) local.Set(key, values, _state.DeviceId, ref counter);
            }
            var merged = CloudDocument.Merge(local, remote); var desired = merged.Snapshot();
            var synchronized = CloudBookStorage.Select(expected, desired);
            if (!await apply(expected, desired, ct)) return new(true, Conflicts.Count, uploaded, Outcome: CloudSyncOutcome.Deferred);
            db.Document = merged; db.Counter = counter; _state.Db = db; _state.Pending = null;
            _state.Unresolved = desired.Conflicts.ToDictionary(p => p.Key, p => p.Value.ToList());
            foreach (var key in _state.Overrides.Keys.ToArray())
                if (CloudMerge.Equal(key, _state.Overrides[key], desired.Values.GetValueOrDefault(key))) _state.Overrides.Remove(key);
            _state.Baseline = desired;
            _state.NeedsUpload = !SameDocument(merged, remote);
            if (direction != SyncDirection.Download && !SameDocument(merged, remote))
            {
                var own = newest.FirstOrDefault(f => CloudDbCodec.DeviceName.Match(f.Name).Groups[1].Value == _state.DeviceId);
                if (own != null && long.Parse(CloudDbCodec.DeviceName.Match(own.Name).Groups[2].Value) > db.Generation)
                    throw new InvalidDataException("另一工作区副本正在使用相同设备身份，请使用独立设备工作区。");
                db.Generation = checked(db.Generation + 1);
                var bytes = await Task.Run(() => CloudDbCodec.Encode(new() { AccountId = _account, DeviceId = _state.DeviceId, Generation = db.Generation, Document = merged }), ct);
                db.PendingName = "device-" + _state.DeviceId + "-" + db.Generation.ToString("D20") + "-" + CloudJson.Hash(bytes) + ".db";
                CloudDbCodec.AtomicBytes(Path.Combine(_books.StateDirectory, "outbox", db.PendingName), bytes);
                SaveState(); uploaded |= await FlushDbPendingAsync(ct, progress);
                _state.NeedsUpload = false;
            }
            _state.LastSyncedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); SaveState();
            progress?.Report(new("正在检查云存档数量"));
            var warning = await CleanupAsync(seed, ct);
            // Only acknowledge versions consumed above or uploaded by this operation.
            // A concurrent remote write discovered during cleanup must remain unseen.
            if (_lastUploadedFile != null) files = files.Where(f => f.Name != _lastUploadedFile.Name).Append(_lastUploadedFile).ToArray();
            var knownRemoved = seed.Legacy.Select(v => v.File.Name).ToHashSet();
            if (warning == null) syncFiles = syncFiles.Where(f => !knownRemoved.Contains(f.Name)).ToArray();
            _state.ImportFingerprint = importedAtStart;
            SaveCheckpoint(synchronized, Inventory(syncFiles, files));
            ClearExpiredLimit();
            progress?.Report(new("云内容已加载", 1, 1));
            return new(false, _state.Unresolved.Count, uploaded, warning);
        }
        catch (CloudTransportException ex) { RememberLimit(ex); RuntimeLog.Write("sync.failed", new { ex.Status, ex.Kind, ex.Code, ex.RetryAt }, ex); throw; }
        catch (Exception ex) { RuntimeLog.Write("sync.failed", error: ex); throw; }
        finally { _gate.Release(); }
    }
    private CloudFile? _lastUploadedFile;
    private static bool SameDocument(CloudDocument a, CloudDocument b)
    {
        // Semantic equality alone must not discard causal conflict resolutions.
        return a.Records.Count == b.Records.Count && a.Records.All(p => b.Records.TryGetValue(p.Key, out var row) &&
            p.Value.Context.Count == row.Context.Count && p.Value.Context.All(v => row.Context.GetValueOrDefault(v.Key) == v.Value) &&
            p.Value.Versions.Count == row.Versions.Count && p.Value.Versions.All(v => row.Versions.Any(w => w.Device == v.Device && w.Counter == v.Counter && w.Value.Semantic(p.Key) == v.Value.Semantic(p.Key))));
    }
    private async Task<string?> CleanupAsync(CloudDbEnvelope seed, CancellationToken ct)
    {
        var db = _state.Db!;
        try
        {
            db.CleanupRequired = true; SaveState();
            var sync = await _files.ListAsync(CloudArea.Sync, ct); CheckLegacyWriters(sync, seed);
            if (sync.Count(f => CloudDbCodec.ProtocolName.IsMatch(f.Name)) != 1) throw new InvalidDataException("云协议存档发生冲突，未清理旧文件。");
            var data = await _files.ListAsync(CloudArea.Data, ct);
            var own = data.Where(f => CloudDbCodec.DeviceName.IsMatch(f.Name) && CloudDbCodec.DeviceName.Match(f.Name).Groups[1].Value == _state.DeviceId)
                .OrderByDescending(f => f.Name, StringComparer.Ordinal).ToArray();
            foreach (var keep in own.Take(2)) await ReadDbAsync(CloudArea.Data, keep, ct);
            var removals = new List<(CloudArea Area, CloudFile File)>();
            removals.AddRange(own.Skip(2).Select(f => (CloudArea.Data, f)));
            foreach (var source in seed.Legacy)
            {
                var listing = source.Area == CloudArea.Sync ? sync : data;
                var file = listing.SingleOrDefault(f => f.Id == source.File.Id && f.Name == source.File.Name && f.Size == source.File.Size);
                if (file == null) continue;
                var bytes = await _files.ReadAsync(source.Area, file, ct);
                if (CloudJson.Hash(bytes) != source.Sha256) throw new InvalidDataException("旧迁移来源已变化，未回收文件。");
                CloudDbCodec.AtomicBytes(Path.Combine(_books.StateDirectory, "legacy-backup", db.ProtocolName, source.Area.ToString(), file.Name), bytes);
                removals.Add((source.Area, file));
            }
            db.CleanupPending = removals.Select(v => v.Area + "/" + v.File.Name).ToList(); SaveState();
            if (removals.Count == 0) { db.CleanupRequired = false; SaveState(); return null; }
            // Re-check writers immediately before deleting only exact inventoried IDs.
            CheckLegacyWriters(await _files.ListAsync(CloudArea.Sync, ct), seed);
            foreach (var batch in removals.GroupBy(v => v.Area)) await _files.RecycleAsync(batch.Key, batch.Select(v => v.File).ToArray(), ct);
            db.CleanupPending = []; db.CleanupRequired = false; SaveState(); return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        { if (ex is CloudTransportException transport) RememberLimit(transport); RuntimeLog.Write("sync.cleanup.failed", error: ex); return "数据已同步；云文件清理待重试：" + ex.Message; }
    }
}
