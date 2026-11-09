using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

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

    private static async Task<Process> Serve(string protocol, string dir, int port, string user, string pass)
    {
        var p = Proc.Start(Tools.Rclone()!, new[] { "serve", protocol, dir, "--addr", $"127.0.0.1:{port}", "--user", user, "--pass", pass });
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
        throw new TimeoutException(protocol + " sunucusu açılmadı");
    }

    public static IEnumerable<object[]> PasswordProtocols() =>
        new[] { new object[] { "sftp" }, new object[] { "ftp" }, new object[] { "webdav" } };

    [Theory, MemberData(nameof(PasswordProtocols))]
    public async Task Password_target_connects_stores_hidden_password_and_creates_folder(string type)
    {
        using var s = new Sandbox();
        var dir = Directory.CreateDirectory(Path.Combine(s.Root, "sunucu")).FullName;
        var port = FreePort();
        var server = await Serve(type, dir, port, "yedekci", "Sifre!123");
        try
        {
            TargetDefinition Def(string name, string pass) => new()
            {
                Name = name, Type = type, Auth = "password", Host = "127.0.0.1", Port = port, User = "yedekci", Password = pass,
                Url = type == "webdav" ? $"http://127.0.0.1:{port}/" : "",
            };

            await Cloud.CreateTargetAsync(Def("uzak", "Sifre!123"), default);
            Assert.DoesNotContain("Sifre!123", File.ReadAllText(AppPaths.RcloneConfig));
            Assert.Contains(await Cloud.ListTargetsAsync(), t => t.Name == "uzak" && t.Type == type);

            var ok = await Cloud.TestAsync("uzak", default);
            Assert.True(ok.Ok, ok.Message);
            Assert.Empty(Directory.GetFileSystemEntries(dir));

            await Cloud.CreateFolderAsync("uzak", "Yedekler/Muhasebe Ğü", default);
            Assert.True(Directory.Exists(Path.Combine(dir, "Yedekler", "Muhasebe Ğü")));

            await Cloud.CreateTargetAsync(Def("yanlis", "HatalıŞifre"), default);
            Assert.False((await Cloud.TestAsync("yanlis", default)).Ok);

            await Assert.ThrowsAsync<ArgumentException>(() => Cloud.CreateTargetAsync(Def("UZAK", "x"), default));
        }
        finally { Proc.TryKill(server); }
    }

    [Theory, InlineData("drive"), InlineData("dropbox")]
    public async Task Token_target_is_stored_without_opening_browser(string type)
    {
        using var s = new Sandbox();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        const string token = "{\"access_token\":\"ornekdeger\",\"token_type\":\"Bearer\",\"refresh_token\":\"yenile\",\"expiry\":\"2030-01-01T00:00:00Z\"}";
        await Cloud.CreateTargetAsync(new TargetDefinition { Name = "tokenli", Type = type, Auth = "token", Token = token }, cts.Token);
        var conf = File.ReadAllText(AppPaths.RcloneConfig);
        Assert.Contains("ornekdeger", conf);
        Assert.Contains(await Cloud.ListTargetsAsync(), t => t.Name == "tokenli" && t.Type == type);
    }

    [Fact]
    public async Task S3_keys_and_webdav_bearer_are_stored()
    {
        using var s = new Sandbox();
        await Cloud.CreateTargetAsync(new TargetDefinition { Name = "kova", Type = "s3", Auth = "token", AccessKey = "AK1", SecretKey = "SK1", Endpoint = "https://s3.ornek.com", Region = "eu" }, default);
        await Cloud.CreateTargetAsync(new TargetDefinition { Name = "dav", Type = "webdav", Auth = "token", Url = "https://dav.ornek.com", Token = "tasiyici" }, default);
        var conf = File.ReadAllText(AppPaths.RcloneConfig);
        Assert.Contains("access_key_id = AK1", conf);
        Assert.Contains("endpoint = https://s3.ornek.com", conf);
        Assert.Contains("bearer_token = tasiyici", conf);
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
    [InlineData("googleapi: Error 403: Quota exceeded, rateLimitExceeded", "yeniden tanımlayın")]
    [InlineData("\"reason\": \"RATE_LIMIT_EXCEEDED\"", "sizin hesabınızla ilgili değil")]
    [InlineData("Error 403: storageQuotaExceeded", "dolu")]
    [InlineData("oauth2: invalid_grant", "yeniden tanımlayın")]
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
        Assert.Throws<ArgumentException>(() => new TargetDefinition { Name = "a", Type = "drive", Auth = "token" }.Validate());
        Assert.Throws<ArgumentException>(() => new TargetDefinition { Name = "a", Type = "drive", ClientId = "yalniz-id" }.Validate());
    }

    /// <summary>Canlı Drive testinde bulunan hata: rclone'un stderr satırları JSON çıktısını bozuyordu.</summary>
    [Fact]
    public async Task Retention_survives_rclone_log_lines()
    {
        using var s = new Sandbox();
        var folder = Directory.CreateDirectory(Path.Combine(s.Cloud1, "J")).FullName;
        File.WriteAllText(Path.Combine(folder, "A_20200101-000000.7z"), "1");
        File.WriteAllText(Path.Combine(folder, "A_20200102-000000.7z"), "2");
        Environment.SetEnvironmentVariable("RCLONE_LOG_LEVEL", "DEBUG");
        try
        {
            Assert.Equal(1, await Cloud.ApplyRetentionAsync("gdrive", "J", "7z", 1, 0, null, default));
            Assert.Contains(await Cloud.ListTargetsAsync(), t => t.Name == "gdrive");
        }
        finally { Environment.SetEnvironmentVariable("RCLONE_LOG_LEVEL", null); }
    }
}
