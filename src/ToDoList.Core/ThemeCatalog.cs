namespace ToDoList.Core;

// Platform-independent theme contracts also used by migration and contrast tests.
public sealed record ThemeColors(string Canvas, string Sidebar, string Paper, string Input,
    string Ink, string Muted, string Line, string Border, string Accent, string Selection, bool Dark)
{
    public string Hover => ThemeCatalog.Mix(Paper, Accent, Dark ? .12 : .07);
    public string Pressed => ThemeCatalog.Mix(Paper, Accent, Dark ? .18 : .12);
    public string OnAccent => ThemeCatalog.Legible(Accent);
    public string AccentHover => ThemeCatalog.Mix(Accent, OnAccent == "#FFFFFF" ? "#000000" : "#FFFFFF", .08);
    public string AccentPressed => ThemeCatalog.Mix(Accent, OnAccent == "#FFFFFF" ? "#000000" : "#FFFFFF", .16);
    public string Open => Dark ? "#81C9CE" : "#196977";
    public string Verification => Dark ? "#E7C477" : "#79540B";
    public string Completed => Dark ? "#A6D3AE" : "#296740";
    public string Danger => Dark ? "#F2A7A2" : "#A32F43";
}

public sealed record ThemeDefinition(string Id, string Name, ThemeColors Colors,
    string? Source = null, string[]? SourceColors = null)
{
    public bool IsCandidate => Source != null;
    public string Appearance => Id == "system" ? "随系统" : Colors.Dark ? "深色" : "浅色";
}

public static class ThemeCatalog
{
    public const string DefaultId = "classic-light";
    private static readonly ThemeColors Light = new("#F1F4F8", "#EDF1F6", "#FFFFFF", "#F4F7FB", "#202B3A", "#536276", "#DDE4EC", "#7C8999", "#246493", "#DFEAF4", false);
    private static readonly ThemeColors Dark = new("#202328", "#24282E", "#292E35", "#323A43", "#F1F4F8", "#B9C4D0", "#454F5C", "#8B9AAB", "#92C9EF", "#3A4C5D", true);
    public static IReadOnlyList<ThemeDefinition> All { get; } = Array.AsReadOnly<ThemeDefinition>(
    [
        new("system", "跟随系统", Light),
        new(DefaultId, "经典浅色", Light),
        new("classic-dark", "经典深色", Dark),
        new("mint-cream", "薄荷奶油", new("#F0F4E9", "#DFEEE6", "#FFFEF7", "#F1F6ED", "#243D38", "#50655D", "#D4E2D6", "#738D81", "#146B63", "#CCE5D8", false),
            "https://colordrop.io/palette/385", ["#1F9792", "#505050", "#FFF4C5", "#9DD7D5"]),
        new("dusk-gold", "暮色金", new("#232B32", "#263F41", "#2C363F", "#35434B", "#F2F1E7", "#B9C9C9", "#475960", "#8C9E9F", "#E5BD47", "#414C43", true),
            "https://colordrop.io/palette/322", ["#2C363F", "#2F6665", "#E5BD47", "#DCDCDD"]),
        new("midnight-mocha", "午夜摩卡", new("#271D26", "#35232E", "#342730", "#413038", "#F4E8D8", "#CCB6A8", "#60464B", "#AF908A", "#DCC4A2", "#563B38", true),
            "https://colordrop.io/palette/127", ["#2F1B27", "#513533", "#B4442A", "#DCC4A2"]),
        new("lilac-mist", "丁香雾", new("#EFEBF6", "#E6DDF1", "#FCFAFE", "#F3EEF8", "#332C43", "#675C77", "#DFD5E9", "#8B7C9C", "#245D89", "#E3D9F0", false),
            "https://colordrop.io/palette/455", ["#FAD3CF", "#A696C8", "#2470A0", "#060608"]),
        new("warm-sage", "暖灰鼠尾草", new("#E9E5DE", "#E3D9CD", "#FBF9F5", "#F0ECE5", "#3F3830", "#655E52", "#DDD5C8", "#8E8171", "#4D6156", "#D7DFD3", false),
            "https://colordrop.io/palette/351", ["#3F3830", "#6C7874", "#B0B1A1", "#E3D9CD"]),
        new("coral-peach", "珊瑚蜜桃", new("#F9E6DB", "#F9CDAD", "#FFFAF6", "#FCEDE4", "#492F33", "#70464E", "#EBD2C5", "#A17470", "#9D2C49", "#F5C2BE", false),
            "https://colordrop.io/palette/26", ["#FE4365", "#FC9D9A", "#F9CDAD", "#C8C8A9"]),
        new("sky-blue", "晴空蔚蓝", new("#EAF2FA", "#D9E8F7", "#FFFFFF", "#EFF5FB", "#17334F", "#465E76", "#CDDEED", "#6A839C", "#285D8F", "#D6E5F3", false),
            "https://colordrop.io/palette/213", ["#336699", "#FFCC66", "#FFFFFF", "#003366"]),
        new("deep-sea-blue", "深海夜蓝", new("#061C2D", "#03243A", "#0C2B42", "#12354F", "#EDF4FA", "#B3C7D9", "#2C506A", "#799AB4", "#8CB9F2", "#254663", true),
            "https://colordrop.io/palette/387", ["#03243A", "#558AD8", "#C5C5C5", "#74C239"])
    ]);

    public static string FromLegacyMode(string? mode) => mode switch { "深色" => "classic-dark", "跟随系统" => "system", _ => DefaultId };
    public static void Normalize(AppSettings settings)
    {
        if (settings.ThemeId == null && settings.SettingsVersion < 3) settings.ThemeId = FromLegacyMode(settings.Mode);
        if (!All.Any(t => t.Id == settings.ThemeId)) settings.ThemeId = DefaultId;
        settings.SettingsVersion = Math.Max(settings.SettingsVersion, 3);
    }
    public static ThemeDefinition Resolve(string? id, bool systemDark = false) =>
        All.FirstOrDefault(t => t.Id == (id == "system" ? systemDark ? "classic-dark" : DefaultId : id)) ?? All[1];

    public static string Mix(string from, string to, double amount)
    {
        int Channel(int offset) => (int)Math.Round(Convert.ToInt32(from.Substring(offset, 2), 16) * (1 - amount) + Convert.ToInt32(to.Substring(offset, 2), 16) * amount);
        return $"#{Channel(1):X2}{Channel(3):X2}{Channel(5):X2}";
    }
    public static double Contrast(string a, string b)
    {
        static double Luminance(string hex)
        {
            double Channel(int offset) { var c = Convert.ToInt32(hex.Substring(offset, 2), 16) / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
            return .2126 * Channel(1) + .7152 * Channel(3) + .0722 * Channel(5);
        }
        var x = Luminance(a); var y = Luminance(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
    public static string Legible(string background) => Contrast(background, "#FFFFFF") >= Contrast(background, "#202020") ? "#FFFFFF" : "#202020";
}
