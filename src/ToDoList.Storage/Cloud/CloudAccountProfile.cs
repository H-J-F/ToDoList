using System.Text.Json;

namespace ToDoList.Storage.Cloud;

// Display metadata is independent of OAuth tokens and never identifies a workspace.
public sealed record CloudAccountProfile(string AccountId, string? DisplayName, byte[]? AvatarPng = null)
{
    public const int MaxAvatarBytes = 2 * 1024 * 1024;
    public void Validate(string accountId)
    {
        if (AccountId != accountId || DisplayName?.Length > 128 || DisplayName?.Any(char.IsControl) == true ||
            AvatarPng?.Length > MaxAvatarBytes) throw new InvalidDataException("账号显示资料无效。");
    }

    public static (CloudAccountProfile Profile, Uri? Avatar)? ReadVerified(JsonElement info, CloudSession session)
    {
        if (info.ValueKind != JsonValueKind.Object || session.RootKey == null) return null;
        string? Read(params string[] names)
        {
            foreach (var name in names)
                if (info.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString();
            return null;
        }
        var main = Read("main_dir_key", "mainDirKey"); var root = Read("root_dir_key", "rootDirKey");
        if (session.RootKey != main && session.RootKey != root) return null;
        var name = Read("nick_name", "nickname")?.Trim();
        if (name?.Length > 128 || name?.Any(char.IsControl) == true) name = null;
        var avatar = Read("https_head_img_url", "avatar");
        Uri? uri = null;
        if (Uri.TryCreate(avatar, UriKind.Absolute, out var candidate) && IsAvatarUri(candidate)) uri = candidate;
        return (new(session.AccountId, name), uri);
    }

    public static bool IsAvatarUri(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        new[] { "qlogo.cn", "qpic.cn", "gtimg.com", "weiyun.com" }.Any(host =>
            uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.IdnHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
}
