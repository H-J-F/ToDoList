using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ToDoList.Core;

namespace ToDoList.App.Controls;

/// <summary>Shared calendar and report date selection, using local calendar days.</summary>
public sealed class DateRangePicker : UserControl
{
    private readonly ComboBox _scope = new() { DisplayMemberPath = "Label", SelectedValuePath = "Value", MinWidth = 120 };
    private readonly Wpf.Ui.Controls.TextBox _year = new() { Width = 90, MaxLength = 4, ToolTip = "年份（1–9998）" };
    private readonly ComboBox _month = new() { Width = 85, Margin = new(8, 0, 0, 0), ItemsSource = Enumerable.Range(1, 12).Select(m => m + "月") };
    private readonly System.Windows.Controls.Calendar _day = new() { SelectionMode = CalendarSelectionMode.SingleDate };
    private readonly DatePicker _from = new() { MinWidth = 135, SelectedDateFormat = DatePickerFormat.Short };
    private readonly DatePicker _until = new() { MinWidth = 135, SelectedDateFormat = DatePickerFormat.Short };
    private readonly StackPanel _yearMonth = new() { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 0) };
    private readonly StackPanel _range = new() { Margin = new(0, 8, 0, 0) };
    private readonly bool _calendarOnly;
    public DateRangePicker(bool calendarOnly, DateSelection initial)
    {
        _calendarOnly = calendarOnly;
        var choices = new[] { (DateScope.Any, "不限时间"), (DateScope.Year, "整年"), (DateScope.Month, "整月"), (DateScope.Day, "整日"), (DateScope.Range, "自定义范围") };
        _scope.ItemsSource = choices.Where(c => !calendarOnly || c.Item1 is DateScope.Year or DateScope.Month or DateScope.Day).Select(c => new Choice(c.Item1, c.Item2)).ToArray();
        _yearMonth.Children.Add(_year); _yearMonth.Children.Add(_month);
        _range.Children.Add(new TextBlock { Text = "开始日期" }); _range.Children.Add(_from);
        _range.Children.Add(new TextBlock { Text = "结束日期（含当天）", Margin = new(0, 8, 0, 0) }); _range.Children.Add(_until);
        var panel = new StackPanel(); panel.Children.Add(_scope); panel.Children.Add(_yearMonth); panel.Children.Add(_day); panel.Children.Add(_range); Content = panel;
        _scope.SelectionChanged += (_, _) => UpdateVisibility();
        SetSelection(initial);
    }
    public void SetSelection(DateSelection initial)
    {
        _scope.SelectedValue = _calendarOnly && initial.Scope is DateScope.Any or DateScope.Range ? DateScope.Day : initial.Scope;
        _year.Text = initial.Start.Year.ToString(CultureInfo.InvariantCulture); _month.SelectedIndex = initial.Start.Month - 1;
        _day.SelectedDate = initial.Start.Date; _day.DisplayDate = initial.Start.Date;
        _from.SelectedDate = initial.Start.Date; _until.SelectedDate = initial.End.Date;
        UpdateVisibility();
    }
    private void UpdateVisibility()
    {
        var scope = (_scope.SelectedItem as Choice)?.Value ?? DateScope.Day;
        _yearMonth.Visibility = scope is DateScope.Year or DateScope.Month ? Visibility.Visible : Visibility.Collapsed;
        _month.Visibility = scope == DateScope.Month ? Visibility.Visible : Visibility.Collapsed;
        _day.Visibility = scope == DateScope.Day ? Visibility.Visible : Visibility.Collapsed;
        _range.Visibility = scope == DateScope.Range ? Visibility.Visible : Visibility.Collapsed;
    }
    public DateSelection Value
    {
        get
        {
            var scope = ((Choice)_scope.SelectedItem).Value;
            if (scope == DateScope.Any) return DateSelection.Any;
            DateTime start, end;
            if (scope is DateScope.Year or DateScope.Month)
            {
                if (!int.TryParse(_year.Text, out var year) || year is < 1 or > 9998) throw new ArgumentException("请输入 1–9998 之间的年份。");
                start = end = new(year, scope == DateScope.Year ? 1 : _month.SelectedIndex + 1, 1);
            }
            else if (scope == DateScope.Day) start = end = _day.SelectedDate ?? throw new ArgumentException("请选择日期。");
            else
            {
                // DatePicker owns parsing and validation; localized display strings can
                // include weekday text which DateTime.TryParse cannot round-trip.
                start = _from.SelectedDate ?? throw new ArgumentException("请选择开始日期。");
                end = _until.SelectedDate ?? throw new ArgumentException("请选择结束日期。");
            }
            var selection = new DateSelection(scope, start, end); _ = selection.Bounds(); return selection;
        }
    }
    private sealed record Choice(DateScope Value, string Label);
}
