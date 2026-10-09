using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToDoList.App.Controls;
using ToDoList.Core;

namespace ToDoList.App.Services;

// Explicit opt-in, isolated data only. Renders live WPF visuals, including separate
// Popup HWNDs. No mock UI and no access to the user's default database.
internal static class UiThemePreview
{
    internal static async Task RunAsync(MainWindow window)
    {
        var model = window.Model;
        var output = Path.GetFullPath(Path.Combine(model.Library.DataDirectory, ".."));
        var checks = new List<string>(); var failures = new List<string>();
        var shots = new List<(ThemeDefinition Theme, string File)>();
        var completed = false;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks.Add(message); File.AppendAllText(Path.Combine(output, "progress.txt"), message + Environment.NewLine); }
        try
        {
            if (Environment.GetCommandLineArgs().Contains("--theme-restart"))
            {
                Check(model.HasBook && model.Settings.ThemeId == "deep-sea-blue" && ThemeService.Current.Id == "deep-sea-blue", "New blue theme survives process restart");
                Check((string?)((ComboBox)window.FindName("ThemeSetting")).SelectedValue == "deep-sea-blue", "Restart synchronizes selector");
                completed = true; return;
            }
            if (model.HasBook || (await model.Library.ListAsync()).Count != 0) throw new InvalidOperationException("Theme preview requires an empty isolated data directory.");
            window.WindowState = WindowState.Normal; window.Width = 1100; window.Height = 760;
            window.Left = 60; window.Top = 40;
            model.Settings.FontSize = 14; model.Settings.ReduceMotion = true;
            ThemeService.Apply(model.Settings);
            var book = await model.Library.CreateAsync("ThemePreview", "工作与生活 · 主题预览");
            await model.RefreshBooksAsync(); await model.OpenBookAsync(book);
            var repo = model.Repository!;
            var design = await repo.AddProjectAsync("产品设计"); var learn = await repo.AddProjectAsync("学习计划"); var life = await repo.AddProjectAsync("生活清单");
            await repo.AddTaskAsync(new(1, [new([new("阅读与灵感  ", Bold: true), new("打开配色参考", Link: "https://colordrop.io/?sort=POPULAR")])]), learn.Id);
            await repo.AddTaskAsync(RichContent.FromText("留一点时间给生活：散步、阅读，记录今天值得记住的小事。\n长段落也应该有舒适的留白和清楚的文字层次。"), life.Id);
            var done = await repo.AddTaskAsync(RichContent.FromText("整理桌面，给新的一天留出空间"), life.Id);
            await repo.SetStatusAsync(done.Id, TodoStatus.Completed, done.Revision);
            var verify = await repo.AddTaskAsync(RichContent.FromText("确认按钮、文字与选中状态的配色"), design.Id);
            await repo.SetStatusAsync(verify.Id, TodoStatus.Verification, verify.Revision);
            await repo.AddTaskAsync(new(1, [new([new("今天的小目标  ", Bold: true), new("把喜欢的想法变成行动。")])]), design.Id);
            await model.RefreshProjectsAsync(); await model.ReloadAsync(); Invoke(window, "SyncSelectors", false); await Settle();
            var draft = (RichEditor)window.FindName("DraftEditor");
            var content = new RichContent(1, [new([new("记下下一个灵感，按自己的节奏来。"), new("  自定义色", Color: "#B7695C")])]);
            draft.SetContent(content);
            var selector = (ComboBox)window.FindName("ThemeSetting");
            var panel = (Border)window.FindName("SettingsPanel");
            var add = MainWindow.Descendants<Wpf.Ui.Controls.Button>(window).Single(b => AutomationProperties.GetAutomationId(b) == "AddTask");
            var originalReports = (model.ReportOpenColor, model.ReportVerificationColor, model.ReportCompletedColor);
            if (Environment.GetCommandLineArgs().Contains("--dropdown-check"))
            {
                await UiThemeDropdownChecks.RunAsync(window, output, Check);
                completed = true; return;
            }
            Check(window.FindName("PaletteSetting") == null && window.FindName("ModeSetting") == null && selector.Items.Count == ThemeCatalog.All.Count, "One theme selector replaces mode and accent");

            foreach (var theme in ThemeCatalog.All.Where(t => t.Id != "system"))
            {
                selector.SelectedValue = theme.Id; await Settle();
                Check(model.Settings.ThemeId == theme.Id && ThemeService.Current.Id == theme.Id, theme.Name + ": selector applies theme");
                Check(Hex(selector.BorderBrush) == theme.Colors.Border, theme.Name + ": actual input boundary follows theme");
                Check(Hex(add.Background) == theme.Colors.Accent && Hex(add.Foreground) == theme.Colors.OnAccent, theme.Name + ": actual primary button fill and text");
                Check(draft.GetContent().ToJson() == content.ToJson(), theme.Name + ": switching preserves rich draft and explicit color");
                Check(originalReports == (model.ReportOpenColor, model.ReportVerificationColor, model.ReportCompletedColor), theme.Name + ": report colors unchanged");
                foreach (var card in MainWindow.Descendants<TaskCard>((ListBox)window.FindName("TaskList")))
                {
                    var body = (TextBlock)card.FindName("BodyText");
                    Check(body.Opacity == 1, theme.Name + ": task text keeps full opacity");
                    var cb = (CheckBox)card.FindName("CheckButton");
                    var expected = card.Row!.IsCompleted ? theme.Colors.Completed : card.Row.IsVerification ? theme.Colors.Verification : theme.Colors.Open;
                    Check(Hex(cb.BorderBrush) == expected, theme.Name + ": task status uses semantic theme color");
                }
                if (theme.IsCandidate)
                {
                    panel.Visibility = Visibility.Collapsed; await Settle();
                    Capture(window, Path.Combine(output, theme.Id + "-main.png"), theme, "主界面");
                    shots.Add((theme, theme.Id + "-main.png"));
                }
                panel.Visibility = Visibility.Visible; await Settle();
                if (theme.Id is "sky-blue" or "deep-sea-blue") Capture(window, Path.Combine(output, theme.Id + "-alignment.png"), theme, "设置控件对齐");
                selector.IsDropDownOpen = true; await Settle();
                var popup = (Popup)selector.Template.FindName("Popup", selector);
                Check(popup.IsOpen && popup.Child.IsVisible, theme.Name + ": real dropdown popup is visible");
                var dropdown = (Border)selector.Template.FindName("DropDownBorder", selector);
                Check(Hex(dropdown.Background) == theme.Colors.Paper, theme.Name + ": popup background follows theme");
                if (theme.IsCandidate) Capture(window, Path.Combine(output, theme.Id + "-settings.png"), theme, "设置与主题下拉框");
                selector.IsDropDownOpen = false; panel.Visibility = Visibility.Collapsed;

                // Keep a context menu alive while resources change, then return to the candidate.
                var cardForMenu = MainWindow.Descendants<TaskCard>((ListBox)window.FindName("TaskList")).First();
                var menu = cardForMenu.ContextMenu;
                if (menu != null)
                {
                    menu.PlacementTarget = cardForMenu; menu.IsOpen = true; await Settle();
                    model.Settings.ThemeId = theme.Colors.Dark ? "classic-light" : "classic-dark"; ThemeService.Apply(model.Settings);
                    model.Settings.ThemeId = theme.Id; ThemeService.Apply(model.Settings); await Settle();
                    Check(Hex(menu.Background) == theme.Colors.Paper && Hex(menu.Foreground) == theme.Colors.Ink, theme.Name + ": open context menu refreshes");
                    if (theme.IsCandidate) Capture(window, Path.Combine(output, theme.Id + "-menu.png"), theme, "任务右键菜单");
                    menu.IsOpen = false;
                }
                var dialogTask = Dialogs.Input(window, "新建模块", "为你的计划起个名字", "灵感与计划"); await Settle();
                var dialog = MainWindow.Descendants<Wpf.Ui.Controls.ContentDialog>(window).Single();
                var dialogInput = MainWindow.Descendants<TextBox>(dialog).Single(t => t.Text == "灵感与计划");
                Check(dialogInput.SelectedText == "灵感与计划" && dialogInput.SelectionOpacity == .2 && Hex(dialogInput.SelectionBrush) == theme.Colors.Accent,
                    theme.Name + ": selected textbox text has a translucent theme overlay");
                Check(Hex(dialog.Foreground) == theme.Colors.Ink, theme.Name + ": dialog text follows theme");
                if (theme.IsCandidate) Capture(window, Path.Combine(output, theme.Id + "-dialog.png"), theme, "输入弹窗");
                dialog.TemplateButtonCommand.Execute(Wpf.Ui.Controls.ContentDialogButton.Close); await dialogTask;
                ((Button)window.FindName("CalendarButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
                var datePopup = (Popup)typeof(MainWindow).GetField("_datePopup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Check(datePopup.IsOpen && Hex(((Border)datePopup.Child).Background) == theme.Colors.Paper, theme.Name + ": calendar popup follows theme");
                var picker = MainWindow.Descendants<Wpf.Ui.Controls.CalendarDatePicker>((FrameworkElement)datePopup.Child).First();
                Invoke(picker, "OnClick"); await Settle();
                if (theme.IsCandidate) Capture(window, Path.Combine(output, theme.Id + "-calendar.png"), theme, "日期选择与日历");
                picker.IsCalendarOpen = false; datePopup.IsOpen = false;
            }

            // Editing across dark/light transitions must retain the exact document and selection.
            var editing = MainWindow.Descendants<TaskCard>((ListBox)window.FindName("TaskList")).First(c => !c.Row!.IsCompleted);
            editing.BeginEdit(); await Settle();
            var editor = editing.ActiveEditor!; editor.SetContent(content);
            var input = (RichTextBox)editor.FindName("Editor"); input.Focus();
            input.Selection.Select(input.Document.ContentStart, input.Document.ContentEnd);
            var selectedText = input.Selection.Text;
            Check(input.SelectionOpacity == .2, "Rich editor selection overlay cannot hide glyphs");
            foreach (var theme in ThemeCatalog.All)
            {
                model.Settings.ThemeId = theme.Id; ThemeService.Apply(model.Settings); await Settle();
                Check(editor.GetContent().ToJson() == content.ToJson() && input.Selection.Text == selectedText, theme.Name + ": active editor preserves content and selection");
                if (theme.IsCandidate) Capture(window, Path.Combine(output, theme.Id + "-edit.png"), theme, "任务编辑");
            }
            editing.EndEdit(); editing.Row!.IsEditing = false;
            foreach (var theme in ThemeCatalog.All.Where(t => t.Id != "system"))
            foreach (var dimensions in new[] { (800d, 560d), (1100d, 760d) })
            foreach (var size in new double[] { 12, 14, 16, 18 })
            {
                window.Width = dimensions.Item1; window.Height = dimensions.Item2; model.Settings.ThemeId = theme.Id; model.Settings.FontSize = size;
                ThemeService.Apply(model.Settings); window.SyncSettings(); panel.Visibility = Visibility.Visible; await Settle();
                var name = MainWindow.Descendants<TextBlock>(selector).Single(t => t.Text == theme.Name);
                var text = new FormattedText(name.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(name.FontFamily, name.FontStyle, name.FontWeight, name.FontStretch), name.FontSize, Brushes.Black, 1);
                Check(name.ActualWidth + 1 >= text.Width && selector.ActualWidth > 0, $"{theme.Name}/{size}/{dimensions.Item1}: theme label fits window");
                Check(selector.TransformToAncestor(panel).Transform(new Point(selector.ActualWidth, selector.ActualHeight)).X < panel.ActualWidth - 16, $"{theme.Name}/{size}: selector avoids scrollbar");
                var origin = draft.TransformToAncestor(window).Transform(new Point());
                Check(origin.Y >= 0 && origin.Y + draft.ActualHeight <= window.ActualHeight, $"{theme.Name}/{size}: editor fits minimum window");
                var fontSelector = (ComboBox)window.FindName("FontSetting");
                var themeLeft = selector.PointToScreen(new Point()).X;
                var fontLeft = fontSelector.PointToScreen(new Point()).X;
                Check(Math.Abs(themeLeft - fontLeft) < 1 && Math.Abs(selector.ActualWidth - fontSelector.ActualWidth) < 1,
                    $"{theme.Name}/{size}/{dimensions.Item1}: selector edges align");
                if (theme.Id == "warm-sage")
                {
                    selector.IsDropDownOpen = true; await Settle();
                    var item = (ComboBoxItem)selector.ItemContainerGenerator.ContainerFromItem(selector.SelectedItem);
                    var label = MainWindow.Descendants<TextBlock>(item).Single(t => t.Text == theme.Name);
                    Check(label.ActualWidth + 1 >= text.Width, $"{size}/{dimensions.Item1}: longest popup label fits");
                    selector.IsDropDownOpen = false;
                }
                if (size == 18 && theme.IsCandidate && dimensions.Item1 == 800) Capture(window, Path.Combine(output, theme.Id + "-minimum.png"), theme, "800×560 · 18 DIP");
            }
            window.Width = 1100; window.Height = 760; model.Settings.FontSize = 14; ThemeService.Apply(model.Settings);
            await UiThemeDropdownChecks.RunAsync(window, output, Check);
            panel.Visibility = Visibility.Collapsed;
            Invoke(window, "ResetSettings_Click", window, new RoutedEventArgs()); await Settle();
            Check(model.Settings.ThemeId == "classic-light" && model.Settings.FontSize == 14, "Reset chooses classic light and standard font");
            model.Settings.ThemeId = "deep-sea-blue"; ThemeService.Apply(model.Settings); model.SaveSettings();
            Check(model.Library.LoadSettings().ThemeId == "deep-sea-blue", "New blue candidate is persisted");
            MakeOverview(output, shots);
            MakeGallery(output);
            completed = true;
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            var name = Environment.GetCommandLineArgs().Contains("--theme-restart") ? "restart-results.json" : "theme-results.json";
            File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(new { Completed = completed, Passed = completed && failures.Count == 0, Checks = checks, Failures = failures }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(failures.Count == 0 ? 0 : 1);
        }
    }

    private static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static string Hex(Brush brush) { var c = ((SolidColorBrush)brush).Color; return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(240); }
    private static void Save(BitmapSource bitmap, string path) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file); }

    private static void Capture(Window window, string path, ThemeDefinition theme, string scene)
    {
        window.UpdateLayout();
        var layers = new List<(FrameworkElement Element, Point Origin)> { (window, new Point()) };
        foreach (var source in PresentationSource.CurrentSources.Cast<PresentationSource>())
        {
            if (source.RootVisual is not FrameworkElement root || root == window || !root.IsVisible || root.ActualWidth <= 0) continue;
            var origin = window.PointFromScreen(root.PointToScreen(new Point()));
            layers.Add((root, origin));
        }
        var left = Math.Min(0, layers.Min(l => l.Origin.X)); var top = Math.Min(0, layers.Min(l => l.Origin.Y));
        var width = Math.Ceiling(layers.Max(l => l.Origin.X + l.Element.ActualWidth) - left);
        var height = Math.Ceiling(layers.Max(l => l.Origin.Y + l.Element.ActualHeight) - top);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(245, 246, 248)), null, new Rect(0, 0, width, height + 54));
            foreach (var (element, origin) in layers)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element); drawing.DrawImage(bitmap, new Rect(origin.X - left, origin.Y - top, element.ActualWidth, element.ActualHeight));
            }
            DrawText(drawing, $"{theme.Name} · {theme.Appearance}  /  {scene}  /  候选主题 · 实际 WPF 渲染", 15, new Point(18, height + 6));
            DrawText(drawing, theme.Source ?? "ToDoList 经典主题", 11, new Point(18, height + 30));
        }
        var result = new RenderTargetBitmap((int)width, (int)height + 54, 96, 96, PixelFormats.Pbgra32); result.Render(visual); Save(result, path);
    }
    private static void DrawText(DrawingContext drawing, string value, double size, Point position) => drawing.DrawText(
        new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, new SolidColorBrush(Color.FromRgb(40, 47, 57)), 1), position);
    private static void MakeOverview(string output, List<(ThemeDefinition Theme, string File)> shots)
    {
        const int columns = 4, width = 2292;
        var height = 96 + (int)Math.Ceiling(shots.Count / (double)columns) * 438 + 8;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(236, 239, 243)), null, new Rect(0, 0, width, height));
            DrawText(drawing, $"ToDoList · {shots.Count} 套主题候选", 26, new Point(24, 16));
            DrawText(drawing, "相同内容 · 真实 WPF 界面 · 固定明暗 · 每套均可保留、调整或移除", 14, new Point(25, 57));
            for (int i = 0; i < shots.Count; i++)
            {
                var bitmap = new BitmapImage(new Uri(Path.Combine(output, shots[i].File)));
                drawing.DrawImage(bitmap, new Rect(20 + i % columns * 568, 96 + i / columns * 438, 550, 407));
            }
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); result.Render(visual); Save(result, Path.Combine(output, "overview.png"));
    }
    private static void MakeGallery(string output)
    {
        var html = new StringBuilder("<!doctype html><html lang='zh-CN'><meta charset='utf-8'><title>ToDoList 主题候选</title><style>body{font:16px 'Microsoft YaHei UI',sans-serif;background:#edf0f4;color:#28303b;margin:32px auto;max-width:1400}h1{font-size:28px}section{background:white;padding:24px;margin:24px 0;border-radius:16px}img{max-width:100%;height:auto;border-radius:8px}a{color:#246493}.grid{display:grid;grid-template-columns:1fr 1fr;gap:20px}summary{cursor:pointer;margin:18px 0}small{color:#526174}</style><h1>主题候选</h1><p>真实 WPF 界面与独立弹出窗口渲染。点击图片查看原尺寸。每套可选择保留、调整或移除。</p><img src='overview.png'>");
        foreach (var t in ThemeCatalog.All.Where(t => t.IsCandidate))
        {
            html.Append($"<section><h2>{t.Name} · {t.Appearance}</h2><p><a href='{t.Source}'>ColorDrop 配色来源</a> <small>{string.Join(" · ", t.SourceColors!)}</small></p><div class='grid'>");
            foreach (var scene in new[] { "main", "settings" }) html.Append($"<a href='{t.Id}-{scene}.png'><img src='{t.Id}-{scene}.png'></a>");
            html.Append("</div><details><summary>查看编辑、弹窗、菜单、日历与最小窗口</summary><div class='grid'>");
            foreach (var scene in new[] { "alignment", "edit", "dialog", "menu", "calendar", "minimum" })
                if (File.Exists(Path.Combine(output, $"{t.Id}-{scene}.png"))) html.Append($"<a href='{t.Id}-{scene}.png'><img loading='lazy' src='{t.Id}-{scene}.png'></a>");
            html.Append("</div></details></section>");
        }
        File.WriteAllText(Path.Combine(output, "index.html"), html.Append("</html>").ToString());
    }
}
