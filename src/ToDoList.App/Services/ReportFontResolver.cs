using System.Windows;
using System.Windows.Media;
using PdfSharp.Fonts;

namespace ToDoList.App.Services;

/// <summary>Use the system's actual font face, including fonts packaged in TTC collections.</summary>
internal sealed class ReportFontResolver : IFontResolver
{
    private readonly Dictionary<string, GlyphTypeface> _faces = new();
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var typeface = new Typeface(new FontFamily(familyName), FontStyles.Normal, isBold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var glyphs)) return null;
        var key = glyphs.FontUri.AbsoluteUri;
        _faces[key] = glyphs;
        return new FontResolverInfo(key, false, isItalic);
    }
    public byte[]? GetFont(string faceName)
    {
        if (!_faces.TryGetValue(faceName, out var glyphs)) return null;
        // WPF extracts this face as a standalone OpenType font. PDFsharp later subsets
        // it to the glyphs used by the report; no system font file is distributed.
        return glyphs.ComputeSubset(glyphs.AdvanceWidths.Keys.ToList());
    }
}
