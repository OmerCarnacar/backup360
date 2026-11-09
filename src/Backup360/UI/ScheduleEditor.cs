using System.Windows;
using System.Windows.Controls;
using Backup360.Core;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

/// <summary>Zamanlama seçici.</summary>
public sealed class ScheduleEditor : StackPanel
{
    private static readonly (string Key, string Label)[] Types =
        { ("none", "Elle (zamanlama yok)"), ("minutes", "Her N dakikada"), ("hourly", "Her N saatte"), ("daily", "Her gün"), ("weekly", "Haftanın günleri") };
    private static readonly (string Key, string Label)[] Days =
        { ("Monday", "Pzt"), ("Tuesday", "Sal"), ("Wednesday", "Çar"), ("Thursday", "Per"), ("Friday", "Cum"), ("Saturday", "Cmt"), ("Sunday", "Paz") };

    private readonly ComboBox _type = new() { Width = 220 };
    private readonly WUI.NumberBox _interval = new() { Minimum = 1, Maximum = 1440, Width = 130, Margin = new Thickness(10, 0, 0, 0) };
    private readonly WUI.TextBox _time = new() { Width = 100, Margin = new Thickness(10, 0, 0, 0), PlaceholderText = "02:00" };
    private readonly WrapPanel _days = new() { Margin = new Thickness(0, 8, 0, 0) };

    public ScheduleEditor(Schedule s)
    {
        foreach (var t in Types) _type.Items.Add(t.Label);
        _type.SelectedIndex = Math.Max(0, Array.FindIndex(Types, t => t.Key == s.Type));
        _interval.Value = Math.Clamp(s.Interval, 1, 1440);
        _time.Text = s.Time;
        var chosen = s.Days.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var d in Days) _days.Children.Add(new CheckBox { Content = d.Label, Tag = d.Key, MinWidth = 72, IsChecked = chosen.Contains(d.Key) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_type);
        row.Children.Add(_interval);
        row.Children.Add(_time);
        Children.Add(row);
        Children.Add(_days);
        _type.SelectionChanged += (_, _) => Sync();
        Sync();
    }

    private string Selected => Types[_type.SelectedIndex].Key;

    private void Sync()
    {
        _interval.Visibility = Selected is "minutes" or "hourly" ? Visibility.Visible : Visibility.Collapsed;
        _time.Visibility = Selected is "daily" or "weekly" ? Visibility.Visible : Visibility.Collapsed;
        _days.Visibility = Selected == "weekly" ? Visibility.Visible : Visibility.Collapsed;
    }

    public Schedule Value
    {
        get
        {
            var days = _days.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            if (Selected == "weekly" && days.Count == 0) throw new ArgumentException("En az bir gün seçin.");
            if (Selected is "daily" or "weekly" && !TimeSpan.TryParse(_time.Text, out _)) throw new ArgumentException("Saati SS:DD biçiminde yazın, örn: 02:00.");
            return new Schedule { Type = Selected, Interval = (int)(_interval.Value ?? 1), Time = TimeSpan.TryParse(_time.Text, out var t) ? t.ToString(@"hh\:mm") : "02:00", Days = string.Join(",", days) };
        }
    }
}
