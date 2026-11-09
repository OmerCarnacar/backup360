using Backup360.Core;

namespace Backup360.UI;

public partial class MainWindow
{
    private static readonly System.Windows.Media.Brush SelectedBrush = Fmt.Brush(234, 88, 12);

    private static IEnumerable<System.Windows.DependencyObject> Tree(System.Windows.DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var x in Tree(child)) yield return x;
        }
    }

    /// <summary>Seçili menü öğesini turuncu boyar.</summary>
    private void PaintMenu()
    {
        foreach (var item in Nav.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().Concat(Nav.FooterMenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>()))
        {
            var parts = Tree(item).ToList();
            var fill = parts.OfType<System.Windows.Controls.Border>().FirstOrDefault(b => b.CornerRadius.TopLeft > 0);
            if (fill != null)
            {
                if (item.IsActive) fill.Background = SelectedBrush;
                else fill.ClearValue(System.Windows.Controls.Border.BackgroundProperty);
            }
            foreach (var text in parts.OfType<System.Windows.Controls.TextBlock>())
            {
                if (item.IsActive) text.Foreground = System.Windows.Media.Brushes.White;
                else text.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
            }
        }
    }

    public static string CurrentUser { get; private set; } = "";

    /// <summary>Gerçek uygulamada pencere kapanınca süreç kesin olarak biter.</summary>
    public static bool ExitProcessOnClose { get; set; }

    public MainWindow(string user)
    {
        CurrentUser = user;
        InitializeComponent();
        Ui.FitToScreen(this);
        Bar.Title = $"Backup360  ·  {user}";
        Nav.Navigated += (_, _) => Dispatcher.BeginInvoke(PaintMenu, System.Windows.Threading.DispatcherPriority.Loaded);
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
            PaintMenu();
            try
            {
                await TaskPlanner.ResyncIfMovedAsync();
                if (ExitProcessOnClose)
                {
                    await TaskPlanner.EnsureAuditAsync();
                    await TaskPlanner.EnsureGuardAsync();
                }
            }
            catch (InvalidOperationException e) { await Ui.Info(e.Message); }
        };
    }
}
