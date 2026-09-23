using System.IO;
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
    public RichEditor() { InitializeComponent(); SetContent(RichContent.FromText("")); DataObject.AddPastingHandler(Editor, OnPaste); InitializeEditing(); Editor.LayoutUpdated += (_, _) => AlignPlaceholder(); }
    public string SubmitLabel { set => Placeholder.Text = $"输入任务内容…  Enter {value}，Shift+Enter 换行"; }
    private void AlignPlaceholder()
    {
        if (Placeholder.Visibility != Visibility.Visible || !Editor.IsLoaded || !Editor.Document.ContentStart.HasValidLayout) return;
        var start = Editor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        if (start == null) return;
        var rect = start.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty) return;
        var point = Editor.TranslatePoint(rect.TopLeft, (UIElement)Placeholder.Parent);
        // A zero-padding TextBlock shares the text metrics; anchor it to the actual insertion line.
        Canvas.SetLeft(Placeholder, point.X); Canvas.SetTop(Placeholder, point.Y);
    }
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
            else if (inline is Run || inline is ColorEmojiInline)
            {
                var r = inline;
                string? color = null; bool underlined = false;
                for (TextElement? node = r; node != null; node = node.Parent as TextElement)
                {
                    if (node is Inline i && i.TextDecorations.Any(d => d.Location == TextDecorationLocation.Underline)) underlined = true;
                }
                color = ReadColor(r);
                var text = r is Run plain ? plain.Text : ((ColorEmojiInline)r).Text;
                var value = new RichRun(text, r.FontWeight >= FontWeights.SemiBold, r.FontStyle == FontStyles.Italic, underlined, color, RichContent.IsSafeLink(link ?? "") ? link : null);
                if (output.Count > 0 && output[^1] with { Text = text } == value) output[^1] = output[^1] with { Text = output[^1].Text + text };
                else output.Add(value);
            }
        }
    }
    private static string? ReadColor(TextElement element)
    {
        for (TextElement? node = element; node != null; node = node.Parent as TextElement)
        {
            if (node.ReadLocalValue(TextElement.ForegroundProperty) is LinearGradientBrush) return null;
            if (node.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush b) return $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
        }
        return null;
    }
    public void SetContent(RichContent content)
    {
        CloseTool(); _selectionStart = _selectionEnd = null; _composing = false;
        _pendingColor = null;
        _setting = true;
        try
        {
            var doc = new FlowDocument { PagePadding = new Thickness(0) };
            doc.SetResourceReference(TextElement.FontSizeProperty, "BodyFontSize");
            doc.SetResourceReference(TextElement.FontFamilyProperty, "BodyFontFamily");
            doc.SetResourceReference(TextElement.ForegroundProperty, "InkBrush");
            doc.FontWeight = FontWeights.Normal; doc.FontStyle = FontStyles.Normal;
            foreach (var p in content.Paragraphs) { var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) }; foreach (var r in p.Runs) paragraph.Inlines.Add(CreateInline(r)); doc.Blocks.Add(paragraph); }
            Editor.Document = doc; Dirty = false;
        }
        finally { _setting = false; }
    }
    public static Inline CreateInline(RichRun data)
    {
        var elements = EmojiRendering.Elements(data.Text).ToList();
        Inline run;
        if (elements.Any(EmojiRendering.IsEmoji))
        {
            var span = new Span();
            foreach (var inline in TextInlines(elements)) span.Inlines.Add(inline);
            run = span;
        }
        else run = new Run(data.Text);
        SetDisplayTypography(run);
        run.FontWeight = data.Bold ? FontWeights.Bold : FontWeights.Normal; run.FontStyle = data.Italic ? FontStyles.Italic : FontStyles.Normal;
        if (data.Underline) run.TextDecorations = TextDecorations.Underline;
        if (data.Color != null) run.Foreground = (Brush)new BrushConverter().ConvertFromString(data.Color)!;
        if (data.Link == null) return run;
        var link = new Hyperlink(run) { NavigateUri = new Uri(data.Link) }; link.SetResourceReference(TextElement.ForegroundProperty, "AccentInkBrush"); return link;
    }
    private static void SetDisplayTypography(TextElement element)
    {
        // Inline resource expressions are cloned by WPF during text insertion and undo.
        // Inherit typography from the document/TextBlock instead of cloning expressions.
        element.ClearValue(TextElement.FontSizeProperty);
        element.ClearValue(TextElement.FontFamilyProperty);
    }
    private static IEnumerable<Inline> TextInlines(IEnumerable<string> elements)
    {
        var plain = new System.Text.StringBuilder();
        foreach (var text in elements)
        {
            if (EmojiRendering.IsEmoji(text) && EmojiRendering.Drawing(text) != null)
            {
                if (plain.Length > 0) { var run = new Run(plain.ToString()); SetDisplayTypography(run); yield return run; plain.Clear(); }
                var emoji = new ColorEmojiInline(text); SetDisplayTypography(emoji); yield return emoji;
            }
            else plain.Append(text);
        }
        if (plain.Length > 0) { var run = new Run(plain.ToString()); SetDisplayTypography(run); yield return run; }
    }
    private void Bold_Click(object sender, RoutedEventArgs e) => EditingCommands.ToggleBold.Execute(null, Editor);
    private void Italic_Click(object sender, RoutedEventArgs e) => EditingCommands.ToggleItalic.Execute(null, Editor);
    private void Underline_Click(object sender, RoutedEventArgs e) => EditingCommands.ToggleUnderline.Execute(null, Editor);
    private void ClearFormat_Click(object sender, RoutedEventArgs e) { Editor.Focus(); Editor.Selection.ClearAllProperties(); ApplyColor(null); }
    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        var value = await Dialogs.Input(Window.GetWindow(this), "添加一个链接", "链接地址（https://…）", "https://");
        if (value == null) return;
        if (!RichContent.IsSafeLink(value)) { await Dialogs.Info(Window.GetWindow(this), "链接格式不正确", "请输入 http 或 https 链接。"); return; }
        if (Editor.Selection.Start.Paragraph != Editor.Selection.End.Paragraph)
        { await Dialogs.Info(Window.GetWindow(this), "请选择一段文字", "链接文字需要位于同一段落中。"); return; }
        try
        {
            if (Editor.Selection.IsEmpty) Editor.Selection.Text = value;
            var link = new Hyperlink(Editor.Selection.Start, Editor.Selection.End) { NavigateUri = new Uri(value) };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentInkBrush"); Editor.Focus();
        }
        catch (ArgumentException) { await Dialogs.Info(Window.GetWindow(this), "暂时无法添加链接", "请选择同一段落内、不包含已有链接的文字。"); }
    }
    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (_composing || e.Key == Key.ImeProcessed) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) EditingCommands.EnterLineBreak.Execute(null, Editor);
            else if (!e.IsRepeat) Submit?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Key == Key.Escape) { e.Handled = true; Cancel?.Invoke(this, EventArgs.Empty); }
    }
    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_setting) { Dirty = true; ColorInsertedText(e); if (!_composing && e.UndoAction is not UndoAction.Undo and not UndoAction.Redo) RenderTypedEmoji(); }
        if (Placeholder != null) Placeholder.Visibility = GetContent().PlainText.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText) && e.DataObject.GetData(DataFormats.UnicodeText) is string text)
            PastePlainText(text);
    }
    internal void PastePlainText(string text)
    {
        _pendingColor = null;
        _setting = true;
        Editor.BeginChange();
        try
        {
            // Keep WPF's insertion pointers across paragraph boundaries; never load RTF.
            Editor.Selection.Text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            Editor.Selection.ClearAllProperties();
            Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, AutomaticColor());
            Editor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
            Editor.Selection.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
            Editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
            Editor.CaretPosition = Editor.Selection.End;
            NormalizeTypography();
        }
        finally { Editor.EndChange(); _setting = false; }
        Dirty = true;
        RenderTypedEmoji();
        Placeholder.Visibility = GetContent().PlainText.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
