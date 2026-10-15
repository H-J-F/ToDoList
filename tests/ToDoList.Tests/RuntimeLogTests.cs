using System.Text.Json;
using ToDoList.Core;

namespace ToDoList.Tests;

public sealed class RuntimeLogTests
{
    [Fact] public void ReleaseLogRollsLocallyAndRedactsCredentialsAndContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "todo-log-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var log = new FileRuntimeLog(root, 100))
            {
                Assert.Equal(Path.Combine(root, "Log"), log.DirectoryPath);
                for (var i = 0; i < 3; i++) log.Write("request", "id", new { access_token = "secret-token", file_data = "secret-payload", ContentJson = "secret-content", cookie = "secret-cookie", message = "access_token=secret-inline https://example.test/?signature=secret-url" }, new IOException("Bearer secret-auth"));
            }
            var files = Directory.GetFiles(Path.Combine(root, "Log")); Assert.True(files.Length > 1);
            var all = string.Join("", files.Select(File.ReadAllText)); Assert.DoesNotContain("secret-", all);
            foreach (var file in files) foreach (var line in File.ReadLines(file)) using (JsonDocument.Parse(line)) { }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory] [InlineData("新笔记", "新笔记")] [InlineData("a/b:c?", "a_b_c_")] [InlineData(" . ", "待办笔记")] [InlineData("con", "_con")] [InlineData("LPT9.test", "_LPT9.test")]
    public void ExportUsesSafeCurrentTitle(string title, string expected) => Assert.Equal(expected, ExportNames.FromTitle(title));
}
