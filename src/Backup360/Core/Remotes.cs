using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Backup360.Core;

/// <summary>Hedefteki dosya veya klasör. Path listelenen klasöre göredir, ayraç '/'. Modified UTC'dir.</summary>
public sealed record RemoteItem(string Path, long Size, bool IsDir, DateTime? Modified, string Md5)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>Sağlayıcı hatası; iletisi kullanıcıya gösterilecek biçimde hazırlanmıştır.</summary>
public sealed class RemoteException(string message, Exception inner) : IOException(message, inner);

/// <summary>Bir hedefe doğrudan kendi protokolü veya API'si ile erişir.</summary>
public abstract class Remote : IAsyncDisposable
{
    /// <summary>Yüklenen dosyanın değiştirilme zamanı hedefte korunuyorsa true.</summary>
    public virtual bool KeepsModTime => true;

    /// <summary>Klasörün doğrudan içeriği. Klasör yoksa DirectoryNotFoundException.</summary>
    public abstract Task<List<RemoteItem>> ListAsync(string path, CancellationToken ct);
    /// <summary>Üst klasörleri oluşturur, var olan dosyanın üzerine yazar.</summary>
    public abstract Task UploadAsync(string local, string path, Action<long>? progress, CancellationToken ct);
    public abstract Task DownloadAsync(string path, string local, Action<long>? progress, CancellationToken ct);
    public abstract Task DeleteAsync(string path, bool isDir, CancellationToken ct);
    public abstract Task MkdirAsync(string path, CancellationToken ct);
    public abstract Task MoveAsync(string from, string to, CancellationToken ct);
    public virtual ValueTask DisposeAsync() => default;

    /// <summary>Alt klasörlerle birlikte tüm içerik. Klasör yoksa boş liste döner.</summary>
    public virtual async Task<List<RemoteItem>> ListTreeAsync(string path, bool hashes, CancellationToken ct, int maxDepth = int.MaxValue)
    {
        var result = new List<RemoteItem>();
        async Task Walk(string folder, string prefix, int depth)
        {
            List<RemoteItem> items;
            try { items = await ListAsync(folder, ct); }
            catch (DirectoryNotFoundException) { return; }
            foreach (var item in items)
            {
                var rel = prefix + item.Path;
                result.Add(item with { Path = rel });
                if (item.IsDir && depth < maxDepth) await Walk(Join(folder, item.Path), rel + "/", depth + 1);
            }
        }
        await Walk(Clean(path), "", 1);
        return result;
    }

    public virtual async Task<RemoteItem?> StatAsync(string path, bool hash, CancellationToken ct)
    {
        var (parent, name) = Split(path);
        try { return (await ListAsync(parent, ct)).FirstOrDefault(i => i.Path == name); }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static string Clean(string? path) => (path ?? "").Replace('\\', '/').Trim('/');

    public static string Join(string a, string b)
    {
        (a, b) = (Clean(a), Clean(b));
        return a.Length == 0 ? b : b.Length == 0 ? a : a + "/" + b;
    }

    public static (string Parent, string Name) Split(string path)
    {
        path = Clean(path);
        var cut = path.LastIndexOf('/');
        return cut < 0 ? ("", path) : (path[..cut], path[(cut + 1)..]);
    }
}

/// <summary>Ağ işlemleri için ortak araçlar: tek HttpClient, zaman aşımı ve ilerleme bildiren akışlar.</summary>
public static class Web
{
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Yanıt vermeyen bağlantı işi sonsuza kadar bekletmesin; kullanıcının iptali ayrı tutulur.</summary>
    public static async Task<T> IdleAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct, int seconds = 120)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { return await work(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Bağlantı yanıt vermedi (zaman aşımı)."); }
    }

    public static async Task CopyAsync(Stream from, Stream to, Action<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        long total = 0;
        while (true)
        {
            var read = await IdleAsync(tok => from.ReadAsync(buffer, tok).AsTask(), ct);
            if (read == 0) break;
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
            progress?.Invoke(total);
        }
    }

    /// <summary>Akışı önce geçici ada yazar; yarım kalan indirme hedef dosyayı bozmaz.</summary>
    public static async Task SaveAsync(Stream body, string local, Action<long>? progress, DateTime? modifiedUtc, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(local))!);
        var temp = local + ".b360indir";
        try
        {
            await using (var file = File.Create(temp)) await CopyAsync(body, file, progress, ct);
            File.Move(temp, local, true);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (modifiedUtc is { } time && time.Year > 1980) File.SetLastWriteTimeUtc(local, time);
    }

    public static Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct) => ReadFullAsync(stream, buffer.AsMemory(), ct);

    public static async Task<int> ReadFullAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    public static string Trim(string text, int max = 400) => text.Length <= max ? text : text[..max];
}

/// <summary>Okunan bayt sayısını bildiren akış; yükleme ilerlemesi için.</summary>
public sealed class ProgressStream(Stream inner, Action<long>? progress) : Stream
{
    private long _total;
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set { inner.Position = value; _total = value; } }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => _total = inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Report(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await inner.ReadAsync(buffer, ct);
        Report(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private void Report(int read)
    {
        if (read <= 0) return;
        _total += read;
        progress?.Invoke(_total);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Bellekteki parçayı dilimler halinde gönderir ve ilerlemeyi bildirir.</summary>
public sealed class ProgressContent(byte[] buffer, int count, Action<long>? progress) : HttpContent
{
    private const int Slice = 256 * 1024;

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken ct)
    {
        for (var position = 0; position < count; position += Slice)
        {
            var size = Math.Min(Slice, count - position);
            await stream.WriteAsync(buffer.AsMemory(position, size), ct);
            progress?.Invoke(position + size);
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = count;
        return true;
    }
}

/// <summary>Yerel klasör, ağ yolu (UNC) ve SMB paylaşımı.</summary>
public sealed class LocalRemote(string root) : Remote
{
    private string Full(string path)
    {
        var clean = Clean(path);
        if (clean.Split('/').Contains("..")) throw new ArgumentException("Geçersiz yol: " + path);
        return clean.Length == 0 ? root : Path.Combine(root, clean.Replace('/', Path.DirectorySeparatorChar));
    }

    public static LocalRemote Smb(TargetDefinition d)
    {
        var share = $@"\\{d.Host.Trim().Trim('\\')}\{d.Path.Trim().Replace('/', '\\').Trim('\\')}";
        var resource = new NetResource { Type = 1, RemoteName = share };
        var code = WNetAddConnection2(ref resource, d.Password, d.User, 0);
        // 1219: aynı sunucuya başka kimlikle zaten bağlı; mevcut bağlantı kullanılır.
        if (code != 0 && code != 1219) throw new IOException($"{share}: {new Win32Exception(code).Message}");
        return new LocalRemote(share);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope, Type, DisplayType, Usage;
        public string? LocalName, RemoteName, Comment, Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? user, int flags);

    private static RemoteItem Map(FileSystemInfo e, string path, bool hash) => e is FileInfo f
        ? new RemoteItem(path, f.Length, false, f.LastWriteTimeUtc, hash ? BackupAudit.Md5Of(f.FullName) : "")
        : new RemoteItem(path, 0, true, e.LastWriteTimeUtc, "");

    public override Task<List<RemoteItem>> ListAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        var dir = new DirectoryInfo(Full(path));
        if (!dir.Exists) throw new DirectoryNotFoundException("Klasör bulunamadı: " + dir.FullName);
        return dir.EnumerateFileSystemInfos().Select(e => Map(e, e.Name, false)).ToList();
    }, ct);

    public override Task<List<RemoteItem>> ListTreeAsync(string path, bool hashes, CancellationToken ct, int maxDepth = int.MaxValue) => Task.Run(() =>
    {
        var result = new List<RemoteItem>();
        void Walk(DirectoryInfo dir, string prefix, int depth)
        {
            foreach (var e in dir.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                result.Add(Map(e, prefix + e.Name, hashes));
                if (e is DirectoryInfo sub && depth < maxDepth) Walk(sub, prefix + e.Name + "/", depth + 1);
            }
        }
        var start = new DirectoryInfo(Full(path));
        if (start.Exists) Walk(start, "", 1);
        return result;
    }, ct);

    public override Task<RemoteItem?> StatAsync(string path, bool hash, CancellationToken ct) => Task.Run(() =>
    {
        var full = Full(path);
        var name = Split(path).Name;
        if (File.Exists(full)) return Map(new FileInfo(full), name, hash);
        return Directory.Exists(full) ? Map(new DirectoryInfo(full), name, false) : null;
    }, ct);

    public override async Task UploadAsync(string local, string path, Action<long>? progress, CancellationToken ct)
    {
        var full = Full(path);
        // Dosya zaten yerindeyse (kaynak ve hedef aynı yol) kendi üzerine kopyalanmaz.
        if (string.Equals(Path.GetFullPath(local), Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var source = File.OpenRead(local);
        await Web.SaveAsync(source, full, progress, File.GetLastWriteTimeUtc(local), ct);
    }

    public override async Task DownloadAsync(string path, string local, Action<long>? progress, CancellationToken ct)
    {
        var full = Full(path);
        await using var source = File.OpenRead(full);
        await Web.SaveAsync(source, local, progress, File.GetLastWriteTimeUtc(full), ct);
    }

    public override Task DeleteAsync(string path, bool isDir, CancellationToken ct)
    {
        var full = Full(path);
        if (isDir) { if (Directory.Exists(full)) Directory.Delete(full, true); }
        else File.Delete(full);
        return Task.CompletedTask;
    }

    public override Task MkdirAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Full(path));
        return Task.CompletedTask;
    }

    public override Task MoveAsync(string from, string to, CancellationToken ct)
    {
        var (source, target) = (Full(from), Full(to));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(source)) Directory.Move(source, target);
        else File.Move(source, target, true);
        return Task.CompletedTask;
    }
}

/// <summary>Hedef tanımları veritabanında, tümü Windows DPAPI ile şifreli durur.</summary>
public static class TargetStore
{
    public static List<Target> List() =>
        Db.Query("SELECT name,type FROM targets ORDER BY name COLLATE NOCASE", r => new Target(r.GetString(0), r.GetString(1)));

    public static bool Exists(string name) =>
        Db.Query("SELECT 1 FROM targets WHERE name=$n", _ => 1, ("$n", name)).Count > 0;

    public static TargetDefinition? Load(string name)
    {
        var data = Db.Query("SELECT data FROM targets WHERE name=$n", r => r.GetString(0), ("$n", name)).FirstOrDefault();
        if (data == null) return null;
        var json = Secrets.Unprotect(data) ?? throw new IOException($"{name}: hedef bilgileri bu Windows hesabıyla açılamıyor. Hedefi kaldırıp yeniden tanımlayın.");
        return JsonSerializer.Deserialize<TargetDefinition>(json);
    }

    public static void Save(TargetDefinition d) =>
        Db.Exec("INSERT INTO targets(name,type,data) VALUES($n,$t,$d) ON CONFLICT(name) DO UPDATE SET type=$t, data=$d",
            ("$n", d.Name), ("$t", d.Type), ("$d", Secrets.Protect(JsonSerializer.Serialize(d))));

    public static void Delete(string name) => Db.Exec("DELETE FROM targets WHERE name=$n", ("$n", name));

    public static void SaveToken(string name, string token)
    {
        if (Load(name) is not { } d) return;
        d.Token = token;
        Save(d);
    }

    /// <summary>
    /// Önceki sürümlerin hedef dosyasını bir kez okur. Klasör hedefleri aktarılır; bulut hesapları
    /// başka bir uygulamanın kimliğine bağlı olduğundan aktarılamaz, adları yeniden tanımlanmak üzere saklanır.
    /// </summary>
    public static void ImportLegacy()
    {
        var file = Path.Combine(AppPaths.DataDir, "rclone.conf");
        if (!File.Exists(file) || Db.GetSetting("legacy_targets") != null) return;
        var pending = new List<string>();
        string? name = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Flush()
        {
            if (name == null) return;
            if (values.GetValueOrDefault("type") is "alias" or "local" && values.GetValueOrDefault("remote") is { Length: > 0 } remote && !Exists(name))
                Save(new TargetDefinition { Name = name, Type = "alias", Auth = "none", Path = remote });
            else if (!Exists(name)) pending.Add(name);
            values.Clear();
        }
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                name = line[1..^1];
            }
            else if (line.IndexOf('=') is > 0 and var cut) values[line[..cut].Trim()] = line[(cut + 1)..].Trim();
        }
        Flush();
        Db.SetSetting("legacy_targets", string.Join("|", pending));
    }

    /// <summary>Önceki sürümden kalıp henüz yeniden tanımlanmamış hedeflerin adları.</summary>
    public static List<string> PendingLegacy() =>
        (Db.GetSetting("legacy_targets") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Where(n => !Exists(n)).ToList();
}
