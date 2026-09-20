using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;

namespace ToDoList.App.Services;
public static class Dialogs
{
    private static Window Shell(Window? owner, string title, out StackPanel panel)
    {
        var window = new Window { Owner = owner, Title = title, Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None, ShowInTaskbar = false, FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"), MaxHeight = 700 };
        window.SetResourceReference(Window.BackgroundProperty, "PaperBrush"); window.SetResourceReference(Window.ForegroundProperty, "InkBrush"); window.SetResourceReference(Window.FontSizeProperty, "BodyFontSize");
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 40, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0) });
        panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        var border = new Border { Child = panel, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) }; border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); window.Content = border;
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); }; return window;
    }
    public static string? Input(Window? owner, string title, string label, string initial = "")
    {
        var window = Shell(owner, title, out var panel); panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        var input = new TextBox { Text = initial, Margin = new(0, 0, 0, 18) }; panel.Children.Add(input);
        string? result = null; AddButtons(window, panel, ("取消", () => window.Close()), ("确定", () => { result = input.Text; window.Close(); }));
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); }; window.ShowDialog(); return result;
    }
    public static (string Name, string Title)? CreateBook(Window owner)
    {
        var window = Shell(owner, "新建一本待办书", out var panel);
        panel.Children.Add(new TextBlock { Text = "英文标识名 · 仅英文字母", Margin = new(0, 0, 0, 8) }); var name = new TextBox { Margin = new(0, 0, 0, 16), MaxLength = 64 }; panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "展示标题 · 给它起个喜欢的名字", Margin = new(0, 0, 0, 8) }); var title = new TextBox { Margin = new(0, 0, 0, 12), MaxLength = 100 }; panel.Children.Add(title);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) }; error.SetResourceReference(TextBlock.ForegroundProperty, "YellowBrush"); panel.Children.Add(error);
        (string, string)? result = null;
        AddButtons(window, panel, ("取消", () => window.Close()), ("创建待办书", () => { try { Core.BookRules.ValidateName(name.Text); var text = Core.BookRules.ValidateTitle(title.Text); result = (name.Text, text); window.Close(); } catch (ArgumentException ex) { error.Text = ex.Message; } }));
        window.Loaded += (_, _) => name.Focus(); window.ShowDialog(); return result;
    }
    public static int Choose(Window? owner, string title, string message, params string[] choices)
    {
        var window = Shell(owner, title, out var panel); panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 22) });
        int result = -1; AddButtons(window, panel, choices.Select((text, index) => (text, (Action)(() => { result = index; window.Close(); }))).ToArray()); window.ShowDialog(); return result;
    }
    public static void Info(Window? owner, string title, string message) => Choose(owner, title, message, "知道了");
    private static void AddButtons(Window window, StackPanel panel, params (string Text, Action Action)[] actions)
    {
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < actions.Length; i++) { var action = actions[i]; var button = new Button { Content = action.Text, Margin = new(6, 4, 0, 0), MinWidth = 74 }; if (i == actions.Length - 1) button.Style = (Style)window.FindResource("PrimaryButton"); button.Click += (_, _) => action.Action(); buttons.Children.Add(button); }
        panel.Children.Add(buttons);
    }
}
