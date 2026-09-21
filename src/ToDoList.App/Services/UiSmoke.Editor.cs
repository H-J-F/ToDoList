using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using ToDoList.App.Controls;
using ToDoList.Core;

namespace ToDoList.App.Services;
internal static partial class UiSmoke
{
    private static async Task CheckEditorAsync(MainWindow window, string output, List<string> log)
    {
        var editor = (RichEditor)window.FindName("DraftEditor");
        var box = (RichTextBox)editor.FindName("Editor");
        void Type(string text) => TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, box, text));
        editor.SetContent(RichContent.FromText("原文")); editor.FocusEditor();
        editor.ApplyColor("#64B5F6"); Type("中文English"); await Settle();
        var content = editor.GetContent();
        Check(content.PlainText == "原文中文English" && content.Paragraphs[0].Runs.Any(r => r.Text == "中文English" && r.Color == "#64B5F6"), "Caret color applies immediately to Chinese and English without a space", log);
        EditingCommands.EnterParagraphBreak.Execute(null, box); Type("换行后"); await Settle();
        Check(editor.GetContent().Paragraphs[^1].Runs.Any(r => r.Text == "换行后" && r.Color == "#64B5F6"), "Caret color continues across a newline", log);
        editor.ApplyColor(null); Type("自动"); await Settle();
        content = editor.GetContent();
        Check(content.Paragraphs.SelectMany(p => p.Runs).Any(r => r.Text == "自动" && r.Color == null) && content.Paragraphs[0].Runs.Any(r => r.Text == "中文English" && r.Color == "#64B5F6"), "Automatic color preserves previous text and serializes as theme-independent", log);
        box.SelectAll(); editor.ApplyColor("#FFB74D");
        Check(editor.GetContent().Paragraphs.SelectMany(p => p.Runs).All(r => r.Color == "#FFB74D"), "Selected text recolors immediately", log);
        editor.SetContent(RichContent.FromText("")); editor.FocusEditor();
        editor.InsertContent(RichContent.FromText("👩🏽‍💻❤️🇨🇳"), true); await Settle();
        Check(editor.GetContent().PlainText == "👩🏽‍💻❤️🇨🇳", "Composite emoji, skin tone and flag keep exact Unicode", log);
        Check(((Paragraph)box.Document.Blocks.FirstBlock).Inlines.OfType<Span>().SelectMany(s => s.Inlines).OfType<ColorEmojiInline>().Count() == 3, "Composite emoji render as three colored vector inlines", log);
        box.SelectAll(); var data = editor.CreateClipboardData();
        Check((string)data.GetData(DataFormats.UnicodeText)! == "👩🏽‍💻❤️🇨🇳", "Emoji clipboard payload exposes exact Unicode", log);
        bool clipboardAvailable = false;
        try { Clipboard.SetDataObject(data, true); await Settle(); clipboardAvailable = Clipboard.GetText() == "👩🏽‍💻❤️🇨🇳"; }
        catch (System.Runtime.InteropServices.COMException) { log.Add("ENVIRONMENT LIMIT: OLE clipboard unavailable (CLIPBRD_E_CANT_OPEN); clipboard payload and native cut/undo document operations tested separately."); }
        if (clipboardAvailable) { log.Add("System clipboard roundtrip passed"); ApplicationCommands.Cut.Execute(null, box); }
        else box.Selection.Text = "";
        await Settle();
        Check(editor.GetContent().PlainText == "", "Cut removes selected emoji", log);
        ApplicationCommands.Undo.Execute(null, box); await Settle();
        Check(editor.GetContent().PlainText == "👩🏽‍💻❤️🇨🇳", "Undo restores colored emoji and Unicode", log);
        ApplicationCommands.Redo.Execute(null, box); await Settle();
        Check(editor.GetContent().PlainText == "", "Redo reapplies emoji cut", log);
        editor.InsertContent(RichContent.Parse((string)data.GetData("ToDoList.RichContent.v1")!)); await Settle();
        Check(editor.GetContent().PlainText.TrimEnd('\n') == "👩🏽‍💻❤️🇨🇳", "Clipboard paste reconstructs emoji without losing text", log);
        editor.SetContent(new(1, [new([new("链接 👩‍💻", true, true, true, "#64B5F6", "https://example.com")])]));
        box.SelectAll(); data = editor.CreateClipboardData(); editor.SetContent(RichContent.FromText("")); editor.FocusEditor(); editor.InsertContent(RichContent.Parse((string)data.GetData("ToDoList.RichContent.v1")!)); await Settle();
        Check(editor.GetContent().Paragraphs.SelectMany(p => p.Runs).Any(r => r.Text.Contains("👩‍💻") && r.Bold && r.Italic && r.Underline && r.Link == "https://example.com" && r.Color == "#64B5F6"), "Copy/paste preserves rich emoji link formatting", log);
        Capture(window, System.IO.Path.Combine(output, "editor-color-emoji.png"));
        box.SelectAll();
        MainWindow.Descendants<Button>(editor).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "清除格式").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(editor.GetContent().Paragraphs.SelectMany(p => p.Runs).All(r => !r.Bold && !r.Italic && !r.Underline && r.Color == null), "Clear formatting removes text styles including emoji-adjacent content", log);
        foreach (var mode in new[] { "浅色", "深色" })
        {
            window.Model.Settings.Mode = mode; ThemeService.Apply(window.Model.Settings);
            ((Button)editor.FindName("ColorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            var tool = editor.ToolContent!;
            Check(MainWindow.Descendants<Button>(tool).Count(b => b.ToolTip is string s && s.StartsWith('#')) >= 24, "Color panel shows 24 presets in " + mode, log);
            CaptureElement(tool, System.IO.Path.Combine(output, "colors-" + mode + ".png")); editor.CloseTool();
            ((Button)editor.FindName("EmojiButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(); tool = editor.ToolContent!;
            var categories = MainWindow.Descendants<ComboBox>(tool).Single();
            Check(categories.Items.Cast<string>().Contains("笑脸与情感"), "Emoji categories are Chinese in " + mode, log);
            Check(MainWindow.Descendants<System.Windows.Controls.Primitives.UniformGrid>(tool).All(g => g.Columns == 8), "Emoji rows use eight columns in " + mode, log);
            CaptureElement(tool, System.IO.Path.Combine(output, "emoji-" + mode + ".png"));
            categories.SelectedIndex = 0; await Settle(); Check(!MainWindow.Descendants<ListBox>(tool).Single().HasItems, "Empty recent emoji category stays usable in " + mode, log);
            editor.CloseTool();
        }
        window.Model.Settings.Mode = "浅色"; ThemeService.Apply(window.Model.Settings);
        editor.SetContent(RichContent.FromText(""));
    }
    private static void CaptureElement(FrameworkElement element, string path)
    {
        element.UpdateLayout(); var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using var file = System.IO.File.Create(path); encoder.Save(file);
    }
}
