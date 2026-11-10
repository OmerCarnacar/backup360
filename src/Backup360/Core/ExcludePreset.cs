namespace Backup360.Core;

/// <summary>
/// Paket yöneticisi ve derleyicinin yeniden ürettiği klasörler. Yalnızca adı bu amaca ayrılmış
/// olanlar listededir; bin, dist, build gibi adlar kullanıcının kendi dosyalarını da taşıyabileceği için yoktur.
/// </summary>
public static class ExcludePreset
{
    public static readonly IReadOnlyList<string> Rebuildable = new[] { "node_modules", ".next", ".nuxt", ".angular", "obj", ".vs", "__pycache__", ".venv", ".gradle" };

    private static List<string> Parse(string text) => text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static bool IsOn(string text)
    {
        var items = Parse(text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Rebuildable.All(items.Contains);
    }

    /// <summary>Kullanıcının kendi yazdığı kuralları korur; yalnızca hazır listeyi ekler veya çıkarır.</summary>
    public static string Apply(string text, bool on)
    {
        var items = Parse(text).Where(i => !Rebuildable.Contains(i, StringComparer.OrdinalIgnoreCase)).ToList();
        if (on) items.AddRange(Rebuildable);
        return string.Join(";", items);
    }
}
