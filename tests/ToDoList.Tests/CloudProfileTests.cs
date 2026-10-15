using System.Text.Json;
using ToDoList.Storage.Cloud;

namespace ToDoList.Tests;

public sealed class CloudProfileTests
{
    private static readonly CloudSession Session = new("client", "access", "refresh", long.MaxValue, "space-a");
    [Theory]
    [InlineData("main_dir_key", "nick_name", "https_head_img_url")]
    [InlineData("mainDirKey", "nickname", "avatar")]
    [InlineData("root_dir_key", "nick_name", "https_head_img_url")]
    public void OfficialProfileRequiresVerifiedSpace(string key, string name, string avatar)
    {
        var data = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [key] = "space-a", [name] = "测试昵称", [avatar] = "https://thirdwx.qlogo.cn/mmopen/avatar" });
        var result = CloudAccountProfile.ReadVerified(data, Session);
        Assert.NotNull(result); Assert.Equal(Session.AccountId, result.Value.Profile.AccountId);
        Assert.Equal("测试昵称", result.Value.Profile.DisplayName); Assert.NotNull(result.Value.Avatar);
        Assert.Null(CloudAccountProfile.ReadVerified(data, Session with { RootKey = "space-b" }));
        Assert.Null(CloudAccountProfile.ReadVerified(data, Session with { RootKey = null }));
    }
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"nickname\":\"Someone\"}")]
    [InlineData("{\"main_dir_key\":123,\"nick_name\":true}")]
    public void MissingAndMalformedIdentityNeverSuppliesDisplayInfo(string json)
    {
        using var doc = JsonDocument.Parse(json); Assert.Null(CloudAccountProfile.ReadVerified(doc.RootElement, Session));
    }
    [Theory]
    [InlineData("http://thirdwx.qlogo.cn/avatar")]
    [InlineData("https://qlogo.cn.attacker.invalid/avatar")]
    [InlineData("https://127.0.0.1/avatar")]
    [InlineData("https://name:password@qlogo.cn/avatar")]
    [InlineData("https://qlogo.cn:8443/avatar")]
    public void UnsafeAvatarDoesNotDiscardVerifiedNickname(string avatar)
    {
        var data = JsonSerializer.SerializeToElement(new { main_dir_key = "space-a", nick_name = "昵称", https_head_img_url = avatar });
        var result = CloudAccountProfile.ReadVerified(data, Session);
        Assert.Equal("昵称", result!.Value.Profile.DisplayName); Assert.Null(result.Value.Avatar);
    }
    [Fact] public void InvalidNicknameFallsBackWithoutAcceptingAnotherAccount()
    {
        foreach (var nickname in new[] { " ", "line\nbreak", new string('长', 129) })
        {
            var data = JsonSerializer.SerializeToElement(new { root_dir_key = "space-a", nick_name = nickname });
            Assert.Null(CloudAccountProfile.ReadVerified(data, Session)!.Value.Profile.DisplayName);
        }
        Assert.Throws<InvalidDataException>(() => new CloudAccountProfile(Session.AccountId, "昵称").Validate((Session with { RootKey = "space-b" }).AccountId));
        Assert.Throws<InvalidDataException>(() => new CloudAccountProfile(Session.AccountId, null, new byte[CloudAccountProfile.MaxAvatarBytes + 1]).Validate(Session.AccountId));
    }
    [Fact] public void EncryptedProfileCacheIsIndependentOfTokensAccountsAndWorkspace()
    {
        if (!OperatingSystem.IsWindows()) return;
        var data = Path.Combine(Path.GetTempPath(), "ToDoList-ProfileCheck-" + Guid.NewGuid().ToString("N"));
        var first = new WindowsCloudCredentials(data); var secondWorkspace = new WindowsCloudCredentials(data + "-other");
        var other = Session with { RootKey = "space-b" };
        first.Save(Session); first.SaveProfile(new(Session.AccountId, "测试昵称", [1, 2, 3]));
        Assert.Equal("测试昵称", first.LoadProfile(Session.AccountId)!.DisplayName);
        var loaded = first.Load(Session.AccountId)!; Assert.Equal(Session, loaded with { AuthorizedAt = null });
        Assert.NotNull(loaded.AuthorizedAt); Assert.Equal(loaded.AuthorizedAt, first.Load(Session.AccountId)!.AuthorizedAt);
        Assert.Null(first.LoadProfile(other.AccountId)); Assert.Null(secondWorkspace.LoadProfile(Session.AccountId));
        first.Save(Session with { AccessToken = "rotated" });
        Assert.Equal("测试昵称", first.LoadProfile(Session.AccountId)!.DisplayName);
        first.Activate(Session.AccountId); first.Logout(); Assert.Null(first.ActiveAccount);
        Assert.Equal("测试昵称", first.LoadProfile(Session.AccountId)!.DisplayName);
    }
}
