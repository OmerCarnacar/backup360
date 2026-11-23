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
/// <summary>
/// Yedek sürerken bilgisayarın boşta kalıp uykuya geçmesini engeller; ekran kapanabilir. Kullanıcının
/// kendisi uyku ya da kapatma seçerse Windows yine uyur. Çağıran iş parçacığı yaşadığı sürece geçerlidir.
/// </summary>
public sealed class KeepAwake : IDisposable
{
    private const uint Continuous = 0x80000000, SystemRequired = 0x00000001;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private KeepAwake() { }

    public static KeepAwake Begin()
    {
        SetThreadExecutionState(Continuous | SystemRequired);
        return new KeepAwake();
    }

    public void Dispose() => SetThreadExecutionState(Continuous);
}

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
/// <summary>Sıkıştırma sürerken arka planda yapılan bir gönderim.</summary>
public sealed record SendLine(string Label, string Stage, int Percent, double Speed, long Eta);

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
    public List<SendLine> Sending { get; set; } = new();
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
    private readonly (string Kind, long RefId, string Name) _owner;
    private readonly DateTime _started = DateTime.Now;
    private StreamWriter? _writer;
    private long _lastFlush;
    // Çalışmaya ait ayrı günlük: kayıt numarası belli olana kadar satırlar bekletilir.
    private List<string>? _runBuffer;
    private StreamWriter? _runWriter;

    /// <summary>Çalışma başına günlük dosyalarının klasörü.</summary>
    public static string RunsDir => Directory.CreateDirectory(Path.Combine(AppPaths.LogDir, "calismalar")).FullName;

    public static string RunFile(long runId) => Path.Combine(RunsDir, $"{runId}.log");

    /// <summary>
    /// İşi kimin başlattığı. Uygulama işi başlatırken bunu ortam değişkeniyle bildirir; değişken yoksa iş
    /// Windows Görev Zamanlayıcı tarafından başlatılmıştır.
    /// </summary>
    public const string TriggerVariable = "BACKUP360_TRIGGER";

    public static string Trigger() => Environment.GetEnvironmentVariable(TriggerVariable) switch
    {
        "kullanici" => $"{Environment.UserName} kullanıcısı",
        "telafi" => "kaçırılan zamanlamanın telafisi",
        "grup" => "grup çalıştırması",
        _ => "zamanlayıcı",
    };

    /// <summary>Çalışmanın ilk satırı: ne, nerede, kim tarafından, hangi sürümle.</summary>
    public static string StartedBy(string what, string name) =>
        $"\"{name}\" {what} \"{Environment.MachineName}\" üzerinde {Trigger()} tarafından başlatıldı " +
        $"(sürüm: {Backup360.AppInfo.Version}, Windows {Environment.OSVersion.Version}, {(Environment.Is64BitProcess ? "64" : "32")} bit)";

    /// <summary>Çalışmanın son satırı: durum, süre, boyut ve ayrıntı tek satırda.</summary>
    public static string Summary(string status, string detail, long bytes, DateTime started)
    {
        var text = status switch
        {
            "success" => "Başarılı", "warning" => "Uyarılı", "failed" => "Hatalı", "canceled" => "İptal edildi",
            "corrupt" => "YEDEK BOZUK", "ransomware" => "Durduruldu (fidye şüphesi)", "skipped" => "Atlandı", _ => status,
        };
        var took = DateTime.Now - started;
        var size = bytes <= 0 ? "veri aktarılmadı"
            : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : $"{bytes / (double)(1L << 20):0.0} MB";
        return $"Özet: {text} · süre {(int)took.TotalHours:00}:{took.Minutes:00}:{took.Seconds:00} · {size}" + (detail.Length > 0 ? $" · {detail}" : "");
    }

    /// <summary>Bu günlüğü bir çalışma kaydına bağlar; o andan önceki satırlar da çalışmanın dosyasına yazılır.</summary>
    public void AttachRun(long runId)
    {
        lock (_gate)
        {
            try
            {
                PruneRunFiles();
                _runWriter = new StreamWriter(new FileStream(RunFile(runId), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(true)) { AutoFlush = true };
                foreach (var line in _runBuffer ?? new List<string>()) _runWriter.WriteLine(line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _runWriter = null; }
            _runBuffer = null;
        }
    }

    /// <summary>180 günden eski çalışma günlükleri silinir.</summary>
    private static void PruneRunFiles()
    {
        foreach (var file in new DirectoryInfo(RunsDir).EnumerateFiles("*.log"))
            if (file.LastWriteTime < DateTime.Now.AddDays(-180))
                try { file.Delete(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private static readonly Mutex FileGate = new(false, "Backup360-Gunluk");

    public RunLog(string title, Action<string>? ui, string kind = "", long refId = 0, string name = "", string mode = "archive")
    {
        _ui = ui;
        _owner = kind.Length > 0 ? (kind, refId, name) : ("sistem", 0, title);
        if (kind.Length > 0) _runBuffer = new List<string>();
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
            try
            {
                _writer ??= new StreamWriter(new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
                // Aynı anda çalışan iki iş aynı dosyaya yazar. Her süreç kendi konumunu tuttuğu için sıra
                // beklenmeden yazılırsa satırlar birbirinin üzerine biner; yazmadan önce dosyanın sonuna gidilir.
                var held = false;
                try { held = FileGate.WaitOne(3000); }
                catch (AbandonedMutexException) { held = true; }
                try
                {
                    _writer.BaseStream.Seek(0, SeekOrigin.End);
                    _writer.WriteLine(line);
                }
                finally { if (held) FileGate.ReleaseMutex(); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _writer = null; }
            if (_runBuffer != null) _runBuffer.Add(line);
            else if (_runWriter != null)
                try { _runWriter.WriteLine(line); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _runWriter = null; }
            if (_live != null)
            {
                _live.Log.Add(line[11..]);
                if (_live.Log.Count > 60) _live.Log.RemoveAt(0);
                Flush(force: false);   // Çok dosyalı aktarımda diski yormasın
            }
        }
        Problems.Capture(_owner.Kind, _owner.RefId, _owner.Name, message, _started);
        _ui?.Invoke(line);
    }

    /// <summary>İletisi boş hatalar da anlaşılır olsun: ileti yoksa hatanın türü yazılır.</summary>
    public static string Describe(Exception e) =>
        string.IsNullOrWhiteSpace(e.Message) ? $"Beklenmeyen hata ({e.GetType().Name}). Ayrıntı günlük dosyasında." : e.Message;

    /// <summary>Hatanın tam dökümünü yalnızca günlük dosyasına yazar; ekrandaki akışı kalabalıklaştırmaz.</summary>
    public void Trace(Exception e)
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
                var lines = e.ToString().Split('\n').Select(l => "      | " + l.TrimEnd()).ToList();
                File.AppendAllLines(_file, lines);
                foreach (var line in lines) _runWriter?.WriteLine(line);
            }
            catch (IOException) { }
        }
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
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            _runWriter?.Dispose();
            _runWriter = null;
        }
        if (_liveFile == null) return;
        lock (_gate)
        {
            try { File.Delete(_liveFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
