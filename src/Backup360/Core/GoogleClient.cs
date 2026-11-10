using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Backup360.Core;

/// <summary>
/// Uygulamayla gelen Google kimliği (varsa): kullanıcı Client ID oluşturmadan tarayıcıdan bağlanır.
/// Değer derlemede google-client.txt dosyasından gömülür (ilk satır ID, ikinci satır secret); depoya konmaz.
/// </summary>
public static class GoogleClient
{
    private static (string Id, string Secret)? Load()
    {
        var env = Environment.GetEnvironmentVariable("BACKUP360_GOOGLE_CLIENT");
        string? text = env;
        if (env == null)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("google-client.txt");
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        var lines = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length >= 2 ? (lines[0], lines[1]) : null;
    }

    public static bool HasBuiltIn => Load() != null;

    /// <summary>Kullanıcı kendi kimliğini girdiyse onu, girmediyse gömülü kimliği döndürür.</summary>
    public static (string? Id, string? Secret) Resolve(TargetDefinition d)
    {
        if (!string.IsNullOrWhiteSpace(d.ClientId)) return (d.ClientId, d.ClientSecret);
        return Load() is { } builtIn ? (builtIn.Id, builtIn.Secret) : (null, null);
    }

    /// <summary>Google'ın indirdiği kimlik dosyasını okur. Desktop false ise kimlik "Web" türündedir ve masaüstünde çalışmaz.</summary>
    public static (string Id, string Secret, bool Desktop)? ReadJson(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var kind in new[] { "installed", "web" })
            {
                if (!doc.RootElement.TryGetProperty(kind, out var node)) continue;
                var id = node.TryGetProperty("client_id", out var i) ? i.GetString() : null;
                var secret = node.TryGetProperty("client_secret", out var s) ? s.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret)) return (id, secret, kind == "installed");
            }
            return null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern string SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token);

    public static string DownloadsFolder()
    {
        try { return SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero); }
        catch (Exception e) when (e is COMException or ArgumentException or IOException) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); }
    }

    /// <summary>
    /// Verilen andan sonra inen kimlik dosyasını bulur. Daha eski dosyalara bakılmaz:
    /// başka projelere ait kimlikler yanlışlıkla kullanılmasın.
    /// </summary>
    public static string? FindNewDownload(string folder, DateTime sinceUtc)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("client_secret*.json")
                .Where(f => f.CreationTimeUtc >= sinceUtc && ReadJson(f.FullName) != null)
                .OrderByDescending(f => f.CreationTimeUtc).FirstOrDefault()?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
