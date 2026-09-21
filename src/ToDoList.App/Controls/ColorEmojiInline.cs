using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ToDoList.App.Services;

namespace ToDoList.App.Controls;

// Unicode is the durable value. The image is derived and never stored in task JSON.
public sealed class ColorEmojiInline : InlineUIContainer
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(ColorEmojiInline), new FrameworkPropertyMetadata("", OnTextChanged));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public ColorEmojiInline() : base(new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false })
    { BaselineAlignment = BaselineAlignment.Center; }
    public ColorEmojiInline(string text) : this() { Text = text; }
    public new Image Child => (Image)base.Child;
    public bool ShouldSerializeChild() => false;
    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ColorEmojiInline)d).Refresh();
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontSizeProperty) Refresh();
    }
    private void Refresh()
    {
        if (base.Child is not Image image || string.IsNullOrEmpty(Text)) return;
        image.Source = EmojiRendering.Drawing(Text);
        image.Width = image.Height = FontSize * 1.3;
        System.Windows.Automation.AutomationProperties.SetName(image, Text);
    }
}
