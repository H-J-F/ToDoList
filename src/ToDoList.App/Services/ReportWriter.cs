using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ToDoList.Core;

namespace ToDoList.App.Services;

internal static class ReportWriter
{
    private static readonly Lazy<bool> FontsReady = new(() => { PdfSharp.Fonts.GlobalFontSettings.FontResolver = new ReportFontResolver(); return true; });
    public static Task WriteAsync(TaskReport report, ReportFormat format, string destination)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Exception? failure = null;
            try
            {
                switch (format)
                {
                    case ReportFormat.Markdown: File.WriteAllText(temporary, report.ToMarkdown(), new UTF8Encoding(false)); break;
                    case ReportFormat.Docx: WriteDocx(report, temporary); break;
                    case ReportFormat.Pdf: WritePdf(report, temporary); break;
                    default: throw new ArgumentOutOfRangeException(nameof(format));
                }
                File.Move(temporary, destination, true);
            }
            catch (Exception ex) { failure = ex; }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure ??= ex; } }
            if (failure == null) completion.TrySetResult(); else completion.TrySetException(failure);
        }) { IsBackground = true, Name = "Task report export" };
        // WPF font discovery and emoji drawings need an STA, without blocking the application dispatcher.
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    private static void WriteDocx(TaskReport report, string path)
    {
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, XElement root)
        {
            using var stream = archive.CreateEntry(name).Open(); new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Save(stream);
        }
        Part("[Content_Types].xml", new(types + "Types",
            new XElement(types + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(types + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            new XElement(types + "Override", new XAttribute("PartName", "/word/document.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"))));
        Part("_rels/.rels", new(rel + "Relationships", new XElement(rel + "Relationship", new XAttribute("Id", "rId1"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"), new XAttribute("Target", "word/document.xml"))));
        var body = new XElement(w + "body");
        foreach (var line in report.Lines)
        {
            var bold = line.Kind is ReportLineKind.Title or ReportLineKind.Heading or ReportLineKind.DateHeading;
            var size = line.Kind switch { ReportLineKind.Title => "32", ReportLineKind.Heading => "25", ReportLineKind.DateHeading => "22", ReportLineKind.Metadata => "18", _ => "20" };
            var after = line.Kind switch { ReportLineKind.Title => "100", ReportLineKind.Heading => "70", ReportLineKind.DateHeading => "50", ReportLineKind.Metadata => "40", _ => "0" };
            var properties = new XElement(w + "pPr", new XElement(w + "spacing", new XAttribute(w + "after", after), new XAttribute(w + "line", "240"), new XAttribute(w + "lineRule", "auto")));
            if (line.Kind is ReportLineKind.Title or ReportLineKind.Heading or ReportLineKind.DateHeading or ReportLineKind.TaskContent) properties.Add(new XElement(w + "keepNext"));
            if (line.Kind is ReportLineKind.Title or ReportLineKind.Heading or ReportLineKind.DateHeading)
                properties.Add(new XElement(w + "outlineLvl", new XAttribute(w + "val", line.Kind == ReportLineKind.Title ? "0" : line.Kind == ReportLineKind.Heading ? "1" : "2")));
            var paragraph = new XElement(w + "p", properties);
            foreach (var run in line.Runs)
            {
                var runProperties = new XElement(w + "rPr", new XElement(w + "rFonts", new XAttribute(w + "ascii", "Microsoft YaHei"), new XAttribute(w + "hAnsi", "Microsoft YaHei"), new XAttribute(w + "eastAsia", "Microsoft YaHei")), new XElement(w + "sz", new XAttribute(w + "val", size)));
                if (bold) runProperties.Add(new XElement(w + "b"));
                if (run.Color != ReportColor.Default) runProperties.Add(new XElement(w + "color", new XAttribute(w + "val", report.ResolveColor(run.Color).TrimStart('#'))));
                paragraph.Add(new XElement(w + "r", runProperties, new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), run.Text)));
            }
            body.Add(paragraph);
        }
        body.Add(new XElement(w + "sectPr", new XElement(w + "pgSz", new XAttribute(w + "w", "11906"), new XAttribute(w + "h", "16838")), new XElement(w + "pgMar", new XAttribute(w + "top", "1134"), new XAttribute(w + "bottom", "1134"), new XAttribute(w + "left", "1134"), new XAttribute(w + "right", "1134"))));
        Part("word/document.xml", new(w + "document", new XAttribute(XNamespace.Xmlns + "w", w), body));
    }

    private static void WritePdf(TaskReport report, string path)
    {
        using var document = new PdfDocument(); document.Info.Title = report.Lines[0].Text;
        _ = FontsReady.Value;
        var regular = new XFont("Microsoft YaHei UI", 10.5, XFontStyleEx.Regular);
        var metadata = new XFont("Microsoft YaHei UI", 9.5, XFontStyleEx.Regular);
        var task = regular;
        var date = new XFont("Microsoft YaHei UI", 12, XFontStyleEx.Bold);
        var heading = new XFont("Microsoft YaHei UI", 13, XFontStyleEx.Bold);
        var title = new XFont("Microsoft YaHei UI", 18, XFontStyleEx.Bold);
        var emojiFont = new XFont("Segoe UI Emoji", 10.5, XFontStyleEx.Regular);
        var emojis = new Dictionary<string, XImage?>();
        var imageStreams = new List<MemoryStream>();
        XGraphics? graphics = null;
        double y = 0, width = 0, height = 0;
        const double margin = 48;
        void NewPage()
        {
            graphics?.Dispose(); var page = document.AddPage(); page.Size = PdfSharp.PageSize.A4;
            width = page.Width.Point; height = page.Height.Point; graphics = XGraphics.FromPdfPage(page); y = margin;
        }
        XImage? Emoji(string text)
        {
            if (emojis.TryGetValue(text, out var cached)) return cached;
            XImage? image = null;
            var drawing = new DrawingImage();
            try
            {
                global::Emoji.Wpf.Image.SetSource(drawing, text);
                if (drawing.Drawing is { Bounds.IsEmpty: false })
                {
                    var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawImage(drawing, new Rect(0, 0, 64, 64));
                    var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var stream = new MemoryStream(); imageStreams.Add(stream); encoder.Save(stream); stream.Position = 0; image = XImage.FromStream(stream);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { }
            emojis[text] = image; return image;
        }
        try
        {
            NewPage();
            foreach (var line in report.Lines)
            {
                var font = line.Kind switch { ReportLineKind.Title => title, ReportLineKind.Heading => heading, ReportLineKind.DateHeading => date, ReportLineKind.TaskContent => task, ReportLineKind.Metadata => metadata, _ => regular };
                var lineHeight = font.Size * 1.25;
                if (line.Kind is ReportLineKind.Title or ReportLineKind.Heading) y += 6;
                else if (line.Kind == ReportLineKind.DateHeading) y += 5;
                double x = margin;
                var buffer = new StringBuilder(); var bufferX = x; XBrush bufferBrush = XBrushes.Black;
                void Flush() { if (buffer.Length > 0) { graphics!.DrawString(buffer.ToString(), font, bufferBrush, new XPoint(bufferX, y), XStringFormats.TopLeft); buffer.Clear(); } }
                foreach (var run in line.Runs)
                {
                    var color = report.ResolveColor(run.Color);
                    var brush = run.Color == ReportColor.Default ? XBrushes.Black : new XSolidBrush(XColor.FromArgb(Convert.ToByte(color[1..3], 16), Convert.ToByte(color[3..5], 16), Convert.ToByte(color[5..7], 16)));
                    foreach (var element in EmojiRendering.Elements(run.Text.Replace("\t", "    ")))
                    {
                        var isEmoji = EmojiRendering.IsEmoji(element);
                        var image = isEmoji ? Emoji(element) : null;
                        var elementFont = isEmoji && image == null ? emojiFont : font;
                        var span = image != null ? font.Size * 1.3 : graphics!.MeasureString(element, elementFont).Width;
                        if (x + span > width - margin && x > margin) { Flush(); x = margin; y += lineHeight; }
                        if (y + lineHeight > height - margin) { Flush(); NewPage(); x = margin; }
                        if (image != null || isEmoji)
                        {
                            Flush();
                            if (image != null) graphics!.DrawImage(image, x, y + 1, span, span);
                            else graphics!.DrawString(element, elementFont, brush, new XPoint(x, y), XStringFormats.TopLeft);
                        }
                        else
                        {
                            if (buffer.Length == 0) { bufferX = x; bufferBrush = brush; }
                            else if (!ReferenceEquals(bufferBrush, brush)) { Flush(); bufferX = x; bufferBrush = brush; }
                            buffer.Append(element);
                        }
                        x += span;
                    }
                }
                Flush(); y += lineHeight;
            }
            graphics?.Dispose(); graphics = null; document.Save(path);
        }
        finally { graphics?.Dispose(); foreach (var image in emojis.Values) image?.Dispose(); foreach (var stream in imageStreams) stream.Dispose(); }
    }
}
