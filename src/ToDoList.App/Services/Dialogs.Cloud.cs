using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ToDoList.App.Controls;
using ToDoList.Core;
using ToDoList.Storage.Cloud;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ToDoList.App.Services;

public static partial class Dialogs
{
    public sealed record ConflictChoice(int Version, bool KeepBoth);
    public static async Task<ConflictChoice?> CloudConflict(Window owner, string key, IReadOnlyList<CloudValue> versions, CloudValue? current)
    {
        await Queue.WaitAsync();
        try
        {
            var panel = new StackPanel { MinWidth = 380, MaxWidth = 480 };
            panel.Children.Add(new TextBlock { Text = "请选择要保留的版本。稍后处理会继续保留双方，其他数据可正常同步。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
            var list = new ListBox { MaxHeight = 320, BorderThickness = new(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var localIndex = -1;
            for (var i = 0; i < versions.Count; i++)
            {
                var version = versions[i]; var local = current != null && current.Semantic(key) == version.Semantic(key);
                if (local) localIndex = i;
                var body = new StackPanel { Margin = new(6) };
                body.Children.Add(new TextBlock { Text = local ? "本地版本" : "云端版本 " + (i + 1), FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 6) });
                if (!version.Deleted && CloudSnapshot.Kind(key) == "task")
                {
                    var task = version.Read<TodoItem>(); var doc = new FlowDocument { PagePadding = new(0), FontSize = 13 };
                    foreach (var p in RichContent.Parse(task.ContentJson).Paragraphs)
                    {
                        var paragraph = new Paragraph { Margin = new(0, 0, 0, 4) };
                        foreach (var run in p.Runs) paragraph.Inlines.Add(RichEditor.CreateInline(run)); doc.Blocks.Add(paragraph);
                    }
                    var viewer = new FlowDocumentScrollViewer { Document = doc, IsToolBarVisible = false, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                    viewer.SetResourceReference(Control.ForegroundProperty, "InkBrush"); body.Children.Add(viewer);
                    body.Children.Add(new TextBlock { Text = "状态：" + (task.Status switch { TodoStatus.Open => "未完成", TodoStatus.Verification => "待验证", TodoStatus.Completed => "已完成", _ => "回收站" }) });
                }
                else body.Children.Add(new TextBlock { Text = CloudMerge.Describe(key, version), TextWrapping = TextWrapping.Wrap });
                list.Items.Add(new ListBoxItem { Content = body });
            }
            list.SelectedIndex = localIndex >= 0 ? localIndex : 0; panel.Children.Add(list);
            var bothAllowed = CloudSnapshot.Kind(key) is "task" or "book" or "project";
            if (bothAllowed) panel.Children.Add(new TextBlock { Text = "保留双方会为另一版本建立独立副本；删除冲突中的正文也会保留为副本。", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
            var dialog = Create(owner, "处理同步冲突", panel);
            dialog.PrimaryButtonText = "保留所选版本"; dialog.SecondaryButtonText = bothAllowed ? "保留双方" : ""; dialog.CloseButtonText = "稍后处理";
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.None ? null : new(Math.Max(0, list.SelectedIndex), result == ContentDialogResult.Secondary);
        }
        finally { Queue.Release(); }
    }
}
