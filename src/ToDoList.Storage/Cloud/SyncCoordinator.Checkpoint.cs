using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ToDoList.Core;

namespace ToDoList.Storage.Cloud;

public sealed partial class SyncCoordinator
{
    private string ImportFingerprint() => CloudJson.Hash(Encoding.UTF8.GetBytes(CloudJson.Serialize(_books.ImportVersions())));
    private bool HasComparisonWork => HasPending || _state.NeedsUpload || _state.Db?.CleanupPending.Count > 0 || _state.Db?.CleanupRequired == true || ImportFingerprint() != _state.ImportFingerprint;
    private void ClearExpiredLimit()
    {
        var limit = ReadLimit();
        if (limit != null && limit.Kind != CloudFailureKind.Disabled && limit.Until <= DateTimeOffset.UtcNow) File.Delete(LimitPath);
    }
    private string CheckpointPath(string hash) => Path.Combine(_books.StateDirectory, "checkpoints", "local-" + hash + ".json");
    public static byte[] LocalCheckpointBytes(CloudSnapshot snapshot)
    {
        // Compare logical records, never the mutable SQLite/WAL file layout.
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, versions) in snapshot.Values)
        {
            var book = CloudSnapshot.Book(key);
            if (CloudSnapshot.Kind(key) != "book" && snapshot.Values.GetValueOrDefault(CloudSnapshot.Key(book, "book", book))?.All(v => v.Deleted) == true) continue;
            var value = versions.Single();
            if (value.Deleted) { values[key] = "deleted"; continue; }
            var node = JsonNode.Parse(value.Json!)!;
            if (CloudSnapshot.Kind(key) == "task")
            {
                node.AsObject().Remove("revision"); node.AsObject().Remove("updatedAt");
                node["contentJson"] = RichContent.Parse(node["contentJson"]!.GetValue<string>()).ToJson();
            }
            values[key] = CloudJson.Serialize(node);
        }
        return Encoding.UTF8.GetBytes(CloudJson.Serialize(values));
    }
    private bool MatchesCheckpoint(byte[] bytes)
    {
        var hash = _state.LocalCheckpoint;
        if (hash == null || !Regex.IsMatch(hash, "\\A[a-f0-9]{64}\\z")) return false;
        try
        {
            var previous = File.ReadAllBytes(CheckpointPath(hash));
            return CloudJson.Hash(previous) == hash && previous.SequenceEqual(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RuntimeLog.Write("sync.checkpoint.invalid", error: ex); return false; }
    }
    private void SaveCheckpoint(CloudSnapshot synchronized, string inventory)
    {
        var bytes = LocalCheckpointBytes(synchronized); var hash = CloudJson.Hash(bytes);
        var old = _state.LocalCheckpoint;
        CloudDbCodec.AtomicBytes(CheckpointPath(hash), bytes);
        _state.LocalCheckpoint = hash; _state.RemoteInventory = inventory; SaveState();
        if (old != null && old != hash && Regex.IsMatch(old, "\\A[a-f0-9]{64}\\z"))
            try { File.Delete(CheckpointPath(old)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static CloudFile[] NewestFiles(IReadOnlyList<CloudFile> files)
    {
        var devices = files.Where(f => CloudDbCodec.DeviceName.IsMatch(f.Name)).ToArray();
        if (devices.GroupBy(f => f.Name).Any(g => g.Count() != 1)) throw new InvalidDataException("云 DB 存在同名文件冲突。");
        if (devices.GroupBy(f => { var m = CloudDbCodec.DeviceName.Match(f.Name); return (m.Groups[1].Value, m.Groups[2].Value); }).Any(g => g.Count() > 1))
            throw new InvalidDataException("相同设备身份的同一代次存在多个云 DB，请先处理工作区副本冲突；未选择或清理任何版本。");
        return devices.GroupBy(f => CloudDbCodec.DeviceName.Match(f.Name).Groups[1].Value)
            .Select(g => g.OrderByDescending(f => f.Name, StringComparer.Ordinal).First()).OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
    }
    private static string Inventory(IReadOnlyList<CloudFile> sync, IReadOnlyList<CloudFile> data) => CloudJson.Hash(Encoding.UTF8.GetBytes(CloudJson.Serialize(new
    {
        protocol = sync.Where(f => CloudDbCodec.ProtocolName.IsMatch(f.Name) || LegacyCommit(f)).OrderBy(f => f.Name, StringComparer.Ordinal).ToArray(),
        devices = NewestFiles(data)
    })));
    private string LimitPath => Path.Combine(_books.StateDirectory, "request-limit.json");
    private sealed record RequestLimit(CloudFailureKind Kind, DateTimeOffset? Until, int Failures, string Message, int? Code);
    public DateTimeOffset? RetryAt => ReadLimit()?.Until;
    private RequestLimit? ReadLimit() => File.Exists(LimitPath) ? CloudJson.Parse<RequestLimit>(File.ReadAllText(LimitPath)) : null;
    private void EnsureRequestAllowed()
    {
        var limit = ReadLimit();
        if (limit != null && (limit.Kind == CloudFailureKind.Disabled || limit.Until > DateTimeOffset.UtcNow))
            throw new CloudTransportException(limit.Message, kind: limit.Kind, code: limit.Code, retryAt: limit.Until);
    }
    private void RememberLimit(CloudTransportException error)
    {
        if (error.Kind is not (CloudFailureKind.RateLimit or CloudFailureKind.DailyQuota or CloudFailureKind.Disabled)) return;
        var old = ReadLimit(); var failures = (old?.Failures ?? 0) + 1;
        if (old != null && old.Kind == error.Kind && (old.Kind == CloudFailureKind.Disabled || old.Until > DateTimeOffset.UtcNow)) { error.RetryAt = old.Until; return; }
        var now = DateTimeOffset.UtcNow;
        var until = error.RetryAt;
        if (error.Kind == CloudFailureKind.RateLimit && until == null)
            until = now.AddSeconds(Math.Min(900, 60 * Math.Pow(2, Math.Min(4, failures - 1))) + Random.Shared.Next(1, 11));
        if (error.Kind == CloudFailureKind.DailyQuota && until == null)
        {
            var china = now.ToOffset(TimeSpan.FromHours(8));
            until = new DateTimeOffset(china.Date.AddDays(1), TimeSpan.FromHours(8)).AddSeconds(Random.Shared.Next(5, 31));
        }
        error.RetryAt = until;
        CloudBookStorage.AtomicWrite(LimitPath, CloudJson.Serialize(new RequestLimit(error.Kind, until, failures, error.Message, error.Code)));
    }
    public void AuthorizationRenewed()
    {
        if (ReadLimit()?.Kind == CloudFailureKind.Disabled) File.Delete(LimitPath);
    }
}
