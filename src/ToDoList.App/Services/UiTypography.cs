using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using ToDoList.App.Controls;
using ToDoList.Core;

namespace ToDoList.App.Services;

internal static class UiTypography
{
    public static async Task RunAsync(MainWindow window)
    {
        var records = new List<object>(); var failures = new List<string>();
        var model = window.Model;
        try
        {
            if (!model.HasBook) await model.OpenBookAsync(await model.Library.CreateAsync("Typography", "字体与首次编辑回归"));
            RichEditor? previousEditor = null;
            foreach (var size in new[] { 12d, 14d, 16d, 18d })
            {
                model.Settings.FontSize = size; ThemeService.Apply(model.Settings);
                await model.AddAsync(new(1, [new([new("普通文字 👩‍💻 正文 "), new("粗体结尾 👍🏽", true)])]), null, null);
                var id = model.Tasks[^1].Id;
                async Task<TaskCard> Locate()
                {
                    var row = model.Tasks.Single(t => t.Id == id); var list = (ListBox)window.FindName("TaskList");
                    list.ScrollIntoView(row); await Settle(); return window.FindCard(row)!;
                }
                var card = await Locate(); Inspect(((TextBlock)card.FindName("BodyText")).Inlines, "first-add", size);
                await model.AddAsync(RichContent.FromText("随后添加的任务"), null, null);
                card = await Locate(); Inspect(((TextBlock)card.FindName("BodyText")).Inlines, "second-add", size);
                var watch = Stopwatch.StartNew(); card.BeginEdit(); watch.Stop();
                records.Add(new { Stage = "begin-edit", ExpectedSize = size, SynchronousMilliseconds = watch.Elapsed.TotalMilliseconds });
                var editor = card.ActiveEditor!; var box = (RichTextBox)editor.FindName("Editor");
                if (previousEditor != null && !ReferenceEquals(previousEditor, editor)) failures.Add("Task editor was not reused");
                previousEditor = editor;
                var heights = new List<double>();
                for (var frame = 0; frame < 8; frame++) { await Task.Delay(20); heights.Add(box.ActualHeight); }
                records.Add(new { Stage = "expand", ExpectedSize = size, EditorHeights = heights });
                if (heights.Max() - heights.Min() > 1) failures.Add($"{size}: RichTextBox changed height during expansion");
                await Settle(); Inspect(((Paragraph)box.Document.Blocks.FirstBlock).Inlines, "edit-original", size);
                TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, box, "新输入中文ABC"));
                await Settle(); Inspect(((Paragraph)box.Document.Blocks.FirstBlock).Inlines, "edit-typed", size);
                var typed = Inlines(((Paragraph)box.Document.Blocks.FirstBlock).Inlines).OfType<Run>().FirstOrDefault(r => r.Text.Contains("新输入"));
                if (typed?.FontWeight != FontWeights.Bold) failures.Add($"{size}: appended text did not inherit the bold end of the document");
                editor.InsertContent(new(1, [new([new("粘贴 👩‍💻 内容", false, true)])]));
                await Settle(); Inspect(((Paragraph)box.Document.Blocks.FirstBlock).Inlines, "paste", size);
                editor.SetContent(editor.GetContent()); await Settle(); Inspect(((Paragraph)box.Document.Blocks.FirstBlock).Inlines, "roundtrip", size);
                model.Settings.FontSize = size == 18 ? 12 : 18; ThemeService.Apply(model.Settings);
                await Settle(); Inspect(((Paragraph)box.Document.Blocks.FirstBlock).Inlines, "live-font-change", model.Settings.FontSize);
                model.Settings.FontSize = size; ThemeService.Apply(model.Settings);
                card.EndEdit(); model.Tasks.Single(t => t.Id == id).IsEditing = false;
                card.BeginEdit(); card.EndEdit(); card.BeginEdit(); await Settle();
                if (card.ActiveEditor != editor || ((ContentControl)card.FindName("EditorHost")).Opacity != 1)
                    failures.Add("Rapid cancel/reopen left stale editor or animation state");
                card.EndEdit(); model.Tasks.Single(t => t.Id == id).IsEditing = false;
            }
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            File.WriteAllText(Path.Combine(model.Library.DataDirectory, "..", "typography.json"), JsonSerializer.Serialize(new { Passed = failures.Count == 0, Failures = failures, Records = records }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown();
        }
        void Inspect(InlineCollection collection, string stage, double size)
        {
            foreach (var inline in Inlines(collection).Where(i => i is Run or ColorEmojiInline))
            {
                records.Add(new { Stage = stage, ExpectedSize = size, Kind = inline.GetType().Name, inline.FontSize, Family = inline.FontFamily.Source, Weight = inline.FontWeight.ToString(), Text = inline is Run run ? run.Text : ((ColorEmojiInline)inline).Text });
                if (Math.Abs(inline.FontSize - size) > .01) failures.Add($"{stage}: {inline.GetType().Name} {inline.FontSize} != {size}");
            }
        }
    }
    private static IEnumerable<Inline> Inlines(InlineCollection collection)
    { foreach (var inline in collection) { yield return inline; if (inline is Span span) foreach (var child in Inlines(span.Inlines)) yield return child; } }
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(220); }
}
