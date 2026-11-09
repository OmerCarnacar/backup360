using System.Reflection;

namespace Backup360.Core;

/// <summary>
/// Uygulamayla gelen Google kimliği: kullanıcı Client ID oluşturmadan tarayıcıdan bağlanır.
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
}
