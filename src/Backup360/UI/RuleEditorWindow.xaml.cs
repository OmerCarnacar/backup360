using System.Windows;
using Backup360.Core;
using Microsoft.Win32;

namespace Backup360.UI;

public partial class RuleEditorWindow
{
    private static readonly string[] Conflicts = { "rename", "skip", "overwrite" };
    private readonly MoveRule _rule;
    private readonly ScheduleEditor _schedule;

    /// <summary>Kaynak değiştiyse veya kural yeniyse mevcut içerik baz alınmalıdır.</summary>
    public bool NeedsBaseline { get; private set; }

    public RuleEditorWindow(MoveRule rule)
    {
        _rule = rule;
        InitializeComponent();
        Ui.FitToScreen(this);
        Title = Bar.Title = rule.Id == 0 ? "Yeni taşıma kuralı" : "Kuralı düzenle";
        NameBox.Text = rule.Name;
        foreach (var g in Db.Rules().Select(x => x.Group).Append("Genel").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)) GroupBox.Items.Add(g);
        GroupBox.Text = rule.Group;
        Source.Text = rule.Source;
        Target.Text = rule.Target;
        Pattern.Text = rule.Pattern;
        Recursive.IsChecked = rule.Recursive;
        OnlyNew.IsChecked = rule.OnlyNew;
        Conflict.SelectedIndex = Math.Max(0, Array.IndexOf(Conflicts, rule.Conflict));
        Stable.Value = rule.StableSeconds;
        _schedule = new ScheduleEditor(rule.Schedule);
        ScheduleHost.Content = _schedule;
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var box = (string)((FrameworkElement)sender).Tag == "source" ? Source : Target;
        var d = new OpenFolderDialog { InitialDirectory = Directory.Exists(box.Text) ? box.Text : "" };
        if (d.ShowDialog(this) == true) box.Text = d.FolderName;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var schedule = _schedule.Value;
            NeedsBaseline = _rule.Id == 0
                || !string.Equals(_rule.Source, Source.Text.Trim(), StringComparison.OrdinalIgnoreCase)
                || _rule.OnlyNew != (OnlyNew.IsChecked == true)
                || _rule.Recursive != (Recursive.IsChecked == true);
            _rule.Name = NameBox.Text.Trim();
            _rule.Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? "Genel" : GroupBox.Text.Trim();
            _rule.Source = Source.Text.Trim();
            _rule.Target = Target.Text.Trim();
            _rule.Pattern = string.IsNullOrWhiteSpace(Pattern.Text) ? "*" : Pattern.Text.Trim();
            _rule.Recursive = Recursive.IsChecked == true;
            _rule.OnlyNew = OnlyNew.IsChecked == true;
            _rule.Conflict = Conflicts[Math.Max(0, Conflict.SelectedIndex)];
            _rule.StableSeconds = (int)(Stable.Value ?? 0);
            _rule.Schedule = schedule;
            Validation.Check(_rule);
            DialogResult = true;
        }
        catch (ArgumentException x)
        {
            await Ui.Info(x.Message, "Eksik bilgi");
        }
    }
}
