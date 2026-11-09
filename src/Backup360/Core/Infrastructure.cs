using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Backup360.Core;

/// <summary>Tüm veriler exe'nin yanındaki data klasöründe durur; klasör taşınınca veriler de taşınır.</summary>
public static class AppPaths
{
    public static string BaseDir => AppContext.BaseDirectory;
    public static string DataDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("BACKUP360_DATA");
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(BaseDir, "data");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
    public static string Database => Path.Combine(DataDir, "backup360.db");
    public static string RcloneConfig => Path.Combine(DataDir, "rclone.conf");
    public static string LogDir => Directory.CreateDirectory(Path.Combine(DataDir, "logs")).FullName;
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(BaseDir, "Backup360.exe");
}

/// <summary>Harici araçları önce uygulamanın tools klasöründe, sonra sistemde arar.</summary>
public static class Tools
{
    private static string? Bundled(string file)
    {
        var p = Path.Combine(AppPaths.BaseDir, "tools", file);
        return File.Exists(p) ? p : null;
    }

    private static string ProgramFile(string rel) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), rel);

    public static string? SevenZip()
    {
        var p = Bundled("7z.exe") ?? ProgramFile(@"7-Zip\7z.exe");
        return File.Exists(p) ? p : null;
    }

    public static string? Rar()
    {
        var p = ProgramFile(@"WinRAR\Rar.exe");
        return File.Exists(p) ? p : null;
    }

    public static string? Rclone()
    {
        var p = Bundled("rclone.exe");
        if (p != null) return p;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "rclone.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages");
        if (!Directory.Exists(packages)) return null;
        return Directory.EnumerateFiles(packages, "rclone.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    public static string Require(string? path, string name) =>
        path ?? throw new InvalidOperationException($"{name} bulunamadı. Uygulamayı yeniden kurun.");
}

public static class Proc
{
    public static Process Start(string exe, IEnumerable<string> args, bool pipeIn = false, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = pipeIn,
            // Araçlar UTF-8 yazar; varsayılan konsol kodlaması Türkçe dosya adlarını bozar.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        return Process.Start(psi) ?? throw new InvalidOperationException($"{Path.GetFileName(exe)} başlatılamadı.");
    }

    public static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }

    private static readonly Regex PercentToken = new(@"^\s*(\d{1,3})%", RegexOptions.Compiled);

    /// <summary>Akışı satırlara böler. 7-Zip ilerlemeyi \b ve \r ile yazdığı için onlar da ayraçtır.</summary>
    public static async Task PumpAsync(StreamReader reader, StringBuilder sink, Action<string>? log, Action<int>? percent)
    {
        var buffer = new char[4096];
        var token = new StringBuilder();
        void Flush()
        {
            if (token.Length == 0) return;
            var text = token.ToString();
            token.Clear();
            var match = PercentToken.Match(text);
            if (match.Success) { percent?.Invoke(int.Parse(match.Groups[1].Value)); return; }
            if (string.IsNullOrWhiteSpace(text)) return;
            lock (sink) sink.AppendLine(text);
            log?.Invoke(text);
        }
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] is '\n' or '\r' or '\b') Flush();
                else token.Append(buffer[i]);
            }
        }
        Flush();
    }

    /// <summary>Standart çıktı ve hatayı ayrı toplar: JSON gibi makine çıktılarına uyarı satırları karışmaz.</summary>
    public static async Task<(int Code, string Out, string Err)> CaptureAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        using var p = Start(exe, args);
        using var reg = ct.Register(() => TryKill(p));
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        await Task.WhenAll(stdout, stderr);
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return (p.ExitCode, await stdout, await stderr);
    }

    /// <summary>Komutu çalıştırır, çıktısını satır satır toplar. İptalde süreci öldürür.</summary>
    public static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, Action<string>? log, CancellationToken ct, Action<int>? percent = null, IDictionary<string, string>? env = null)
    {
        using var p = Start(exe, args, env: env);
        using var reg = ct.Register(() => TryKill(p));
        var sb = new StringBuilder();
        await Task.WhenAll(PumpAsync(p.StandardOutput, sb, log, percent), PumpAsync(p.StandardError, sb, log, percent));
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return (p.ExitCode, sb.ToString());
    }
}

/// <summary>
/// Arşiv parolaları Windows DPAPI ile saklanır: zamanlanmış görev parolayı kullanıcı girmeden
/// okuyabilir, ama veritabanı başka bir Windows hesabına taşınırsa parola yeniden girilmelidir.
/// </summary>
public static class Secrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Backup360.v1");

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            throw new InvalidOperationException("Arşiv parolası bu Windows hesabında çözülemedi (uygulama başka bir bilgisayara taşınmış olabilir). İşi açıp parolayı yeniden girin.");
        }
    }
}

/// <summary>
/// Aynı işin ikinci kopyasını engeller. Dosya kilidi kullanılır: await sonrası başka iş parçacığından
/// bırakılabilir (Mutex bırakılamaz) ve süreç kapanınca Windows kilidi kendiliğinden çözer.
/// </summary>
public static class RunGate
{
    public static IDisposable? TryEnter(string key)
    {
        var dir = Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "locks")).FullName;
        try
        {
            return new FileStream(Path.Combine(dir, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Çalışan işin anlık durumu. Arka plandaki süreç yazar, pano okuyup canlandırır.</summary>
public sealed class LiveState
{
    public string Kind { get; set; } = "";
    public long RefId { get; set; }
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public string Mode { get; set; } = "archive";
    public string Stage { get; set; } = "";
    public string Item { get; set; } = "";
    public int Percent { get; set; } = -1;
    public double Speed { get; set; }
    public long Eta { get; set; } = -1;
    public long Done { get; set; }
    public long Total { get; set; }
    public int Files { get; set; }
    public int FilesTotal { get; set; }
    public int Step { get; set; }
    public int Steps { get; set; }
    public string Started { get; set; } = "";
    public List<string> Log { get; set; } = new();
    public List<string> Transfers { get; set; } = new();
    public string Key => $"{Kind}-{RefId}";
}

public static class LiveFiles
{
    public static string Dir => Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "live")).FullName;

    public static List<LiveState> ReadAll()
    {
        var list = new List<LiveState>();
        foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
        {
            try
            {
                var state = JsonSerializer.Deserialize<LiveState>(File.ReadAllText(file));
                if (state == null) continue;
                if (IsAlive(state.Pid)) list.Add(state);
                else File.Delete(file);   // Süreç kapanmış; artık kayıt kalmasın.
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return list;
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

public sealed class RunLog : IDisposable
{
    private readonly Action<string>? _ui;
    private readonly string _file;
    private readonly object _gate = new();
    private readonly LiveState? _live;
    private readonly string? _liveFile;
    private long _lastFlush;

    public RunLog(string title, Action<string>? ui, string kind = "", long refId = 0, string name = "", string mode = "archive")
    {
        _ui = ui;
        _file = Path.Combine(AppPaths.LogDir, $"backup360-{DateTime.Now:yyyyMM}.log");
        if (kind.Length > 0)
        {
            _live = new LiveState { Kind = kind, RefId = refId, Name = name, Mode = mode, Pid = Environment.ProcessId, Started = DateTime.Now.ToString("HH:mm:ss") };
            _liveFile = Path.Combine(LiveFiles.Dir, $"{_live.Key}.json");
        }
        Write($"=== {title} ===");
    }

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        lock (_gate)
        {
            try { File.AppendAllText(_file, line + Environment.NewLine); } catch (IOException) { }
            if (_live != null)
            {
                _live.Log.Add(line[11..]);
                if (_live.Log.Count > 60) _live.Log.RemoveAt(0);
                Flush(force: false);   // Çok dosyalı aktarımda diski yormasın
            }
        }
        _ui?.Invoke(line);
    }

    public void Update(Action<LiveState> change)
    {
        if (_live == null) return;
        lock (_gate)
        {
            change(_live);
            Flush(force: false);
        }
    }

    private void Flush(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastFlush < 200) return;
        _lastFlush = now;
        try
        {
            var temp = _liveFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_live));
            File.Move(temp, _liveFile!, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_liveFile == null) return;
        lock (_gate)
        {
            try { File.Delete(_liveFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
