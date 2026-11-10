using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

public class GoogleClientTests
{
    private const string Desktop = "{\"installed\":{\"client_id\":\"masaustu.apps.googleusercontent.com\",\"client_secret\":\"sir-1\",\"redirect_uris\":[\"http://localhost\"]}}";

    [Fact]
    public void Downloaded_file_is_read_and_web_type_is_recognised()
    {
        using var s = new Sandbox();
        var file = Path.Combine(s.Root, "client_secret_1.json");
        File.WriteAllText(file, Desktop);
        Assert.Equal(("masaustu.apps.googleusercontent.com", "sir-1", true), GoogleClient.ReadJson(file));

        File.WriteAllText(file, "{\"web\":{\"client_id\":\"web-id\",\"client_secret\":\"sir-2\"}}");
        Assert.False(GoogleClient.ReadJson(file)!.Value.Desktop);

        File.WriteAllText(file, "{\"installed\":{\"client_id\":\"yalniz-id\"}}");
        Assert.Null(GoogleClient.ReadJson(file));
        File.WriteAllText(file, "json degil");
        Assert.Null(GoogleClient.ReadJson(file));
        Assert.Null(GoogleClient.ReadJson(Path.Combine(s.Root, "yok.json")));
    }

    [Fact]
    public void Only_files_downloaded_after_the_click_are_picked_up()
    {
        using var s = new Sandbox();
        var folder = Directory.CreateDirectory(Path.Combine(s.Root, "indirilenler")).FullName;
        var old = Path.Combine(folder, "client_secret_baska-proje.json");
        File.WriteAllText(old, Desktop.Replace("masaustu", "baska-proje"));
        File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        File.WriteAllText(Path.Combine(folder, "baska.json"), Desktop);
        File.WriteAllText(Path.Combine(folder, "client_secret_yarim.json"), "{\"installed\":");

        var clicked = DateTime.UtcNow.AddSeconds(-5);
        Assert.Null(GoogleClient.FindNewDownload(folder, clicked));

        var fresh = Path.Combine(folder, "client_secret_yeni.json");
        File.WriteAllText(fresh, Desktop);
        Assert.Equal(fresh, GoogleClient.FindNewDownload(folder, clicked));
        Assert.Null(GoogleClient.FindNewDownload(Path.Combine(s.Root, "olmayan"), clicked));
        Assert.True(Directory.Exists(GoogleClient.DownloadsFolder()));
    }

    [Fact]
    public void Built_in_client_only_sees_its_own_files()
    {
        Environment.SetEnvironmentVariable("BACKUP360_GOOGLE_CLIENT", "gomulu-id\ngomulu-secret");
        try
        {
            var builtIn = OAuth.AppFor(new TargetDefinition { Name = "g", Type = "drive", FullAccess = true });
            Assert.Equal("gomulu-id", builtIn.ClientId);
            Assert.EndsWith("/auth/drive.file", builtIn.Scope);
            new TargetDefinition { Name = "g", Type = "drive" }.Validate();
        }
        finally { Environment.SetEnvironmentVariable("BACKUP360_GOOGLE_CLIENT", null); }
    }
}
