using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

/// <summary>
/// Google'ın onay, token ve Drive v3 uçlarını bu bilgisayarda taklit eder: klasör sorguları,
/// parçalı (resumable) yükleme, indirme, taşıma, çöpe atma, süresi dolan oturum ve hız sınırı.
/// </summary>
internal sealed class FakeGoogle : IDisposable
{
    private sealed class Node
    {
        public string Id = "", Name = "", Parent = "", Modified = "";
        public bool Folder, Trashed;
        public byte[] Data = Array.Empty<byte>();
    }

    private sealed class Session
    {
        public Node Node = new();
        public bool IsNew;
        public long Size;
        public MemoryStream Data = new();
    }

    private const string FolderMime = "application/vnd.google-apps.folder";
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, Node> _nodes = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly string[] _saved;
    private int _ids;

    public string Base { get; }
    public string Access { get; private set; } = "erisim-0";
    public int Refreshes, Uploads, ThrottleNext, DropNextChunk, Chunks, Sessions;
    public List<long> ChunkStarts { get; } = new();
    /// <summary>Bu kadar parça alındıktan sonra bağlantı kesilmiş gibi davranır.</summary>
    public int OutageAfterChunks = int.MaxValue;
    public bool Outage;
    public List<Dictionary<string, string>> TokenForms { get; } = new();
    public string? LastAuthQuery { get; private set; }

    public FakeGoogle()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Base = $"http://localhost:{port}/";
        _listener.Prefixes.Add(Base);
        _listener.Start();
        _ = Task.Run(LoopAsync);
        _saved = new[] { Endpoints.GoogleAuth, Endpoints.GoogleToken, Endpoints.DriveApi, Endpoints.DriveUpload };
        (Endpoints.GoogleAuth, Endpoints.GoogleToken, Endpoints.DriveApi, Endpoints.DriveUpload) = (Base + "auth", Base + "token", Base + "drive", Base + "upload");
    }

    public void Dispose()
    {
        (Endpoints.GoogleAuth, Endpoints.GoogleToken, Endpoints.DriveApi, Endpoints.DriveUpload) = (_saved[0], _saved[1], _saved[2], _saved[3]);
        _listener.Close();
    }

    public void ExpireAccess() => Access = "gecersiz-" + Guid.NewGuid().ToString("N")[..6];

    public IEnumerable<string> Paths()
    {
        lock (_nodes)
        {
            string PathOf(Node n) => n.Parent == "root" ? n.Name : PathOf(_nodes[n.Parent]) + "/" + n.Name;
            bool Live(Node n) => !n.Trashed && (n.Parent == "root" || Live(_nodes[n.Parent]));
            return _nodes.Values.Where(n => !n.Folder && Live(n)).Select(PathOf).OrderBy(p => p, StringComparer.Ordinal).ToList();
        }
    }

    public int Trashed { get { lock (_nodes) return _nodes.Values.Count(n => n.Trashed); } }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(context); }
                catch (Exception e) { Reply(context, 500, new { error = e.ToString() }); }
            });
        }
    }

    private static void Reply(HttpListenerContext c, int status, object? body = null)
    {
        try
        {
            c.Response.StatusCode = status;
            var bytes = body == null ? Array.Empty<byte>() : body as byte[] ?? Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
            c.Response.ContentLength64 = bytes.Length;
            c.Response.OutputStream.Write(bytes);
            c.Response.Close();
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
    }

    private object Json(Node n) => new Dictionary<string, object>
    {
        ["id"] = n.Id, ["name"] = n.Name, ["mimeType"] = n.Folder ? FolderMime : "application/octet-stream", ["size"] = n.Data.Length.ToString(),
        ["md5Checksum"] = Convert.ToHexStringLower(MD5.HashData(n.Data)), ["modifiedTime"] = n.Modified, ["parents"] = new[] { n.Parent },
    };

    private static async Task<byte[]> BodyAsync(HttpListenerContext c)
    {
        using var buffer = new MemoryStream();
        await c.Request.InputStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private async Task HandleAsync(HttpListenerContext c)
    {
        var path = c.Request.Url!.AbsolutePath;
        var query = HttpUtility.ParseQueryString(c.Request.Url.Query);
        var method = c.Request.HttpMethod;

        if (path == "/auth")
        {
            LastAuthQuery = c.Request.Url.Query;
            c.Response.Redirect($"{query["redirect_uri"]}?code=kod-1&state={query["state"]}");
            c.Response.Close();
            return;
        }
        if (path == "/token")
        {
            var form = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(await BodyAsync(c)));
            lock (TokenForms) TokenForms.Add(form.AllKeys.ToDictionary(k => k!, k => form[k]!));
            if (form["grant_type"] == "authorization_code" && (form["code"] != "kod-1" || string.IsNullOrEmpty(form["code_verifier"]))) { Reply(c, 400, new { error = "invalid_grant" }); return; }
            if (form["grant_type"] == "refresh_token")
            {
                if (form["refresh_token"] != "yenile-1") { Reply(c, 400, new { error = "invalid_grant" }); return; }
                Refreshes++;
            }
            Access = "erisim-" + Guid.NewGuid().ToString("N")[..8];
            Reply(c, 200, form["grant_type"] == "refresh_token"
                ? new { access_token = Access, expires_in = 3600, refresh_token = (string?)null }
                : new { access_token = Access, expires_in = 3600, refresh_token = (string?)"yenile-1" });
            return;
        }

        if (path.StartsWith("/upload/session/"))
        {
            await ChunkAsync(c, path["/upload/session/".Length..]);
            return;
        }

        if (c.Request.Headers["Authorization"] != "Bearer " + Access) { Reply(c, 401, new { error = new { message = "Invalid Credentials" } }); return; }
        if (ThrottleNext > 0)
        {
            ThrottleNext--;
            c.Response.AddHeader("Retry-After", "0");
            Reply(c, 429, new { error = new { message = "rateLimitExceeded" } });
            return;
        }

        if (path == "/upload/files" || path.StartsWith("/upload/files/"))
        {
            using var meta = JsonDocument.Parse(await BodyAsync(c));
            var session = new Session { Size = long.Parse(c.Request.Headers["X-Upload-Content-Length"]!) };
            lock (_nodes)
            {
                if (path == "/upload/files")
                {
                    session.IsNew = true;
                    session.Node = new Node { Id = "f" + ++_ids, Name = meta.RootElement.GetProperty("name").GetString()!, Parent = meta.RootElement.GetProperty("parents")[0].GetString()! };
                }
                else session.Node = _nodes[path["/upload/files/".Length..]];
                session.Node.Modified = meta.RootElement.GetProperty("modifiedTime").GetString()!;
                var id = "s" + ++_ids;
                _sessions[id] = session;
                Sessions++;
                c.Response.AddHeader("Location", $"{Base}upload/session/{id}");
            }
            Reply(c, 200);
            return;
        }

        if (path == "/drive/files" && method == "GET")
        {
            var q = query["q"] ?? "";
            var parents = Regex.Matches(q, @"'([^']+)' in parents").Select(m => m.Groups[1].Value).ToHashSet();
            var name = Regex.Match(q, @"name = '((?:\\.|[^'\\])*)'");
            var wanted = name.Success ? Regex.Replace(name.Groups[1].Value, @"\\(.)", "$1") : null;
            List<object> files;
            lock (_nodes)
                files = _nodes.Values.Where(n => !n.Trashed && parents.Contains(n.Parent) && (wanted == null || n.Name == wanted)
                                                 && (!q.Contains("mimeType = '") || n.Folder) && (!q.Contains("mimeType != '") || !n.Folder)).Select(Json).ToList();
            Reply(c, 200, new { files });
            return;
        }
        if (path == "/drive/files" && method == "POST")
        {
            using var meta = JsonDocument.Parse(await BodyAsync(c));
            var node = new Node { Name = meta.RootElement.GetProperty("name").GetString()!, Parent = meta.RootElement.GetProperty("parents")[0].GetString()!, Folder = true, Modified = DateTime.UtcNow.ToString("o") };
            lock (_nodes)
            {
                node.Id = "d" + ++_ids;
                _nodes[node.Id] = node;
            }
            Reply(c, 200, new { id = node.Id });
            return;
        }
        if (path.StartsWith("/drive/files/"))
        {
            Node? node;
            lock (_nodes) _nodes.TryGetValue(path["/drive/files/".Length..], out node);
            if (node == null) { Reply(c, 404, new { error = new { message = "File not found" } }); return; }
            if (method == "GET") { Reply(c, 200, node.Data); return; }
            if (method == "PATCH")
            {
                using var patch = JsonDocument.Parse(await BodyAsync(c));
                lock (_nodes)
                {
                    if (patch.RootElement.TryGetProperty("trashed", out var t)) node.Trashed = t.GetBoolean();
                    if (patch.RootElement.TryGetProperty("name", out var n)) node.Name = n.GetString()!;
                    if (query["addParents"] is { } added) node.Parent = added;
                }
                Reply(c, 200, new { id = node.Id });
                return;
            }
        }
        Reply(c, 404, new { error = new { message = "bilinmeyen uç: " + method + " " + path } });
    }

    private async Task ChunkAsync(HttpListenerContext c, string id)
    {
        Session session;
        lock (_nodes) session = _sessions[id];
        var body = await BodyAsync(c);
        if (Outage) { Reply(c, 503); return; }
        var range = c.Request.Headers["Content-Range"] ?? "";
        if (!range.StartsWith("bytes */"))
        {
            Chunks++;
            if (DropNextChunk > 0)
            {
                DropNextChunk--;
                Reply(c, 503);
                return;
            }
            var from = long.Parse(Regex.Match(range, @"bytes (\d+)-").Groups[1].Value);
            if (from != session.Data.Length) { Reply(c, 400, new { error = "yanlis konum" }); return; }
            lock (ChunkStarts) ChunkStarts.Add(from);
            session.Data.Write(body);
            if (ChunkStarts.Count >= OutageAfterChunks) Outage = true;
        }
        if (session.Data.Length < session.Size)
        {
            if (session.Data.Length > 0) c.Response.AddHeader("Range", $"bytes=0-{session.Data.Length - 1}");
            Reply(c, 308);
            return;
        }
        lock (_nodes)
        {
            session.Node.Data = session.Data.ToArray();
            if (session.IsNew) _nodes[session.Node.Id] = session.Node;
            Uploads++;
        }
        Reply(c, 200, Json(session.Node));
    }
}

public class DriveApiTests
{
    private static readonly HttpClient Browser = new();

    /// <summary>Kullanıcının tarayıcıda izin vermesinin yerine geçer: onay adresine gider, yönlendirmeyi izler.</summary>
    private static void Approve(string url) => _ = Browser.GetAsync(url);

    private static Task ConnectAsync(string name = "Google") =>
        Cloud.CreateTargetAsync(new TargetDefinition { Name = name, Type = "drive", Auth = "browser", ClientId = "benim-kimligim", ClientSecret = "benim-sirrim" }, default, Approve);

    [Fact]
    public async Task Browser_sign_in_uses_own_client_with_pkce_and_stores_refresh_token()
    {
        using var s = new Sandbox();
        using var google = new FakeGoogle();
        await ConnectAsync();

        var asked = HttpUtility.ParseQueryString(google.LastAuthQuery!);
        Assert.Equal("benim-kimligim", asked["client_id"]);
        Assert.Equal("https://www.googleapis.com/auth/drive.file", asked["scope"]);
        Assert.Equal(("S256", "offline", "code"), (asked["code_challenge_method"], asked["access_type"], asked["response_type"]));
        Assert.StartsWith("http://127.0.0.1:", asked["redirect_uri"]);

        var form = google.TokenForms.Single();
        Assert.Equal(("benim-kimligim", "benim-sirrim", asked["redirect_uri"]), (form["client_id"], form["client_secret"], form["redirect_uri"]));
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(asked["code_challenge"], challenge);

        var stored = OAuthToken.Parse(TargetStore.Load("Google")!.Token);
        Assert.Equal((google.Access, "yenile-1"), (stored.Access, stored.Refresh));
        Assert.DoesNotContain("yenile-1", Encoding.Latin1.GetString(File.ReadAllBytes(AppPaths.Database)));
    }

    [Fact]
    public async Task Token_button_returns_token_without_saving_a_target()
    {
        using var s = new Sandbox();
        using var google = new FakeGoogle();
        var json = await Cloud.AuthorizeAsync(new TargetDefinition { Name = "x", Type = "drive", ClientId = "kimlik", ClientSecret = "sir", FullAccess = true }, Approve, default);
        Assert.Equal("yenile-1", OAuthToken.Parse(json).Refresh);
        Assert.Equal("https://www.googleapis.com/auth/drive", HttpUtility.ParseQueryString(google.LastAuthQuery!)["scope"]);
        Assert.DoesNotContain(await Cloud.ListTargetsAsync(), t => t.Type == "drive");

        await Cloud.CreateTargetAsync(new TargetDefinition { Name = "Tokenli", Type = "drive", Auth = "token", Token = json, ClientId = "kimlik", ClientSecret = "sir" }, default);
        Assert.True((await Cloud.TestAsync("Tokenli", default)).Ok);
    }

    [Fact]
    public async Task Denied_consent_is_reported_and_nothing_is_saved()
    {
        using var s = new Sandbox();
        using var google = new FakeGoogle();
        void Deny(string url)
        {
            var q = HttpUtility.ParseQueryString(new Uri(url).Query);
            _ = Browser.GetAsync($"{q["redirect_uri"]}?error=access_denied&state={q["state"]}");
        }
        var error = await Assert.ThrowsAsync<IOException>(() => Cloud.CreateTargetAsync(new TargetDefinition { Name = "Red", Type = "drive", ClientId = "k", ClientSecret = "s" }, default, Deny));
        Assert.Contains("access_denied", error.Message);
        Assert.Empty(await Cloud.ListTargetsAsync().ContinueWith(t => t.Result.Where(x => x.Name == "Red")));
    }

    [Fact]
    public async Task Every_operation_works_against_the_drive_api()
    {
        using var s = new Sandbox();
        using var google = new FakeGoogle();
        await ConnectAsync();
        await TargetExercise.RunAsync(s, "Google");
        Assert.True(google.Trashed > 0);
    }

    [Fact]
    public async Task Expired_session_is_refreshed_and_rate_limit_is_retried()
    {
        using var s = new Sandbox();
        using var google = new FakeGoogle();
        await ConnectAsync();
        var file = Path.Combine(s.Root, "A_20260101-000000.7z");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(5000));

        google.ExpireAccess();
        google.ThrottleNext = 2;
        await Cloud.UploadAsync(file, "Google", "Yedek/Is", null, null, default);
        Assert.Equal(1, google.Refreshes);
        Assert.Equal(0, google.ThrottleNext);
        Assert.Equal(new[] { "Yedek/Is/A_20260101-000000.7z" }, google.Paths());
        Assert.Equal(google.Access, OAuthToken.Parse(TargetStore.Load("Google")!.Token).Access);

        // Aynı ad yeniden yüklenince ikinci bir kopya oluşmaz, dosya güncellenir.
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(7000));
        await Cloud.UploadAsync(file, "Google", "Yedek/Is", null, null, default);
        Assert.Single(google.Paths());
        Assert.Equal(7000, (await Cloud.ListHashesAsync("Google", "Yedek/Is", default)).Single().Value.Size);
    }

    [Fact]
    public async Task Large_upload_is_sent_in_chunks_and_resumes_after_a_dropped_chunk()
    {
        using var s = new Sandbox();
        using var google = new FakeGoogle();
        await ConnectAsync();
        var file = Path.Combine(s.Root, "Buyuk_20260101-000000.7z");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(40 * 1024 * 1024 + 123));

        google.DropNextChunk = 1;
        var seen = new List<long>();
        await Cloud.UploadAsync(file, "Google", "Buyuk", null, st => { lock (seen) seen.Add(st.Bytes); }, default);
        Assert.Equal(4, google.Chunks);
        Assert.Contains(seen, b => b > 0 && b < 40 * 1024 * 1024);

        var back = Path.Combine(s.Root, "geri.7z");
        await Cloud.DownloadFileAsync("Google", "Buyuk", "Buyuk_20260101-000000.7z", back, default);
        Assert.Equal(BackupAudit.Md5Of(file), BackupAudit.Md5Of(back));
    }

    [Fact]
    public async Task Interrupted_large_upload_continues_from_where_it_stopped_on_the_next_run()
    {
        using var s = new Sandbox();
        using var google = new FakeGoogle();
        await ConnectAsync();
        var file = Path.Combine(s.Root, "Buyuk_20261110-112715.zip");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(50 * 1024 * 1024));

        // İlk parça gittikten sonra bağlantı kesilir ve geri gelmez: gönderim hata ile biter.
        google.OutageAfterChunks = 1;
        DriveRemote.RetrySeconds = 0;
        try
        {
            var error = await Assert.ThrowsAnyAsync<IOException>(() => Cloud.UploadAsync(file, "Google", "Yedek", null, null, default));
            Assert.Contains("kaldığı yerden", error.Message);
        }
        finally { DriveRemote.RetrySeconds = 5; }
        Assert.Empty(google.Paths());
        Assert.Equal(new long[] { 0 }, google.ChunkStarts);
        Assert.Contains(Db.Query("SELECT key FROM settings", r => r.GetString(0)), k => k.StartsWith("resume:"));

        // Bağlantı geri geldi, iş yeniden çalıştırıldı.
        (google.Outage, google.OutageAfterChunks) = (false, int.MaxValue);
        var seen = new List<long>();
        await Cloud.UploadAsync(file, "Google", "Yedek", null, st => { lock (seen) seen.Add(st.Bytes); }, default);
        Assert.Equal(1, google.Sessions);
        Assert.Single(google.ChunkStarts, start => start == 0);
        Assert.True(seen.Min() >= 16 * 1024 * 1024, "ilerleme baştan başlamamalı");
        Assert.Equal(new[] { "Yedek/Buyuk_20261110-112715.zip" }, google.Paths());
        var back = Path.Combine(s.Root, "geri.zip");
        await Cloud.DownloadFileAsync("Google", "Yedek", "Buyuk_20261110-112715.zip", back, default);
        Assert.Equal(BackupAudit.Md5Of(file), BackupAudit.Md5Of(back));
        Assert.DoesNotContain(Db.Query("SELECT key FROM settings", r => r.GetString(0)), k => k.StartsWith("resume:"));

        // Dosya değiştiyse eski oturum kullanılmaz.
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(20 * 1024 * 1024));
        await Cloud.UploadAsync(file, "Google", "Yedek", null, null, default);
        Assert.Equal(2, google.Sessions);
    }

    [Fact]
    public async Task Rerunning_a_folder_upload_sends_only_the_missing_files()
    {
        using var s = new Sandbox();
        using var google = new FakeGoogle();
        await ConnectAsync();
        var run = Directory.CreateDirectory(Path.Combine(s.Root, "PROJE_YEDEKLE", "10-11-2026_11_27")).FullName;
        foreach (var name in new[] { "A", "B", "C" }) File.WriteAllBytes(Path.Combine(run, $"{name}_20261110-112715.zip"), RandomNumberGenerator.GetBytes(2000));
        var job = new Job { Name = "Google_Bulut", Mode = "dated", DatedOutput = "folder", UseDateFolder = true, DateBy = "folder", OnlyChanged = true, Sources = new() { Path.Combine(s.Root, "PROJE_YEDEKLE") }, Targets = new() { "Google" }, RemoteFolder = "Backup360", Keep = 0 };
        Db.Save(job);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.Equal(3, google.Uploads);
        var day = File.GetLastWriteTime(Path.Combine(run, "A_20261110-112715.zip")).ToString("yyyy-MM-dd");
        Assert.All(google.Paths(), p => Assert.StartsWith($"Backup360/Google_Bulut/{day}/10-11-2026_11_27/", p));

        // Zipleme sürerken klasöre yeni dosya eklendi: yalnızca o gider, ötekiler yeniden yüklenmez.
        File.WriteAllBytes(Path.Combine(run, "D_20261110-112715.zip"), RandomNumberGenerator.GetBytes(2000));
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.Equal(4, google.Uploads);
        Assert.Equal(4, google.Paths().Count());

        // Hiçbir şey değişmediyse hiçbir şey gönderilmez.
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.Equal(4, google.Uploads);
    }

    [Fact]
    public async Task Backup_job_runs_restores_and_audits_through_the_drive_api()
    {
        using var s = new Sandbox();
        using var google = new FakeGoogle();
        await ConnectAsync();
        var root = Path.Combine(s.Root, "Projeler");
        foreach (var name in new[] { "P1", "P2", "P3" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(root, name, "kod.txt"), name + " içeriği");
        }
        var job = new Job { Name = "Proje_yedekle", Mode = "dated", UseDateFolder = false, DatedOutput = "archive", Sources = new() { root }, Formats = new() { "7z" }, Targets = new() { "Google" }, RemoteFolder = "PROJE_YEDEKLE", Keep = 0 };
        Db.Save(job);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.True(r.Status == "success", r.Detail);
        Assert.All(google.Paths(), p => Assert.Matches(@"^PROJE_YEDEKLE/\d{2}-\d{2}-\d{4}_\d{2}_\d{2}/P\d_\d{8}-\d{6}\.7z$", p));
        Assert.Equal(3, google.Paths().Count());

        var audit = await BackupAudit.RunAsync(true, null, default);
        Assert.True(audit.Status == "success", audit.Detail);

        var point = (await RestoreCatalog.ListAsync(job, default)).Single(p => p.Item == "P2");
        var dest = await RestoreEngine.RestoreFilesAsync(point, Directory.CreateDirectory(Path.Combine(s.Root, "don")).FullName, _ => { }, null, default);
        Assert.Equal("P2 içeriği", File.ReadAllText(Path.Combine(dest, "P2", "kod.txt")));
    }
}
