using System.Drawing.Drawing2D;
using Backup360.Core;

namespace Backup360.UI;

/// <summary>Çalışan bir işin canlı görünümü: aşama hattı, akan noktalar, ilerleme, hız ve kalan süre.</summary>
public sealed class ActivityCard : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly Button _stop;
    private float _phase, _shown;
    private LiveState _state;

    public string Key => _state.Key;

    private static readonly Dictionary<string, (string Stage, string Label)[]> Pipelines = new()
    {
        ["archive"] = new[] { ("archive", "Sıkıştır"), ("verify", "Doğrula"), ("upload", "Gönder"), ("retention", "Temizle") },
        ["dated"] = new[] { ("scan", "Tara"), ("archive", "Sıkıştır"), ("verify", "Doğrula"), ("upload", "Gönder"), ("retention", "Temizle") },
        ["database"] = new[] { ("scan", "Listele"), ("sqlbackup", "DB yedeği"), ("sqlverify", "DB doğrula"), ("archive", "Sıkıştır"), ("verify", "Doğrula"), ("upload", "Gönder"), ("retention", "Temizle") },
        ["mirror"] = new[] { ("scan", "Hazırla"), ("upload", "Eşitle") },
        ["move"] = new[] { ("scan", "Yeni dosya bul"), ("move", "Taşı") },
    };

    public ActivityCard(LiveState state)
    {
        _state = state;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _stop = Ui.Button("Durdur", false, (_, _) => Launcher.Cancel(_state.Kind, _state.RefId));
        _stop.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(_stop);
        Height = 214;
        Margin = new Padding(0, 0, 0, 12);
        _timer.Tick += (_, _) =>
        {
            _phase = (_phase + 0.018f) % 1f;
            var target = Math.Max(0, _state.Percent);
            _shown += (target - _shown) * 0.12f;
            Invalidate();
        };
        _timer.Start();
    }

    public void Update(LiveState state)
    {
        if (state.Stage != _state.Stage || state.Item != _state.Item) { if (state.Percent <= 0) _shown = 0; }
        _state = state;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        _stop.Location = new Point(Width - _stop.Width - 12, 10);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Bg);
        using (var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), 12))
        {
            g.FillPath(Brushes.White, path);
            using var border = new Pen(Theme.Border);
            g.DrawPath(border, path);
        }

        using var text = new SolidBrush(Theme.Text);
        using var muted = new SolidBrush(Theme.Muted);
        g.DrawString(_state.Name, Theme.Bold, text, 18, 14);
        var step = _state.Steps > 0 ? $"Adım {_state.Step}/{_state.Steps} · " : "";
        g.DrawString($"{step}Başlangıç {_state.Started}", Theme.Small, muted, 18, 38);

        DrawPipeline(g, muted, text);
        DrawBar(g, new RectangleF(18, 124, Width - 36, 10));

        var format = new StringFormat { Trimming = StringTrimming.EllipsisPath, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(_state.Item, Theme.Body, text, new RectangleF(18, 140, Width * 0.62f, 20), format);
        var percent = _state.Percent >= 0 ? $"%{Math.Round(_shown)}" : "Çalışıyor";
        var right = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString(percent, Theme.Bold, new SolidBrush(Theme.Primary), new RectangleF(0, 139, Width - 18, 22), right);

        var parts = new List<string>();
        if (_state.Speed > 0) parts.Add($"Hız {Theme.Size(_state.Speed)}/sn");
        if (_state.Eta >= 0) parts.Add($"Kalan {Theme.Duration(_state.Eta)}");
        if (_state.Total > 0) parts.Add($"{Theme.Size(_state.Done)} / {Theme.Size(_state.Total)}");
        else if (_state.Done > 0) parts.Add(Theme.Size(_state.Done));
        if (_state.FilesTotal > 0) parts.Add($"Dosya {_state.Files}/{_state.FilesTotal}");
        else if (_state.Files > 0) parts.Add($"{_state.Files} dosya");
        g.DrawString(parts.Count > 0 ? string.Join("   ·   ", parts) : " ", Theme.Small, muted, 18, 164);

        var last = _state.Log.LastOrDefault() ?? "";
        g.DrawString(last, Theme.Small, muted, new RectangleF(18, 186, Width - 36, 18), format);
    }

    private void DrawPipeline(Graphics g, Brush muted, Brush text)
    {
        var nodes = Pipelines.GetValueOrDefault(_state.Mode) ?? Pipelines["archive"];
        var active = Math.Max(0, Array.FindIndex(nodes, n => n.Stage == _state.Stage));
        var left = 60f;
        var gap = nodes.Length > 1 ? (Width - 120f) / (nodes.Length - 1) : 0;
        const float y = 76f;

        for (var i = 0; i < nodes.Length - 1; i++)
        {
            var x1 = left + i * gap + 15;
            var x2 = left + (i + 1) * gap - 15;
            using var pen = new Pen(i < active ? Theme.Primary : Theme.Border, 3f);
            g.DrawLine(pen, x1, y, x2, y);
            if (i != active) continue;
            for (var d = 0; d < 4; d++)
            {
                var t = (_phase + d / 4f) % 1f;
                var alpha = (int)(255 * Math.Sin(t * Math.PI));
                using var dot = new SolidBrush(Color.FromArgb(alpha, Theme.Accent));
                var x = x1 + (x2 - x1) * t;
                g.FillEllipse(dot, x - 4, y - 4, 8, 8);
            }
        }

        var center = new StringFormat { Alignment = StringAlignment.Center };
        for (var i = 0; i < nodes.Length; i++)
        {
            var x = left + i * gap;
            var isActive = i == active;
            if (isActive)
            {
                var pulse = (float)(Math.Sin(_phase * Math.PI * 4) + 1) / 2;
                var r = 17 + pulse * 6;
                using var halo = new SolidBrush(Color.FromArgb(50, Theme.Primary));
                g.FillEllipse(halo, x - r, y - r, 2 * r, 2 * r);
            }
            var color = i < active ? Theme.Success : isActive ? Theme.Primary : Theme.Border;
            using (var fill = new SolidBrush(color)) g.FillEllipse(fill, x - 13, y - 13, 26, 26);
            if (i < active)
            {
                using var check = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(check, new[] { new PointF(x - 6, y), new PointF(x - 1.5f, y + 5), new PointF(x + 6, y - 5) });
            }
            else
            {
                using var number = new SolidBrush(isActive ? Color.White : Theme.Muted);
                g.DrawString((i + 1).ToString(), Theme.Small, number, new RectangleF(x - 13, y - 9, 26, 18), center);
            }
            g.DrawString(nodes[i].Label, isActive ? Theme.Bold : Theme.Small, isActive ? text : muted, new RectangleF(x - 60, y + 16, 120, 20), center);
        }
    }

    private void DrawBar(Graphics g, RectangleF r)
    {
        using (var track = Theme.Round(r, 5)) g.FillPath(new SolidBrush(Theme.Border), track);
        RectangleF fill;
        if (_state.Percent < 0)
        {
            // Yüzde bilinmiyor: kayan parça
            var w = r.Width * 0.22f;
            var x = r.X - w + (r.Width + w) * _phase;
            fill = RectangleF.Intersect(new RectangleF(x, r.Y, w, r.Height), r);
        }
        else fill = new RectangleF(r.X, r.Y, r.Width * Math.Clamp(_shown / 100f, 0, 1), r.Height);
        if (fill.Width < 1) return;

        using var path = Theme.Round(fill, 5);
        using var brush = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), Theme.Primary, Theme.Accent, 0f);
        g.FillPath(brush, path);

        // Dolu kısımda gezen parıltı
        var sx = fill.X + fill.Width * _phase;
        var state = g.Save();
        g.SetClip(path);
        using var shine = new LinearGradientBrush(new RectangleF(sx - 40, r.Y, 80, r.Height), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0f)
        {
            InterpolationColors = new ColorBlend
            {
                Positions = new[] { 0f, 0.5f, 1f },
                Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(150, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
            },
        };
        g.FillRectangle(shine, sx - 40, r.Y, 80, r.Height);
        g.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

public sealed record UpcomingEntry(string Name, string Kind, string Plan, DateTime? Next, TimeSpan Window);

/// <summary>Zamanlanmış işlerin geri sayım halkaları.</summary>
public sealed class UpcomingView : Control
{
    private const int CardWidth = 250, CardHeight = 112, Gap = 12;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
    private List<UpcomingEntry> _items = new();
    private float _phase;

    public UpcomingView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _timer.Tick += (_, _) => { _phase = (_phase + 0.01f) % 1f; Invalidate(); };
        _timer.Start();
    }

    public void SetItems(List<UpcomingEntry> items)
    {
        _items = items;
        AdjustHeight();
    }

    private int Columns => Math.Max(1, (Width + Gap) / (CardWidth + Gap));

    private void AdjustHeight()
    {
        var rows = Math.Max(1, (int)Math.Ceiling(_items.Count / (double)Columns));
        Height = rows * (CardHeight + Gap);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        AdjustHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Bg);
        if (_items.Count == 0)
        {
            using var b = new SolidBrush(Theme.Muted);
            g.DrawString("Henüz zamanlanmış iş yok. Bir yedekleme işi veya taşıma kuralı oluşturun.", Theme.Body, b, 2, 6);
            return;
        }
        for (var i = 0; i < _items.Count; i++)
            DrawCard(g, _items[i], (i % Columns) * (CardWidth + Gap), (i / Columns) * (CardHeight + Gap));
    }

    private void DrawCard(Graphics g, UpcomingEntry item, int x, int y)
    {
        using (var path = Theme.Round(new RectangleF(x, y, CardWidth - 1, CardHeight - 1), 12))
        {
            g.FillPath(Brushes.White, path);
            using var border = new Pen(Theme.Border);
            g.DrawPath(border, path);
        }

        var ring = new RectangleF(x + 16, y + 24, 62, 62);
        using (var track = new Pen(Theme.Border, 6f)) g.DrawEllipse(track, ring);
        var remaining = item.Next is { } n ? n - DateTime.Now : TimeSpan.Zero;
        var fraction = item.Next != null && item.Window.TotalSeconds > 0 ? (float)Math.Clamp(1 - remaining.TotalSeconds / item.Window.TotalSeconds, 0.02, 1) : 0.02f;
        using (var brush = new LinearGradientBrush(ring, Theme.Primary, Theme.Accent, 45f))
        using (var pen = new Pen(brush, 6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, ring, -90, 360 * fraction);

        // Halkada ilerlemeyi vurgulayan nabız noktası
        var angle = (-90 + 360 * fraction) * Math.PI / 180;
        var pulse = (float)(Math.Sin(_phase * Math.PI * 2) + 1) * 2 + 3;
        var (cx, cy) = (ring.X + ring.Width / 2 + Math.Cos(angle) * ring.Width / 2, ring.Y + ring.Height / 2 + Math.Sin(angle) * ring.Height / 2);
        using (var dot = new SolidBrush(Color.FromArgb(180, Theme.Accent))) g.FillEllipse(dot, (float)cx - pulse, (float)cy - pulse, 2 * pulse, 2 * pulse);

        using var text = new SolidBrush(Theme.Text);
        using var muted = new SolidBrush(Theme.Muted);
        using var primary = new SolidBrush(Theme.Primary);
        var clip = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(item.Name, Theme.Bold, text, new RectangleF(x + 90, y + 14, CardWidth - 100, 22), clip);
        g.DrawString($"{item.Kind} · {item.Plan}", Theme.Small, muted, new RectangleF(x + 90, y + 38, CardWidth - 100, 18), clip);
        g.DrawString(item.Next == null ? "Bekliyor" : remaining.TotalSeconds <= 0 ? "Şimdi" : Theme.Duration((long)remaining.TotalSeconds) + " sonra", Theme.Bold, primary, x + 90, y + 58);
        if (item.Next is { } when_) g.DrawString(when_.ToString("dd.MM HH:mm"), Theme.Small, muted, x + 90, y + 82);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class DashboardPage : Panel
{
    private readonly FlowLayoutPanel _activity = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
    private readonly Label _idle = Ui.Label("Şu anda çalışan işlem yok.", Theme.Body, Theme.Muted);
    private readonly UpcomingView _upcoming = new() { Height = 124 };
    private readonly StatTile _ok = new("", "Başarılı (7 gün)", Theme.Success);
    private readonly StatTile _bad = new("", "Hata / uyarı (7 gün)", Theme.Danger);
    private readonly StatTile _sent = new("", "Aktarılan (7 gün)", Theme.Primary);
    private readonly StatTile _speed = new("", "Ortalama hız", Theme.Accent);
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _slow = new() { Interval = 30_000 };

    public DashboardPage()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        AutoScroll = true;
        Padding = new Padding(26, 20, 26, 20);
        var flow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Theme.Bg };
        flow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        flow.Controls.Add(Ui.Label("Pano", Theme.Heading));
        var tiles = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 4) };
        tiles.Controls.AddRange(new Control[] { _ok, _bad, _sent, _speed });
        flow.Controls.Add(tiles);
        flow.Controls.Add(Ui.Label("Canlı aktarımlar", Theme.Bold));
        flow.Controls.Add(_idle);
        flow.Controls.Add(_activity);
        flow.Controls.Add(Ui.Label("Yaklaşan zamanlamalar", Theme.Bold));
        _upcoming.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        flow.Controls.Add(_upcoming);
        Controls.Add(flow);

        _poll.Tick += (_, _) => { if (Visible) RefreshLive(); };
        _slow.Tick += async (_, _) => { if (Visible) await RefreshScheduleAsync(); };
        _poll.Start();
        _slow.Start();
        VisibleChanged += async (_, _) => { if (Visible) await RefreshScheduleAsync(); };
        HandleCreated += async (_, _) => await RefreshScheduleAsync();
    }

    public void RefreshLive()
    {
        var states = LiveFiles.ReadAll();
        var keys = states.Select(s => s.Key).ToHashSet();
        foreach (var card in _activity.Controls.OfType<ActivityCard>().Where(c => !keys.Contains(c.Key)).ToList())
        {
            _activity.Controls.Remove(card);
            card.Dispose();
        }
        foreach (var state in states)
        {
            var card = _activity.Controls.OfType<ActivityCard>().FirstOrDefault(c => c.Key == state.Key);
            if (card == null)
            {
                card = new ActivityCard(state) { Width = Math.Max(500, ClientSize.Width - 70) };
                _activity.Controls.Add(card);
            }
            card.Update(state);
        }
        foreach (var card in _activity.Controls.OfType<ActivityCard>()) card.Width = Math.Max(500, ClientSize.Width - 70);
        _idle.Visible = states.Count == 0;
    }

    private static TimeSpan Window(Schedule s) => s.Type switch
    {
        "minutes" => TimeSpan.FromMinutes(Math.Max(1, s.Interval)),
        "hourly" => TimeSpan.FromHours(Math.Max(1, s.Interval)),
        "weekly" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(1),
    };

    public async Task RefreshScheduleAsync()
    {
        var recent = Db.RecentRuns(1000).Where(r => DateTime.TryParse(r.Started, out var d) && d > DateTime.Now.AddDays(-7)).ToList();
        _ok.Value = recent.Count(r => r.Status == "success").ToString();
        _bad.Value = $"{recent.Count(r => r.Status == "failed")} / {recent.Count(r => r.Status == "warning")}";
        _sent.Value = Theme.Size(recent.Sum(r => r.Bytes));
        var seconds = recent.Where(r => r.Bytes > 0).Sum(Theme.RunSeconds);
        _speed.Value = seconds > 0 ? $"{Theme.Size(recent.Sum(r => r.Bytes) / seconds)}/sn" : "—";

        var times = await TaskPlanner.ReadTimesAsync();
        DateTime? Next(string task) => times.TryGetValue(task, out var t) ? t.Next : null;
        var list = new List<UpcomingEntry>();
        foreach (var j in Db.Jobs().Where(x => x.Schedule.Type != "none"))
            list.Add(new UpcomingEntry(j.Name, j.Group, j.Schedule.Describe(), Next(TaskPlanner.JobTask(j.Id)), Window(j.Schedule)));
        foreach (var r in Db.Rules().Where(x => x.Schedule.Type != "none"))
            list.Add(new UpcomingEntry(r.Name, r.Group, r.Schedule.Describe(), Next(TaskPlanner.RuleTask(r.Id)), Window(r.Schedule)));
        if (!IsDisposed) _upcoming.SetItems(list.OrderBy(x => x.Next ?? DateTime.MaxValue).ToList());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _poll.Dispose(); _slow.Dispose(); }
        base.Dispose(disposing);
    }
}
