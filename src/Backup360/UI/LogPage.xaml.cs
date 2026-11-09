using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Backup360.Core;

namespace Backup360.UI;

public partial class LogPage
{
    private const int MaxLines = 1500;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private long _position;
    private string _file = "";

    public LogPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => { if (Follow.IsChecked == true) Tail(); };
        Loaded += (_, _) => { Tail(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Tail()
    {
        var file = Path.Combine(AppPaths.LogDir, $"backup360-{DateTime.Now:yyyyMM}.log");
        if (file != _file) { _file = file; _position = 0; Output.Clear(); }
        if (!File.Exists(file)) return;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (_position == 0 && stream.Length > 400_000) _position = stream.Length - 400_000;
            if (stream.Length <= _position) return;
            stream.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            _position = stream.Length;
            Output.AppendText(text);
            if (Output.LineCount > MaxLines)
            {
                var cut = Output.GetCharacterIndexFromLineIndex(Output.LineCount - MaxLines);
                Output.Text = Output.Text[cut..];
            }
            Output.ScrollToEnd();
        }
        catch (IOException) { }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", AppPaths.LogDir);
}
