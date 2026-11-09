namespace Backup360.Core;

internal static class ClientTools
{
    public static string? Find(string envName, string exe, params string[] roots)
    {
        var env = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, exe))) return env;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir.Trim(), exe))) return dir.Trim();
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d))
            {
                if (File.Exists(Path.Combine(dir, "bin", exe))) return Path.Combine(dir, "bin");
                if (File.Exists(Path.Combine(dir, exe))) return dir;
            }
        return null;
    }

    public static bool LooksCorrupt(string text) =>
        new[] { "corrupt", "checksum", "invalid page", "could not read block", "wrong page type", "not a valid database", "bad page" }
            .Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    public static string Last(string text) =>
        string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
}

public static class PostgresEngine
{
    public static string? BinDir() => ClientTools.Find("BACKUP360_PG_BIN", "pg_dump.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL"));

    private static string Bin(string exe) => Path.Combine(BinDir() ?? throw new InvalidOperationException("pg_dump bulunamadı. PostgreSQL istemci araçlarını kurun."), exe);

    private static Dictionary<string, string> Env(Job j) => new() { ["PGPASSWORD"] = Secrets.Unprotect(j.DbPasswordEnc) ?? "", ["PGCLIENTENCODING"] = "UTF8" };

    private static List<string> Conn(Job j, params string[] rest)
    {
        var args = new List<string> { "-h", j.DbServer, "-U", j.DbUser, "-w" };
        if (j.DbPort > 0) { args.Add("-p"); args.Add(j.DbPort.ToString()); }
        args.AddRange(rest);
        return args;
    }

    private static async Task<string> RunAsync(Job j, string exe, List<string> args, string error, CancellationToken ct)
    {
        var (code, text) = await Proc.RunAsync(Bin(exe), args, null, ct, null, Env(j));
        if (code == 0) return text;
        if (ClientTools.LooksCorrupt(text)) throw new BackupCorruptException($"PostgreSQL yedeği bozuk: {ClientTools.Last(text)}");
        throw new IOException($"{error}: {ClientTools.Last(text)}");
    }

    public static async Task<List<string>> ListAsync(Job j, CancellationToken ct)
    {
        var text = await RunAsync(j, "psql.exe", Conn(j, "-d", "postgres", "-At", "-c", "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate AND datname <> 'postgres' ORDER BY 1"), "PostgreSQL bağlantısı kurulamadı", ct);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>Sıkıştırma pg_dump'a değil, seçilen arşiv formatına bırakılır.</summary>
    public static Task DumpAsync(Job j, string database, string path, CancellationToken ct) =>
        RunAsync(j, "pg_dump.exe", Conn(j, "-Fc", "--compress=0", "-f", path, database), "pg_dump hata verdi", ct);

    public static async Task VerifyAsync(Job j, string path, CancellationToken ct)
    {
        var (code, text) = await Proc.RunAsync(Bin("pg_restore.exe"), new[] { "--list", path }, null, ct, null, Env(j));
        if (code != 0 || !text.Contains("TOC", StringComparison.OrdinalIgnoreCase)) throw new BackupCorruptException($"PostgreSQL yedeği okunamadı: {ClientTools.Last(text)}");
    }

    /// <summary>Yedeği geçici veritabanına tamamen geri yükler; sonra siler.</summary>
    public static async Task RestoreTestAsync(Job j, string path, CancellationToken ct)
    {
        var temp = "backup360_verify_" + Guid.NewGuid().ToString("N")[..10];
        await RunAsync(j, "psql.exe", Conn(j, "-d", "postgres", "-c", $"CREATE DATABASE {temp}"), "Geçici veritabanı oluşturulamadı", ct);
        try
        {
            var (code, text) = await Proc.RunAsync(Bin("pg_restore.exe"), Conn(j, "-d", temp, "--no-owner", "--no-privileges", "--exit-on-error", path), null, ct, null, Env(j));
            if (code != 0) throw new BackupCorruptException($"PostgreSQL yedeği geri yüklenemedi: {ClientTools.Last(text)}");
        }
        finally
        {
            await Proc.RunAsync(Bin("psql.exe"), Conn(j, "-d", "postgres", "-c", $"DROP DATABASE IF EXISTS {temp} WITH (FORCE)"), null, CancellationToken.None, null, Env(j));
        }
    }
}

public static class FirebirdEngine
{
    public static string? BinDir() => ClientTools.Find("BACKUP360_FB_BIN", "gbak.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Firebird"));

    private static string Bin(string exe) => Path.Combine(BinDir() ?? throw new InvalidOperationException("gbak bulunamadı. Firebird istemci araçlarını kurun."), exe);

    /// <summary>Kullanıcı ve şifre ortam değişkeniyle verilir; komut satırında görünmez.</summary>
    private static Dictionary<string, string> Env(Job j) => new() { ["ISC_USER"] = j.DbUser, ["ISC_PASSWORD"] = Secrets.Unprotect(j.DbPasswordEnc) ?? "" };

    private static string Conn(Job j, string database) => j.DbPort > 0 ? $"{j.DbServer}/{j.DbPort}:{database}" : $"{j.DbServer}:{database}";

    private static async Task RunAsync(Job j, string exe, string[] args, string error, CancellationToken ct)
    {
        var (code, text) = await Proc.RunAsync(Bin(exe), args, null, ct, null, Env(j));
        if (code == 0 && !ClientTools.LooksCorrupt(text)) return;
        if (ClientTools.LooksCorrupt(text)) throw new BackupCorruptException($"Firebird veritabanı veya yedeği bozuk: {ClientTools.Last(text)}");
        throw new IOException($"{error}: {ClientTools.Last(text)}");
    }

    public static Task BackupAsync(Job j, string database, string path, CancellationToken ct) =>
        RunAsync(j, "gbak.exe", new[] { "-b", Conn(j, database), path }, "gbak hata verdi", ct);

    public static bool IsLocal(Job j) => j.DbServer is "localhost" or "127.0.0.1" || string.Equals(j.DbServer, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Yedeği geçici dosyaya geri yükler ve gfix ile tüm sayfaları doğrular.</summary>
    public static async Task RestoreTestAsync(Job j, string path, string work, CancellationToken ct)
    {
        var temp = Path.Combine(work, $"verify-{Guid.NewGuid():N}.fdb");
        try
        {
            await RunAsync(j, "gbak.exe", new[] { "-c", path, Conn(j, temp) }, "Geri yükleme sınavı yapılamadı", ct);
            await RunAsync(j, "gfix.exe", new[] { "-v", "-full", Conn(j, temp) }, "gfix hata verdi", ct);
        }
        finally
        {
            for (var i = 0; i < 10 && File.Exists(temp); i++)
            {
                try { File.Delete(temp); }
                catch (IOException) { await Task.Delay(500, CancellationToken.None); }
            }
        }
    }
}
