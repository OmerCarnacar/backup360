using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Backup360.Core;
using Wpf.Ui.Appearance;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

public static class ThemeService
{
    public static readonly (string Key, string Label, Color Color)[] Accents =
    {
        ("indigo", "İndigo", Color.FromRgb(79, 70, 229)),
        ("mavi", "Mavi", Color.FromRgb(0, 120, 212)),
        ("turkuaz", "Turkuaz", Color.FromRgb(8, 145, 178)),
        ("yesil", "Yeşil", Color.FromRgb(5, 150, 105)),
        ("mor", "Mor", Color.FromRgb(124, 58, 237)),
        ("pembe", "Pembe", Color.FromRgb(219, 39, 119)),
    };

    public static bool IsDark => Db.GetSetting("theme") != "light";
    public static string AccentKey => Db.GetSetting("accent") ?? "indigo";

    public static void Apply()
    {
        var theme = IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme, WUI.WindowBackdropType.Mica, false);
        var color = Accents.FirstOrDefault(a => a.Key == AccentKey, Accents[0]).Color;
        ApplicationAccentColorManager.Apply(color, theme);
    }

    public static void Set(bool dark, string accent)
    {
        Db.SetSetting("theme", dark ? "dark" : "light");
        Db.SetSetting("accent", accent);
        Apply();
    }
}

public static class Fmt
{
    public static string Size(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return $"{bytes:0.#} {units[i]}";
    }

    public static string Duration(long seconds)
    {
        if (seconds < 0) return "—";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} g {t.Hours} sa";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} sa {t.Minutes} dk";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} dk {t.Seconds} sn";
        return $"{t.Seconds} sn";
    }

    public static double RunSeconds(RunRecord r) =>
        DateTime.TryParse(r.Started, out var s) && DateTime.TryParse(r.Ended, out var e) ? Math.Max(0, (e - s).TotalSeconds) : 0;

    public static Brush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static readonly Brush Success = Brush(16, 185, 129);
    public static readonly Brush Warning = Brush(245, 158, 11);
    public static readonly Brush Danger = Brush(239, 68, 68);
    public static readonly Brush Neutral = Brush(148, 163, 184);
    public static readonly Brush Indigo = Brush(99, 102, 241);
    public static readonly Brush Teal = Brush(6, 182, 212);
    public static readonly Brush Violet = Brush(139, 92, 246);

    public static (string Text, Brush Color) Status(string? status) => status switch
    {
        "success" => ("Başarılı", Success),
        "warning" => ("Uyarılı", Warning),
        "failed" => ("Hata", Danger),
        "canceled" => ("İptal", Neutral),
        "skipped" => ("Atlandı", Neutral),
        "running" => ("Çalışıyor", Indigo),
        _ => ("Çalışmadı", Neutral),
    };
}

public sealed class RelayCommand(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed record CardAction(string Label, WUI.SymbolRegular Icon, ICommand Command, bool Danger = false);

/// <summary>Kart üzerindeki canlı durum: yüzde, o anki işlem ve kayıt akışı.</summary>
public sealed class LiveCardVm(ICommand? stop = null) : Observable
{
    private static readonly Dictionary<string, string> Stages = new()
    {
        ["scan"] = "Karşılaştırılıyor", ["archive"] = "Sıkıştırılıyor", ["verify"] = "Arşiv doğrulanıyor", ["upload"] = "Aktarılıyor",
        ["retention"] = "Eski yedekler temizleniyor", ["sqlbackup"] = "Veritabanı yedekleniyor", ["sqlverify"] = "Yedek doğrulanıyor", ["move"] = "Taşınıyor",
    };

    private Visibility _visibility = Visibility.Collapsed;
    private double _percent;
    private bool _indeterminate = true;
    private string _stage = "", _percentText = "", _item = "", _stats = "", _flow = "";
    private Visibility _stopVisibility = Visibility.Collapsed;

    public ICommand? Stop { get; } = stop;
    public Visibility Visibility { get => _visibility; private set => Set(ref _visibility, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public bool Indeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }
    public string Stage { get => _stage; private set => Set(ref _stage, value); }
    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }
    public string Item { get => _item; private set => Set(ref _item, value); }
    public string Stats { get => _stats; private set => Set(ref _stats, value); }
    public string Flow { get => _flow; private set => Set(ref _flow, value); }
    public Visibility StopVisibility { get => _stopVisibility; private set => Set(ref _stopVisibility, value); }
    public bool IsActive => Visibility == Visibility.Visible;
    public DateTime StartedAt { get; private set; }
    public bool PendingExpired => DateTime.Now - StartedAt > TimeSpan.FromSeconds(10);

    public void Begin(string stage = "Başlatılıyor")
    {
        StartedAt = DateTime.Now;
        Stage = stage;
        Item = Stats = PercentText = "";
        Flow = "";
        Indeterminate = true;
        StopVisibility = stop != null ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }

    public void Step(string stage) => Stage = stage;

    public void Update(LiveState s)
    {
        if (!IsActive) StartedAt = DateTime.Now;
        Visibility = Visibility.Visible;
        StopVisibility = stop != null ? Visibility.Visible : Visibility.Collapsed;
        Stage = (Stages.GetValueOrDefault(s.Stage, "Hazırlanıyor")) + (s.Steps > 1 ? $"  ·  Adım {s.Step}/{s.Steps}" : "");
        Indeterminate = s.Percent < 0;
        Percent = Math.Max(0, s.Percent);
        PercentText = s.Percent >= 0 ? $"%{s.Percent}" : "";
        Item = s.Item;
        var parts = new List<string>();
        if (s.Speed > 0) parts.Add($"{Fmt.Size(s.Speed)}/sn");
        if (s.Eta >= 0) parts.Add($"Kalan {Fmt.Duration(s.Eta)}");
        if (s.Total > 0) parts.Add($"{Fmt.Size(s.Done)} / {Fmt.Size(s.Total)}");
        if (s.FilesTotal > 0) parts.Add($"Dosya {s.Files}/{s.FilesTotal}");
        Stats = string.Join("  ·  ", parts);
        Flow = string.Join("\n", s.Transfers.Take(3).Concat(s.Log.TakeLast(5)));
    }

    public void Hide() => Visibility = Visibility.Collapsed;
}

public sealed record CardVm(WUI.SymbolRegular Icon, Brush Accent, string Title, string Subtitle, string[] Chips, string StatusText, Brush StatusBrush, string Footer, CardAction[] Actions)
{
    public LiveCardVm Live { get; init; } = new();
    public ImageSource? Logo { get; init; }
    public Visibility LogoVisibility => Logo != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconVisibility => Logo == null ? Visibility.Visible : Visibility.Collapsed;
}

public sealed record GroupVm(string Name, int Count, ObservableCollection<CardVm> Cards, CardAction? Action, Visibility ActionVisibility);

public static class Ui
{
    private static async Task<WUI.MessageBoxResult> Box(string title, string text, string? primary, string close)
    {
        var box = new WUI.MessageBox { Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 }, CloseButtonText = close };
        if (primary != null) { box.PrimaryButtonText = primary; box.PrimaryButtonAppearance = WUI.ControlAppearance.Primary; }
        return await box.ShowDialogAsync();
    }

    public static Task Info(string text, string title = "Backup360") => Box(title, text, null, "Tamam");
    public static async Task<bool> Confirm(string text) => await Box("Backup360", text, "Evet", "Vazgeç") == WUI.MessageBoxResult.Primary;

    public static async void Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            new RunLog("Arayüz işlemi", null).Write(e.Message);
            await Info(e.Message, "İşlem tamamlanamadı");
        }
    }

    public static string? Prompt(Window owner, string title, string label, string initial = "")
    {
        var window = new PromptWindow(title, label, initial) { Owner = owner };
        return window.ShowDialog() == true ? window.Value : null;
    }

    /// <summary>Pencereyi çalışma alanına sığdırır; küçük ekranda taşmaz.</summary>
    public static void FitToScreen(Window w)
    {
        var area = SystemParameters.WorkArea;
        w.MinWidth = Math.Min(w.MinWidth, area.Width - 24);
        w.MinHeight = Math.Min(w.MinHeight, area.Height - 24);
        w.Width = Math.Min(w.Width, area.Width - 24);
        w.Height = Math.Min(w.Height, area.Height - 24);
    }

    public static Window Owner(DependencyObject d) => Window.GetWindow(d) ?? Application.Current.MainWindow;

    public static WUI.SymbolIcon Symbol(WUI.SymbolRegular s) => new() { Symbol = s };
}
