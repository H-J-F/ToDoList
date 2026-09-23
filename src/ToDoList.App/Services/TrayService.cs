using System.Drawing;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace ToDoList.App.Services;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon? _ownedIcon;
    private readonly ContextMenu _menu;

    public TrayService(MainWindow window)
    {
        _ownedIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        _icon = new Forms.NotifyIcon { Icon = _ownedIcon, Text = "ToDoList", Visible = true };
        var open = new MenuItem { Header = "打开主窗口", FontWeight = System.Windows.FontWeights.Normal, Padding = new System.Windows.Thickness(10, 5, 14, 5) };
        var exit = new MenuItem { Header = "退出 ToDoList", Padding = new System.Windows.Thickness(10, 5, 14, 5) };
        _menu = new ContextMenu { UseLayoutRounding = true, SnapsToDevicePixels = true, FontWeight = System.Windows.FontWeights.Normal, Placement = PlacementMode.MousePoint, FontSize = 12, MinWidth = 132, StaysOpen = false };
        open.Click += (_, _) => { _menu.IsOpen = false; window.RestoreFromTray(); };
        exit.Click += (_, _) => { _menu.IsOpen = false; window.RequestExit(); };
        TextOptions.SetTextFormattingMode(_menu, TextFormattingMode.Display); TextOptions.SetTextRenderingMode(_menu, TextRenderingMode.ClearType);
        _menu.Items.Add(open); _menu.Items.Add(new Separator()); _menu.Items.Add(exit);
        _icon.DoubleClick += (_, _) => window.Dispatcher.BeginInvoke(window.RestoreFromTray);
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right) window.Dispatcher.BeginInvoke(() => { _menu.IsOpen = false; _menu.IsOpen = true; });
        };
    }

    public void UpdateText(string title)
    {
        var text = "ToDoList · " + title;
        _icon.Text = text.Length <= 63 ? text : text[..60] + "…";
    }

    public void Dispose()
    {
        _menu.IsOpen = false; _icon.Visible = false; _icon.Dispose(); _ownedIcon?.Dispose();
    }
}
