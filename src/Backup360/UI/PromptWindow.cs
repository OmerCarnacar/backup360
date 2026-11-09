using System.Windows;
using System.Windows.Controls;
using WUI = Wpf.Ui.Controls;

namespace Backup360.UI;

/// <summary>Tek satırlık metin isteyen küçük pencere.</summary>
public sealed class PromptWindow : WUI.FluentWindow
{
    private readonly WUI.TextBox _box = new();
    private readonly WUI.PasswordBox _password = new();
    private readonly bool _isPassword;

    public string Value => _isPassword ? _password.Password : _box.Text.Trim();

    public PromptWindow(string title, string label, string initial = "", bool password = false)
    {
        _isPassword = password;
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WUI.WindowBackdropType.Mica;

        var root = new StackPanel();
        root.Children.Add(new WUI.TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false });
        var body = new StackPanel { Margin = new Thickness(24, 12, 24, 20) };
        body.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        _box.Text = initial;
        body.Children.Add(password ? _password : _box);
        var ok = new WUI.Button { Content = "Tamam", Appearance = WUI.ControlAppearance.Primary, IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 18, 8, 0) };
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = new WUI.Button { Content = "Vazgeç", IsCancel = true, MinWidth = 100, Margin = new Thickness(0, 18, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        body.Children.Add(row);
        root.Children.Add(body);
        Content = root;
    }
}
