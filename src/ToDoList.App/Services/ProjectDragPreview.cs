using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ToDoList.Core;

namespace ToDoList.App.Services;

// Only transforms the existing containers while dragging. The collection/database
// change once, on release; hit testing uses stable layout slots, never moving visuals.
internal sealed class ProjectDragPreview : IDisposable
{
    private sealed record Slot(ListBoxItem Item, Rect Bounds, Transform Original, TranslateTransform Offset);
    private readonly ListBox _list;
    private readonly List<Slot> _slots = [];
    private readonly int _sourceIndex;
    private readonly ListBoxItem _source;
    private readonly double _sourceOpacity;
    private readonly AdornerLayer _layer;
    private readonly DragAdorner _ghost;
    private readonly Vector _grab;
    private int _destination;
    public ListBoxItem? Target { get; private set; }
    public bool After => _destination > _sourceIndex;
    internal Point GhostPosition => _ghost.Position;
    internal double GhostOpacity => _ghost.Opacity;

    public ProjectDragPreview(ListBox list, ListBoxItem source, Point press)
    {
        _list = list; _source = source;
        _layer = AdornerLayer.GetAdornerLayer(list) ?? throw new InvalidOperationException("模块列表缺少拖动显示层。");
        foreach (var project in list.Items.OfType<ProjectInfo>().Where(p => p.Id != null))
        {
            if (list.ItemContainerGenerator.ContainerFromItem(project) is not ListBoxItem item) continue;
            var bounds = new Rect(item.TranslatePoint(new Point(), list), item.RenderSize);
            _slots.Add(new(item, bounds, item.RenderTransform, new TranslateTransform()));
        }
        _sourceIndex = _slots.FindIndex(s => s.Item == source); _destination = _sourceIndex;
        var slot = _slots[_sourceIndex]; _grab = press - slot.Bounds.TopLeft;
        _ghost = new DragAdorner(list, source) { Position = slot.Bounds.TopLeft };
        _layer.Add(_ghost);
        _sourceOpacity = source.Opacity; source.SetCurrentValue(UIElement.OpacityProperty, 0d);
        foreach (var row in _slots)
        {
            var group = new TransformGroup(); group.Children.Add(row.Original); group.Children.Add(row.Offset);
            row.Item.SetCurrentValue(UIElement.RenderTransformProperty, group);
        }
    }

    public void Update(Point position)
    {
        _ghost.Position = position - _grab; _ghost.InvalidateArrange();
        var destination = _sourceIndex;
        // Capture continues outside the window. An invalid position cancels the
        // prospective drop but the floating row continues following the pointer.
        if (new Rect(_list.RenderSize).Contains(position) && _slots.Any(s => position.Y >= s.Bounds.Top && position.Y <= s.Bounds.Bottom))
            destination = _slots.Count(s => s.Item != _source && position.Y >= s.Bounds.Top + s.Bounds.Height / 2);
        Target = destination == _sourceIndex ? null : _slots[destination].Item;
        if (_destination == destination) return;
        _destination = destination;
        var height = _source.ActualHeight + _source.Margin.Top + _source.Margin.Bottom;
        for (var i = 0; i < _slots.Count; i++)
        {
            var shift = i > _sourceIndex && i <= destination ? -height : i < _sourceIndex && i >= destination ? height : 0;
            var transform = _slots[i].Offset;
            var current = transform.Y;
            transform.BeginAnimation(TranslateTransform.YProperty, null); transform.Y = shift;
            if (!ThemeService.ReduceMotion)
                transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(current, shift, TimeSpan.FromMilliseconds(180))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
    }

    public void Dispose()
    {
        _layer.Remove(_ghost);
        _source.SetCurrentValue(UIElement.OpacityProperty, _sourceOpacity);
        foreach (var slot in _slots)
        {
            slot.Offset.BeginAnimation(TranslateTransform.YProperty, null);
            slot.Item.SetCurrentValue(UIElement.RenderTransformProperty, slot.Original);
        }
    }

    private sealed class DragAdorner : Adorner
    {
        private readonly Border _row;
        private readonly Size _size;
        public Point Position { get; set; }
        public DragAdorner(ListBox list, ListBoxItem source) : base(list)
        {
            _size = source.RenderSize; IsHitTestVisible = false; Opacity = 0.72;
            _row = new Border
            {
                Background = (Brush)list.FindResource("PaperBrush"), BorderBrush = (Brush)list.FindResource("AccentInkBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = source.Padding,
                Child = new ContentPresenter { Content = source.Content, ContentTemplate = source.ContentTemplate ?? list.ItemTemplate }
            };
            TextElement.SetForeground(_row, source.Foreground); TextElement.SetFontFamily(_row, source.FontFamily);
            TextElement.SetFontSize(_row, source.FontSize); TextElement.SetFontWeight(_row, source.FontWeight);
            AddVisualChild(_row);
        }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => index == 0 ? _row : throw new ArgumentOutOfRangeException(nameof(index));
        protected override Size MeasureOverride(Size constraint) { _row.Measure(_size); return AdornedElement.RenderSize; }
        protected override Size ArrangeOverride(Size finalSize) { _row.Arrange(new Rect(Position, _size)); return finalSize; }
    }
}
