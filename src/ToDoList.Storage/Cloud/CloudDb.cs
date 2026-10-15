using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ToDoList.Storage.Cloud;

public sealed record CloudDot(string Device, long Counter, CloudValue Value);
public sealed class CloudRegister
{
    public Dictionary<string, long> Context { get; set; } = new(StringComparer.Ordinal);
    public List<CloudDot> Versions { get; set; } = [];
    // A deleted container can still be needed by a concurrent child edit.
    public CloudValue? LastLive { get; set; }
}
public sealed class CloudDocument
{
    public Dictionary<string, CloudRegister> Records { get; set; } = new(StringComparer.Ordinal);
    public CloudDocument Copy() => CloudJson.Parse<CloudDocument>(CloudJson.Serialize(this));
    public CloudSnapshot Snapshot()
    {
        var snapshot = new CloudSnapshot(); var fallbacks = new CloudSnapshot();
        foreach (var (key, row) in Records)
        {
            snapshot.Values[key] = CloudMerge.Union(key, row.Versions.Select(v => v.Value));
            if (row.LastLive != null) fallbacks.Values[key] = [row.LastLive];
        }
        CloudMerge.ProtectDependencies(snapshot, fallbacks);
        snapshot.Validate(); return snapshot;
    }
    public void Set(string key, IReadOnlyList<CloudValue> values, string device, ref long counter)
    {
        if (!Records.TryGetValue(key, out var row)) Records[key] = row = new();
        row.Versions = [];
        foreach (var value in values) row.Versions.Add(new(device, ++counter, value));
        row.Context[device] = counter;
        row.LastLive = values.FirstOrDefault(v => !v.Deleted) ?? row.LastLive;
    }
    public static CloudDocument Merge(CloudDocument left, CloudDocument right)
    {
        var merged = new CloudDocument();
        foreach (var key in left.Records.Keys.Union(right.Records.Keys).Order(StringComparer.Ordinal))
        {
            var a = left.Records.GetValueOrDefault(key) ?? new(); var b = right.Records.GetValueOrDefault(key) ?? new();
            var av = a.Versions.ToDictionary(v => (v.Device, v.Counter)); var bv = b.Versions.ToDictionary(v => (v.Device, v.Counter));
            var row = new CloudRegister();
            foreach (var device in a.Context.Keys.Union(b.Context.Keys)) row.Context[device] = Math.Max(a.Context.GetValueOrDefault(device), b.Context.GetValueOrDefault(device));
            foreach (var dot in av.Keys.Union(bv.Keys).OrderBy(d => d.Device, StringComparer.Ordinal).ThenBy(d => d.Counter))
            {
                var inA = av.TryGetValue(dot, out var x); var inB = bv.TryGetValue(dot, out var y);
                if (inA && inB && x!.Value.Semantic(key) != y!.Value.Semantic(key)) throw new InvalidDataException("同一云版本标识对应不同内容。");
                if (inA && inB || inA && dot.Counter > b.Context.GetValueOrDefault(dot.Device) || inB && dot.Counter > a.Context.GetValueOrDefault(dot.Device))
                    row.Versions.Add(inA && inB ? new(dot.Device, dot.Counter, CloudMerge.Union(key, [x!.Value, y!.Value])[0]) : (x ?? y)!);
            }
            if (row.Versions.Count == 0) throw new InvalidDataException("云端记录缺少有效版本。");
            var live = row.Versions.Select(v => v.Value).Where(v => !v.Deleted).ToList();
            if (live.Count == 0) live = new[] { a.LastLive, b.LastLive }.OfType<CloudValue>().ToList();
            row.LastLive = live.Count == 0 ? null : CloudMerge.Union(key, live)[0];
            // A stale offline copy must not replace the last live value of a later deletion.
            if (!row.Versions.Any(v => !v.Value.Deleted))
            {
                bool Dominates(CloudRegister x, CloudRegister y) => y.Context.All(p => x.Context.GetValueOrDefault(p.Key) >= p.Value);
                if (Dominates(a, b) && !Dominates(b, a)) row.LastLive = a.LastLive;
                else if (Dominates(b, a) && !Dominates(a, b)) row.LastLive = b.LastLive;
            }
            merged.Records[key] = row;
        }
        return merged;
    }
    public void Validate()
    {
        if (Records.Count > 200_000) throw new InvalidDataException("云存档记录数量超限。");
        foreach (var row in Records.Values)
        {
            if (row.Context.Count > 1000 || row.Versions.Count is < 1 or > 64 || row.LastLive?.Deleted == true ||
                row.Context.Any(p => !Guid.TryParseExact(p.Key, "N", out _) || p.Value < 1) ||
                row.Versions.Any(v => v.Counter < 1 || v.Counter > row.Context.GetValueOrDefault(v.Device)) ||
                row.Versions.Select(v => (v.Device, v.Counter)).Distinct().Count() != row.Versions.Count)
                throw new InvalidDataException("云存档因果元数据无效或超限。");
        }
        Snapshot();
    }
}
public sealed record CloudLegacyFile(CloudArea Area, CloudFile File, string Sha256);
public sealed class CloudDbEnvelope
{
    public int Protocol { get; set; } = 2;
    public string AccountId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public long Generation { get; set; }
    public CloudDocument Document { get; set; } = new();
    public List<CloudLegacyFile> Legacy { get; set; } = [];
}
public sealed class CloudDbSyncState
{
    public string ProtocolName { get; set; } = "";
    public CloudDocument Document { get; set; } = new();
    public long Counter { get; set; }
    public long Generation { get; set; }
    public string? PendingName { get; set; }
    public List<string> CleanupPending { get; set; } = [];
    public bool CleanupRequired { get; set; }
}

public static class CloudDbCodec
{
    public static readonly Regex DeviceName = new("\\Adevice-([a-f0-9]{32})-([0-9]{20})-([a-f0-9]{64})\\.db\\z", RegexOptions.CultureInvariant);
    public static readonly Regex ProtocolName = new("\\Aprotocol-v2-([a-f0-9]{64})\\.db\\z", RegexOptions.CultureInvariant);
    public static void AtomicBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static byte[] Encode(CloudDbEnvelope envelope)
    {
        envelope.Document.Validate();
        // Fixed insertion order makes identical migration seeds byte-identical.
        envelope.Document.Records = envelope.Document.Records.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
        foreach (var row in envelope.Document.Records.Values)
        {
            row.Context = row.Context.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
            row.Versions = row.Versions.OrderBy(v => v.Device, StringComparer.Ordinal).ThenBy(v => v.Counter).ToList();
        }
        envelope.Legacy = envelope.Legacy.OrderBy(v => v.Area).ThenBy(v => v.File.Name, StringComparer.Ordinal).ToList();
        var payload = Encoding.UTF8.GetBytes(CloudJson.Serialize(envelope));
        if (payload.Length > 128 * 1024 * 1024) throw new InvalidDataException("云存档解压大小超限。");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true)) gzip.Write(payload);
        var path = Path.Combine(Path.GetTempPath(), "todo-cloud-db-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                c.Open(); using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA user_version=2; CREATE TABLE CloudPayload(Id INTEGER PRIMARY KEY CHECK(Id=1),Data BLOB NOT NULL); INSERT INTO CloudPayload VALUES(1,$data);";
                cmd.Parameters.AddWithValue("$data", compressed.ToArray()); cmd.ExecuteNonQuery();
            }
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > WeiyunFileStore.MaxObjectBytes) throw new InvalidDataException("云 DB 超过 32MiB，本地数据仍保留。");
            return bytes;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    public static async Task<CloudDbEnvelope> DecodeAsync(byte[] bytes, string account, CancellationToken ct = default)
    {
        if (bytes.Length > WeiyunFileStore.MaxObjectBytes || bytes.Length < 100 || !bytes.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8)) throw new InvalidDataException("云文件不是有效的 SQLite DB。");
        var path = Path.Combine(Path.GetTempPath(), "todo-cloud-read-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await File.WriteAllBytesAsync(path, bytes, ct); byte[] data;
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA integrity_check";
                if ((string?)cmd.ExecuteScalar() != "ok") throw new InvalidDataException("云 DB 完整性校验失败。");
                cmd.CommandText = "SELECT type FROM sqlite_schema WHERE name='CloudPayload'";
                if ((string?)cmd.ExecuteScalar() != "table") throw new InvalidDataException("云 DB 表结构无效。");
                cmd.CommandText = "SELECT Data FROM CloudPayload WHERE Id=1"; data = cmd.ExecuteScalar() as byte[] ?? throw new InvalidDataException("云 DB 缺少数据。");
            }
            using var stream = new MemoryStream(data); using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            var plain = await WeiyunFileStore.ReadBoundedAsync(gzip, 128 * 1024 * 1024, ct);
            var envelope = CloudJson.Parse<CloudDbEnvelope>(Encoding.UTF8.GetString(plain));
            if (envelope.Protocol != 2 || envelope.AccountId != account || !Guid.TryParseExact(envelope.DeviceId, "N", out _) || envelope.Generation < 0 || envelope.Legacy.Count > 200_000)
                throw new InvalidDataException("云 DB 协议或账号身份无效。");
            foreach (var source in envelope.Legacy)
                if (!Regex.IsMatch(source.Sha256, "\\A[a-f0-9]{64}\\z") || source.File.Size is < 1 or > WeiyunFileStore.MaxObjectBytes ||
                    !(source.Area == CloudArea.Sync && Regex.IsMatch(source.File.Name, "\\Acommit-[a-f0-9]{32}\\.json\\z") || source.Area == CloudArea.Data && Regex.IsMatch(source.File.Name, "\\A[a-f0-9]{64}\\.snapshot\\.gz\\z")))
                    throw new InvalidDataException("迁移来源清单无效。");
            envelope.Document.Validate(); return envelope;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
