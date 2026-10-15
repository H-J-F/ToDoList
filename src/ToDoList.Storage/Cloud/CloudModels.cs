using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ToDoList.Core;

namespace ToDoList.Storage.Cloud;

public static class CloudJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 64 };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("云存档内容不完整。");
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
public sealed record CloudBookMeta(string Name, string Title);
public sealed record CloudStateEvent(string Id, string TaskId, TodoStatus FromStatus, TodoStatus ToStatus, long OccurredAt);
public sealed record CloudValue(string? Json)
{
    public bool Deleted => Json == null;
    public static CloudValue From<T>(T value) => new(CloudJson.Serialize(value));
    public T Read<T>() => CloudJson.Parse<T>(Json ?? throw new InvalidDataException("该版本已删除。"));
    public string Semantic(string key)
    {
        if (Deleted) return "deleted";
        var node = JsonNode.Parse(Json!) ?? throw new InvalidDataException("云存档内容为空。");
        if (CloudSnapshot.Kind(key) == "task")
        {
            node.AsObject().Remove("revision"); node.AsObject().Remove("updatedAt"); node.AsObject().Remove("editedAt");
            node["contentJson"] = RichContent.Parse(node["contentJson"]!.GetValue<string>()).ToJson();
        }
        return node.ToJsonString(CloudJson.Options);
    }
}
public sealed class CloudSnapshot
{
    public int Format { get; set; } = 1;
    public SortedDictionary<string, List<CloudValue>> Values { get; set; } = new(StringComparer.Ordinal);
    public static string Key(string book, string kind, string id) => book + "/" + kind + "/" + Uri.EscapeDataString(id);
    public static string Book(string key) => key.Split('/')[0];
    public static string Kind(string key) => key.Split('/')[1];
    public static string Id(string key) => Uri.UnescapeDataString(key.Split('/')[2]);
    public IEnumerable<KeyValuePair<string, List<CloudValue>>> Conflicts => Values.Where(p => p.Value.Count > 1);
    public CloudSnapshot Copy() => new() { Values = new(Values.ToDictionary(p => p.Key, p => p.Value.ToList()), StringComparer.Ordinal) };
    public void Set<T>(string book, string kind, string id, T value) => Values[Key(book, kind, id)] = [CloudValue.From(value)];
    public void Delete(string book, string kind, string id) => Values[Key(book, kind, id)] = [new(null)];
    public void Validate()
    {
        if (Format != 1 || Values == null || Values.Count > 200000) throw new InvalidDataException("不支持的云存档格式或存档过大。");
        Values = new(Values, StringComparer.Ordinal);
        foreach (var (key, versions) in Values)
        {
            var parts = key.Split('/');
            if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out _) || versions == null || versions.Count is < 1 or > 64)
                throw new InvalidDataException("云存档标识或版本列表无效。");
            var id = Id(key); var kind = parts[1];
            if (kind is not ("book" or "task" or "project" or "event" or "setting")) throw new InvalidDataException("未知云存档记录类型。");
            if (kind != "setting" && !Guid.TryParse(id, out _)) throw new InvalidDataException("云存档记录标识无效。");
            if (kind == "book" && id != parts[0]) throw new InvalidDataException("笔记标识不一致。");
            if (kind == "setting" && (id.Length is < 1 or > 128)) throw new InvalidDataException("笔记设置标识无效。");
            foreach (var value in versions)
            {
                if (value == null) throw new InvalidDataException("云存档版本为空。");
                if (value.Deleted) continue;
                switch (kind)
                {
                    case "book": var meta = value.Read<CloudBookMeta>(); BookRules.ValidateName(meta.Name); BookRules.ValidateTitle(meta.Title); break;
                    case "project":
                        var project = value.Read<ProjectInfo>();
                        if (project.Id != id || project.SortOrder < 0) throw new InvalidDataException("云端模块标识无效。");
                        BookRules.ValidateTitle(project.Name); break;
                    case "task":
                        var task = value.Read<TodoItem>();
                        if (task.Id != id || task.Revision < 1 || !Enum.IsDefined(task.Status) ||
                            task.CreatedAt > task.UpdatedAt || task.EditedAt < task.CreatedAt || task.EditedAt > task.UpdatedAt ||
                            (task.Status == TodoStatus.Completed) != task.CompletedAt.HasValue ||
                            (task.Status == TodoStatus.Deleted) != task.DeletedAt.HasValue ||
                            task.ProjectId != null && !Guid.TryParse(task.ProjectId, out _)) throw new InvalidDataException("云端任务状态或标识无效。");
                        if (task.Status == TodoStatus.Deleted && (task.PreviousStatus is not (TodoStatus.Open or TodoStatus.Verification or TodoStatus.Completed) ||
                            (task.PreviousStatus == TodoStatus.Completed) != task.PreviousCompletedAt.HasValue) ||
                            task.Status != TodoStatus.Deleted && (task.PreviousStatus.HasValue || task.PreviousCompletedAt.HasValue)) throw new InvalidDataException("云端任务恢复信息无效。");
                        if (RichContent.Parse(task.ContentJson).PlainText != task.PlainText || string.IsNullOrWhiteSpace(task.PlainText)) throw new InvalidDataException("云端任务内容不一致。");
                        _ = DateTimeOffset.FromUnixTimeMilliseconds(task.CreatedAt); _ = DateTimeOffset.FromUnixTimeMilliseconds(task.UpdatedAt);
                        break;
                    case "event":
                        var ev = value.Read<CloudStateEvent>();
                        if (ev.Id != id || !Guid.TryParse(ev.TaskId, out _) || !Enum.IsDefined(ev.FromStatus) || !Enum.IsDefined(ev.ToStatus)) throw new InvalidDataException("云端状态历史无效。");
                        _ = DateTimeOffset.FromUnixTimeMilliseconds(ev.OccurredAt); break;
                    case "setting": _ = value.Read<string>(); break;
                }
            }
        }
        foreach (var (key, versions) in Values)
        {
            bool Alive(string parent) => Values.TryGetValue(parent, out var values) && values.Any(v => !v.Deleted);
            var book = Book(key);
            if (Kind(key) != "book" && versions.Any(v => !v.Deleted) && !Alive(Key(book, "book", book))) throw new InvalidDataException("云存档笔记关联不完整。");
            foreach (var value in versions.Where(v => !v.Deleted))
            {
                if (Kind(key) == "task" && value.Read<TodoItem>().ProjectId is { } project && !Alive(Key(book, "project", project))) throw new InvalidDataException("云存档模块关联不完整。");
                if (Kind(key) == "event" && !Alive(Key(book, "task", value.Read<CloudStateEvent>().TaskId))) throw new InvalidDataException("云存档状态历史关联不完整。");
            }
        }
    }
}

public static class CloudMerge
{
    public static bool Equal(string key, IReadOnlyList<CloudValue>? left, IReadOnlyList<CloudValue>? right) =>
        (left ?? []).Select(v => v.Semantic(key)).ToHashSet(StringComparer.Ordinal).SetEquals((right ?? []).Select(v => v.Semantic(key)));
    public static List<CloudValue> Union(string key, IEnumerable<CloudValue> values) => values
        .GroupBy(v => v.Semantic(key), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => g.OrderByDescending(v => v.Deleted ? 0 : CloudSnapshot.Kind(key) == "task" ? v.Read<TodoItem>().UpdatedAt : 0)
            .ThenBy(v => v.Json, StringComparer.Ordinal).First()).ToList();
    public static CloudSnapshot Merge(CloudSnapshot baseline, CloudSnapshot local, CloudSnapshot remote)
    {
        var merged = new CloudSnapshot();
        foreach (var key in baseline.Values.Keys.Concat(local.Values.Keys).Concat(remote.Values.Keys).Distinct(StringComparer.Ordinal))
        {
            baseline.Values.TryGetValue(key, out var b); local.Values.TryGetValue(key, out var l); remote.Values.TryGetValue(key, out var r);
            var result = Equal(key, l, r) ? Union(key, (l ?? []).Concat(r ?? [])) : Equal(key, l, b) ? r : Equal(key, r, b) ? l : Union(key, (l ?? []).Concat(r ?? []));
            if (result is { Count: > 0 }) merged.Values[key] = result.ToList();
        }
        ProtectDependencies(merged, baseline, local, remote);
        return merged;
    }
    public static void ProtectDependencies(CloudSnapshot snapshot, params CloudSnapshot[] sources)
    {
        var pending = new Queue<string>(snapshot.Values.Keys);
        void Require(string key)
        {
            if (snapshot.Values.TryGetValue(key, out var present) && present.Any(v => !v.Deleted)) return;
            var previous = sources.SelectMany(s => s.Values.GetValueOrDefault(key) ?? []).Where(v => !v.Deleted).ToList();
            if (previous.Count == 0) throw new InvalidDataException("云端存档的笔记或模块关联不完整。");
            snapshot.Values[key] = Union(key, (present ?? []).Concat(previous));
            pending.Enqueue(key);
        }
        while (pending.TryDequeue(out var key))
        {
            var values = snapshot.Values[key];
            var book = CloudSnapshot.Book(key);
            if (CloudSnapshot.Kind(key) != "book" && values.Any(v => !v.Deleted)) Require(CloudSnapshot.Key(book, "book", book));
            if (CloudSnapshot.Kind(key) == "task")
                foreach (var value in values.Where(v => !v.Deleted))
                {
                    var task = value.Read<TodoItem>();
                    if (task.ProjectId != null) Require(CloudSnapshot.Key(book, "project", task.ProjectId));
                }
            if (CloudSnapshot.Kind(key) == "event")
                foreach (var value in values.Where(v => !v.Deleted)) Require(CloudSnapshot.Key(book, "task", value.Read<CloudStateEvent>().TaskId));
        }
    }
    public static CloudSnapshot PrepareLocal(CloudSnapshot current, CloudSnapshot baseline, Dictionary<string, List<CloudValue>> overrides)
    {
        foreach (var (key, values) in baseline.Values)
        {
            if (!current.Values.ContainsKey(key) && current.Values.GetValueOrDefault(CloudSnapshot.Key(CloudSnapshot.Book(key), "book", CloudSnapshot.Book(key)))?.All(v => v.Deleted) == true)
                current.Values[key] = [new(null)];
            else if (values.Count > 1 && current.Values.TryGetValue(key, out var local)) current.Values[key] = Union(key, values.Concat(local));
        }
        foreach (var (key, values) in overrides) current.Values[key] = values.ToList();
        return current;
    }
    public static string Describe(string key, CloudValue value)
    {
        if (value.Deleted) return "此版本已删除";
        return CloudSnapshot.Kind(key) switch
        {
            "task" => value.Read<TodoItem>().PlainText + "\n状态：" + (value.Read<TodoItem>().Status switch
            { TodoStatus.Open => "未完成", TodoStatus.Verification => "待验证", TodoStatus.Completed => "已完成", _ => "回收站" }),
            "book" => "笔记：" + value.Read<CloudBookMeta>().Title,
            "project" => "模块：" + value.Read<ProjectInfo>().Name,
            "setting" => "笔记设置：" + value.Read<string>(),
            _ => "任务状态历史"
        };
    }
}
public enum CloudArea { Data, Sync }
public enum SyncDirection { Both, Upload, Download }
public sealed record CloudFile(string Id, string Name, long Size);
public sealed record CloudSpace(string Id, string RootKey, string DataKey, string SyncKey);
public interface ICloudFileStore
{
    Task<CloudSpace> InitializeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CloudFile>> ListAsync(CloudArea area, CancellationToken ct = default);
    Task<byte[]> ReadAsync(CloudArea area, CloudFile file, CancellationToken ct = default);
    /// <summary>Returns only after the immutable remote object has been downloaded and verified against bytes.</summary>
    Task<CloudFile> WriteImmutableAsync(CloudArea area, string name, byte[] bytes, CancellationToken ct = default);
    Task RecycleAsync(CloudArea area, IReadOnlyList<CloudFile> files, CancellationToken ct = default) =>
        throw new NotSupportedException("云文件服务不支持回收文件。");
}
public sealed record CloudSyncProgress(string Stage, int Completed = 0, int? Total = null);
public sealed record CloudCommit(int Format, string AccountId, string Id, string DeviceId, List<string> Parents, string SnapshotName, string Sha256);
