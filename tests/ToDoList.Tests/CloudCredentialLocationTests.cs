using System.Text;
using ToDoList.Storage.Cloud;

namespace ToDoList.Tests;

public sealed class CloudCredentialLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "todo-credential-tests-" + Guid.NewGuid().ToString("N"));
    private CloudSession Session { get; } = new("client", "synthetic-access", "synthetic-refresh", long.MaxValue, "synthetic-space", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private string At(string name) => Path.Combine(_root, name);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [Fact] public void CredentialsProfilesAndSelectionStayInExplicitRootAndMoveWithIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var credentials = new WindowsCloudCredentials(At("Data"), At("Auth"), At("legacy"));
        credentials.Save(Session); credentials.SaveProfile(new(Session.AccountId, "缓存资料")); credentials.Activate(Session.AccountId);
        Assert.Equal(At("Auth"), credentials.DirectoryPath);
        var encrypted = File.ReadAllBytes(Path.Combine(credentials.DirectoryPath, "account-" + Session.AccountId + ".dpapi"));
        Assert.DoesNotContain("synthetic-access", Encoding.UTF8.GetString(encrypted));
        Directory.Move(At("Auth"), At("moved-auth"));
        var moved = new WindowsCloudCredentials(At("other-data"), At("moved-auth"), At("legacy"));
        Assert.Equal(Session, moved.Load(Session.AccountId)); Assert.Equal(Session.AccountId, moved.ActiveAccount);
        Assert.Equal("缓存资料", moved.LoadProfile(Session.AccountId)!.DisplayName);
        Assert.Null(new WindowsCloudCredentials(At("isolated-data")).ActiveAccount);
    }
    [Fact] public void LegacyMigrationValidatesCleansKnownFilesAndNeverResurrectsLogout()
    {
        if (!OperatingSystem.IsWindows()) return;
        var old = new WindowsCloudCredentials(At("Data"), At("legacy"));
        old.Save(Session); old.SaveProfile(new(Session.AccountId, "迁移资料")); old.Activate(Session.AccountId);
        File.WriteAllText(At("legacy/unknown.txt"), "preserved");
        var current = new WindowsCloudCredentials(At("Data"), At("Auth"), At("legacy")); current.MigrateLegacy();
        Assert.Equal(Session, current.Load(Session.AccountId)); Assert.Equal(Session.AccountId, current.ActiveAccount);
        Assert.Equal("迁移资料", current.LoadProfile(Session.AccountId)!.DisplayName);
        Assert.Single(Directory.GetFiles(At("legacy"))); Assert.True(File.Exists(At("legacy/unknown.txt")));
        current.Logout(); old.Activate(Session.AccountId); current.MigrateLegacy();
        Assert.Null(current.ActiveAccount); Assert.Null(current.LegacyActiveAccount);
    }
    [Fact] public void NewSelectionAndTokenWinOverLegacyAndFailedMigrationCanRetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var old = new WindowsCloudCredentials(At("Data"), At("legacy")); old.Save(Session); old.Activate(Session.AccountId);
        Directory.CreateDirectory(_root); File.WriteAllText(At("Auth"), "blocks destination");
        var current = new WindowsCloudCredentials(At("Data"), At("Auth"), At("legacy"));
        Assert.ThrowsAny<IOException>(() => current.MigrateLegacy()); Assert.Equal(Session.AccountId, current.LegacyActiveAccount);
        Assert.NotNull(old.Load(Session.AccountId)); File.Delete(At("Auth"));
        var newer = Session with { AccessToken = "newer-access" };
        var second = Session with { RootKey = "second-space" };
        current.Save(newer); current.Save(second); current.Activate(second.AccountId); current.MigrateLegacy();
        Assert.Equal(newer, current.Load(Session.AccountId)); Assert.Equal(second.AccountId, current.ActiveAccount);
        Assert.False(File.Exists(At("legacy/active.json")));
    }
    [Fact] public void CorruptLegacyCredentialIsPreservedAndLocalSelectionRemainsReadable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var old = new WindowsCloudCredentials(At("Data"), At("legacy")); old.Activate(Session.AccountId);
        var path = At("legacy/account-" + Session.AccountId + ".dpapi"); File.WriteAllBytes(path, [1, 2, 3]);
        var current = new WindowsCloudCredentials(At("Data"), At("Auth"), At("legacy"));
        Assert.ThrowsAny<Exception>(() => current.MigrateLegacy());
        Assert.Equal(Session.AccountId, current.ActiveAccount ?? current.LegacyActiveAccount); Assert.True(File.Exists(path));
    }
}
