using System.Windows;
using System.Windows.Controls;
using ToDoList.Core;

namespace ToDoList.App.Controls;

public sealed class SyncIntervalSlider : Slider
{
    private Canvas? _labels;
    public SyncIntervalSlider()
    {
        Minimum = 0; Maximum = CloudSyncSchedule.Intervals.Count - 1;
        SizeChanged += (_, _) => PositionLabels(); SmallChange = LargeChange = 1;
    }
    public override void OnApplyTemplate()
    {
        if (_labels != null) _labels.SizeChanged -= LabelsSizeChanged;
        base.OnApplyTemplate(); _labels = GetTemplateChild("PART_Labels") as Canvas;
        if (_labels == null) return;
        _labels.Children.Clear();
        foreach (var interval in CloudSyncSchedule.Intervals)
        {
            var label = new TextBlock { Text = interval.ToString(), FontSize = 11 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); _labels.Children.Add(label);
        }
        _labels.SizeChanged += LabelsSizeChanged; PositionLabels();
    }
    private void LabelsSizeChanged(object sender, SizeChangedEventArgs e) => PositionLabels();
    private void PositionLabels()
    {
        if (_labels == null) return;
        var width = _labels.ActualWidth;
        for (var i = 0; i < _labels.Children.Count; i++)
        {
            var label = (TextBlock)_labels.Children[i]; label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // Track reserves half the 14 DIP thumb at both ends, just like TickBar.
            Canvas.SetLeft(label, 7 + Math.Max(0, width - 14) * i / Math.Max(1, _labels.Children.Count - 1) - label.DesiredSize.Width / 2);
        }
    }
}
