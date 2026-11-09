using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Backup360.Core;

public sealed class Schedule
{
    public string Type { get; set; } = "none";   // none | minutes | hourly | daily | weekly
    public int Interval { get; set; } = 1;
    public string Time { get; set; } = "02:00";
    public string Days { get; set; } = "Monday";

    private static readonly Dictionary<string, string> DayNames = new()
    {
        ["Monday"] = "Pzt", ["Tuesday"] = "Sal", ["Wednesday"] = "Çar", ["Thursday"] = "Per",
        ["Friday"] = "Cum", ["Saturday"] = "Cmt", ["Sunday"] = "Paz",
    };

    public string Describe() => Type switch
    {
        "minutes" => $"{Interval} dakikada bir",
        "hourly" => $"{Interval} saatte bir",
        "daily" => $"Her gün {Time}",
        "weekly" => $"{string.Join(", ", Days.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(d => DayNames.GetValueOrDefault(d, d)))} {Time}",
        _ => "Elle",
    };
}

public sealed class Job
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Sources { get; set; } = new();
    public List<string> Formats { get; set; } = new() { "7z" };
    public int Level { get; set; } = 9;   // 1-9; varsayılan en yüksek sıkıştırma
    public string? PasswordEnc { get; set; }
    public string RemoteFolder { get; set; } = "Backup360";
    public int Keep { get; set; } = 7;
    /// <summary>archive: tek arşiv · dated: her alt klasöre tarihli arşiv · mirror: sıkıştırmadan birebir</summary>
    public string Mode { get; set; } = "archive";
    public string Group { get; set; } = "Genel";
    public string SyncKind { get; set; } = "folder";   // folder | sql
    public long SyncFromJob { get; set; }   // SQL eşitlemede yedekleri alınacak iş
    public string DatedOutput { get; set; } = "archive";   // archive: sıkıştırılmış | folder: klasör olarak
    public int DaysFilter { get; set; }   // >0 ise yalnızca son N günde değişen alt klasörler
    public int KeepDays { get; set; }     // >0 ise N günden eski yedekler silinir
    public List<string> Targets { get; set; } = new();
    public string DateBy { get; set; } = "run";   // run | folder
    public bool OnlyChanged { get; set; } = true;
    public bool MirrorDelete { get; set; }

    // Veritabanı yedeği (Mode = "database")
    public string DbEngine { get; set; } = "mssql";   // mssql | mysql | sqlite
    public string DbServer { get; set; } = "";
    public int DbPort { get; set; }
    public string DbAuth { get; set; } = "windows";   // windows | sql
    public string DbUser { get; set; } = "";
    public string? DbPasswordEnc { get; set; }
    public bool DbAll { get; set; } = true;
    public List<string> DbNames { get; set; } = new();
    public string DbType { get; set; } = "full";      // full | diff | log
    public bool DbCopyOnly { get; set; } = true;
    public string DbBackupDir { get; set; } = "";
    public string DbAgPolicy { get; set; } = "preferred";   // preferred | current
    public bool DbMultiSubnet { get; set; }
    public List<string> Excludes { get; set; } = new();
    public Schedule Schedule { get; set; } = new();
}

public sealed class MoveRule
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Group { get; set; } = "Genel";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public bool Recursive { get; set; } = true;
    public string Pattern { get; set; } = "*";
    public string Conflict { get; set; } = "rename";   // rename | skip | overwrite
    public bool OnlyNew { get; set; } = true;
    public int StableSeconds { get; set; } = 10;
    public Schedule Schedule { get; set; } = new() { Type = "minutes", Interval = 5 };
}

public sealed record RunRecord(long Id, string Kind, long RefId, string Name, string Started, string? Ended, string Status, string Detail, long Bytes);

public static class Db
{
    public static SqliteConnection Open()
    {
        // Havuzlama kapalı ve WAL yok: veritabanı her zaman tek dosyadır, kopyalanıp taşınabilir.
        var cs = new SqliteConnectionStringBuilder { DataSource = AppPaths.Database, Pooling = false, DefaultTimeout = 30 }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        return c;
    }

    public static void Init()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS users(
              id INTEGER PRIMARY KEY, username TEXT NOT NULL UNIQUE COLLATE NOCASE,
              salt BLOB NOT NULL, hash BLOB NOT NULL, iterations INTEGER NOT NULL,
              failed INTEGER NOT NULL DEFAULT 0, locked_until TEXT);
            CREATE TABLE IF NOT EXISTS jobs(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS rules(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS rule_baseline(
              rule_id INTEGER NOT NULL, rel_path TEXT NOT NULL COLLATE NOCASE, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
              PRIMARY KEY(rule_id, rel_path));
            CREATE TABLE IF NOT EXISTS runs(
              id INTEGER PRIMARY KEY, kind TEXT NOT NULL, ref_id INTEGER NOT NULL, name TEXT NOT NULL,
              started TEXT NOT NULL, ended TEXT, status TEXT NOT NULL, detail TEXT NOT NULL DEFAULT '', bytes INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_runs_ref ON runs(kind, ref_id, id);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS job_state(job_id INTEGER NOT NULL, item TEXT NOT NULL COLLATE NOCASE, fingerprint TEXT NOT NULL, PRIMARY KEY(job_id, item));
            """);
    }

    public static string? GetFingerprint(long jobId, string item) =>
        Query("SELECT fingerprint FROM job_state WHERE job_id=$j AND item=$i", r => r.GetString(0), ("$j", jobId), ("$i", item)).FirstOrDefault();

    public static void SetFingerprint(long jobId, string item, string fingerprint) =>
        Exec("INSERT INTO job_state(job_id,item,fingerprint) VALUES($j,$i,$f) ON CONFLICT(job_id,item) DO UPDATE SET fingerprint=$f", ("$j", jobId), ("$i", item), ("$f", fingerprint));

    public static int Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static long Insert(string sql, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql + "; SELECT last_insert_rowid();", args);
        return (long)cmd.ExecuteScalar()!;
    }

    // ---- ayarlar
    public static string? GetSetting(string key) =>
        Query("SELECT value FROM settings WHERE key=$k", r => r.GetString(0), ("$k", key)).FirstOrDefault();

    public static void SetSetting(string key, string value) =>
        Exec("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));

    // ---- işler ve kurallar
    private static T Parse<T>(long id, string json) where T : class
    {
        var item = JsonSerializer.Deserialize<T>(json)!;
        typeof(T).GetProperty("Id")!.SetValue(item, id);
        return item;
    }

    public static List<Job> Jobs() => Query("SELECT id,data FROM jobs ORDER BY name", r => Parse<Job>(r.GetInt64(0), r.GetString(1)));
    public static Job? GetJob(long id) => Query("SELECT id,data FROM jobs WHERE id=$i", r => Parse<Job>(r.GetInt64(0), r.GetString(1)), ("$i", id)).FirstOrDefault();

    public static void Save(Job j)
    {
        var data = JsonSerializer.Serialize(j);
        if (j.Id == 0) j.Id = Insert("INSERT INTO jobs(name,data) VALUES($n,$d)", ("$n", j.Name), ("$d", data));
        else Exec("UPDATE jobs SET name=$n,data=$d WHERE id=$i", ("$n", j.Name), ("$d", data), ("$i", j.Id));
    }

    public static void DeleteJob(long id)
    {
        Exec("DELETE FROM job_state WHERE job_id=$i", ("$i", id));
        Exec("DELETE FROM jobs WHERE id=$i", ("$i", id));
    }

    public static List<MoveRule> Rules() => Query("SELECT id,data FROM rules ORDER BY name", r => Parse<MoveRule>(r.GetInt64(0), r.GetString(1)));
    public static MoveRule? GetRule(long id) => Query("SELECT id,data FROM rules WHERE id=$i", r => Parse<MoveRule>(r.GetInt64(0), r.GetString(1)), ("$i", id)).FirstOrDefault();

    public static void Save(MoveRule m)
    {
        var data = JsonSerializer.Serialize(m);
        if (m.Id == 0) m.Id = Insert("INSERT INTO rules(name,data) VALUES($n,$d)", ("$n", m.Name), ("$d", data));
        else Exec("UPDATE rules SET name=$n,data=$d WHERE id=$i", ("$n", m.Name), ("$d", data), ("$i", m.Id));
    }

    public static void DeleteRule(long id)
    {
        Exec("DELETE FROM rule_baseline WHERE rule_id=$i", ("$i", id));
        Exec("DELETE FROM rules WHERE id=$i", ("$i", id));
    }

    // ---- çalıştırma geçmişi
    public static long StartRun(string kind, long refId, string name) =>
        Insert("INSERT INTO runs(kind,ref_id,name,started,status) VALUES($k,$r,$n,$s,'running')",
            ("$k", kind), ("$r", refId), ("$n", name), ("$s", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));

    public static void FinishRun(long id, string status, string detail, long bytes) =>
        Exec("UPDATE runs SET ended=$e,status=$s,detail=$d,bytes=$b WHERE id=$i",
            ("$e", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), ("$s", status), ("$d", detail), ("$b", bytes), ("$i", id));

    public static void DeleteRun(long id) => Exec("DELETE FROM runs WHERE id=$i", ("$i", id));

    private static RunRecord MapRun(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt64(8));

    private const string RunColumns = "id,kind,ref_id,name,started,ended,status,detail,bytes";

    public static List<RunRecord> RecentRuns(int limit = 300) =>
        Query($"SELECT {RunColumns} FROM runs ORDER BY id DESC LIMIT $l", MapRun, ("$l", limit));

    public static RunRecord? LastRun(string kind, long refId) =>
        Query($"SELECT {RunColumns} FROM runs WHERE kind=$k AND ref_id=$r ORDER BY id DESC LIMIT 1", MapRun, ("$k", kind), ("$r", refId)).FirstOrDefault();

    /// <summary>Çalışan uygulamayı durdurmadan veritabanının tutarlı bir kopyasını üretir.</summary>
    public static void ExportTo(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        Exec("VACUUM INTO $p", ("$p", path));
    }
}

public enum LoginResult { Ok, Invalid, Locked }

public static class Auth
{
    private const int Iterations = 310_000;
    private const int MaxFailures = 5;
    public const int MinPasswordLength = 8;

    private static (byte[] Salt, byte[] Hash) Derive(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (salt, Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32));
    }

    public static bool HasUsers() => Db.Query("SELECT 1 FROM users LIMIT 1", r => 1).Count > 0;
    public static List<string> Users() => Db.Query("SELECT username FROM users ORDER BY username", r => r.GetString(0));

    public static void AddUser(string username, string password)
    {
        username = username.Trim();
        if (username.Length == 0) throw new ArgumentException("Kullanıcı adı boş olamaz.");
        Validate(password);
        if (Users().Contains(username, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Bu kullanıcı adı zaten var.");
        var (salt, hash) = Derive(password);
        Db.Exec("INSERT INTO users(username,salt,hash,iterations) VALUES($u,$s,$h,$i)", ("$u", username), ("$s", salt), ("$h", hash), ("$i", Iterations));
    }

    private static void Validate(string password)
    {
        if (password.Length < MinPasswordLength) throw new ArgumentException($"Parola en az {MinPasswordLength} karakter olmalı.");
    }

    public static LoginResult Login(string username, string password)
    {
        var row = Db.Query("SELECT id,salt,hash,iterations,failed,locked_until FROM users WHERE username=$u",
            r => (Id: r.GetInt64(0), Salt: (byte[])r[1], Hash: (byte[])r[2], Iter: r.GetInt32(3), Failed: r.GetInt32(4), Locked: r.IsDBNull(5) ? null : r.GetString(5)),
            ("$u", username.Trim())).Cast<(long Id, byte[] Salt, byte[] Hash, int Iter, int Failed, string? Locked)?>().FirstOrDefault();

        if (row is not { } u)
        {
            // Bilinmeyen kullanıcıda da aynı süre harcansın: kullanıcı adı yoklanamasın.
            Rfc2898DeriveBytes.Pbkdf2(password, new byte[16], Iterations, HashAlgorithmName.SHA256, 32);
            return LoginResult.Invalid;
        }
        if (u.Locked != null && DateTime.Parse(u.Locked, CultureInfo.InvariantCulture) > DateTime.UtcNow) return LoginResult.Locked;

        var hash = Rfc2898DeriveBytes.Pbkdf2(password, u.Salt, u.Iter, HashAlgorithmName.SHA256, 32);
        if (CryptographicOperations.FixedTimeEquals(hash, u.Hash))
        {
            Db.Exec("UPDATE users SET failed=0,locked_until=NULL WHERE id=$i", ("$i", u.Id));
            return LoginResult.Ok;
        }
        var failed = u.Failed + 1;
        var lockUntil = failed >= MaxFailures ? DateTime.UtcNow.AddMinutes(1).ToString("o", CultureInfo.InvariantCulture) : null;
        Db.Exec("UPDATE users SET failed=$f,locked_until=$l WHERE id=$i", ("$f", failed >= MaxFailures ? 0 : failed), ("$l", lockUntil), ("$i", u.Id));
        return lockUntil != null ? LoginResult.Locked : LoginResult.Invalid;
    }

    public static void ChangePassword(string username, string newPassword)
    {
        Validate(newPassword);
        var (salt, hash) = Derive(newPassword);
        Db.Exec("UPDATE users SET salt=$s,hash=$h,iterations=$i,failed=0,locked_until=NULL WHERE username=$u",
            ("$s", salt), ("$h", hash), ("$i", Iterations), ("$u", username));
    }

    public static void DeleteUser(string username)
    {
        if (Users().Count <= 1) throw new InvalidOperationException("Son kullanıcı silinemez.");
        Db.Exec("DELETE FROM users WHERE username=$u", ("$u", username));
    }

    /// <summary>Parola unutulduğunda: tüm girişleri siler, ilk açılış ekranı yeniden gelir. İşler ve kurallar korunur.</summary>
    public static void ResetLogins() => Db.Exec("DELETE FROM users");
}
