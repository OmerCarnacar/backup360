using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backup360.Core;
using Backup360.UI;
using Xunit;
using WUI = Wpf.Ui.Controls;

namespace Backup360.Tests;

public class ScreenTests
{
    private static readonly List<string> Errors = new();

    private static string ImgDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "assets"))) d = d.Parent;
        return Directory.CreateDirectory(Path.Combine(d!.FullName, "docs", "img")).FullName;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Save(Window w, string file)
    {
        var element = (FrameworkElement)w.Content;
        var (width, height) = ((int)w.ActualWidth, (int)w.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(Path.Combine(ImgDir(), file));
        png.Save(f);
    }

    private static void Shot(Window w, string file, Action? before = null, int wait = 1800)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -6000;
        w.Top = -6000;
        w.ShowInTaskbar = false;
        w.Show();
        Pump(500);
        before?.Invoke();
        Pump(wait);
        Save(w, file);
        w.Close();
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var x in Tree(child)) yield return x;
        }
    }

    [Fact]
    public void All_screens_render_without_errors()
    {
        using var s = new Sandbox();
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { Run(); }
            catch (Exception e) { failure = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        Assert.True(Errors.Count == 0, string.Join("\n---\n", Errors));
    }

    private static void Run()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        app.DispatcherUnhandledException += (_, e) => { Errors.Add(e.Exception.ToString()); e.Handled = true; };
        ThemeService.Apply();

        Auth.AddUser("admin", "GucluParola1");
        Shot(new LoginWindow(), "login.png");

        var job = new Job { Name = "Muhasebe Yedek", Mode = "dated", DatedOutput = "archive", Sources = new() { @"D:\Sirket\Muhasebe", @"\\NAS\Arsiv" }, Formats = new() { "7z", "zip" }, Targets = new() { "gdrive" }, Schedule = new Schedule { Type = "daily", Time = "02:00" } };
        Db.Save(job);
        var run = Db.StartRun("job", job.Id, job.Name);
        Db.FinishRun(run, "success", "3 öğe, 2 format, 1 hedef", 8_400_000_000);
        Db.Save(new Job { Name = "ERP Veritabanı", Group = "Veritabanları", Mode = "database", DbServer = "SQLAG-LISTENER", Formats = new() { "7z" }, Targets = new() { "gdrive" }, Schedule = new Schedule { Type = "hourly", Interval = 1 } });
        Db.Save(new Job { Name = "Satış Klasörleri", Group = "Dosyalar", Mode = "mirror", Sources = new() { @"D:\Satis" }, Targets = new() { "gdrive" }, Schedule = new Schedule { Type = "weekly", Days = "Monday,Friday", Time = "22:00" } });
        Db.Save(new MoveRule { Name = "Gelen Faturalar", Source = @"D:\Gelen", Target = @"D:\Arsiv" });
        Db.SetSetting("exe_path", AppPaths.ExePath);

        var live = new LiveState { Kind = "job", RefId = job.Id, Name = job.Name, Mode = "dated", Pid = Environment.ProcessId, Stage = "upload", Item = "Satis_20261109-020000.7z", Percent = 64, Speed = 18_300_000, Eta = 94, Done = 3_100_000_000, Total = 4_800_000_000, Files = 1, FilesTotal = 1, Step = 3, Steps = 6, Started = "02:00:04", Log = new() { "02:06:11  [Satis.7z] gönderiliyor" } };
        var liveFile = Path.Combine(LiveFiles.Dir, live.Key + ".json");
        File.WriteAllText(liveFile, JsonSerializer.Serialize(live));

        void Page(Type page, string file, Action<Window>? check = null)
        {
            var w = new MainWindow("admin");
            Shot(w, file, () => ((WUI.NavigationView)w.FindName("Nav")).Navigate(page), 2600);
        }

        var dash = new MainWindow("admin");
        Shot(dash, "pano.png", () => { }, 3000);

        Db.Save(new Job { Name = "NAS Arşiv", Group = "Şube", Mode = "sync", SyncKind = "folder", Sources = new() { @"D:\Arsiv" }, Targets = new() { "nas" }, RemoteFolder = "" });
        Db.Save(new Job { Name = "SQL NAS", Mode = "sync", SyncKind = "sql", SyncFromJob = 2, Targets = new() { "nas" }, RemoteFolder = "SQL" });
        Page(typeof(FolderBackupPage), "isler.png");
        File.Delete(liveFile);
        Page(typeof(ExplorerPage), "gezgin.png");
        File.AppendAllText(AppPaths.RcloneConfig, "\n[Sirket-Drive]\ntype = drive\n\n[OneDrive]\ntype = onedrive\n\n[Dropbox]\ntype = dropbox\n\n[Kova]\ntype = s3\n");
        Page(typeof(FolderSyncPage), "esitleme.png");
        Page(typeof(SqlBackupPage), "sql-yedek.png");
        Page(typeof(SqlSyncPage), "sql-esitleme.png");
        Page(typeof(LogPage), "gunluk.png");
        Page(typeof(RulesPage), "kurallar.png");
        Page(typeof(TargetsPage), "hedefler.png");
        Page(typeof(HistoryPage), "gecmis.png");
        Page(typeof(SettingsPage), "ayarlar.png");
        File.Delete(Path.Combine(ImgDir(), "ayarlar.png"));   // Yerel yol içerir, depoya konmaz.
        Page(typeof(AboutPage), "hakkinda.png");

        var targets = new List<Target> { new("gdrive", "drive"), new("nas", "alias") };
        void Tab(Job j, int index, string file)
        {
            var w = new JobEditorWindow(j, targets);
            Shot(w, file, () => ((TabControl)w.FindName("Tabs")).SelectedIndex = index, 900);
        }
        var fresh = new Job { Mode = "mirror", DatedOutput = "folder" };
        Tab(fresh, 0, "is-gonderim.png");
        Tab(job, 2, "is-sikistirma.png");
        var sqlEditor = new JobEditorWindow(new Job { Mode = "database", DbServer = "SQLAG-LISTENER" }, targets, "sql");
        Shot(sqlEditor, "is-veritabani.png", null, 900);
        Shot(new SyncEditorWindow(new Job { Mode = "sync", SyncKind = "sql", RemoteFolder = "SQL" }, targets), "esitleme-duzenle.png");
        Tab(job, 3, "is-hedef.png");
        Shot(new TargetEditorWindow(), "hedef.png");
        Shot(new RuleEditorWindow(new MoveRule()), "kural.png");
        Shot(new PromptWindow("Klasör oluştur", "Klasör yolu:", "Backup360"), "prompt.png", null, 400);
        File.Delete(Path.Combine(ImgDir(), "prompt.png"));
    }
}
