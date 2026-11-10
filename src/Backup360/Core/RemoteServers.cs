using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using FluentFTP;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Backup360.Core;

/// <summary>S3 uyumlu depolama. Kök, hedefte tanımlanan bucket'tır.</summary>
public sealed class S3Remote : Remote
{
    private static readonly Regex Md5 = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    public override bool KeepsModTime => false;

    public S3Remote(TargetDefinition d)
    {
        // Yeni varsayılan sağlama toplamı başlıklarını S3 uyumlu sunucuların çoğu tanımaz.
        var config = new AmazonS3Config { RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED, ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED };
        var region = string.IsNullOrWhiteSpace(d.Region) ? "us-east-1" : d.Region.Trim();
        if (string.IsNullOrWhiteSpace(d.Endpoint)) config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        else
        {
            config.ServiceURL = d.Endpoint.Trim();
            config.ForcePathStyle = true;
            config.AuthenticationRegion = region;
        }
        _client = new AmazonS3Client(new BasicAWSCredentials(d.AccessKey.Trim(), d.SecretKey.Trim()), config);
        _bucket = d.Path.Trim().Trim('/');
    }

    private static string Prefix(string path) => Clean(path).Length == 0 ? "" : Clean(path) + "/";

    private static RemoteItem Map(S3Object o, string path)
    {
        var tag = (o.ETag ?? "").Trim('"').ToLowerInvariant();
        return new RemoteItem(path, o.Size ?? 0, false, o.LastModified?.ToUniversalTime(), Md5.IsMatch(tag) ? tag : "");
    }

    private async Task<List<S3Object>> ObjectsAsync(string prefix, string? delimiter, List<string>? folders, CancellationToken ct)
    {
        var list = new List<S3Object>();
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, Delimiter = delimiter };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, ct);
            list.AddRange(response.S3Objects ?? new List<S3Object>());
            folders?.AddRange(response.CommonPrefixes ?? new List<string>());
            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);
        return list;
    }

    public override async Task<List<RemoteItem>> ListAsync(string path, CancellationToken ct)
    {
        var prefix = Prefix(path);
        var folders = new List<string>();
        var objects = await ObjectsAsync(prefix, "/", folders, ct);
        var list = folders.Select(f => new RemoteItem(f[prefix.Length..].TrimEnd('/'), 0, true, null, "")).ToList();
        list.AddRange(objects.Where(o => o.Key != prefix).Select(o => Map(o, o.Key[prefix.Length..])));
        return list;
    }

    public override async Task<List<RemoteItem>> ListTreeAsync(string path, bool hashes, CancellationToken ct, int maxDepth = int.MaxValue)
    {
        var prefix = Prefix(path);
        var result = new List<RemoteItem>();
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in await ObjectsAsync(prefix, null, null, ct))
        {
            var rel = o.Key[prefix.Length..];
            if (rel.Length == 0) continue;
            var parts = rel.TrimEnd('/').Split('/');
            for (var i = 1; i < parts.Length || (i == parts.Length && rel.EndsWith('/')); i++)
            {
                var folder = string.Join("/", parts.Take(i));
                if (i <= maxDepth && folders.Add(folder)) result.Add(new RemoteItem(folder, 0, true, null, ""));
            }
            if (!rel.EndsWith('/') && parts.Length <= maxDepth) result.Add(Map(o, rel));
        }
        return result;
    }

    public override async Task UploadAsync(string local, string path, Action<long>? progress, CancellationToken ct)
    {
        using var transfer = new TransferUtility(_client);
        var request = new TransferUtilityUploadRequest { BucketName = _bucket, Key = Clean(path), FilePath = local, PartSize = 16 * 1024 * 1024 };
        request.UploadProgressEvent += (_, e) => progress?.Invoke(e.TransferredBytes);
        await transfer.UploadAsync(request, ct);
    }

    public override async Task DownloadAsync(string path, string local, Action<long>? progress, CancellationToken ct)
    {
        using var response = await _client.GetObjectAsync(_bucket, Clean(path), ct);
        await Web.SaveAsync(response.ResponseStream, local, progress, null, ct);
    }

    public override async Task DeleteAsync(string path, bool isDir, CancellationToken ct)
    {
        if (!isDir)
        {
            await _client.DeleteObjectAsync(_bucket, Clean(path), ct);
            return;
        }
        foreach (var o in await ObjectsAsync(Prefix(path), null, null, ct)) await _client.DeleteObjectAsync(_bucket, o.Key, ct);
    }

    public override Task MkdirAsync(string path, CancellationToken ct) =>
        _client.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = Prefix(path), ContentBody = "" }, ct);

    public override async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        var (source, target) = (Clean(from), Clean(to));
        var keys = (await ObjectsAsync(source + "/", null, null, ct)).Select(o => o.Key).ToList();
        if (keys.Count == 0) keys.Add(source);
        foreach (var key in keys)
        {
            await CopyAsync(key, target + key[source.Length..], ct);
            await _client.DeleteObjectAsync(_bucket, key, ct);
        }
    }

    /// <summary>Sunucu tarafında kopyalar. S3 uyumlu bazı sunucular bunu desteklemez; o zaman indirip yeniden yükler.</summary>
    private async Task CopyAsync(string from, string to, CancellationToken ct)
    {
        try
        {
            await _client.CopyObjectAsync(_bucket, from, _bucket, to, ct);
            return;
        }
        catch (AmazonS3Exception e) when (e.StatusCode != HttpStatusCode.NotFound) { }
        var temp = Path.Combine(WorkFolder.CreateWork(), "kopya.bin");
        try
        {
            await DownloadAsync(from, temp, null, ct);
            await UploadAsync(temp, to, null, ct);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(temp)!, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public override ValueTask DisposeAsync()
    {
        _client.Dispose();
        return default;
    }
}

/// <summary>SFTP. Yollar oturumun açıldığı klasöre göredir.</summary>
public sealed class SftpRemote : Remote
{
    private readonly SftpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SftpRemote(TargetDefinition d)
    {
        _client = new SftpClient(d.Host.Trim(), d.Port > 0 ? d.Port : 22, d.User.Trim(), d.Password) { OperationTimeout = TimeSpan.FromMinutes(2) };
        _client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
    }

    private static string P(string path) => Clean(path).Length == 0 ? "." : Clean(path);

    private async Task<T> RunAsync<T>(Func<SftpClient, T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                if (!_client.IsConnected) _client.Connect();
                return work(_client);
            }, ct);
        }
        catch (SftpPathNotFoundException e) { throw new DirectoryNotFoundException(e.Message); }
        catch (SshAuthenticationException) { throw new IOException("Kullanıcı adı veya şifre kabul edilmedi."); }
        catch (Exception e) when (e is SshException or System.Net.Sockets.SocketException) { throw new IOException(e.Message, e); }
        finally { _gate.Release(); }
    }

    private Task RunAsync(Action<SftpClient> work, CancellationToken ct) => RunAsync(c => { work(c); return 0; }, ct);

    public override Task<List<RemoteItem>> ListAsync(string path, CancellationToken ct) => RunAsync(c =>
        c.ListDirectory(P(path)).Where(f => f.Name is not ("." or ".."))
            .Select(f => new RemoteItem(f.Name, f.IsDirectory ? 0 : f.Length, f.IsDirectory, f.LastWriteTimeUtc, "")).ToList(), ct);

    private static void MakeDirs(SftpClient c, string path)
    {
        var walked = "";
        foreach (var part in Clean(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            walked = Join(walked, part);
            if (!c.Exists(walked)) c.CreateDirectory(walked);
        }
    }

    public override Task MkdirAsync(string path, CancellationToken ct) => RunAsync(c => MakeDirs(c, path), ct);

    public override Task UploadAsync(string local, string path, Action<long>? progress, CancellationToken ct) => RunAsync(c =>
    {
        MakeDirs(c, Split(path).Parent);
        using (var file = File.OpenRead(local)) c.UploadFile(file, P(path), true, done => { ct.ThrowIfCancellationRequested(); progress?.Invoke((long)done); });
        // Bazı sunucular zaman ayarına izin vermez; dosya yine de yüklenmiştir.
        try { c.SetLastWriteTimeUtc(P(path), File.GetLastWriteTimeUtc(local)); } catch (SshException) { }
    }, ct);

    public override async Task DownloadAsync(string path, string local, Action<long>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(local))!);
        var modified = await RunAsync(c =>
        {
            using (var file = File.Create(local)) c.DownloadFile(P(path), file, done => { ct.ThrowIfCancellationRequested(); progress?.Invoke((long)done); });
            return c.GetLastWriteTimeUtc(P(path));
        }, ct);
        if (modified.Year > 1980) File.SetLastWriteTimeUtc(local, modified);
    }

    private static void Purge(SftpClient c, string path)
    {
        foreach (var f in c.ListDirectory(path).Where(f => f.Name is not ("." or "..")))
        {
            if (f.IsDirectory) Purge(c, path + "/" + f.Name);
            else c.DeleteFile(path + "/" + f.Name);
        }
        c.DeleteDirectory(path);
    }

    public override Task DeleteAsync(string path, bool isDir, CancellationToken ct) => RunAsync(c =>
    {
        if (!c.Exists(P(path))) return;
        if (isDir) Purge(c, P(path));
        else c.DeleteFile(P(path));
    }, ct);

    public override Task MoveAsync(string from, string to, CancellationToken ct) => RunAsync(c =>
    {
        MakeDirs(c, Split(to).Parent);
        if (c.Exists(P(to))) c.DeleteFile(P(to));
        c.RenameFile(P(from), P(to));
    }, ct);

    public override ValueTask DisposeAsync()
    {
        _client.Dispose();
        return default;
    }
}

/// <summary>FTP ve FTPS. Yollar oturumun açıldığı klasöre göredir.</summary>
public sealed class FtpRemote : Remote
{
    private readonly AsyncFtpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _home;
    private bool _keepsTime;

    public override bool KeepsModTime => _keepsTime;

    public FtpRemote(TargetDefinition d)
    {
        _client = new AsyncFtpClient(d.Host.Trim(), d.User.Trim(), d.Password, d.Port > 0 ? d.Port : 21);
        _client.Config.EncryptionMode = d.Tls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None;
        // NAS cihazları çoğunlukla kendi imzaladığı sertifikayı kullanır.
        _client.Config.ValidateAnyCertificate = true;
        _client.Config.TimeConversion = FtpDate.UTC;
        _client.Config.ConnectTimeout = 30000;
        _client.Config.RetryAttempts = 2;
    }

    private string P(string path) => Clean(path).Length == 0 ? _home! : _home!.TrimEnd('/') + "/" + Clean(path);

    private async Task<T> RunAsync<T>(Func<AsyncFtpClient, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_client.IsConnected)
            {
                await _client.Connect(ct);
                _home ??= await _client.GetWorkingDirectory(ct);
                _keepsTime = _client.HasFeature(FtpCapability.MFMT);
            }
            return await work(_client);
        }
        catch (FluentFTP.Exceptions.FtpAuthenticationException) { throw new IOException("Kullanıcı adı veya şifre kabul edilmedi."); }
        catch (Exception e) when (e is FluentFTP.Exceptions.FtpException or System.Net.Sockets.SocketException or TimeoutException) { throw new IOException(e.InnerException?.Message ?? e.Message, e); }
        finally { _gate.Release(); }
    }

    private Task RunAsync(Func<AsyncFtpClient, Task> work, CancellationToken ct) => RunAsync(async c => { await work(c); return 0; }, ct);

    public override Task<List<RemoteItem>> ListAsync(string path, CancellationToken ct) => RunAsync(async c =>
    {
        var items = await c.GetListing(P(path), ct);
        if (items.Length == 0 && Clean(path).Length > 0 && !await c.DirectoryExists(P(path), ct)) throw new DirectoryNotFoundException("Klasör bulunamadı: " + path);
        return items.Where(i => i.Type is FtpObjectType.File or FtpObjectType.Directory)
            .Select(i => new RemoteItem(i.Name, i.Type == FtpObjectType.Directory ? 0 : i.Size, i.Type == FtpObjectType.Directory,
                i.Modified.Year > 1980 ? DateTime.SpecifyKind(i.Modified, DateTimeKind.Utc) : null, "")).ToList();
    }, ct);

    public override Task MkdirAsync(string path, CancellationToken ct) => RunAsync(c => c.CreateDirectory(P(path), true, ct), ct);

    public override Task UploadAsync(string local, string path, Action<long>? progress, CancellationToken ct) => RunAsync(async c =>
    {
        var status = await c.UploadFile(local, P(path), FtpRemoteExists.Overwrite, true, FtpVerify.None, new Progress(progress), ct);
        if (status != FtpStatus.Success) throw new IOException("FTP sunucusu dosyayı kabul etmedi: " + path);
        if (_keepsTime) await c.SetModifiedTime(P(path), File.GetLastWriteTimeUtc(local), ct);
    }, ct);

    public override Task DownloadAsync(string path, string local, Action<long>? progress, CancellationToken ct) => RunAsync(async c =>
    {
        var status = await c.DownloadFile(local, P(path), FtpLocalExists.Overwrite, FtpVerify.None, new Progress(progress), ct);
        if (status != FtpStatus.Success) throw new FileNotFoundException("Dosya hedeften indirilemedi: " + path);
    }, ct);

    public override Task DeleteAsync(string path, bool isDir, CancellationToken ct) => RunAsync(async c =>
    {
        if (isDir) { if (await c.DirectoryExists(P(path), ct)) await c.DeleteDirectory(P(path), ct); }
        else if (await c.FileExists(P(path), ct)) await c.DeleteFile(P(path), ct);
    }, ct);

    public override Task MoveAsync(string from, string to, CancellationToken ct) => RunAsync(async c =>
    {
        await c.CreateDirectory(P(Split(to).Parent), true, ct);
        if (await c.DirectoryExists(P(from), ct)) await c.MoveDirectory(P(from), P(to), FtpRemoteExists.Overwrite, ct);
        else await c.MoveFile(P(from), P(to), FtpRemoteExists.Overwrite, ct);
    }, ct);

    private sealed class Progress(Action<long>? sink) : IProgress<FtpProgress>
    {
        public void Report(FtpProgress value) => sink?.Invoke(value.TransferredBytes);
    }

    public override async ValueTask DisposeAsync() => await _client.DisposeAsync();
}

/// <summary>WebDAV (Nextcloud, ownCloud, NAS).</summary>
public sealed class WebDavRemote : Remote
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly HttpMethod Propfind = new("PROPFIND"), Mkcol = new("MKCOL"), MoveMethod = new("MOVE");
    private readonly Uri _base;
    private readonly AuthenticationHeaderValue _auth;
    private readonly HashSet<string> _made = new(StringComparer.Ordinal);

    public override bool KeepsModTime => false;

    public WebDavRemote(TargetDefinition d)
    {
        _base = new Uri(d.Url.Trim().TrimEnd('/') + "/");
        _auth = d.Auth == "token"
            ? new AuthenticationHeaderValue("Bearer", d.Token.Trim())
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{d.User}:{d.Password}")));
    }

    private Uri Url(string path, bool folder)
    {
        var clean = Clean(path);
        var escaped = string.Join("/", clean.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return new Uri(_base, escaped + (folder && escaped.Length > 0 ? "/" : ""));
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct, bool headersOnly = false, int seconds = 120)
    {
        var response = await Web.IdleAsync(tok =>
        {
            var request = make();
            request.Headers.Authorization = _auth;
            return Web.Client.SendAsync(request, headersOnly ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, tok);
        }, ct, seconds);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new IOException("Kullanıcı adı, şifre veya token kabul edilmedi.");
        }
        return response;
    }

    private static string PathOf(Uri uri) => Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');

    public override async Task<List<RemoteItem>> ListAsync(string path, CancellationToken ct)
    {
        var url = Url(path, true);
        const string body = "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:propfind xmlns:D=\"DAV:\"><D:prop><D:resourcetype/><D:getcontentlength/><D:getlastmodified/></D:prop></D:propfind>";
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(Propfind, url) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
            request.Headers.Add("Depth", "1");
            return request;
        }, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new DirectoryNotFoundException("Klasör bulunamadı: " + path);
        await ApiClient.EnsureAsync(response, ct);
        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var self = PathOf(url);
        var list = new List<RemoteItem>();
        foreach (var r in doc.Descendants(Dav + "response"))
        {
            var href = r.Element(Dav + "href")?.Value ?? "";
            var full = PathOf(new Uri(_base, href));
            if (string.Equals(full, self, StringComparison.Ordinal)) continue;
            var prop = r.Descendants(Dav + "prop").FirstOrDefault(p => p.Elements().Any());
            var dir = prop?.Element(Dav + "resourcetype")?.Element(Dav + "collection") != null;
            long.TryParse(prop?.Element(Dav + "getcontentlength")?.Value, out var size);
            DateTime? modified = DateTime.TryParse(prop?.Element(Dav + "getlastmodified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
            list.Add(new RemoteItem(full[(full.LastIndexOf('/') + 1)..], dir ? 0 : size, dir, modified, ""));
        }
        return list;
    }

    public override async Task MkdirAsync(string path, CancellationToken ct)
    {
        var walked = "";
        foreach (var part in Clean(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            walked = Join(walked, part);
            lock (_made) if (_made.Contains(walked)) continue;
            var url = Url(walked, true);
            using var response = await SendAsync(() => new HttpRequestMessage(Mkcol, url), ct);
            // 405: klasör zaten var.
            if (response.StatusCode != HttpStatusCode.MethodNotAllowed) await ApiClient.EnsureAsync(response, ct);
            lock (_made) _made.Add(walked);
        }
    }

    public override async Task UploadAsync(string local, string path, Action<long>? progress, CancellationToken ct)
    {
        await MkdirAsync(Split(path).Parent, ct);
        var seconds = new DateTimeOffset(File.GetLastWriteTimeUtc(local)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        await using var file = File.OpenRead(local);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Put, Url(path, false)) { Content = new StreamContent(new ProgressStream(file, progress)) };
            // Nextcloud ve ownCloud bu başlıkla dosyanın özgün zamanını korur.
            request.Headers.Add("X-OC-Mtime", seconds);
            return request;
        }, ct, seconds: 6 * 3600);
        await ApiClient.EnsureAsync(response, ct);
    }

    public override async Task DownloadAsync(string path, string local, Action<long>? progress, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path, false)), ct, headersOnly: true);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException("Dosya hedefte bulunamadı: " + path);
        await ApiClient.EnsureAsync(response, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await Web.SaveAsync(body, local, progress, response.Content.Headers.LastModified?.UtcDateTime, ct);
    }

    public override async Task DeleteAsync(string path, bool isDir, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, Url(path, isDir)), ct);
        if (response.StatusCode != HttpStatusCode.NotFound) await ApiClient.EnsureAsync(response, ct);
        lock (_made) _made.RemoveWhere(m => m == Clean(path) || m.StartsWith(Clean(path) + "/", StringComparison.Ordinal));
    }

    public override async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        await MkdirAsync(Split(to).Parent, ct);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(MoveMethod, Url(from, false));
            request.Headers.Add("Destination", Url(to, false).AbsoluteUri);
            request.Headers.Add("Overwrite", "T");
            return request;
        }, ct);
        await ApiClient.EnsureAsync(response, ct);
    }
}
