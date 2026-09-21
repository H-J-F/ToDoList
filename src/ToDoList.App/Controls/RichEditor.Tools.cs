using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ToDoList.App.Services;
using ToDoList.Core;
using UiButton = Wpf.Ui.Controls.Button;

namespace ToDoList.App.Controls;

public partial class RichEditor
{
    private const string ClipboardFormat = "ToDoList.RichContent.v1";
    private bool _composing, _renderingEmoji;
    private Popup? _popup;
    internal FrameworkElement? ToolContent => _popup?.Child as FrameworkElement;
    internal void CloseTool() { if (_popup != null) _popup.IsOpen = false; }
    private TextPointer? _selectionStart, _selectionEnd;
    private Brush? _pendingColor;
    private bool _applyingColor;
    private void ColorInsertedText(TextChangedEventArgs e)
    {
        if (_pendingColor == null || _composing || _applyingColor || e.UndoAction is UndoAction.Undo or UndoAction.Redo) return;
        var brush = _pendingColor;
        foreach (var change in e.Changes.Where(c => c.AddedLength > 0))
        {
            var start = Editor.Document.ContentStart.GetPositionAtOffset(change.Offset);
            var end = start?.GetPositionAtOffset(change.AddedLength);
            if (start == null || end == null || new TextRange(start, end).Text.Length == 0) continue;
            _applyingColor = true;
            try { new TextRange(start, end).ApplyPropertyValue(TextElement.ForegroundProperty, brush); }
            finally { _applyingColor = false; }
        }
    }
    public static readonly string[] PresetColors =
    ["#E57373", "#F06292", "#BA68C8", "#9575CD", "#7986CB", "#64B5F6", "#4FC3F7", "#4DD0E1",
     "#81C784", "#AED581", "#DCE775", "#FFD54F", "#FFB74D", "#FF8A65", "#A1887F", "#90A4AE",
     "#C62828", "#AD1457", "#7B1FA2", "#1565C0", "#00796B", "#388E3C", "#FFFFFF", "#202B3A"];
    private AppSettings? Preferences => (Window.GetWindow(this) as MainWindow)?.Model.Settings;
    private void InitializeEditing()
    {
        TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(this, (_, _) =>
        {
            _composing = false;
            Editor.Dispatcher.BeginInvoke(new Action(RenderTypedEmoji));
        });
        Editor.PreviewMouseDown += (_, _) => _pendingColor = null;
        Editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Tab) _pendingColor = null;
        };
        CommandManager.AddPreviewExecutedHandler(this, OnClipboardCommand);
        Loaded += (_, _) => ThemeService.Changed += RefreshAutomaticColors;
        Unloaded += (_, _) => { ThemeService.Changed -= RefreshAutomaticColors; if (_popup != null) _popup.IsOpen = false; };
    }
    private Brush AutomaticColor()
    {
        var color = ((SolidColorBrush)FindResource("InkBrush")).Color;
        // A solid gradient marks automatic color without changing neighbouring runs or
        // serializing a theme-specific RGB value into the user's document.
        return new LinearGradientBrush(color, color, 0);
    }
    private void RefreshAutomaticColors()
    {
        var setting = _setting; _setting = true;
        try
        {
            if (_pendingColor is LinearGradientBrush) _pendingColor = AutomaticColor();
            foreach (var inline in Paragraphs(Editor.Document.Blocks).SelectMany(p => AllInlines(p.Inlines)).ToList())
                if (inline.ReadLocalValue(TextElement.ForegroundProperty) is LinearGradientBrush)
                    inline.Foreground = AutomaticColor();
        }
        finally { _setting = setting; }
    }
    private static IEnumerable<Inline> AllInlines(InlineCollection collection)
    {
        foreach (var inline in collection)
        { yield return inline; if (inline is Span span) foreach (var child in AllInlines(span.Inlines)) yield return child; }
    }
    internal void ApplyColor(string? color)
    {
        // WPF's insertion format is established after focus/selection restoration.
        RestoreSelection(); Editor.Focus();
        _pendingColor = Editor.Selection.IsEmpty ? color == null ? AutomaticColor() : (Brush)new BrushConverter().ConvertFromString(color)! : null;
        if (color == null)
        {
            Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, AutomaticColor());
        }
        else
        {
            Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, (Brush)new BrushConverter().ConvertFromString(color)!);
            var preferences = Preferences;
            if (preferences != null) { Remember(preferences.RecentColors, color, 8); SavePreferences(); }
        }
        ColorIndicator.Fill = color == null ? (Brush)FindResource("InkBrush") : (Brush)new BrushConverter().ConvertFromString(color)!;
    }
    private static void Remember(List<string> values, string value, int limit)
    { values.Remove(value); values.Insert(0, value); if (values.Count > limit) values.RemoveRange(limit, values.Count - limit); }
    private void SavePreferences()
    {
        if (Window.GetWindow(this) is not MainWindow window) return;
        try { window.Model.SaveSettings(); } catch (Exception) { window.Model.Message = "最近使用的选项暂时无法保存，编辑内容仍然保留。"; }
    }
    private void RestoreSelection()
    {
        if (_selectionStart != null && _selectionEnd != null)
        { Editor.Selection.Select(_selectionStart, _selectionEnd); _selectionStart = _selectionEnd = null; }
    }
    private void OpenTool(FrameworkElement anchor, FrameworkElement content)
    {
        if (_popup != null) _popup.IsOpen = false;
        _selectionStart = Editor.Selection.Start; _selectionEnd = Editor.Selection.End;
        var border = new Border { Child = content, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "PaperBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        _popup = new Popup { Child = border, PlacementTarget = anchor, Placement = PlacementMode.Top, StaysOpen = false, AllowsTransparency = true };
        border.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _popup.IsOpen = false; RestoreSelection(); Editor.Focus(); e.Handled = true; } };
        _popup.IsOpen = true;
    }
    private void Color_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Width = 288 };
        panel.Children.Add(new TextBlock { Text = "文字颜色", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var auto = new UiButton { Content = "自动 · 跟随主题", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
        auto.Click += (_, _) => { _popup!.IsOpen = false; ApplyColor(null); }; panel.Children.Add(auto);
        var current = Editor.Selection.GetPropertyValue(TextElement.ForegroundProperty) as SolidColorBrush;
        void AddColors(IEnumerable<string> colors)
        {
            var grid = new UniformGrid { Columns = 8 };
            foreach (var color in colors)
            {
                var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
                var swatch = new Border { Background = brush, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Width = 24, Height = 24 };
                if (current?.Color == brush.Color) swatch.Child = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Checkmark16) { FontSize = 16, Foreground = brush.Color.R + brush.Color.G + brush.Color.B > 450 ? Brushes.Black : Brushes.White };
                var button = new UiButton { Content = swatch, Padding = new Thickness(3), Width = 36, Height = 36, MinHeight = 36, ToolTip = color, Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent };
                button.Click += (_, _) => { _popup!.IsOpen = false; ApplyColor(color); }; grid.Children.Add(button);
            }
            panel.Children.Add(grid);
        }
        AddColors(PresetColors);
        if (Preferences?.RecentColors is { Count: > 0 } recent)
        { panel.Children.Add(new TextBlock { Text = "最近使用", Margin = new Thickness(0, 12, 0, 4) }); AddColors(recent); }
        var input = new Wpf.Ui.Controls.TextBox { PlaceholderText = "自定义 #RRGGBB", MaxLength = 7, Margin = new Thickness(0, 12, 0, 8) }; panel.Children.Add(input);
        var apply = new UiButton { Content = "应用自定义颜色", HorizontalAlignment = HorizontalAlignment.Stretch };
        apply.Click += (_, _) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(input.Text, "^#[0-9a-fA-F]{6}$")) { input.ToolTip = "请输入 # 加六位十六进制颜色"; input.Focus(); return; }
            _popup!.IsOpen = false; ApplyColor(input.Text.ToUpperInvariant());
        };
        panel.Children.Add(apply); OpenTool((FrameworkElement)sender, panel);
    }
    private sealed record EmojiChoice(string Text, string Name);
    private void Emoji_Click(object sender, RoutedEventArgs e)
    {
        var groups = Emoji.Wpf.EmojiData.AllGroups;
        string Translate(string name) => name switch
        { "Smileys & Emotion" => "笑脸与情感", "People & Body" => "人物与身体", "Animals & Nature" => "动物与自然", "Food & Drink" => "食物与饮品", "Travel & Places" => "旅行与地点", "Activities" => "活动", "Objects" => "物品", "Symbols" => "符号", "Flags" => "旗帜", "Component" => "肤色与组件", _ => name };
        var panel = new StackPanel { Width = 320 };
        var categories = new ComboBox { ItemsSource = new[] { "最近使用" }.Concat(groups.Select(g => Translate(g.Name))), Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(categories);
        var list = new ListBox { Height = 240, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var row = new FrameworkElementFactory(typeof(ItemsControl)); row.SetBinding(ItemsControl.ItemsSourceProperty, new Binding());
        var grid = new FrameworkElementFactory(typeof(UniformGrid)); grid.SetValue(UniformGrid.ColumnsProperty, 8);
        row.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(grid));
        var button = new FrameworkElementFactory(typeof(UiButton)); button.SetValue(Control.PaddingProperty, new Thickness(2));
        button.SetValue(FrameworkElement.WidthProperty, 36d); button.SetValue(FrameworkElement.HeightProperty, 36d); button.SetValue(FrameworkElement.MinHeightProperty, 36d);
        button.SetBinding(FrameworkElement.TagProperty, new Binding("Text")); button.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Name"));
        button.SetValue(Wpf.Ui.Controls.Button.AppearanceProperty, Wpf.Ui.Controls.ControlAppearance.Transparent);
        button.AddHandler(Button.ClickEvent, new RoutedEventHandler((s, _) =>
        {
            var text = (string)((Button)s).Tag; _popup!.IsOpen = false; RestoreSelection(); InsertContent(RichContent.FromText(text), true);
            if (Preferences is { } prefs) { Remember(prefs.RecentEmoji, text, 24); SavePreferences(); }
        }));
        var image = new FrameworkElementFactory(typeof(Image)); image.SetValue(FrameworkElement.WidthProperty, 26d); image.SetValue(FrameworkElement.HeightProperty, 26d);
        image.SetBinding(Image.SourceProperty, new Binding("Text") { Converter = EmojiImageConverter.Instance }); button.AppendChild(image);
        row.SetValue(ItemsControl.ItemTemplateProperty, new DataTemplate { VisualTree = button });
        list.ItemTemplate = new DataTemplate { VisualTree = row };
        list.ItemContainerStyle = new Style(typeof(ListBoxItem), (Style)FindResource(typeof(ListBoxItem)))
        { Setters = { new Setter(Control.PaddingProperty, new Thickness(0)), new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch), new Setter(Control.FocusableProperty, false) } };
        categories.SelectionChanged += (_, _) =>
        {
            IEnumerable<EmojiChoice> choices = categories.SelectedIndex == 0
                ? (Preferences?.RecentEmoji ?? []).Select(t => new EmojiChoice(t, t))
                : groups[categories.SelectedIndex - 1].EmojiList.SelectMany(x => x.HasVariations ? x.VariationList : [x]).Where(x => EmojiRendering.SupportedBySystem(x.Text)).Select(x => new EmojiChoice(x.Text, x.Name));
            list.ItemsSource = choices.Chunk(8).ToList(); if (list.Items.Count > 0) list.ScrollIntoView(list.Items[0]);
        };
        panel.Children.Add(list); OpenTool((FrameworkElement)sender, panel); categories.SelectedIndex = Preferences?.RecentEmoji.Count > 0 ? 0 : 1;
    }
    private sealed class EmojiImageConverter : IValueConverter
    {
        public static readonly EmojiImageConverter Instance = new();
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => EmojiRendering.Drawing((string)value);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
    internal void InsertContent(RichContent content, bool inheritFormat = false)
    {
        Editor.Focus(); Editor.BeginChange();
        try
        {
            if (inheritFormat) { Editor.Selection.Text = content.PlainText; Editor.CaretPosition = Editor.Selection.End; return; }
            var temporary = new FlowDocument();
            foreach (var paragraph in content.Paragraphs)
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                foreach (var run in paragraph.Runs) p.Inlines.Add(CreatePlainInline(run)); temporary.Blocks.Add(p);
            }
            using var stream = new MemoryStream(); new TextRange(temporary.ContentStart, temporary.ContentEnd).Save(stream, DataFormats.Rtf); stream.Position = 0;
            Editor.Selection.Load(stream, DataFormats.Rtf); Editor.CaretPosition = Editor.Selection.End;
        }
        finally { Editor.EndChange(); RenderTypedEmoji(); }
    }
    private static Inline CreatePlainInline(RichRun value)
    {
        var run = new Run(value.Text) { FontWeight = value.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = value.Italic ? FontStyles.Italic : FontStyles.Normal };
        if (value.Underline) run.TextDecorations = TextDecorations.Underline;
        if (value.Color != null) run.Foreground = (Brush)new BrushConverter().ConvertFromString(value.Color)!;
        return value.Link == null ? run : new Hyperlink(run) { NavigateUri = new Uri(value.Link) };
    }
    private void OnClipboardCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Paste) _pendingColor = null;
        if (e.Command != ApplicationCommands.Copy && e.Command != ApplicationCommands.Cut || Editor.Selection.IsEmpty) return;
        var data = CreateClipboardData();
        try { Clipboard.SetDataObject(data, true); if (e.Command == ApplicationCommands.Cut) Editor.Selection.Text = ""; }
        catch (System.Runtime.InteropServices.COMException)
        { if (Window.GetWindow(this) is MainWindow window) window.Model.Message = "剪贴板正忙，请稍后再复制或剪切；原文已保留。"; }
        e.Handled = true;
    }
    internal DataObject CreateClipboardData()
    {
        var content = SelectedContent(); var data = new DataObject();
        data.SetData(ClipboardFormat, content.ToJson()); data.SetData(DataFormats.UnicodeText, content.PlainText);
        var document = new FlowDocument();
        foreach (var paragraph in content.Paragraphs) { var p = new Paragraph(); foreach (var run in paragraph.Runs) p.Inlines.Add(CreatePlainInline(run)); document.Blocks.Add(p); }
        using var stream = new MemoryStream(); new TextRange(document.ContentStart, document.ContentEnd).Save(stream, DataFormats.Rtf);
        data.SetData(DataFormats.Rtf, Encoding.UTF8.GetString(stream.ToArray()));
        return data;
    }
    private RichContent SelectedContent()
    {
        var result = new List<RichParagraph>(); var start = Editor.Selection.Start; var end = Editor.Selection.End;
        foreach (var paragraph in Paragraphs(Editor.Document.Blocks))
        {
            if (paragraph.ContentEnd.CompareTo(start) < 0 || paragraph.ContentStart.CompareTo(end) > 0) continue;
            var runs = new List<RichRun>();
            foreach (var leaf in Leaves(paragraph.Inlines))
            {
                if (leaf.ContentEnd.CompareTo(start) <= 0 || leaf.ContentStart.CompareTo(end) >= 0) continue;
                string? link = null; for (var parent = leaf.Parent; parent is TextElement te; parent = te.Parent) if (parent is Hyperlink h) link = h.NavigateUri?.OriginalString;
                var text = leaf is ColorEmojiInline emoji ? emoji.Text : leaf is LineBreak ? "\n" : new TextRange(start.CompareTo(leaf.ContentStart) > 0 ? start : leaf.ContentStart, end.CompareTo(leaf.ContentEnd) < 0 ? end : leaf.ContentEnd).Text;
                string? color = null; bool underline = false;
                for (TextElement? node = leaf; node != null; node = node.Parent as TextElement)
                { if (node is Inline inline && inline.TextDecorations.Contains(TextDecorations.Underline[0])) underline = true; }
                color = ReadColor(leaf);
                runs.Add(new(text, leaf.FontWeight >= FontWeights.SemiBold, leaf.FontStyle == FontStyles.Italic, underline, color, link));
            }
            result.Add(new(runs));
        }
        return new(1, result.Count > 0 ? result : [new([])]);
    }
    private static IEnumerable<Inline> Leaves(InlineCollection collection)
    {
        foreach (var inline in collection) if (inline is Span span) { foreach (var child in Leaves(span.Inlines)) yield return child; } else yield return inline;
    }
    private void RenderTypedEmoji()
    {
        if (_setting || _renderingEmoji || _composing) return;
        var candidates = Paragraphs(Editor.Document.Blocks).SelectMany(p => Leaves(p.Inlines)).OfType<Run>()
            .Where(r => EmojiRendering.Elements(r.Text).Any(EmojiRendering.IsEmoji)).ToList();
        // Opening a document change with no replacement clears WPF's pending caret
        // formatting. Ordinary typing must keep the editor's native insertion state.
        if (candidates.Count == 0) return;
        _renderingEmoji = true; Editor.BeginChange();
        try
        {
            foreach (var run in candidates)
            {
                var elements = EmojiRendering.Elements(run.Text).ToList();
                if (!elements.Any(EmojiRendering.IsEmoji)) continue;
                var parent = run.Parent is Paragraph p ? p.Inlines : run.Parent is Span span ? span.Inlines : null;
                if (parent == null) continue;
                var replacement = new Span { FontWeight = run.FontWeight, FontStyle = run.FontStyle, TextDecorations = run.TextDecorations.Clone() };
                if (run.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush brush) replacement.Foreground = brush;
                foreach (var element in elements) replacement.Inlines.Add(EmojiRendering.IsEmoji(element) && EmojiRendering.Drawing(element) != null ? new ColorEmojiInline(element) : new Run(element));
                bool caretInRun = Editor.Selection.IsEmpty && Editor.CaretPosition.CompareTo(run.ElementStart) >= 0 && Editor.CaretPosition.CompareTo(run.ElementEnd) <= 0;
                var offset = caretInRun ? new TextRange(run.ContentStart, Editor.CaretPosition).Text.Length : -1;
                parent.InsertBefore(run, replacement); parent.Remove(run);
                if (caretInRun)
                {
                    var count = 0; var position = replacement.ContentStart;
                    foreach (var inline in replacement.Inlines)
                    {
                        int length = inline is Run r ? r.Text.Length : ((ColorEmojiInline)inline).Text.Length;
                        if (count + length > offset) { position = inline is Run plain ? plain.ContentStart.GetPositionAtOffset(Math.Max(0, offset - count))! : inline.ElementStart; break; }
                        count += length; position = inline.ElementEnd;
                    }
                    Editor.CaretPosition = position.GetInsertionPosition(LogicalDirection.Forward);
                }
            }
        }
        finally { Editor.EndChange(); _renderingEmoji = false; }
    }
}
