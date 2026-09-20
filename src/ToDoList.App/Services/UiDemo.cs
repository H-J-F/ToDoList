using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using System.Threading.Channels;
using ToDoList.App.Controls;
using ToDoList.Core;

namespace ToDoList.App.Services;

// Run only with --data-dir <isolated-directory> --ui-demo. Captures real WPF animation frames.
internal static class UiDemo
{
    public static async Task RunAsync(MainWindow window)
    {
        var model = window.Model;
        var output = Path.GetFullPath(Path.Combine(model.Library.DataDirectory, "..", "demo-frames"));
        Directory.CreateDirectory(output);
        var frame = 0;
        var captureClock = new Stopwatch();
        var timings = new List<(string Name, double Seconds)>();
        var frames = Channel.CreateBounded<(BitmapSource Bitmap, string Name)>(8);
        var writer = Task.Run(async () =>
        {
            await foreach (var item in frames.Reader.ReadAllAsync())
            {
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(item.Bitmap));
                using var stream = File.Create(Path.Combine(output, item.Name)); png.Save(stream);
            }
        });
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000d / 30) };
        timer.Tick += (_, _) =>
        {
            var bitmap = new RenderTargetBitmap(1100, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            bitmap.Freeze(); var name = $"{frame:D5}.png";
            if (frames.Writer.TryWrite((bitmap, name))) { timings.Add((name, captureClock.Elapsed.TotalSeconds)); frame++; }
        };
        try
        {
            window.Width = 1100; window.Height = 760; model.Settings.ReduceMotion = false; model.Settings.Mode = "浅色"; model.Settings.AccentPreset = "浅蓝"; ThemeService.Apply(model.Settings);
            if (!model.HasBook)
            {
                var book = await model.Library.CreateAsync("Demo2026", "产品设计工作台");
                await model.RefreshBooksAsync(); await model.OpenBookAsync(book);
                foreach (var text in new[] { "整理用户访谈记录", "检查桌面端窗口缩放", "准备本周项目进度", "验证导入与备份流程", "完成新版交互设计" })
                    await model.Repository!.AddTaskAsync(RichContent.FromText(text), null);
                await model.ReloadAsync();
                ((ComboBox)window.FindName("BookSelector")).SelectedItem = model.Books[0];
                ((ListBox)window.FindName("ProjectList")).SelectedIndex = 0;
                ((ComboBox)window.FindName("DraftProject")).SelectedIndex = 0;
            }
            await Task.Delay(400); captureClock.Start(); timer.Start(); await Task.Delay(800);
            var row = model.Tasks[1]; await model.ChangeStatusAsync(row, true); await Task.Delay(700);
            await model.ChangeStatusAsync(row, false); await Task.Delay(700);
            var tabs = (TabControl)window.FindName("Tabs");
            void Select(string name) => tabs.Items.Cast<TabItem>().Single(t => (string)t.Tag == name).IsSelected = true;
            Select("Open"); await Task.Delay(500);
            await model.ChangeStatusAsync(model.Tasks[0], false); await Task.Delay(600);
            var settingsButton = MainWindow.Descendants<Button>(window).First(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Settings");
            settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(800);
            var panel = (Border)window.FindName("SettingsPanel");
            var close = MainWindow.Descendants<Button>(panel).First(b => Equals(b.ToolTip, "关闭设置"));
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(400);
            Select("Today"); await Task.Delay(400);
            row = model.Tasks.First(r => !r.IsDeleted); await model.DeleteRestoreAsync(row); await Task.Delay(900);
            Select("Deleted"); await Task.Delay(600);
            await model.DeleteRestoreAsync(model.Tasks[0]); await Task.Delay(600);
            Select("Today"); await Task.Delay(500);
            var card = window.FindCard(model.Tasks[0]); card?.BeginEdit(); await Task.Delay(700); card?.EndEdit(); if (card?.Row != null) card.Row.IsEditing = false;
            await Task.Delay(500);
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); }
        finally
        {
            timer.Stop(); captureClock.Stop(); frames.Writer.Complete(); await writer;
            var lines = new List<string>();
            for (int i = 0; i < timings.Count; i++)
            {
                lines.Add($"file '{timings[i].Name}'");
                var end = i + 1 < timings.Count ? timings[i + 1].Seconds : captureClock.Elapsed.TotalSeconds;
                lines.Add("duration " + Math.Max(.001, end - timings[i].Seconds).ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
            }
            if (timings.Count > 0) lines.Add($"file '{timings[^1].Name}'");
            File.WriteAllLines(Path.Combine(output, "frames.txt"), lines);
            File.WriteAllText(Path.Combine(output, "capture.txt"), $"Actual WPF frames with wall-clock timing; {frame} frames / {captureClock.Elapsed.TotalSeconds:F2} seconds. No mocked UI.");
            Application.Current.Shutdown();
        }
    }
}
