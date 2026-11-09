using System.Drawing.Drawing2D;
using System.Reflection;

namespace Backup360.UI;

public static class Theme
{
    public static readonly Color Bg = Color.FromArgb(244, 246, 251);
    public static readonly Color Surface = Color.White;
    public static readonly Color Sidebar = Color.FromArgb(17, 24, 39);
    public static readonly Color SidebarHover = Color.FromArgb(31, 41, 55);
    public static readonly Color Primary = Color.FromArgb(79, 70, 229);
    public static readonly Color Accent = Color.FromArgb(6, 182, 212);
    public static readonly Color Text = Color.FromArgb(17, 24, 39);
    public static readonly Color Muted = Color.FromArgb(107, 114, 128);
    public static readonly Color Border = Color.FromArgb(229, 231, 235);
    public static readonly Color Success = Color.FromArgb(16, 185, 129);
    public static readonly Color Warning = Color.FromArgb(245, 158, 11);
    public static readonly Color Danger = Color.FromArgb(239, 68, 68);

    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Bold = new("Segoe UI Semibold", 10.25f);
    public static readonly Font Heading = new("Segoe UI Semibold", 17f);
    public static readonly Font Big = new("Segoe UI Semibold", 22f);

    private static Image? _logo;
    public static Image Logo => _logo ??= Image.FromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png")!);

    private static Icon? _icon;
    public static Icon AppIcon => _icon ??= new Icon(Assembly.GetExecutingAssembly().GetManifestResourceStream("Backup360.ico")!);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

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

    public static double RunSeconds(Backup360.Core.RunRecord r) =>
        DateTime.TryParse(r.Started, out var s) && DateTime.TryParse(r.Ended, out var e) ? Math.Max(0, (e - s).TotalSeconds) : 0;

    public static (string Text, Color Color) Status(string? status) => status switch
    {
        "success" => ("Başarılı", Success),
        "warning" => ("Uyarılı", Warning),
        "failed" => ("Hata", Danger),
        "canceled" => ("İptal", Muted),
        "skipped" => ("Atlandı", Muted),
        "running" => ("Çalışıyor", Primary),
        _ => ("—", Muted),
    };
}

public static class Ui
{
    public static Button Button(string text, bool primary = false, EventHandler? click = null)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 4),
            Margin = new Padding(0, 0, 8, 8),
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Body,
            BackColor = primary ? Theme.Primary : Theme.Surface,
            ForeColor = primary ? Color.White : Theme.Text,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = primary ? Theme.Primary : Theme.Border;
        if (click != null) b.Click += click;
        return b;
    }

    public static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? Theme.Body,
        ForeColor = color ?? Theme.Text,
        Margin = new Padding(0, 6, 8, 2),
    };

    public static ListView Grid(params (string Title, int Width)[] columns)
    {
        var v = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            Font = Theme.Body,
        };
        foreach (var (title, width) in columns) v.Columns.Add(title, width);
        v.SmallImageList = new ImageList { ImageSize = new Size(1, 30) };   // Satır yüksekliği
        return v;
    }

    /// <summary>Tıklanınca e-posta programını açan adres ve kopyalama düğmesi.</summary>
    public static Control MailRow()
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0), BackColor = Color.Transparent };
        var link = new LinkLabel { Text = AppInfo.Email, AutoSize = true, Font = Theme.Body, LinkColor = Theme.Primary, Margin = new Padding(0, 6, 8, 0) };
        link.LinkClicked += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"mailto:{AppInfo.Email}?subject=Backup360") { UseShellExecute = true });
        var copy = new Button { Text = "Kopyala", AutoSize = true, FlatStyle = FlatStyle.Flat, Font = Theme.Small, BackColor = Theme.Surface, ForeColor = Theme.Text, Cursor = Cursors.Hand, Margin = new Padding(0) };
        copy.FlatAppearance.BorderColor = Theme.Border;
        copy.Click += (_, _) => { Clipboard.SetText(AppInfo.Email); copy.Text = "Kopyalandı"; };
        row.Controls.Add(link);
        row.Controls.Add(copy);
        return row;
    }

    public static void Error(Exception e) => MessageBox.Show(e.Message, "Backup360", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static bool Confirm(string text) =>
        MessageBox.Show(text, "Backup360", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    public static string? Prompt(string title, string label, string initial = "")
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(440, 130), Font = Theme.Body, BackColor = Theme.Surface };
        var l = new Label { Text = label, Location = new Point(16, 14), AutoSize = true };
        var t = new TextBox { Text = initial, Location = new Point(16, 40), Width = 408 };
        var ok = Button("Tamam", true);
        ok.Location = new Point(340, 84);
        ok.DialogResult = DialogResult.OK;
        f.Controls.AddRange(new Control[] { l, t, ok });
        f.AcceptButton = ok;
        return f.ShowDialog() == DialogResult.OK ? t.Text.Trim() : null;
    }

    public static void Enable(Control c)
    {
        typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(c, true);
    }
}

/// <summary>Sol menü düğmesi: simge, etkin çubuk ve üzerine gelme durumu.</summary>
public sealed class NavButton : Control
{
    private static readonly Font IconFont = new("Segoe MDL2 Assets", 12f);
    private readonly string _glyph;
    private bool _hover, _active;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Active { get => _active; set { _active = value; Invalidate(); } }

    public NavButton(string glyph, string text)
    {
        _glyph = glyph;
        Text = text;
        Size = new Size(212, 44);
        Margin = new Padding(0);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(_active ? Theme.SidebarHover : _hover ? Color.FromArgb(24, 33, 48) : Theme.Sidebar);
        if (_active)
        {
            using var bar = new SolidBrush(Theme.Accent);
            g.FillRectangle(bar, 0, 8, 3, Height - 16);
        }
        var color = _active ? Color.White : Color.FromArgb(171, 182, 197);
        using var brush = new SolidBrush(color);
        var middle = new StringFormat { LineAlignment = StringAlignment.Center };
        g.DrawString(_glyph, IconFont, brush, new RectangleF(20, 0, 28, Height), middle);
        g.DrawString(Text, _active ? Theme.Bold : Theme.Body, brush, new RectangleF(52, 0, Width - 56, Height), middle);
    }
}

/// <summary>Panodaki özet kutusu.</summary>
public sealed class StatTile : Control
{
    private static readonly Font IconFont = new("Segoe MDL2 Assets", 15f);
    private readonly string _glyph, _title;
    private readonly Color _color;
    private string _value = "—";

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Value { get => _value; set { _value = value; Invalidate(); } }

    public StatTile(string glyph, string title, Color color)
    {
        (_glyph, _title, _color) = (glyph, title, color);
        Size = new Size(204, 84);
        Margin = new Padding(0, 0, 12, 12);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Bg);
        using (var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), 10))
        {
            g.FillPath(Brushes.White, path);
            using var border = new Pen(Theme.Border);
            g.DrawPath(border, path);
        }
        using (var soft = new SolidBrush(Color.FromArgb(32, _color))) g.FillEllipse(soft, 14, 21, 42, 42);
        using var tint = new SolidBrush(_color);
        var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(_glyph, IconFont, tint, new RectangleF(14, 21, 42, 42), center);
        using var muted = new SolidBrush(Theme.Muted);
        using var text = new SolidBrush(Theme.Text);
        g.DrawString(_title, Theme.Small, muted, 66, 16);
        g.DrawString(_value, new Font("Segoe UI Semibold", 14f), text, 64, 36);
    }
}

/// <summary>Logonun çevresinde dönen ışık halkası.</summary>
public sealed class LogoView : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private float _angle;

    public LogoView()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
        _timer.Tick += (_, _) => { _angle = (_angle + 4) % 360; Invalidate(); };
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var side = Math.Min(Width, Height);
        var ring = new RectangleF((Width - side) / 2f + 4, (Height - side) / 2f + 4, side - 8, side - 8);
        for (var i = 0; i < 28; i++)
        {
            using var p = new Pen(Color.FromArgb(i * 8 + 10, 255, 255, 255), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(p, ring, _angle + i * 6, 7);
        }
        var inset = side * 0.17f;
        g.DrawImage(Theme.Logo, (Width - side) / 2f + inset, (Height - side) / 2f + inset, side - 2 * inset, side - 2 * inset);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
