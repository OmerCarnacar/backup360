using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Backup360.Core;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

/// <summary>Çalışan bir işin canlı kartı: aşama hattı, ilerleme, hız ve kalan süre.</summary>
public sealed class ActivityView : UserControl
{
    private static readonly Dictionary<string, (string Stage, string Label)[]> Pipelines = new()
    {
        ["archive"] = new[] { ("archive", "Sıkıştır"), ("verify", "Doğrula"), ("upload", "Gönder"), ("retention", "Temizle") },
        ["dated"] = new[] { ("scan", "Tara"), ("archive", "Sıkıştır"), ("verify", "Doğrula"), ("upload", "Gönder"), ("retention", "Temizle") },
        ["database"] = new[] { ("scan", "Listele"), ("sqlbackup", "DB yedeği"), ("sqlverify", "DB doğrula"), ("sqlrestore", "Geri yükle"), ("archive", "Sıkıştır"), ("verify", "Doğrula"), ("upload", "Gönder"), ("retention", "Temizle") },
        ["mirror"] = new[] { ("scan", "Hazırla"), ("upload", "Gönder") },
        ["sync"] = new[] { ("scan", "Karşılaştır"), ("upload", "Farkları aktar") },
        ["audit"] = new[] { ("scan", "Hedefleri karşılaştır"), ("audit", "İndir ve aç") },
        ["move"] = new[] { ("scan", "Yeni dosya bul"), ("move", "Taşı") },
    };

    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _step = new() { FontSize = 12, Opacity = 0.7 };
    private readonly TextBlock _item = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _percent = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _stats = new() { FontSize = 12, Opacity = 0.75 };
    private readonly TextBox _log = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, IsReadOnly = true, Height = 118, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _active = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _bar = new() { Height = 6, Maximum = 100 };
    private readonly Grid _pipeline = new() { Margin = new Thickness(0, 14, 0, 12) };
    private string _builtFor = "";

    public string Key { get; }

    public ActivityView(LiveState state)
    {
        Key = state.Key;
        Margin = new Thickness(0, 0, 0, 12);
        var stop = new WUI.Button { Content = "Durdur", Icon = Ui.Symbol(WUI.SymbolRegular.Stop24), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        stop.Click += (_, _) => Launcher.Cancel(state.Kind, state.RefId);

        var head = new Grid();
        var titles = new StackPanel();
        titles.Children.Add(_title);
        titles.Children.Add(_step);
        head.Children.Add(titles);
        head.Children.Add(stop);

        var itemRow = new Grid { Margin = new Thickness(0, 8, 0, 4) };
        itemRow.ColumnDefinitions.Add(new ColumnDefinition());
        itemRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        itemRow.Children.Add(_item);
        Grid.SetColumn(_percent, 1);
        itemRow.Children.Add(_percent);

        var body = new StackPanel();
        foreach (var child in new UIElement[] { head, _pipeline, _bar, itemRow, _stats, _active, _log }) body.Children.Add(child);
        Content = new WUI.Card { Content = body, Padding = new Thickness(18) };
        Update(state);
    }

    public void Update(LiveState s)
    {
        _title.Text = s.Name;
        _step.Text = (s.Steps > 0 ? $"Adım {s.Step}/{s.Steps}  ·  " : "") + $"Başlangıç {s.Started}";
        _item.Text = s.Item;
        _percent.Text = s.Percent >= 0 ? $"%{s.Percent}" : "Çalışıyor";
        _bar.IsIndeterminate = s.Percent < 0;
        if (s.Percent >= 0) _bar.BeginAnimation(RangeBase_Value, new DoubleAnimation(s.Percent, TimeSpan.FromMilliseconds(350)));

        var parts = new List<string>();
        if (s.Speed > 0) parts.Add($"Hız {Fmt.Size(s.Speed)}/sn");
        if (s.Eta >= 0) parts.Add($"Kalan {Fmt.Duration(s.Eta)}");
        if (s.Total > 0) parts.Add($"{Fmt.Size(s.Done)} / {Fmt.Size(s.Total)}");
        else if (s.Done > 0) parts.Add(Fmt.Size(s.Done));
        if (s.FilesTotal > 0) parts.Add($"Dosya {s.Files}/{s.FilesTotal}");
        else if (s.Files > 0) parts.Add($"{s.Files} dosya");
        _stats.Text = string.Join("   ·   ", parts);
        _active.Text = string.Join("\n", s.Transfers);
        _active.Visibility = s.Transfers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var text = string.Join("\n", s.Log);
        if (text != _log.Text)
        {
            _log.Text = text;
            _log.ScrollToEnd();
        }

        var signature = s.Mode + "|" + s.Stage;
        if (signature != _builtFor)
        {
            _builtFor = signature;
            BuildPipeline(s);
        }
    }

    private static readonly DependencyProperty RangeBase_Value = System.Windows.Controls.Primitives.RangeBase.ValueProperty;

    private void BuildPipeline(LiveState s)
    {
        var nodes = Pipelines.GetValueOrDefault(s.Mode) ?? Pipelines["archive"];
        var active = Math.Max(0, Array.FindIndex(nodes, n => n.Stage == s.Stage));
        _pipeline.Children.Clear();
        _pipeline.ColumnDefinitions.Clear();
        var accent = (Brush)FindResourceSafe("AccentFillColorDefaultBrush", Fmt.Indigo);

        for (var i = 0; i < nodes.Length; i++)
        {
            _pipeline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var done = i < active;
            var isActive = i == active;
            var circle = new Grid { Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Center };
            if (isActive) circle.Children.Add(Pulse(accent));
            circle.Children.Add(new Ellipse { Width = 26, Height = 26, Fill = done ? Fmt.Success : isActive ? accent : Fmt.Neutral, Opacity = done || isActive ? 1 : 0.35 });
            circle.Children.Add(done
                ? new WUI.SymbolIcon { Symbol = WUI.SymbolRegular.Checkmark24, FontSize = 14, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : new TextBlock { Text = (i + 1).ToString(), FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var stack = new StackPanel { MinWidth = 76 };
            stack.Children.Add(circle);
            stack.Children.Add(new TextBlock { Text = nodes[i].Label, FontSize = 12, FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), Opacity = done || isActive ? 1 : 0.6 });
            Grid.SetColumn(stack, _pipeline.ColumnDefinitions.Count - 1);
            _pipeline.Children.Add(stack);

            if (i == nodes.Length - 1) break;
            _pipeline.ColumnDefinitions.Add(new ColumnDefinition());
            FrameworkElement line = isActive
                ? new ProgressBar { IsIndeterminate = true, Height = 3 }
                : new Border { Height = 3, CornerRadius = new CornerRadius(2), Background = done ? accent : Fmt.Neutral, Opacity = done ? 1 : 0.3 };
            line.VerticalAlignment = VerticalAlignment.Top;
            line.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetColumn(line, _pipeline.ColumnDefinitions.Count - 1);
            _pipeline.Children.Add(line);
        }
    }

    private object FindResourceSafe(string key, object fallback) => TryFindResource(key) ?? fallback;

    private static Ellipse Pulse(Brush brush)
    {
        var halo = new Ellipse { Width = 30, Height = 30, Fill = brush, Opacity = 0.25, RenderTransformOrigin = new Point(0.5, 0.5) };
        var scale = new ScaleTransform(1, 1);
        halo.RenderTransform = scale;
        var grow = new DoubleAnimation(0.85, 1.25, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        return halo;
    }
}
