using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

/// <summary>Bir hedefte uygulamanın kullandığı tüm işlemleri sırayla dener; sonuçları hedeften geri indirerek doğrular.</summary>
internal static class TargetExercise
{
    public static async Task<Dictionary<string, string>> RemoteHashes(Sandbox s, string target, string folder)
    {
        var dir = Path.Combine(s.Root, "geri-" + Guid.NewGuid().ToString("N")[..6]);
        await Cloud.DownloadFolderAsync(target, folder, dir, default);
        return Directory.Exists(dir) ? Sandbox.Hashes(dir) : new();
    }

    public static async Task RunAsync(Sandbox s, string target)
    {
        var test = await Cloud.TestAsync(target, default);
        Assert.True(test.Ok, test.Message);
        Assert.True(test.UploadBytesPerSecond > 0);
        Assert.DoesNotContain(await CloudExplorer.ListAsync(target, "", default), e => e.Name.StartsWith(".backup360"));

        await Cloud.CreateFolderAsync(target, "Yedekler/Muhasebe Ğü", default);
        Assert.Contains(await CloudExplorer.ListAsync(target, "Yedekler", default), e => e is { Name: "Muhasebe Ğü", IsDir: true });

        // Birebir gönderim: ilk tur hepsi, ikinci tur yalnızca farklar.
        var src = s.Tree("Kaynak " + target);
        File.WriteAllText(Path.Combine(src, "silinecek.txt"), "sil");
        File.WriteAllText(Path.Combine(src, "bos.txt"), "");
        var lines = new List<string>();
        void Log(string line) { lock (lines) lines.Add(line); }
        await Cloud.MirrorAsync(src, target, "Ayna", true, "20260101-000000", Array.Empty<string>(), Log, null, default, "Veri");
        Assert.Equal(Sandbox.Hashes(src), await RemoteHashes(s, target, "Ayna/Veri"));
        Assert.Equal(4, lines.Count(l => l.Contains("Yeni:")));

        var pair = new SyncPair(src, target, "Ayna/Veri");
        Assert.All((await SyncPlanner.DiffAsync(pair, Array.Empty<string>(), default)).Items, i => Assert.Equal(SyncPlanner.Same, i.Kind));

        await Task.Delay(1500);
        File.WriteAllText(Path.Combine(src, "rapor şüğ.txt"), "değişti, boyutu da farklı");
        File.Delete(Path.Combine(src, "silinecek.txt"));
        File.WriteAllText(Path.Combine(src, "Alt Klasör ğüşıöç", "yeni.txt"), "yeni");
        var diff = await SyncPlanner.DiffAsync(pair, Array.Empty<string>(), default);
        Assert.Equal((1, 1, 1, 2), (diff.Count(SyncPlanner.New), diff.Count(SyncPlanner.Changed), diff.Count(SyncPlanner.Extra), diff.Count(SyncPlanner.Same)));

        lines.Clear();
        await Cloud.MirrorAsync(src, target, "Ayna", true, "20260102-000000", Array.Empty<string>(), Log, null, default, "Veri");
        Assert.Equal(Sandbox.Hashes(src), await RemoteHashes(s, target, "Ayna/Veri"));
        Assert.DoesNotContain(lines, l => l.Contains("veri.bin"));
        var kept = await RemoteHashes(s, target, "Ayna/_silinenler/20260102-000000");
        Assert.Equal(new[] { "rapor şüğ.txt", "silinecek.txt" }, kept.Keys.OrderBy(k => k));

        // Arşiv yükleme, parmak izi listesi, tek dosya indirme ve saklama kuralı.
        var archive = Path.Combine(s.Root, "Proje_20200101-000000.7z");
        File.WriteAllBytes(archive, RandomNumberGenerator.GetBytes(300_000));
        var newer = Path.Combine(s.Root, "Proje_20200102-000000.7z");
        File.WriteAllBytes(newer, RandomNumberGenerator.GetBytes(1000));
        var stats = new List<TransferStats>();
        await Cloud.UploadAsync(archive, target, "Arsiv/01-01-2020_10_00", null, st => { lock (stats) stats.Add(st); }, default);
        await Cloud.UploadAsync(newer, target, "Arsiv/02-01-2020_10_00", null, null, default);
        Assert.Equal((300_000L, 300_000L), (stats[^1].Bytes, stats[^1].Total));
        var hashes = await Cloud.ListHashesAsync(target, "Arsiv", default);
        Assert.Equal(300_000, hashes["01-01-2020_10_00/Proje_20200101-000000.7z"].Size);

        var back = Path.Combine(s.Root, "inen.7z");
        await Cloud.DownloadFileAsync(target, "Arsiv", "01-01-2020_10_00/Proje_20200101-000000.7z", back, default);
        Assert.Equal(File.ReadAllBytes(archive), File.ReadAllBytes(back));

        Assert.Equal(1, await Cloud.ApplyRetentionAsync(target, "Arsiv", "7z", 1, 0, null, default));
        Assert.Equal(new[] { "02-01-2020_10_00" }, (await CloudExplorer.ListAsync(target, "Arsiv", default)).Select(e => e.Name));
        Assert.Empty(await Cloud.ListHashesAsync(target, "HicOlmayan", default));
    }
}

public class TargetTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Protokol testleri için yerel deneme sunucusu. Ürünle dağıtılmaz; yolu ortam değişkeninden gelir.</summary>
    private static async Task<Process> Serve(int port, params string[] args)
    {
        var exe = Environment.GetEnvironmentVariable("BACKUP360_TEST_SERVER");
        Assert.False(string.IsNullOrEmpty(exe), "BACKUP360_TEST_SERVER ayarlanmadı");
        var p = Proc.Start(exe!, args.Prepend("serve").Concat(new[] { "--addr", $"127.0.0.1:{port}" }));
        for (var i = 0; i < 60; i++)
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port);
                return p;
            }
            catch (SocketException) { await Task.Delay(250); }
        }
        Proc.TryKill(p);
        throw new TimeoutException("deneme sunucusu açılmadı");
    }

    public static IEnumerable<object[]> PasswordProtocols() =>
        new[] { new object[] { "sftp" }, new object[] { "ftp" }, new object[] { "webdav" } };

    [Theory, MemberData(nameof(PasswordProtocols))]
    public async Task Password_target_works_end_to_end_and_password_is_not_stored_in_clear(string type)
    {
        using var s = new Sandbox();
        var dir = Directory.CreateDirectory(Path.Combine(s.Root, "sunucu")).FullName;
        var port = FreePort();
        var server = await Serve(port, type, dir, "--user", "yedekci", "--pass", "Sifre!123");
        try
        {
            TargetDefinition Def(string name, string pass) => new()
            {
                Name = name, Type = type, Auth = "password", Host = "127.0.0.1", Port = port, User = "yedekci", Password = pass,
                Url = type == "webdav" ? $"http://127.0.0.1:{port}/" : "",
            };

            await Cloud.CreateTargetAsync(Def("uzak", "Sifre!123"), default);
            Assert.DoesNotContain("Sifre!123", Encoding.Latin1.GetString(File.ReadAllBytes(AppPaths.Database)));
            Assert.Contains(await Cloud.ListTargetsAsync(), t => t.Name == "uzak" && t.Type == type);

            await TargetExercise.RunAsync(s, "uzak");
            Assert.True(Directory.Exists(Path.Combine(dir, "Yedekler", "Muhasebe Ğü")));
            Assert.False(Directory.Exists(Path.Combine(dir, ".backup360-baglanti-testi")));

            await Cloud.CreateTargetAsync(Def("yanlis", "HatalıŞifre"), default);
            Assert.False((await Cloud.TestAsync("yanlis", default)).Ok);
            await Assert.ThrowsAsync<ArgumentException>(() => Cloud.CreateTargetAsync(Def("UZAK", "x"), default));
        }
        finally { Proc.TryKill(server); }
    }

    [Fact]
    public async Task S3_target_works_end_to_end()
    {
        using var s = new Sandbox();
        var dir = Directory.CreateDirectory(Path.Combine(s.Root, "s3", "kova")).Parent!.FullName;
        var port = FreePort();
        var server = await Serve(port, "s3", dir, "--auth-key", "AKTEST,SKTESTGIZLI");
        try
        {
            await Cloud.CreateTargetAsync(new TargetDefinition { Name = "kova", Type = "s3", Auth = "token", AccessKey = "AKTEST", SecretKey = "SKTESTGIZLI", Endpoint = $"http://127.0.0.1:{port}", Path = "kova" }, default);
            await TargetExercise.RunAsync(s, "kova");

            await Cloud.CreateTargetAsync(new TargetDefinition { Name = "kova-yanlis", Type = "s3", Auth = "token", AccessKey = "AKTEST", SecretKey = "baska", Endpoint = $"http://127.0.0.1:{port}", Path = "kova" }, default);
            Assert.False((await Cloud.TestAsync("kova-yanlis", default)).Ok);
        }
        finally { Proc.TryKill(server); }
    }

    [Fact]
    public async Task Local_folder_target_works_end_to_end()
    {
        using var s = new Sandbox();
        await TargetExercise.RunAsync(s, "gdrive");
    }

    [Theory, InlineData("drive"), InlineData("dropbox"), InlineData("onedrive")]
    public async Task Token_target_is_stored_without_opening_browser(string type)
    {
        using var s = new Sandbox();
        const string token = "{\"access_token\":\"ornekdeger\",\"token_type\":\"Bearer\",\"refresh_token\":\"yenile\",\"expiry\":\"2030-01-01T00:00:00Z\"}";
        var opened = false;
        await Cloud.CreateTargetAsync(new TargetDefinition { Name = "tokenli", Type = type, Auth = "token", Token = token, ClientId = "kimlik", ClientSecret = type == "drive" ? "sir" : "" }, default, _ => opened = true);
        Assert.False(opened);
        Assert.Contains(await Cloud.ListTargetsAsync(), t => t.Name == "tokenli" && t.Type == type);
        Assert.Equal("yenile", OAuthToken.Parse(TargetStore.Load("tokenli")!.Token).Refresh);
        Assert.DoesNotContain("ornekdeger", Encoding.Latin1.GetString(File.ReadAllBytes(AppPaths.Database)));

        await Assert.ThrowsAsync<ArgumentException>(() => Cloud.CreateTargetAsync(new TargetDefinition { Name = "bozuk", Type = type, Auth = "token", Token = "json degil", ClientId = "kimlik", ClientSecret = type == "drive" ? "sir" : "" }, default));
    }

    [Fact]
    public void Account_targets_need_the_users_own_app_and_fixed_redirect()
    {
        Assert.Throws<ArgumentException>(() => new TargetDefinition { Name = "od", Type = "onedrive" }.Validate());
        Assert.Throws<ArgumentException>(() => new TargetDefinition { Name = "db", Type = "dropbox" }.Validate());
        var onedrive = OAuth.AppFor(new TargetDefinition { Name = "od", Type = "onedrive", ClientId = "kimlik" });
        var dropbox = OAuth.AppFor(new TargetDefinition { Name = "db", Type = "dropbox", ClientId = "anahtar" });
        Assert.Equal(("localhost", OAuth.FixedPort, (string?)null), (onedrive.RedirectHost, onedrive.Port, onedrive.ClientSecret));
        Assert.Equal(("localhost", OAuth.FixedPort), (dropbox.RedirectHost, dropbox.Port));
        Assert.Contains("offline_access", onedrive.Scope);
        Assert.Contains("token_access_type=offline", dropbox.Extra);
        var drive = OAuth.AppFor(new TargetDefinition { Name = "g", Type = "drive", ClientId = "id", ClientSecret = "s" });
        Assert.EndsWith("/auth/drive.file", drive.Scope);
        Assert.EndsWith("/auth/drive", OAuth.AppFor(new TargetDefinition { Name = "g", Type = "drive", ClientId = "id", ClientSecret = "s", FullAccess = true }).Scope);
    }

    [Theory]
    [InlineData(false, false, false, "mirror", "archive")]
    [InlineData(false, false, true, "archive", "archive")]
    [InlineData(true, false, false, "dated", "folder")]
    [InlineData(true, false, true, "dated", "archive")]
    [InlineData(false, true, false, "database", "archive")]
    public void Compression_applies_only_when_requested(bool dated, bool database, bool compress, string mode, string output) =>
        Assert.Equal((mode, output), JobModes.Map(dated, database, compress));

    [Theory]
    [InlineData("googleapi: Error 403: Quota exceeded, rateLimitExceeded", "istek sınırına")]
    [InlineData("Error 403: storageQuotaExceeded", "dolu")]
    [InlineData("oauth2: invalid_grant", "yeniden tanımlayın")]
    [InlineData("HTTP 403: {\"reason\": \"accessNotConfigured\"}", "Drive API etkin değil")]
    [InlineData("error=redirect_uri_mismatch", "localhost:53682")]
    public void Provider_errors_are_explained(string raw, string expected) => Assert.Contains(expected, Cloud.Explain(raw));

    [Fact]
    public void Shared_google_client_is_never_used()
    {
        Environment.SetEnvironmentVariable("BACKUP360_GOOGLE_CLIENT", "");
        try
        {
            Assert.False(GoogleClient.HasBuiltIn);
            Assert.Throws<ArgumentException>(() => new TargetDefinition { Name = "g", Type = "drive", Auth = "browser" }.Validate());
            new TargetDefinition { Name = "g", Type = "drive", Auth = "browser", ClientId = "id", ClientSecret = "s" }.Validate();
        }
        finally { Environment.SetEnvironmentVariable("BACKUP360_GOOGLE_CLIENT", null); }
    }

    [Fact]
    public void Built_in_google_client_is_used_unless_user_supplies_one()
    {
        Environment.SetEnvironmentVariable("BACKUP360_GOOGLE_CLIENT", "gomulu-id\ngomulu-secret");
        try
        {
            Assert.True(GoogleClient.HasBuiltIn);
            Assert.Equal(("gomulu-id", "gomulu-secret"), GoogleClient.Resolve(new TargetDefinition { Type = "drive" }));
            Assert.Equal(("benim", "sir"), GoogleClient.Resolve(new TargetDefinition { Type = "drive", ClientId = "benim", ClientSecret = "sir" }));
        }
        finally { Environment.SetEnvironmentVariable("BACKUP360_GOOGLE_CLIENT", null); }
    }

    [Fact]
    public void Validation_rejects_incomplete_definitions()
    {
        Assert.Throws<ArgumentException>(() => new TargetDefinition { Name = "", Type = "alias", Path = "x" }.Validate());
        Assert.Throws<ArgumentException>(() => new TargetDefinition { Name = "a", Type = "sftp", Auth = "password", Host = "h", User = "u" }.Validate());
        Assert.Throws<ArgumentException>(() => new TargetDefinition { Name = "a", Type = "drive", Auth = "token", ClientId = "id", ClientSecret = "s" }.Validate());
        Assert.Throws<ArgumentException>(() => new TargetDefinition { Name = "a", Type = "drive", ClientId = "yalniz-id" }.Validate());
        Assert.Throws<ArgumentException>(() => new TargetDefinition { Name = "a", Type = "s3", AccessKey = "k", SecretKey = "s" }.Validate());
        Assert.Throws<ArgumentException>(() => new TargetDefinition { Name = "a", Type = "smb", Host = "nas", User = "u", Password = "p" }.Validate());
        Assert.Throws<ArgumentException>(() => Cloud.CleanTargetName("C"));
    }

    [Fact]
    public void Targets_of_earlier_versions_are_imported_or_listed_for_redefinition()
    {
        using var s = new Sandbox();
        File.WriteAllText(Path.Combine(AppPaths.DataDir, "rclone.conf"), $"[Eski-Klasor]\ntype = alias\nremote = {s.Cloud2}\n\n[Google]\ntype = drive\ntoken = x\n");
        TargetStore.ImportLegacy();
        Assert.Equal(s.Cloud2, TargetStore.Load("Eski-Klasor")!.Path);
        Assert.Equal(new[] { "Google" }, TargetStore.PendingLegacy());

        TargetStore.Save(new TargetDefinition { Name = "Google", Type = "alias", Path = s.Cloud1 });
        Assert.Empty(TargetStore.PendingLegacy());
        TargetStore.Delete("Eski-Klasor");
        TargetStore.ImportLegacy();
        Assert.Null(TargetStore.Load("Eski-Klasor"));
    }
}
