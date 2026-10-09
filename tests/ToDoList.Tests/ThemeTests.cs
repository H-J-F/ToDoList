using ToDoList.Core;
using ToDoList.Storage;

namespace ToDoList.Tests;

public sealed class ThemeTests
{
    public static IEnumerable<object[]> Themes => ThemeCatalog.All.Where(t => t.Id != "system").Select(t => new object[] { t.Id });

    [Theory]
    [MemberData(nameof(Themes))]
    public void ReadingAndInteractionColorsHaveSufficientContrast(string id)
    {
        var c = ThemeCatalog.Resolve(id).Colors;
        void Check(string foreground, string background, double minimum, string role) =>
            Assert.True(ThemeCatalog.Contrast(foreground, background) >= minimum, $"{id}: {role} {foreground}/{background}: {ThemeCatalog.Contrast(foreground, background):F2} < {minimum}");
        foreach (var surface in new[] { c.Canvas, c.Sidebar, c.Paper, c.Input, c.Hover, c.Pressed, c.Selection })
        {
            Check(c.Ink, surface, 4.5, "body"); Check(c.Muted, surface, 4.5, "secondary/completed");
            Check(c.Accent, surface, 4.5, "link/focus");
        }
        foreach (var surface in new[] { c.Input, c.Paper })
            Check(ThemeCatalog.Mix(c.Ink, c.Accent, .2), ThemeCatalog.Mix(surface, c.Accent, .2), 4.5, "text selection overlay");
        foreach (var fill in new[] { c.Accent, c.AccentHover, c.AccentPressed }) Check(c.OnAccent, fill, 4.5, "primary button");
        foreach (var fill in new[] { c.Open, c.Verification, c.Completed, c.Danger })
        {
            foreach (var surface in new[] { c.Paper, c.Hover }) Check(fill, surface, 4.5, "status label");
            Check(ThemeCatalog.Legible(fill), fill, 4.5, "status glyph");
        }
        foreach (var surface in new[] { c.Paper, c.Input }) Check(c.Border, surface, 3, "control boundary");
    }

    [Theory]
    [InlineData("浅色", "classic-light")]
    [InlineData("深色", "classic-dark")]
    [InlineData("跟随系统", "system")]
    [InlineData("unknown", "classic-light")]
    public void LegacySettingsMigrateAndSurviveReload(string mode, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "ToDoList-theme-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new BookLibrary(root); library.EnsureWritable();
            library.SaveSettings(new() { Mode = mode, AccentPreset = "紫色", SettingsVersion = 2, FontSize = 18, Density = "紧凑", LastBook = "Work" });
            var settings = library.LoadSettings();
            Assert.Equal(expected, settings.ThemeId); Assert.Equal(3, settings.SettingsVersion);
            Assert.Equal(18, settings.FontSize); Assert.Equal("紧凑", settings.Density); Assert.Equal("Work", settings.LastBook);
            settings.ThemeId = "midnight-mocha"; library.SaveSettings(settings);
            Assert.Equal("midnight-mocha", library.LoadSettings().ThemeId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void InvalidOrMissingModernThemeFallsBackWithoutReadingOldAccent()
    {
        foreach (var id in new string?[] { "removed-theme", "", null })
        {
            var settings = new AppSettings { ThemeId = id, SettingsVersion = 3, Mode = "深色", AccentPreset = "紫色" };
            ThemeCatalog.Normalize(settings); Assert.Equal("classic-light", settings.ThemeId);
        }
        var fresh = new AppSettings(); ThemeCatalog.Normalize(fresh); Assert.Equal("classic-light", fresh.ThemeId);
    }

    [Fact]
    public void OnlySystemThemeRespondsToSystemAppearance()
    {
        Assert.Equal("classic-dark", ThemeCatalog.Resolve("system", true).Id);
        Assert.Equal("classic-light", ThemeCatalog.Resolve("system", false).Id);
        foreach (var t in ThemeCatalog.All.Where(t => t.Id != "system")) Assert.Equal(ThemeCatalog.Resolve(t.Id, true), ThemeCatalog.Resolve(t.Id, false));
        Assert.Equal(8, ThemeCatalog.All.Count(t => t.IsCandidate));
        Assert.Equal(ThemeCatalog.All.Count, ThemeCatalog.All.Select(t => t.Id).Distinct().Count());
    }
}
