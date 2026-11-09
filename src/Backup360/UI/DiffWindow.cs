using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Backup360.Core;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

/// <summary>Eşitlemeden önce kaynak ve hedef arasındaki dosya farklarını gösterir.</summary>
public sealed class DiffWindow : WUI.FluentWindow
{
    private sealed record Row(string Kind, string File, string Destination);

    private readonly Job _job;
    private readonly TextBlock _summary = new() { Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None, CanUserAddRows = false };
    private readonly WUI.ProgressRing _ring = new() { IsIndeterminate = true, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0) };

    public DiffWindow(Job job)
    {
        _job = job;
        Title = $"Farklar: {job.Name}";
        Width = 900;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WUI.WindowBackdropType.Mica;

        _grid.Columns.Add(new DataGridTextColumn { Header = "Durum", Binding = new Binding(nameof(Row.Kind)), Width = 120 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Dosya", Binding = new Binding(nameof(Row.File)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Hedef", Binding = new Binding(nameof(Row.Destination)), Width = 240 });

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(new WUI.TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false });
        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 18) };
        DockPanel.SetDock(_summary, Dock.Top);
        DockPanel.SetDock(_ring, Dock.Top);
        body.Children.Add(_summary);
        body.Children.Add(_ring);
        body.Children.Add(_grid);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _summary.Text = "Kaynak ve hedef karşılaştırılıyor...";
        try
        {
            var rows = new List<Row>();
            var all = new List<DiffEntry>();
            foreach (var pair in SyncPlanner.Pairs(_job))
            {
                var diff = await SyncPlanner.DiffAsync(pair, _job.Excludes, CancellationToken.None);
                all.AddRange(diff.Items);
                rows.AddRange(diff.Items.Where(i => i.Kind != SyncPlanner.Same).Select(i => new Row(i.Kind, i.Path, $"{pair.Target}:{pair.Folder}")));
            }
            var result = new DiffResult(all);
            _grid.ItemsSource = rows.OrderBy(r => r.Kind).ThenBy(r => r.File).ToList();
            _summary.Text = $"Yeni: {result.Count(SyncPlanner.New)}   ·   Değişen: {result.Count(SyncPlanner.Changed)}   ·   Hedefte fazla: {result.Count(SyncPlanner.Extra)}   ·   Aynı: {result.Count(SyncPlanner.Same)}";
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            _summary.Text = "Karşılaştırılamadı: " + e.Message;
        }
        _ring.Visibility = Visibility.Collapsed;
    }
}
