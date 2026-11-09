using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Backup360.Core;
using Backup360.UI;

namespace Backup360;

public static class AppInfo
{
    public const string Author = "Ömer ÇARNAÇAR";
    public const string LinkedIn = "https://www.linkedin.com/in/omercarnacar/";
    public const string Twitter = "https://twitter.com/omer_carnacar";
    public const string GitHub = "https://github.com/OmerCarnacar";
    public const string Repository = "https://github.com/OmerCarnacar/backup360";
    public const string Releases = "https://github.com/OmerCarnacar/backup360/releases/latest";
    public const string Email = "omer.carnacar@outlook.com.tr";

    /// <summary>Derleme anı: yıl.ay.gün.saatdakika</summary>
    public static string Version =>
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";

    /// <summary>Her derlemede üretilen benzersiz kod.</summary>
    public static string BuildCode =>
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildCode")?.Value ?? "";
}

public static class Launcher
{
    public static string EventName(string kind, long id) => $@"Local\Backup360-cancel-{kind}-{id}";

    /// <summary>İşler her zaman ayrı süreçte çalışır: pencere kapansa da iş sürer, pano canlı durumu izler.</summary>
    public static void Start(string kind, long id) =>
        Process.Start(new ProcessStartInfo(AppPaths.ExePath, kind == "job" ? $"--run-job {id}" : $"--run-rule {id}") { UseShellExecute = false, CreateNoWindow = true });

    public static void StartAudit(bool deep) =>
        Process.Start(new ProcessStartInfo(AppPaths.ExePath, deep ? "--run-audit deep" : "--run-audit quick") { UseShellExecute = false, CreateNoWindow = true });

    public static void StartGuard() =>
        Process.Start(new ProcessStartInfo(AppPaths.ExePath, "--run-guard") { UseShellExecute = false, CreateNoWindow = true });

    public static void StartGroup(string group)
    {
        var psi = new ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--run-group");
        psi.ArgumentList.Add(group);
        Process.Start(psi);
    }

    public static void Cancel(string kind, long id)
    {
        if (EventWaitHandle.TryOpenExisting(EventName(kind, id), out var ev))
            using (ev) ev.Set();
    }

    public static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--alert")
        {
            var what = args.Length == 4 && args[3] == "ransom" ? "Kaynak klasörlerde fidye yazılımı (şifreleme) belirtisi bulundu. Yedekleme durduruldu; sağlam yedeklere dokunulmadı. Bilgisayarı ağdan ayırıp kontrol edin."
                : args.Length == 4 ? "Hedefteki yedeklerde sorun bulundu." : $"{args[1]} işinin yedeği bozuk çıktı ve gönderilmedi.";
            MessageBox.Show($"{what}\n\n{args[2]}\n\nAyrıntı için Backup360 Yedek Sağlığı ve Geçmiş sayfalarına bakın.", "Backup360: YEDEK BOZUK",
                MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);
            return 0;
        }

        Db.Init();
        if (args.Length == 2 && long.TryParse(args[1], out var id))
        {
            if (args[0] == "--run-job") return Headless("job", id);
            if (args[0] == "--run-rule") return Headless("rule", id);
        }
        if (args.Length == 2 && args[0] == "--run-group") return RunGroup(args[1]);
        if (args.Length == 2 && args[0] == "--run-audit") return RunAudit(args[1] == "deep");
        if (args.Length == 1 && args[0] == "--run-guard") return RunGuard();

        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        ThemeService.Apply();
        app.DispatcherUnhandledException += (_, e) =>
        {
            new RunLog("Arayüz hatası", null).Write(e.Exception.ToString());
            MessageBox.Show(e.Exception.Message, "Backup360", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        if (args.Length == 1 && args[0] == "--reset-login")
        {
            if (MessageBox.Show("Tüm giriş hesapları silinecek; işler ve kurallar korunur. Devam edilsin mi?", "Backup360",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                Auth.ResetLogins();
            return 0;
        }

        var login = new LoginWindow();
        if (login.ShowDialog() != true) return 0;
        MainWindow.ExitProcessOnClose = true;
        var main = new MainWindow(login.UserName);
        app.MainWindow = main;
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        return app.Run(main);
    }

    /// <summary>Gruptaki işleri sırayla çalıştırır; biri hata verse de diğerleri devam eder.</summary>
    private static int RunGroup(string group)
    {
        var code = 0;
        foreach (var job in Db.Jobs().Where(j => string.Equals(j.Group, group, StringComparison.OrdinalIgnoreCase)))
            code = Math.Max(code, Headless("job", job.Id));
        return code;
    }

    /// <summary>Uyarı ayrı süreçte açılır: kullanıcı onaylayana kadar sonraki zamanlanmış yedekler engellenmez.</summary>
    private static int RunGuard()
    {
        if (!RansomGuard.Enabled) return 0;
        var result = RansomGuard.MonitorAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (result.Alarm) Alert("Fidye yazılımı koruması", RansomGuard.Describe(result), "ransom");
        return result.Alarm ? 1 : 0;
    }

    private static int RunAudit(bool deep)
    {
        using var cts = new CancellationTokenSource();
        using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Launcher.EventName("audit", 0));
        ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => cts.Cancel(), null, -1, true);
        var outcome = BackupAudit.RunAsync(deep, null, cts.Token).GetAwaiter().GetResult();
        if (outcome.Status == "corrupt") Alert("Yedek denetimi", outcome.Detail, "stored");
        return outcome.Status is "success" or "skipped" ? 0 : 1;
    }

    private static void Alert(string name, string detail, string? kind = null)
    {
        var psi = new ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = false };
        psi.ArgumentList.Add("--alert");
        psi.ArgumentList.Add(name);
        psi.ArgumentList.Add(detail.Length > 400 ? detail[..400] + "..." : detail);
        if (kind != null) psi.ArgumentList.Add(kind);
        Process.Start(psi);
    }

    private static int Headless(string kind, long id)
    {
        using var cts = new CancellationTokenSource();
        using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Launcher.EventName(kind, id));
        ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => cts.Cancel(), null, -1, true);
        try
        {
            if (kind == "job")
            {
                var outcome = BackupRunner.RunAsync(id, null, cts.Token).GetAwaiter().GetResult();
                if (outcome.Status == "corrupt") Alert(Db.GetJob(id)?.Name ?? id.ToString(), outcome.Detail);
                if (outcome.Status == "ransomware") Alert(Db.GetJob(id)?.Name ?? id.ToString(), outcome.Detail, "ransom");
                return outcome.Status is "success" or "warning" or "skipped" ? 0 : 1;
            }
            return MoveEngine.RunAsync(id, null, cts.Token).GetAwaiter().GetResult().Failed == 0 ? 0 : 1;
        }
        catch (Exception e)
        {
            new RunLog("Beklenmeyen hata", null).Write(e.ToString());
            return 2;
        }
    }
}
