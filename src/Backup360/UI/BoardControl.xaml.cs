using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

public partial class BoardControl
{
    public BoardControl() => InitializeComponent();

    public void Setup(string title, string hint, string empty)
    {
        TitleText.Text = title;
        HintText.Text = hint;
        EmptyText.Text = empty;
    }

    public void AddTop(string label, WUI.SymbolRegular icon, Action click, bool primary = false)
    {
        var button = new WUI.Button
        {
            Content = label,
            Icon = Ui.Symbol(icon),
            Appearance = primary ? WUI.ControlAppearance.Primary : WUI.ControlAppearance.Secondary,
            Margin = new Thickness(8, 0, 0, 0),
        };
        button.Click += (_, _) => click();
        TopActions.Children.Add(button);
    }

    public void Notify(string message, WUI.InfoBarSeverity severity)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    public void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetGroups(IEnumerable<IGrouping<string, CardVm>> groups, Func<string, CardAction?>? groupAction = null)
    {
        var list = groups.Select(g =>
        {
            var action = groupAction?.Invoke(g.Key);
            return new GroupVm(g.Key, g.Count(), new ObservableCollection<CardVm>(g), action, action != null ? Visibility.Visible : Visibility.Collapsed);
        }).ToList();
        Groups.ItemsSource = list;
        Empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
