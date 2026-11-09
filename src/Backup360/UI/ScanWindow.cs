using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Backup360.Core;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

/// <summary>SQL Server örneklerini tarar ve seçileni düzenleyiciye verir.</summary>
public sealed class ScanWindow : WUI.FluentWindow
{
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.All, BorderThickness = new Thickness(1), CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly ProgressBar _bar = new() { Height = 5, Maximum = 100, Margin = new Thickness(0, 0, 0, 10) };
    private readonly CancellationTokenSource _cts = new();

    public SqlInstance? Selected { get; private set; }

    public ScanWindow(bool start = true)
    {
        Title = "SQL Server örneklerini tara";
        Width = 760;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WUI.WindowBackdropType.Mica;

        foreach (var property in new[] { DataGrid.HorizontalGridLinesBrushProperty, DataGrid.VerticalGridLinesBrushProperty, BorderBrushProperty })
            _grid.SetResourceReference(property, "ControlStrongStrokeColorDefaultBrush");
        void Column(string header, string path, DataGridLength width) => _grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width });
        Column("Sunucu", nameof(SqlInstance.Host), new DataGridLength(1, DataGridLengthUnitType.Star));
        Column("Örnek", nameof(SqlInstance.InstanceLabel), 140);
        Column("Port", nameof(SqlInstance.PortText), 80);
        Column("Sürüm", nameof(SqlInstance.Version), 120);
        Column("Bulunduğu yer", nameof(SqlInstance.Source), 130);
        _grid.MouseDoubleClick += (_, _) => Choose();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new WUI.TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false });
        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 8) };
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(_bar, Dock.Top);
        body.Children.Add(_status);
        body.Children.Add(_bar);
        body.Children.Add(_grid);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 4, 20, 16) };
        var cancel = new WUI.Button { Content = "Vazgeç", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var pick = new WUI.Button { Content = "Seç", Appearance = WUI.ControlAppearance.Primary, MinWidth = 110 };
        pick.Click += (_, _) => Choose();
        buttons.Children.Add(cancel);
        buttons.Children.Add(pick);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;

        Ui.FitToScreen(this);
        Closed += (_, _) => _cts.Cancel();
        if (start) Loaded += async (_, _) => await ScanAsync();
    }

    private void Choose()
    {
        if (_grid.SelectedItem is not SqlInstance item) return;
        Selected = item;
        DialogResult = true;
    }

    private async Task ScanAsync()
    {
        _status.Text = "Bu bilgisayar ve yerel ağ taranıyor...";
        try
        {
            var found = await SqlDiscovery.ScanAsync(p => Dispatcher.Invoke(() => _bar.Value = p), _cts.Token);
            _grid.ItemsSource = found;
            _status.Text = found.Count == 0 ? "SQL Server bulunamadı. Sunucu adını elle yazabilirsiniz." : $"{found.Count} SQL Server örneği bulundu. Birini seçin.";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _status.Text = "Tarama tamamlanamadı: " + e.Message;
        }
        _bar.Visibility = Visibility.Collapsed;
    }
}
