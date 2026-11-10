using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Backup360.Core;

/// <summary>Google Drive: doğrudan Drive API v3.</summary>
public sealed class DriveRemote(ApiClient api, string scope = "") : Remote
{
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const string Fields = "id,name,mimeType,size,md5Checksum,modifiedTime,parents";
    private const int Chunk = 16 * 1024 * 1024;   // 256 KB'nin katı olmalı
    private readonly ConcurrentDictionary<string, string> _folders = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _create = new(1, 1);

    private static string Esc(string text) => text.Replace("\\", "\\\\").Replace("'", "\\'");
    private static string Id(JsonElement file) => file.GetProperty("id").GetString()!;
    private static bool Found(JsonElement file) => file.ValueKind == JsonValueKind.Object;

    private async Task<List<JsonElement>> QueryAsync(string query, CancellationToken ct)
    {
        var list = new List<JsonElement>();
        string? page = null;
        do
        {
            var url = $"{Endpoints.DriveApi}/files?q={Uri.EscapeDataString(query)}&fields={Uri.EscapeDataString($"nextPageToken,files({Fields})")}&pageSize=1000"
                      + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
            using var doc = await api.JsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
            if (doc.RootElement.TryGetProperty("files", out var files))
                foreach (var f in files.EnumerateArray()) list.Add(f.Clone());
            page = doc.RootElement.TryGetProperty("nextPageToken", out var token) ? token.GetString() : null;
        } while (!string.IsNullOrEmpty(page));
        return list;
    }

    private static RemoteItem Map(JsonElement f, string path)
    {
        var dir = f.GetProperty("mimeType").GetString() == FolderMime;
        long size = 0;
        if (f.TryGetProperty("size", out var s)) size = s.ValueKind == JsonValueKind.Number ? s.GetInt64() : long.TryParse(s.GetString(), out var n) ? n : 0;
        DateTime? modified = f.TryGetProperty("modifiedTime", out var m) && DateTime.TryParse(m.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        return new RemoteItem(path, size, dir, modified, f.TryGetProperty("md5Checksum", out var md5) ? md5.GetString() ?? "" : "");
    }

    private async Task<string?> FolderIdAsync(string path, bool create, CancellationToken ct)
    {
        path = Clean(path);
        if (path.Length == 0) return "root";
        if (_folders.TryGetValue(path, out var known)) return known;
        // Paralel yüklemeler aynı klasörü iki kez oluşturmasın.
        if (create) await _create.WaitAsync(ct);
        try
        {
            var (id, walked) = ("root", "");
            foreach (var part in path.Split('/'))
            {
                walked = Join(walked, part);
                if (_folders.TryGetValue(walked, out var cached)) { id = cached; continue; }
                var found = (await QueryAsync($"'{id}' in parents and name = '{Esc(part)}' and mimeType = '{FolderMime}' and trashed = false", ct)).FirstOrDefault();
                string next;
                if (Found(found)) next = Id(found);
                else if (!create) return null;
                else
                {
                    var parent = id;
                    using var doc = await api.JsonAsync(() => ApiClient.Json(HttpMethod.Post, $"{Endpoints.DriveApi}/files?fields=id", new { name = part, mimeType = FolderMime, parents = new[] { parent } }), ct);
                    next = Id(doc.RootElement);
                }
                _folders[walked] = next;
                id = next;
            }
            return id;
        }
        finally
        {
            if (create) _create.Release();
        }
    }

    private async Task<JsonElement> FindAsync(string path, bool? folder, CancellationToken ct)
    {
        var (parent, name) = Split(path);
        if (await FolderIdAsync(parent, false, ct) is not { } parentId) return default;
        var kind = folder switch { true => $" and mimeType = '{FolderMime}'", false => $" and mimeType != '{FolderMime}'", _ => "" };
        return (await QueryAsync($"'{parentId}' in parents and name = '{Esc(name)}'{kind} and trashed = false", ct)).FirstOrDefault();
    }

    private void Forget(string path)
    {
        path = Clean(path);
        foreach (var key in _folders.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList()) _folders.TryRemove(key, out _);
    }

    public override async Task<List<RemoteItem>> ListAsync(string path, CancellationToken ct)
    {
        var id = await FolderIdAsync(path, false, ct) ?? throw new DirectoryNotFoundException("Klasör bulunamadı: " + path);
        var list = new List<RemoteItem>();
        foreach (var f in await QueryAsync($"'{id}' in parents and trashed = false", ct))
        {
            var item = Map(f, f.GetProperty("name").GetString()!);
            if (item.IsDir) _folders[Join(path, item.Path)] = Id(f);
            list.Add(item);
        }
        return list;
    }

    /// <summary>Klasörleri tek tek sormak yerine her düzeyi toplu sorgular.</summary>
    public override async Task<List<RemoteItem>> ListTreeAsync(string path, bool hashes, CancellationToken ct, int maxDepth = int.MaxValue)
    {
        var result = new List<RemoteItem>();
        if (await FolderIdAsync(path, false, ct) is not { } rootId) return result;
        var frontier = new Dictionary<string, string> { [rootId] = "" };
        for (var depth = 1; frontier.Count > 0 && depth <= maxDepth; depth++)
        {
            var next = new Dictionary<string, string>();
            foreach (var group in frontier.Keys.Chunk(30))
            {
                var parents = string.Join(" or ", group.Select(id => $"'{id}' in parents"));
                foreach (var f in await QueryAsync($"({parents}) and trashed = false", ct))
                {
                    var parent = f.TryGetProperty("parents", out var p) ? p.EnumerateArray().Select(x => x.GetString()!).FirstOrDefault(frontier.ContainsKey) : null;
                    if (parent == null) continue;
                    var item = Map(f, frontier[parent] + f.GetProperty("name").GetString());
                    result.Add(item);
                    if (!item.IsDir) continue;
                    next[Id(f)] = item.Path + "/";
                    _folders[Join(path, item.Path)] = Id(f);
                }
            }
            frontier = next;
        }
        return result;
    }

    public override async Task<RemoteItem?> StatAsync(string path, bool hash, CancellationToken ct)
    {
        var found = await FindAsync(path, null, ct);
        return Found(found) ? Map(found, Split(path).Name) : null;
    }

    public override async Task MkdirAsync(string path, CancellationToken ct) => await FolderIdAsync(path, true, ct);

    private static HttpRequestMessage ChunkRequest(Uri session, HttpContent content, long? from, long? to, long size)
    {
        content.Headers.ContentRange = from is { } a && to is { } b ? new ContentRangeHeaderValue(a, b, size) : new ContentRangeHeaderValue(size);
        return new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
    }

    private static long? NextOffset(HttpResponseMessage response)
    {
        if (!response.Headers.NonValidated.TryGetValues("Range", out var values)) return null;
        var text = values.ToString();
        var dash = text.LastIndexOf('-');
        return dash >= 0 && long.TryParse(text[(dash + 1)..], out var last) ? last + 1 : null;
    }

    /// <summary>Yarım kalan oturumda sunucunun aldığı bayt sayısını sorar.</summary>
    private static async Task<long> ResumeOffsetAsync(Uri session, long size, CancellationToken ct)
    {
        using var response = await Web.IdleAsync(tok => Web.Client.SendAsync(ChunkRequest(session, new ByteArrayContent(Array.Empty<byte>()), null, null, size), tok), ct);
        if (response.IsSuccessStatusCode) return size;
        if ((int)response.StatusCode == 308) return NextOffset(response) ?? 0;
        throw await ApiClient.ErrorAsync(response, ct);
    }

    public override async Task UploadAsync(string local, string path, Action<long>? progress, CancellationToken ct)
    {
        var (parent, name) = Split(path);
        var parentId = (await FolderIdAsync(parent, true, ct))!;
        var existing = (await QueryAsync($"'{parentId}' in parents and name = '{Esc(name)}' and mimeType != '{FolderMime}' and trashed = false", ct)).FirstOrDefault();
        var size = new FileInfo(local).Length;
        var modified = File.GetLastWriteTimeUtc(local).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var meta = Found(existing) ? JsonSerializer.Serialize(new { modifiedTime = modified }) : JsonSerializer.Serialize(new { name, parents = new[] { parentId }, modifiedTime = modified });
        var url = Found(existing) ? $"{Endpoints.DriveUpload}/files/{Id(existing)}?uploadType=resumable" : $"{Endpoints.DriveUpload}/files?uploadType=resumable";

        // Büyük dosyada oturum adresi saklanır: bağlantı kopsa da, uygulama kapansa da gönderim kaldığı bayttan sürer.
        var resumeKey = size > Chunk ? ResumeKey(scope, path, local, size) : null;
        Uri? session = null;
        long offset = 0;
        var ask = false;
        if (resumeKey != null && SavedSession(resumeKey) is { } saved)
        {
            session = saved;
            try { offset = await ResumeOffsetAsync(saved, size, ct); }
            // Yalnızca Google oturumu tanımıyorsa (süresi dolmuş) baştan başlanır; bağlantı sorunu oturumu geçersiz kılmaz.
            catch (IOException e) when (e.Message.StartsWith("HTTP 4", StringComparison.Ordinal))
            {
                Db.DeleteSetting(resumeKey);
                session = null;
            }
            catch (Exception e) when (e is IOException or HttpRequestException) { ask = true; }
        }
        if (session == null)
        {
            using var start = await api.SendAsync(() =>
            {
                var request = ApiClient.Json(Found(existing) ? HttpMethod.Patch : HttpMethod.Post, url, meta);
                request.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
                request.Headers.Add("X-Upload-Content-Length", size.ToString(CultureInfo.InvariantCulture));
                return request;
            }, ct);
            await ApiClient.EnsureAsync(start, ct);
            session = start.Headers.Location ?? throw new IOException("Google yükleme oturumu açmadı.");
            if (resumeKey != null) Db.SetSetting(resumeKey, $"{DateTime.UtcNow.Ticks}|{session.AbsoluteUri}");
        }

        if (size == 0)
        {
            using var empty = await Web.IdleAsync(tok => Web.Client.SendAsync(ChunkRequest(session, new ByteArrayContent(Array.Empty<byte>()), null, null, 0), tok), ct);
            await ApiClient.EnsureAsync(empty, ct);
            return;
        }

        await using var file = File.OpenRead(local);
        var buffer = new byte[(int)Math.Min(Chunk, size)];
        var failures = 0;
        if (offset > 0) progress?.Invoke(offset);
        while (offset < size)
        {
            if (ask)
            {
                // Kesintiden sonra sunucunun gerçekten aldığı bayt sorulur; yaklaşık 15 dakikalık kesintiye dayanır.
                if (++failures > MaxFailures) throw new IOException("Bağlantı uzun süre kurulamadı; gönderim sonraki çalıştırmada kaldığı yerden sürer.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, RetrySeconds * failures)), ct);
                try { offset = await ResumeOffsetAsync(session, size, ct); }
                catch (Exception e) when (e is HttpRequestException || (e is IOException && !e.Message.StartsWith("HTTP 4", StringComparison.Ordinal))) { continue; }
                ask = false;
                continue;
            }
            file.Position = offset;
            var count = await Web.ReadFullAsync(file, buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - offset)), ct);
            if (count == 0) throw new IOException("Dosya yükleme sırasında kısaldı: " + local);
            var (from, to) = (offset, offset + count - 1);
            HttpResponseMessage response;
            try
            {
                response = await Web.IdleAsync(tok => Web.Client.SendAsync(ChunkRequest(session, new ProgressContent(buffer, count, done => progress?.Invoke(from + done)), from, to, size), tok), ct, 900);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                ask = true;
                continue;
            }
            using (response)
            {
                if (response.IsSuccessStatusCode) break;
                if ((int)response.StatusCode == 308)
                {
                    offset = NextOffset(response) ?? to + 1;
                    failures = 0;
                }
                else if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests) ask = true;
                else
                {
                    if (resumeKey != null) Db.DeleteSetting(resumeKey);
                    throw await ApiClient.ErrorAsync(response, ct);
                }
            }
        }
        if (resumeKey != null) Db.DeleteSetting(resumeKey);
        progress?.Invoke(size);
    }

    private const int MaxFailures = 20;
    /// <summary>Yeniden denemeler arasındaki bekleme çarpanı (saniye).</summary>
    public static int RetrySeconds { get; set; } = 5;

    private static string ResumeKey(string scope, string path, string local, long size)
    {
        var identity = $"{scope}|{Clean(path)}|{size}|{File.GetLastWriteTimeUtc(local).Ticks}";
        return "resume:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
    }

    /// <summary>Google yükleme oturumları bir hafta geçerlidir; daha eskisi kullanılmaz.</summary>
    private static Uri? SavedSession(string key)
    {
        var parts = (Db.GetSetting(key) ?? "").Split('|', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || !Uri.TryCreate(parts[1], UriKind.Absolute, out var uri)) return null;
        if (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromDays(6)) return uri;
        Db.DeleteSetting(key);
        return null;
    }

    public override async Task DownloadAsync(string path, string local, Action<long>? progress, CancellationToken ct)
    {
        var found = await FindAsync(path, false, ct);
        if (!Found(found)) throw new FileNotFoundException("Dosya hedefte bulunamadı: " + path);
        using var response = await api.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Endpoints.DriveApi}/files/{Id(found)}?alt=media"), ct, headersOnly: true);
        await ApiClient.EnsureAsync(response, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await Web.SaveAsync(body, local, progress, Map(found, "").Modified, ct);
    }

    /// <summary>Silinenler Drive çöp kutusuna gider; yanlışlıkla silinen yedek 30 gün içinde geri alınabilir.</summary>
    public override async Task DeleteAsync(string path, bool isDir, CancellationToken ct)
    {
        var found = await FindAsync(path, isDir, ct);
        if (!Found(found)) return;
        using var doc = await api.JsonAsync(() => ApiClient.Json(HttpMethod.Patch, $"{Endpoints.DriveApi}/files/{Id(found)}?fields=id", new { trashed = true }), ct);
        if (isDir) Forget(path);
    }

    public override async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        var found = await FindAsync(from, null, ct);
        if (!Found(found)) throw new FileNotFoundException("Taşınacak öğe bulunamadı: " + from);
        var (newParent, newName) = Split(to);
        var target = (await FolderIdAsync(newParent, true, ct))!;
        var old = string.Join(",", found.GetProperty("parents").EnumerateArray().Select(p => p.GetString()));
        var url = $"{Endpoints.DriveApi}/files/{Id(found)}?addParents={Uri.EscapeDataString(target)}&removeParents={Uri.EscapeDataString(old)}&fields=id";
        using var doc = await api.JsonAsync(() => ApiClient.Json(HttpMethod.Patch, url, new { name = newName }), ct);
        Forget(from);
    }
}

/// <summary>OneDrive: doğrudan Microsoft Graph.</summary>
public sealed class GraphRemote(ApiClient api) : Remote
{
    private const int Chunk = 320 * 1024 * 32;   // 320 KB'nin katı olmalı
    private readonly ConcurrentDictionary<string, bool> _made = new(StringComparer.OrdinalIgnoreCase);

    private static string Item(string path)
    {
        path = Clean(path);
        var root = $"{Endpoints.Graph}/me/drive/root";
        return path.Length == 0 ? root : $"{root}:/{string.Join("/", path.Split('/').Select(Uri.EscapeDataString))}:";
    }

    private static string Child(string path, string action) => Clean(path).Length == 0 ? $"{Item(path)}/{action}" : $"{Item(path)}/{action}";

    private static RemoteItem Map(JsonElement e)
    {
        DateTime? modified = null;
        if (e.TryGetProperty("fileSystemInfo", out var fs) && fs.TryGetProperty("lastModifiedDateTime", out var m) && m.TryGetDateTime(out var d)) modified = d.ToUniversalTime();
        return new RemoteItem(e.GetProperty("name").GetString()!, e.TryGetProperty("size", out var s) ? s.GetInt64() : 0, e.TryGetProperty("folder", out _), modified, "");
    }

    public override async Task<List<RemoteItem>> ListAsync(string path, CancellationToken ct)
    {
        var list = new List<RemoteItem>();
        var url = Child(path, "children") + "?$select=name,size,folder,fileSystemInfo&$top=999";
        while (url != null)
        {
            var current = url;
            using var response = await api.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, current), ct);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new DirectoryNotFoundException("Klasör bulunamadı: " + path);
            await ApiClient.EnsureAsync(response, ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var item = Map(e);
                list.Add(item with { Size = item.IsDir ? 0 : item.Size });
            }
            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
        return list;
    }

    public override async Task MkdirAsync(string path, CancellationToken ct)
    {
        var walked = "";
        foreach (var part in Clean(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var parent = walked;
            walked = Join(walked, part);
            if (_made.ContainsKey(walked)) continue;
            var body = new Dictionary<string, object> { ["name"] = part, ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "fail" };
            using var response = await api.SendAsync(() => ApiClient.Json(HttpMethod.Post, Child(parent, "children"), body), ct);
            if (response.StatusCode != HttpStatusCode.Conflict) await ApiClient.EnsureAsync(response, ct);
            _made[walked] = true;
        }
    }

    private Task TouchAsync(string path, string modified, CancellationToken ct) => DisposeAfter(api.JsonAsync(() =>
        ApiClient.Json(HttpMethod.Patch, Item(path), new { fileSystemInfo = new { lastModifiedDateTime = modified } }), ct));

    private static async Task DisposeAfter(Task<JsonDocument> task) => (await task).Dispose();

    public override async Task UploadAsync(string local, string path, Action<long>? progress, CancellationToken ct)
    {
        await MkdirAsync(Split(path).Parent, ct);
        var size = new FileInfo(local).Length;
        var modified = File.GetLastWriteTimeUtc(local).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        if (size == 0)
        {
            using var empty = await api.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, $"{Item(path)}/content") { Content = new ByteArrayContent(Array.Empty<byte>()) }, ct);
            await ApiClient.EnsureAsync(empty, ct);
            await TouchAsync(path, modified, ct);
            return;
        }

        var body = new Dictionary<string, object>
        {
            ["item"] = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "replace", ["fileSystemInfo"] = new { lastModifiedDateTime = modified } },
        };
        string session;
        using (var doc = await api.JsonAsync(() => ApiClient.Json(HttpMethod.Post, $"{Item(path)}/createUploadSession", body), ct))
            session = doc.RootElement.GetProperty("uploadUrl").GetString()!;

        await using var file = File.OpenRead(local);
        var buffer = new byte[(int)Math.Min(Chunk, size)];
        long offset = 0;
        var failures = 0;
        while (offset < size)
        {
            file.Position = offset;
            var count = await Web.ReadFullAsync(file, buffer, ct);
            if (count == 0) throw new IOException("Dosya yükleme sırasında kısaldı: " + local);
            var from = offset;
            try
            {
                using var response = await Web.IdleAsync(tok =>
                {
                    var content = new ProgressContent(buffer, count, done => progress?.Invoke(from + done));
                    content.Headers.ContentRange = new ContentRangeHeaderValue(from, from + count - 1, size);
                    return Web.Client.SendAsync(new HttpRequestMessage(HttpMethod.Put, session) { Content = content }, tok);
                }, ct, 900);
                if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                await ApiClient.EnsureAsync(response, ct);
                offset += count;
                failures = 0;
            }
            catch (HttpRequestException) when (++failures <= 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * failures), ct);
                // Oturum, sunucunun beklediği sonraki baytı bildirir.
                using var status = await Web.IdleAsync(tok => Web.Client.GetAsync(session, tok), ct);
                await ApiClient.EnsureAsync(status, ct);
                using var doc = JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct));
                var range = doc.RootElement.GetProperty("nextExpectedRanges")[0].GetString()!;
                offset = long.Parse(range.Split('-')[0], CultureInfo.InvariantCulture);
            }
        }
        progress?.Invoke(size);
    }

    public override async Task DownloadAsync(string path, string local, Action<long>? progress, CancellationToken ct)
    {
        var info = await StatAsync(path, false, ct);
        using var response = await api.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Item(path)}/content"), ct, headersOnly: true);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException("Dosya hedefte bulunamadı: " + path);
        await ApiClient.EnsureAsync(response, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await Web.SaveAsync(body, local, progress, info?.Modified, ct);
    }

    public override async Task<RemoteItem?> StatAsync(string path, bool hash, CancellationToken ct)
    {
        using var response = await api.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Item(path) + "?$select=name,size,folder,fileSystemInfo"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await ApiClient.EnsureAsync(response, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Map(doc.RootElement);
    }

    public override async Task DeleteAsync(string path, bool isDir, CancellationToken ct)
    {
        using var response = await api.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, Item(path)), ct);
        if (response.StatusCode != HttpStatusCode.NotFound) await ApiClient.EnsureAsync(response, ct);
        foreach (var key in _made.Keys.Where(k => k.Equals(Clean(path), StringComparison.OrdinalIgnoreCase) || k.StartsWith(Clean(path) + "/", StringComparison.OrdinalIgnoreCase)).ToList()) _made.TryRemove(key, out _);
    }

    public override async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        var (parent, name) = Split(to);
        await MkdirAsync(parent, ct);
        var reference = parent.Length == 0 ? "/drive/root:" : "/drive/root:/" + parent;
        await DisposeAfter(api.JsonAsync(() => ApiClient.Json(HttpMethod.Patch, Item(from), new { parentReference = new { path = reference }, name }), ct));
    }
}

/// <summary>Dropbox: doğrudan Dropbox API v2.</summary>
public sealed class DropboxRemote(ApiClient api) : Remote
{
    private const int Chunk = 16 * 1024 * 1024;
    private const long SingleLimit = 100L * 1024 * 1024;

    private static string P(string path) => Clean(path).Length == 0 ? "" : "/" + Clean(path);

    private static bool Missing(string body) => body.Contains("not_found", StringComparison.OrdinalIgnoreCase);

    /// <summary>409 yanıtını gövdesiyle birlikte döndürür; çağıran "bulunamadı" ile "çakışma"yı ayırır.</summary>
    private async Task<(bool Ok, string Body)> CallAsync(string method, object body, CancellationToken ct)
    {
        using var response = await api.SendAsync(() => ApiClient.Json(HttpMethod.Post, $"{Endpoints.DropboxApi}/{method}", body), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return (false, text);
        if (!response.IsSuccessStatusCode) throw new IOException($"HTTP {(int)response.StatusCode}: {Web.Trim(text, 600)}");
        return (true, text);
    }

    private static RemoteItem Map(JsonElement e)
    {
        var dir = e.GetProperty(".tag").GetString() == "folder";
        DateTime? modified = e.TryGetProperty("client_modified", out var m) && m.TryGetDateTime(out var d) ? d.ToUniversalTime() : null;
        return new RemoteItem(e.GetProperty("name").GetString()!, !dir && e.TryGetProperty("size", out var s) ? s.GetInt64() : 0, dir, modified, "");
    }

    public override async Task<List<RemoteItem>> ListAsync(string path, CancellationToken ct)
    {
        var list = new List<RemoteItem>();
        var (ok, text) = await CallAsync("files/list_folder", new { path = P(path), recursive = false, limit = 2000 }, ct);
        while (true)
        {
            if (!ok) throw Missing(text) ? new DirectoryNotFoundException("Klasör bulunamadı: " + path) : new IOException(Web.Trim(text));
            using var doc = JsonDocument.Parse(text);
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
                if (e.GetProperty(".tag").GetString() is "file" or "folder") list.Add(Map(e));
            if (!doc.RootElement.GetProperty("has_more").GetBoolean()) return list;
            (ok, text) = await CallAsync("files/list_folder/continue", new { cursor = doc.RootElement.GetProperty("cursor").GetString() }, ct);
        }
    }

    public override async Task<RemoteItem?> StatAsync(string path, bool hash, CancellationToken ct)
    {
        if (P(path).Length == 0) return new RemoteItem("", 0, true, null, "");
        var (ok, text) = await CallAsync("files/get_metadata", new { path = P(path) }, ct);
        if (!ok) return Missing(text) ? null : throw new IOException(Web.Trim(text));
        using var doc = JsonDocument.Parse(text);
        return Map(doc.RootElement);
    }

    private async Task<string> ContentAsync(string method, object arg, byte[] buffer, int count, Action<long>? progress, CancellationToken ct)
    {
        // Başlıktaki JSON yalnızca ASCII olabilir; System.Text.Json Türkçe harfleri \u ile yazar.
        var json = JsonSerializer.Serialize(arg);
        using var response = await api.SendAsync(() =>
        {
            var content = new ProgressContent(buffer, count, progress);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var request = new HttpRequestMessage(HttpMethod.Post, $"{Endpoints.DropboxContent}/{method}") { Content = content };
            request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", json);
            return request;
        }, ct, seconds: 900);
        await ApiClient.EnsureAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public override async Task UploadAsync(string local, string path, Action<long>? progress, CancellationToken ct)
    {
        var size = new FileInfo(local).Length;
        var commit = new { path = P(path), mode = "overwrite", mute = true, client_modified = File.GetLastWriteTimeUtc(local).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) };
        await using var file = File.OpenRead(local);
        var buffer = new byte[(int)Math.Min(Chunk, Math.Max(1, size))];
        if (size <= SingleLimit && size <= buffer.Length)
        {
            var count = await Web.ReadFullAsync(file, buffer, ct);
            await ContentAsync("files/upload", commit, buffer, count, progress, ct);
            return;
        }

        string session;
        using (var doc = JsonDocument.Parse(await ContentAsync("files/upload_session/start", new { close = false }, buffer, 0, null, ct)))
            session = doc.RootElement.GetProperty("session_id").GetString()!;
        long offset = 0;
        while (offset < size)
        {
            var count = await Web.ReadFullAsync(file, buffer, ct);
            if (count == 0) throw new IOException("Dosya yükleme sırasında kısaldı: " + local);
            var from = offset;
            await ContentAsync("files/upload_session/append_v2", new { cursor = new { session_id = session, offset = from }, close = false }, buffer, count, done => progress?.Invoke(from + done), ct);
            offset += count;
        }
        await ContentAsync("files/upload_session/finish", new { cursor = new { session_id = session, offset = size }, commit }, buffer, 0, null, ct);
        progress?.Invoke(size);
    }

    public override async Task DownloadAsync(string path, string local, Action<long>? progress, CancellationToken ct)
    {
        var info = await StatAsync(path, false, ct) ?? throw new FileNotFoundException("Dosya hedefte bulunamadı: " + path);
        var json = JsonSerializer.Serialize(new { path = P(path) });
        using var response = await api.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{Endpoints.DropboxContent}/files/download");
            request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", json);
            return request;
        }, ct, headersOnly: true);
        await ApiClient.EnsureAsync(response, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await Web.SaveAsync(body, local, progress, info.Modified, ct);
    }

    public override async Task DeleteAsync(string path, bool isDir, CancellationToken ct)
    {
        var (ok, text) = await CallAsync("files/delete_v2", new { path = P(path) }, ct);
        if (!ok && !Missing(text)) throw new IOException(Web.Trim(text));
    }

    public override async Task MkdirAsync(string path, CancellationToken ct)
    {
        if (P(path).Length == 0) return;
        var (ok, text) = await CallAsync("files/create_folder_v2", new { path = P(path), autorename = false }, ct);
        if (!ok && !text.Contains("conflict", StringComparison.OrdinalIgnoreCase)) throw new IOException(Web.Trim(text));
    }

    public override async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        var (ok, text) = await CallAsync("files/move_v2", new { from_path = P(from), to_path = P(to), autorename = false }, ct);
        if (!ok) throw new IOException(Web.Trim(text));
    }
}
