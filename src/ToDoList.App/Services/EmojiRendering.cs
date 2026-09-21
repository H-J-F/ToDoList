using System.Globalization;
using System.Windows.Media;

namespace ToDoList.App.Services;

public static class EmojiRendering
{
    private const int Capacity = 256;
    private static readonly Dictionary<string, DrawingImage?> Cache = new();
    private static readonly Queue<string> Order = new();
    private static readonly Lazy<GlyphTypeface?> SystemGlyphs = new(() => new Typeface("Segoe UI Emoji").TryGetGlyphTypeface(out var glyphs) ? glyphs : null);
    public static int CachedCount => Cache.Count;
    public static bool SupportedBySystem(string text)
    {
        var scalar = char.ConvertToUtf32(text, 0);
        // Emoji.Wpf supplies its own regional flag vectors.
        return scalar is >= 0x1F1E6 and <= 0x1F1FF || SystemGlyphs.Value?.CharacterToGlyphMap.TryGetValue(scalar, out var glyph) == true && glyph != 0;
    }
    public static IEnumerable<string> Elements(string text)
    {
        var iterator = StringInfo.GetTextElementEnumerator(text);
        while (iterator.MoveNext()) yield return iterator.GetTextElement();
    }
    public static bool IsEmoji(string text)
    {
        if (!text.Any(c => char.IsSurrogate(c) || c is >= '\u2300' and <= '\u27FF' || c == '\uFE0F')) return false;
        var match = Emoji.Wpf.EmojiData.MatchOne.Match(text);
        return match.Success && match.Index == 0 && match.Length == text.Length;
    }
    public static DrawingImage? Drawing(string text)
    {
        if (Cache.TryGetValue(text, out var cached)) return cached;
        if (!SupportedBySystem(text)) return null;
        DrawingImage? image = null;
        try
        {
            var candidate = new DrawingImage(); Emoji.Wpf.Image.SetSource(candidate, text);
            if (candidate.Drawing != null && !candidate.Drawing.Bounds.IsEmpty)
            { if (candidate.CanFreeze) candidate.Freeze(); image = candidate; }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { }
        if (Cache.Count == Capacity) Cache.Remove(Order.Dequeue());
        Cache[text] = image; Order.Enqueue(text); return image;
    }
}
