using Backup360.Core;

namespace Backup360.UI;

public partial class MainWindow
{
    public static string CurrentUser { get; private set; } = "";

    /// <summary>Gerçek uygulamada pencere kapanınca süreç kesin olarak biter.</summary>
    public static bool ExitProcessOnClose { get; set; }

    public MainWindow(string user)
    {
        CurrentUser = user;
        InitializeComponent();
        Ui.FitToScreen(this);
        Bar.Title = $"Backup360  ·  {user}";
        Closing += (_, e) =>
        {
            if (e.Cancel) return;
            DriveMounter.UnmountAll();
            // WPF'in kapanış telemetrisi tek dosyalık pakette hata verip süreci görünmez bırakıyor.
            if (ExitProcessOnClose) Environment.Exit(0);
        };
        Loaded += async (_, _) =>
        {
            Nav.Navigate(typeof(DashboardPage));
            try { await TaskPlanner.ResyncIfMovedAsync(); }
            catch (InvalidOperationException e) { await Ui.Info(e.Message); }
        };
    }
}
