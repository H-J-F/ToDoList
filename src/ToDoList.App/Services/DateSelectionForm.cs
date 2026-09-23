using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Calendar = System.Windows.Controls.Calendar;
using System.Windows.Data;
using ToDoList.Core;
using Wpf.Ui.Controls;
using CalendarDatePicker = Wpf.Ui.Controls.CalendarDatePicker;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ToDoList.App.Services;

/// <summary>Composes WPF-UI date pickers for the application's date scopes.</summary>
internal sealed class DateSelectionForm
{
    private readonly ComboBox _scope = new() { DisplayMemberPath = "Label", SelectedValuePath = "Value", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 32 };
    private readonly CalendarDatePicker _date = Picker("DateSelection");
    private readonly CalendarDatePicker _from = Picker("DateFrom");
    private readonly CalendarDatePicker _until = Picker("DateUntil");
    private readonly Grid _single = new();
    private readonly Grid _range = new();
    private readonly bool _calendarOnly;
    public FrameworkElement View { get; }

    public DateSelectionForm(bool calendarOnly, DateSelection initial)
    {
        _calendarOnly = calendarOnly;
        var choices = new[] { (DateScope.Any, "不限时间"), (DateScope.Year, "整年"), (DateScope.Month, "整月"), (DateScope.Day, "整日"), (DateScope.Range, "自定义范围") };
        _scope.ItemsSource = choices.Where(c => !calendarOnly || c.Item1 is DateScope.Year or DateScope.Month or DateScope.Day).Select(c => new Choice(c.Item1, c.Item2)).ToArray();
        AutomationProperties.SetAutomationId(_scope, "DateScope");

        _single.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _single.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _single.Children.Add(new TextBlock { Text = "选择所属日期", Margin = new(0, 0, 0, 6) });
        Grid.SetRow(_date, 1); _single.Children.Add(_date);

        _range.RowDefinitions.Add(new() { Height = GridLength.Auto }); _range.RowDefinitions.Add(new() { Height = GridLength.Auto });
        AddRangeColumn("开始日期", _from, 0); AddRangeColumn("结束日期（含当天）", _until, 1);

        var panel = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, UseLayoutRounding = true };
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.Children.Add(_scope);
        Grid.SetRow(_single, 1); _single.Margin = new(0, 10, 0, 0); panel.Children.Add(_single);
        Grid.SetRow(_range, 1); _range.Margin = new(0, 10, 0, 0); panel.Children.Add(_range);
        View = panel;
        _scope.SelectionChanged += (_, _) => UpdateVisibility();
        SetSelection(initial);
    }

    private static CalendarDatePicker Picker(string id)
    {
        var text = new TextBlock();
        var picker = new CalendarDatePicker { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0), IsTodayHighlighted = true, MinHeight = 32 };
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(CalendarDatePicker.Date)) { Source = picker, StringFormat = "{0:yyyy年M月d日}", TargetNullValue = "选择日期" });
        // CalendarDatePicker creates a separate Popup; stretching its button does
        // not stretch the Calendar. Scope the library calendar style to this picker.
        var calendarStyle = new Style(typeof(Calendar), (Style)Application.Current.FindResource("DefaultCalendarStyle"));
        calendarStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, new Binding(nameof(FrameworkElement.ActualWidth)) { Source = picker }));
        calendarStyle.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        calendarStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        // Stretch the seven-column layout, not each day's circular visual.
        var dayStyle = new Style(typeof(System.Windows.Controls.Primitives.CalendarDayButton),
            (Style)Application.Current.FindResource("DefaultCalendarDayButtonStyle"));
        dayStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, 40d));
        dayStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 40d));
        dayStyle.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        dayStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        calendarStyle.Setters.Add(new Setter(Calendar.CalendarDayButtonStyleProperty, dayStyle));
        picker.Resources[typeof(Calendar)] = calendarStyle;
        AutomationProperties.SetAutomationId(picker, id);
        return picker;
    }

    private void AddRangeColumn(string label, CalendarDatePicker picker, int column)
    {
        var panel = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, UseLayoutRounding = true }; panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 0, 0, 6) });
        Grid.SetRow(picker, 1); panel.Children.Add(picker); Grid.SetRow(panel, column); panel.Margin = new Thickness(0, column == 0 ? 0 : 10, 0, 0); _range.Children.Add(panel);
    }

    public void SetSelection(DateSelection initial)
    {
        _scope.SelectedValue = _calendarOnly && initial.Scope is DateScope.Any or DateScope.Range ? DateScope.Day : initial.Scope;
        _date.Date = initial.Start.Date; _from.Date = initial.Start.Date; _until.Date = initial.End.Date;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var scope = (_scope.SelectedItem as Choice)?.Value ?? DateScope.Day;
        _single.Visibility = scope is DateScope.Year or DateScope.Month or DateScope.Day ? Visibility.Visible : Visibility.Collapsed;
        _range.Visibility = scope == DateScope.Range ? Visibility.Visible : Visibility.Collapsed;
    }

    public DateSelection Value
    {
        get
        {
            var scope = ((Choice)_scope.SelectedItem).Value;
            if (scope == DateScope.Any) return DateSelection.Any;
            var start = scope == DateScope.Range ? _from.Date : _date.Date;
            var end = scope == DateScope.Range ? _until.Date : start;
            if (start == null) throw new ArgumentException(scope == DateScope.Range ? "请选择开始日期。" : "请选择日期。");
            if (end == null) throw new ArgumentException("请选择结束日期。");
            var selection = new DateSelection(scope, start.Value.Date, end.Value.Date); _ = selection.Bounds(); return selection;
        }
    }

    private sealed record Choice(DateScope Value, string Label);
}
