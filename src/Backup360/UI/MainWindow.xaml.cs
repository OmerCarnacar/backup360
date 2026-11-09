using Backup360.Core;

namespace Backup360.UI;

public partial class MainWindow
{
    public static string CurrentUser { get; private set; } = "";

    public MainWindow(string user)
    {
        CurrentUser = user;
        InitializeComponent();
        Bar.Title = $"Backup360  ·  {user}";
        Closed += (_, _) => DriveMounter.UnmountAll();
        Loaded += async (_, _) =>
        {
            Nav.Navigate(typeof(DashboardPage));
            try { await TaskPlanner.ResyncIfMovedAsync(); }
            catch (InvalidOperationException e) { await Ui.Info(e.Message); }
        };
    }
}
