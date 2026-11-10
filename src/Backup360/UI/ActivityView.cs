using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Backup360.Core;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

/// <summary>
/// Çalışan bir işin kompakt canlı kartı: aşama şeridi, ilerleme, altı sabit gösterge ve
/// yeni satır geldikçe yukarı kayan akış alanı.
/// </summary>
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

    public const int FlowLines = 6;
    private static readonly Brush ConsoleBack = Fmt.Brush(11, 15, 12), ConsoleEdge = Fmt.Brush(31, 59, 42), ConsoleText = Fmt.Brush(74, 222, 128), ConsoleBright = Fmt.Brush(187, 247, 208);
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _item = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _percent = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
    private readonly ProgressBar _bar = new() { Height = 5, Maximum = 100, Margin = new Thickness(0, 6, 0, 6) };
    private readonly WrapPanel _stages = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock[] _values = new TextBlock[6];
    private readonly TextBlock _flow = new() { FontFamily = Mono, FontSize = 11.5, LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Foreground = ConsoleText, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TranslateTransform _slide = new();
    private readonly LiveCardVm _timing = new();
    private string _builtFor = "", _flowText = "", _lastLine = "";

    public string Key { get; }

    public ActivityView(LiveState state)
    {
        Key = state.Key;
        Margin = new Thickness(0, 0, 8, 8);

        var stop = new WUI.Button { Content = "Durdur", Icon = Ui.Symbol(WUI.SymbolRegular.Stop24), Padding = new Thickness(9, 3, 9, 3), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        stop.Click += (_, _) => Launcher.Cancel(state.Kind, state.RefId);

        var head = new Grid();
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) head.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var dot = PulseDot();
        head.Children.Add(dot);
        Place(head, _title, 1);
        Place(head, _percent, 2);
        Place(head, stop, 3);

        var metrics = new UniformGrid { Columns = 6, Margin = new Thickness(0, 0, 0, 6) };
        var labels = new[] { "Kalan süre", "Geçen süre", "Hız", "Boyut", "Dosya", "Adım" };
        for (var i = 0; i < labels.Length; i++)
        {
            _values[i] = new TextBlock { Text = "—", FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var cell = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
            cell.Children.Add(new TextBlock { Text = labels[i], FontSize = 10.5, Opacity = 0.65 });
            cell.Children.Add(_values[i]);
            metrics.Children.Add(cell);
        }

        _flow.RenderTransform = _slide;
        var console = new Border
        {
            Background = ConsoleBack, BorderBrush = ConsoleEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7), Height = FlowLines * 15 + 16, ClipToBounds = true, Child = _flow,
        };

        var body = new StackPanel();
        foreach (var child in new UIElement[] { head, _stages, _bar, _item, metrics, console }) body.Children.Add(child);
        _item.Margin = new Thickness(0, 0, 0, 6);
        Content = new WUI.Card { Content = body, Padding = new Thickness(14, 10, 14, 12) };
        Update(state);
    }

    private static void Place(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    /// <summary>İşin çalıştığını gösteren, sürekli atan nokta.</summary>
    private static Grid PulseDot()
    {
        var holder = new Grid { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var halo = new Ellipse { Width = 16, Height = 16, Fill = Fmt.Success, Opacity = 0.3, RenderTransformOrigin = new Point(0.5, 0.5) };
        var scale = new ScaleTransform(1, 1);
        halo.RenderTransform = scale;
        var grow = new DoubleAnimation(0.5, 1.15, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        holder.Children.Add(halo);
        holder.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Fmt.Success });
        return holder;
    }

    /// <summary>Akış alanının satırları: önce süren aktarımlar, altında son kayıtlar.</summary>
    public static (List<string> Active, List<string> Log) FlowOf(LiveState s)
    {
        var active = s.Transfers.Take(3).ToList();
        return (active, s.Log.TakeLast(FlowLines - active.Count).ToList());
    }

    public void Update(LiveState s)
    {
        var now = DateTime.Now;
        _title.Text = s.Name;
        _item.Text = s.Item;
        _item.ToolTip = s.Item;
        _percent.Text = s.Percent >= 0 ? $"%{s.Percent}" : "";
        _bar.IsIndeterminate = s.Percent < 0;
        if (s.Percent >= 0) _bar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(s.Percent, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        _values[0].Text = Fmt.Clock(_timing.EstimateEta(s, now));
        _values[1].Text = Fmt.Clock(LiveCardVm.ElapsedSeconds(s.Started, now));
        _values[2].Text = s.Speed > 0 ? $"{Fmt.Size(s.Speed)}/sn" : "—";
        _values[3].Text = s.Total > 0 ? $"{Fmt.Size(s.Done)} / {Fmt.Size(s.Total)}" : "—";
        _values[4].Text = s.FilesTotal > 0 ? $"{s.Files} / {s.FilesTotal}" : "—";
        _values[5].Text = s.Steps > 0 ? $"{s.Step} / {s.Steps}" : "—";

        var (active, log) = FlowOf(s);
        var text = string.Join("\n", active.Concat(log));
        if (text != _flowText)
        {
            _flowText = text;
            _flow.Inlines.Clear();
            foreach (var line in active) _flow.Inlines.Add(new Run(line + "\n") { Foreground = ConsoleBright, FontWeight = FontWeights.SemiBold });
            _flow.Inlines.Add(new Run(string.Join("\n", log)));
            // Yeni kayıt geldiğinde satırlar aşağıdan yukarı kayarak yerleşir.
            var newest = log.LastOrDefault() ?? "";
            if (newest != _lastLine)
            {
                _lastLine = newest;
                _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(15, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }

        var signature = s.Mode + "|" + s.Stage;
        if (signature == _builtFor) return;
        _builtFor = signature;
        BuildStages(s);
    }

    /// <summary>Aşama şeridi: bitenler yeşil onaylı, süren aşama vurgulu ve nabız gibi atar.</summary>
    private void BuildStages(LiveState s)
    {
        var nodes = Pipelines.GetValueOrDefault(s.Mode) ?? Pipelines["archive"];
        var active = Math.Max(0, Array.FindIndex(nodes, n => n.Stage == s.Stage));
        var accent = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Fmt.Indigo;
        _stages.Children.Clear();
        for (var i = 0; i < nodes.Length; i++)
        {
            var (done, current) = (i < active, i == active);
            var label = new TextBlock { Text = (done ? "✓ " : "") + nodes[i].Label, FontSize = 11.5, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal, Foreground = current ? Brushes.White : null };
            var pill = new Border
            {
                Child = label, CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 0, 4, 4),
                Background = current ? accent : done ? Fmt.Brush(16, 185, 129) : Brushes.Transparent,
                BorderBrush = current || done ? Brushes.Transparent : Fmt.Neutral, BorderThickness = new Thickness(1), Opacity = done || current ? 1 : 0.55,
            };
            if (done) label.Foreground = Brushes.White;
            if (current) pill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.55, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
            _stages.Children.Add(pill);
        }
    }
}
