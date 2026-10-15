using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ToDoList.Storage.Cloud;

public sealed class WindowsCloudCredentials
{
    private readonly string _directory;
    private readonly string _legacyDirectory;
    public string DirectoryPath => _directory;
    private string ActivePath => Path.Combine(_directory, "active.json");
    public WindowsCloudCredentials(string guestDataDirectory, string? directory = null, string? legacyDirectory = null)
    {
        var context = CloudJson.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(guestDataDirectory).ToUpperInvariant()));
        _directory = Path.GetFullPath(directory ?? Path.Combine(guestDataDirectory, "Auth"));
        _legacyDirectory = legacyDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToDoList", "Cloud", context);
    }
    public string? LegacyActiveAccount => File.Exists(Path.Combine(_directory, "legacy-migrated.json")) ? null : ReadActive(Path.Combine(_legacyDirectory, "active.json"));
    private static string? ReadActive(string path)
    {
        if (!File.Exists(path)) return null;
        var id = CloudJson.Parse<string>(File.ReadAllText(path)); ValidateId(id); return id;
    }
    // Only the matching installation's known files are migrated. The journal also
    // prevents an old active.json from resurrecting a logged-out account.
    public void MigrateLegacy()
    {
        var journal = Path.Combine(_directory, "legacy-migrated.json");
        Dictionary<string, string> migrated;
        if (File.Exists(journal)) migrated = CloudJson.Parse<Dictionary<string, string>>(File.ReadAllText(journal));
        else
        {
            if (!Directory.Exists(_legacyDirectory)) return;
            migrated = [];
            foreach (var source in Directory.EnumerateFiles(_legacyDirectory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(source);
                if (!IsCredentialFile(name)) continue;
                var bytes = File.ReadAllBytes(source); ValidateFile(name, bytes);
                var target = Path.Combine(_directory, name);
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(_directory);
                    var temp = target + ".migration-" + Guid.NewGuid().ToString("N");
                    try { File.WriteAllBytes(temp, bytes); File.Move(temp, target); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                ValidateFile(name, File.ReadAllBytes(target));
                migrated.Add(name, CloudJson.Hash(bytes));
            }
            CloudBookStorage.AtomicWrite(journal, CloudJson.Serialize(migrated));
        }
        foreach (var (name, hash) in migrated)
        {
            if (!IsCredentialFile(name)) throw new InvalidDataException("凭证迁移记录无效。");
            var source = Path.Combine(_legacyDirectory, name);
            if (File.Exists(source) && CloudJson.Hash(File.ReadAllBytes(source)) == hash) File.Delete(source);
        }
        if (Directory.Exists(_legacyDirectory) && !Directory.EnumerateFileSystemEntries(_legacyDirectory).Any()) Directory.Delete(_legacyDirectory);
    }
    private static bool IsCredentialFile(string name) => name == "active.json" || Regex.IsMatch(name, "\\A(account|profile)-[a-f0-9]{64}\\.dpapi\\z");
    private static void ValidateFile(string name, byte[] bytes)
    {
        if (name == "active.json") { ValidateId(CloudJson.Parse<string>(Encoding.UTF8.GetString(bytes))); return; }
        var id = name[(name.IndexOf('-') + 1)..^6];
        var plain = Unprotect(bytes);
        try
        {
            if (name.StartsWith("profile-", StringComparison.Ordinal)) CloudJson.Parse<CloudAccountProfile>(Encoding.UTF8.GetString(plain)).Validate(id);
            else if (CloudJson.Parse<CloudSession>(Encoding.UTF8.GetString(plain)).AccountId != id) throw new InvalidDataException("凭证与账号工作区不一致。");
        }
        finally { Array.Clear(plain); }
    }
    private static void ValidateId(string id) { if (!Regex.IsMatch(id, "\\A[a-f0-9]{64}\\z")) throw new InvalidDataException("账号工作区标识无效。"); }
    public string? ActiveAccount
    {
        get
        {
            return ReadActive(ActivePath);
        }
    }
    public void Activate(string id) { ValidateId(id); CloudBookStorage.AtomicWrite(ActivePath, CloudJson.Serialize(id)); }
    public void Logout() { if (File.Exists(ActivePath)) File.Delete(ActivePath); }
    public CloudSession? Load(string id)
    {
        ValidateId(id); var path = Path.Combine(_directory, "account-" + id + ".dpapi");
        if (!File.Exists(path)) return null;
        var plain = Unprotect(File.ReadAllBytes(path));
        try
        {
            var session = CloudJson.Parse<CloudSession>(Encoding.UTF8.GetString(plain));
            if (session.AccountId != id) throw new InvalidDataException("凭证与账号工作区不一致。");
            if (session.AuthorizedAt == null) { session = session with { AuthorizedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }; Save(session); }
            return session;
        }
        finally { Array.Clear(plain); }
    }
    public void Save(CloudSession session)
    {
        var id = session.AccountId; ValidateId(id); Directory.CreateDirectory(_directory);
        var plain = Encoding.UTF8.GetBytes(CloudJson.Serialize(session));
        try
        {
            var temp = Path.Combine(_directory, "account-" + id + ".dpapi.tmp"); File.WriteAllBytes(temp, Protect(plain));
            File.Move(temp, Path.Combine(_directory, "account-" + id + ".dpapi"), true);
        }
        finally { Array.Clear(plain); }
    }
    public CloudAccountProfile? LoadProfile(string id)
    {
        ValidateId(id); var path = Path.Combine(_directory, "profile-" + id + ".dpapi");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 3 * 1024 * 1024) throw new InvalidDataException("账号资料缓存过大。");
        var plain = Unprotect(File.ReadAllBytes(path));
        try { var profile = CloudJson.Parse<CloudAccountProfile>(Encoding.UTF8.GetString(plain)); profile.Validate(id); return profile; }
        finally { Array.Clear(plain); }
    }
    public void SaveProfile(CloudAccountProfile profile)
    {
        ValidateId(profile.AccountId); profile.Validate(profile.AccountId); Directory.CreateDirectory(_directory);
        var plain = Encoding.UTF8.GetBytes(CloudJson.Serialize(profile));
        try
        {
            var path = Path.Combine(_directory, "profile-" + profile.AccountId + ".dpapi");
            var temp = path + ".tmp"; File.WriteAllBytes(temp, Protect(plain)); File.Move(temp, path, true);
        }
        finally { Array.Clear(plain); }
    }
    public static string Workspace(string guestDataDirectory, string id) { ValidateId(id); return Path.Combine(guestDataDirectory, "Accounts", id); }
    public static byte[] Protect(byte[] bytes) => Transform(bytes, true);
    public static byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("微云凭证保护需要 Windows。");
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保护或读取当前 Windows 用户的微云凭证。");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero) { Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data); }
            if (output.Data != IntPtr.Zero) { if (!protect) Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length); LocalFree(output.Data); }
        }
    }
}
