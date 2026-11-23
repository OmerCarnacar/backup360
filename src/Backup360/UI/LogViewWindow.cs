using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Backup360.Core;

namespace Backup360.UI;

/// <summary>Tek bir çalışmanın günlüğü: siyah üzerine yeşil, hata satırları kırmızı, uyarılar sarı.</summary>
public sealed class LogViewWindow : Window
{
    private static readonly Brush Back = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x0C));
    private static readonly Brush Plain = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush Error = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15));
    private static readonly Brush Head = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));

    public LogViewWindow(string title, string file)
    {
        Title = "Çalışma günlüğü · " + title;
        Width = 1100;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Back;

        string[] lines;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        }
        catch (IOException e) { lines = new[] { "Günlük okunamadı: " + e.Message }; }

        var doc = new System.Windows.Documents.FlowDocument { PagePadding = new Thickness(14), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, Background = Back };
        foreach (var line in lines.Where(l => l.Length > 0))
        {
            var text = line.Length > 21 ? line[21..] : line;
            var brush = Problems.Classify(text) is { } p ? (p.Level == Problems.Error ? Error : Warn)
                : text.StartsWith("Özet:", StringComparison.Ordinal) || text.StartsWith("===", StringComparison.Ordinal) || text.StartsWith('"') ? Head
                : Plain;
            doc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(line)) { Foreground = brush, Margin = new Thickness(0) });
        }

        var view = new FlowDocumentScrollViewer { Document = doc, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var open = new Button { Content = "Not Defteri'nde aç", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
        open.Click += (_, _) => System.Diagnostics.Process.Start("notepad.exe", $"\"{file}\"");
        var close = new Button { Content = "Kapat", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        close.Click += (_, _) => Close();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        bar.Children.Add(open);
        bar.Children.Add(close);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(view);
        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);
        Content = grid;
    }
}
