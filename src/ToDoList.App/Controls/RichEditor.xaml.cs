using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ToDoList.App.Services;
using ToDoList.Core;

namespace ToDoList.App.Controls;
public partial class RichEditor : UserControl
{
    public event EventHandler? Submit;
    public event EventHandler? Cancel;
    public bool Dirty { get; private set; }
    private bool _setting;
    public RichEditor() { InitializeComponent(); SetContent(RichContent.FromText("")); DataObject.AddPastingHandler(Editor, OnPaste); }
    public void FocusEditor() { Editor.Focus(); Editor.CaretPosition = Editor.Document.ContentEnd; }
    public RichContent GetContent()
    {
        var paragraphs = new List<RichParagraph>();
        foreach (var p in Paragraphs(Editor.Document.Blocks)) { var runs = new List<RichRun>(); Walk(p.Inlines, runs); paragraphs.Add(new(runs)); }
        return new(1, paragraphs.Count == 0 ? [new([])] : paragraphs);
    }
    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var b in blocks)
            if (b is Paragraph p) yield return p;
            else if (b is Section s) foreach (var p2 in Paragraphs(s.Blocks)) yield return p2;
            else if (b is System.Windows.Documents.List l) foreach (var li in l.ListItems) foreach (var p3 in Paragraphs(li.Blocks)) yield return p3;
    }
    private static void Walk(InlineCollection inlines, List<RichRun> output, string? link = null)
    {
        foreach (var inline in inlines)
        {
            if (inline is Hyperlink h) Walk(h.Inlines, output, h.NavigateUri?.OriginalString);
            else if (inline is Span span) Walk(span.Inlines, output, link);
            else if (inline is LineBreak) output.Add(new("\n"));
            else if (inline is Run r)
            {
                string? color = null; bool underlined = false;
                for (TextElement? node = r; node != null; node = node.Parent as TextElement)
                {
                    if (color == null && node.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush b) color = $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
                    if (node is Inline i && i.TextDecorations.Any(d => d.Location == TextDecorationLocation.Underline)) underlined = true;
                }
                output.Add(new(r.Text, r.FontWeight >= FontWeights.SemiBold, r.FontStyle == FontStyles.Italic, underlined, color, RichContent.IsSafeLink(link ?? "") ? link : null));
            }
        }
    }
    public void SetContent(RichContent content)
    {
        _setting = true;
        try
        {
            var doc = new FlowDocument { PagePadding = new Thickness(0) };
            foreach (var p in content.Paragraphs) { var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) }; foreach (var r in p.Runs) paragraph.Inlines.Add(CreateInline(r)); doc.Blocks.Add(paragraph); }
            Editor.Document = doc; Dirty = false;
        }
        finally { _setting = false; }
    }
    public static Inline CreateInline(RichRun data)
    {
        var run = new Run(data.Text) { FontWeight = data.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = data.Italic ? FontStyles.Italic : FontStyles.Normal };
        if (data.Underline) run.TextDecorations = TextDecorations.Underline;
        if (data.Color != null) run.Foreground = (Brush)new BrushConverter().ConvertFromString(data.Color)!;
        if (data.Link == null) return run;
        var link = new Hyperlink(run) { NavigateUri = new Uri(data.Link) }; link.SetResourceReference(TextElement.ForegroundProperty, "AccentInkBrush"); return link;
    }
    private void Bold_Click(object sender, RoutedEventArgs e) => EditingCommands.ToggleBold.Execute(null, Editor);
    private void Italic_Click(object sender, RoutedEventArgs e) => EditingCommands.ToggleItalic.Execute(null, Editor);
    private void Underline_Click(object sender, RoutedEventArgs e) => EditingCommands.ToggleUnderline.Execute(null, Editor);
    private void Color_Click(object sender, RoutedEventArgs e) { Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, new BrushConverter().ConvertFromString((string)((Button)sender).Tag)); Editor.Focus(); }
    private void ClearFormat_Click(object sender, RoutedEventArgs e) { Editor.Selection.ClearAllProperties(); Editor.Focus(); }
    private void Link_Click(object sender, RoutedEventArgs e)
    {
        var value = Dialogs.Input(Window.GetWindow(this), "添加一个链接", "链接地址（https://…）", "https://");
        if (value == null) return;
        if (!RichContent.IsSafeLink(value)) { Dialogs.Info(Window.GetWindow(this), "链接格式不正确", "请输入 http 或 https 链接。"); return; }
        if (Editor.Selection.Start.Paragraph != Editor.Selection.End.Paragraph)
        { Dialogs.Info(Window.GetWindow(this), "请选择一段文字", "链接文字需要位于同一段落中。"); return; }
        try
        {
            if (Editor.Selection.IsEmpty) Editor.Selection.Text = value;
            var link = new Hyperlink(Editor.Selection.Start, Editor.Selection.End) { NavigateUri = new Uri(value) };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentInkBrush"); Editor.Focus();
        }
        catch (ArgumentException) { Dialogs.Info(Window.GetWindow(this), "暂时无法添加链接", "请选择同一段落内、不包含已有链接的文字。"); }
    }
    private void Emoji_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var emoji in new[] { "🌱", "✨", "✅", "📚", "💻", "☕", "🎯", "😊", "❤️", "🏃" })
        { var item = new MenuItem { Header = emoji, FontSize = 22 }; item.Click += (_, _) => { Editor.Selection.Text = emoji; Editor.Focus(); }; menu.Items.Add(item); }
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; Submit?.Invoke(this, EventArgs.Empty); }
        else if (e.Key == Key.Escape) { e.Handled = true; Cancel?.Invoke(this, EventArgs.Empty); }
    }
    private void Editor_TextChanged(object sender, TextChangedEventArgs e) { if (!_setting) Dirty = true; }
    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Rtf)) { e.FormatToApply = DataFormats.Rtf; return; }
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText)) e.FormatToApply = DataFormats.UnicodeText; else e.CancelCommand();
    }
}
