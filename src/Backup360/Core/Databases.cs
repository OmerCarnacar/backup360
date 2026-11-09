using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Backup360.Core;

/// <summary>Yedek veya kaynak veritabanı bozuk: normal bir bağlantı hatasından ayrı bildirilir.</summary>
public sealed class BackupCorruptException(string message, Exception? inner = null) : IOException(message, inner);

public static class BackupHealth
{
    // 3043: yedeklenirken bozuk sayfa · 3183/3189/3241-3243/3256: bozuk yedek kümesi · 823/824: disk veya sayfa bozulması
    private static readonly HashSet<int> CorruptionErrors = new() { 823, 824, 3043, 3183, 3189, 3241, 3242, 3243, 3256 };

    // 89xx: DBCC CHECKDB tutarlılık hataları (8921 yalnızca kaynak yetersizliğidir)
    public static bool IsCorruption(SqlException e) =>
        e.Errors.Cast<SqlError>().Any(x => CorruptionErrors.Contains(x.Number) || (x.Number is >= 8900 and <= 8999 && x.Number != 8921));
}

public sealed record DbInfo(string Name, string State, string Recovery, bool InAg, string? AgName, string? Role, string? SyncState, bool Preferred)
{
    public string Label => InAg ? $"{Name}   [Always On: {AgName} · {Role} · {SyncState}]" : Name;
}

public enum DecisionKind { Run, Skip, Warn, Fail }

public sealed record BackupDecision(DecisionKind Kind, bool CopyOnly, string Reason);

/// <summary>Bir veritabanının bu replikada, istenen türde yedeklenip yedeklenemeyeceğine karar verir.</summary>
public static class AlwaysOn
{
    public static BackupDecision Decide(DbInfo db, string type, string policy, bool copyOnlyFull)
    {
        var secondary = false;
        if (db.InAg)
        {
            if (policy == "preferred" && !db.Preferred)
                return new(DecisionKind.Skip, false, "Tercih edilen yedek replikası değil; yedeği diğer replika alır.");
            if (db.Role is not ("PRIMARY" or "SECONDARY"))
                return new(DecisionKind.Fail, false, $"Replika rolü belirsiz: {db.Role ?? "bilinmiyor"}.");
            secondary = db.Role == "SECONDARY";
            if (secondary)
            {
                if (type == "diff")
                    return new(DecisionKind.Fail, false, "Always On ikincil replikasında fark (differential) yedeği alınamaz. Birincil replikaya bağlanın.");
                if (db.SyncState is not ("SYNCHRONIZED" or "SYNCHRONIZING"))
                    return new(DecisionKind.Fail, false, $"Replika eşitlenmiyor ({db.SyncState ?? "bilinmiyor"}); ikincilden yedek alınamaz.");
            }
        }
        else if (!string.Equals(db.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            return new(DecisionKind.Warn, false, $"Veritabanı çevrimiçi değil ({db.State}).");
        }

        if (type == "log" && string.Equals(db.Recovery, "SIMPLE", StringComparison.OrdinalIgnoreCase))
            return new(DecisionKind.Warn, false, "SIMPLE kurtarma modelinde günlük (log) yedeği alınamaz.");
        if (type == "diff" && string.Equals(db.Name, "master", StringComparison.OrdinalIgnoreCase))
            return new(DecisionKind.Warn, false, "master veritabanının fark yedeği alınamaz.");

        // İkincilde tam yedek yalnızca COPY_ONLY olabilir.
        return new(DecisionKind.Run, type == "full" && (secondary || copyOnlyFull), "");
    }
}

public static class MsSql
{
    private static readonly string[] SystemDbs = { "master", "model", "msdb", "tempdb" };

    private static string ConnectionString(Job j)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = j.DbPort > 0 ? $"{j.DbServer},{j.DbPort}" : j.DbServer,
            InitialCatalog = "master",
            IntegratedSecurity = j.DbAuth != "sql",
            Encrypt = SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = true,
            ConnectTimeout = 20,
            MultiSubnetFailover = j.DbMultiSubnet,
            ApplicationName = "Backup360",
            Pooling = false,
        };
        if (j.DbAuth == "sql")
        {
            b.UserID = j.DbUser;
            b.Password = Secrets.Unprotect(j.DbPasswordEnc) ?? "";
        }
        return b.ConnectionString;
    }

    private static async Task<SqlConnection> OpenAsync(Job j, CancellationToken ct)
    {
        var c = new SqlConnection(ConnectionString(j));
        await c.OpenAsync(ct);
        return c;
    }

    private const string PlainQuery =
        "SELECT d.name, d.state_desc, d.recovery_model_desc, CAST(0 AS bit), CAST(NULL AS sysname), CAST(NULL AS nvarchar(60)), CAST(NULL AS nvarchar(60)), CAST(1 AS bit) FROM sys.databases d WHERE d.name <> 'tempdb' ORDER BY d.name";

    public const string AgQuery = """
        SELECT d.name, d.state_desc, d.recovery_model_desc,
               CAST(CASE WHEN d.replica_id IS NULL THEN 0 ELSE 1 END AS bit),
               ag.name, ars.role_desc, drs.synchronization_state_desc,
               CAST(CASE WHEN d.replica_id IS NULL THEN 1 ELSE sys.fn_hadr_backup_is_preferred_replica(d.name) END AS bit)
        FROM sys.databases d
        LEFT JOIN sys.dm_hadr_availability_replica_states ars ON ars.replica_id = d.replica_id AND ars.is_local = 1
        LEFT JOIN sys.availability_groups ag ON ag.group_id = ars.group_id
        LEFT JOIN sys.dm_hadr_database_replica_states drs ON drs.database_id = d.database_id AND drs.is_local = 1
        WHERE d.name <> 'tempdb' ORDER BY d.name
        """;

    public static async Task<List<DbInfo>> ListAsync(Job j, CancellationToken ct)
    {
        await using var c = await OpenAsync(j, ct);
        await using var probe = new SqlCommand("SELECT ISNULL(CAST(SERVERPROPERTY('IsHadrEnabled') AS int), 0)", c);
        var hadr = Convert.ToInt32(await probe.ExecuteScalarAsync(ct)) == 1;
        await using var cmd = new SqlCommand(hadr ? AgQuery : PlainQuery, c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DbInfo>();
        while (await r.ReadAsync(ct))
            list.Add(new DbInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetBoolean(7)));
        return list;
    }

    public static IEnumerable<DbInfo> Select(Job j, List<DbInfo> all) =>
        j.DbAll ? all.Where(d => !SystemDbs.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) : all.Where(d => j.DbNames.Contains(d.Name, StringComparer.OrdinalIgnoreCase));

    /// <summary>Saldırılarda sık kullanılan zayıf SQL Server ayarlarını bildirir; hiçbir ayarı değiştirmez.</summary>
    public static async Task<List<string>> SecurityNotesAsync(Job j, CancellationToken ct)
    {
        var notes = new List<string>();
        if (string.Equals(j.DbUser, "sa", StringComparison.OrdinalIgnoreCase) && j.DbAuth == "sql")
            notes.Add("sa hesabı kullanılıyor: yalnızca yedek yetkili ayrı bir hesap daha güvenlidir.");
        try
        {
            await using var c = await OpenAsync(j, ct);
            await using var cmd = new SqlCommand("SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = 'xp_cmdshell'", c);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1)
                notes.Add("xp_cmdshell açık: saldırganların SQL üzerinden komut çalıştırmasına izin verir, kapatılması önerilir.");
        }
        catch (SqlException) { }
        return notes;
    }

    public static async Task<string> BackupFolderAsync(Job j, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(j.DbBackupDir)) return j.DbBackupDir.Trim();
        await using var c = await OpenAsync(j, ct);
        await using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))", c);
        var path = await cmd.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(path) ? throw new InvalidOperationException("Sunucunun varsayılan yedek klasörü okunamadı. İşte bir yedek klasörü belirtin.") : path;
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>Komutu çalıştırırken ayrı bağlantıdan sunucunun bildirdiği yüzdeyi izler.</summary>
    private static async Task RunWithProgressAsync(Job j, string sql, string path, Action<int>? percent, CancellationToken ct)
    {
        await using var c = await OpenAsync(j, ct);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 };
        cmd.Parameters.AddWithValue("@p", path);
        var spid = c.ServerProcessId;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = MonitorAsync(j, spid, percent, stop.Token);
        try { await cmd.ExecuteNonQueryAsync(ct); }
        finally
        {
            stop.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
        }
    }

    private static async Task MonitorAsync(Job j, int spid, Action<int>? percent, CancellationToken ct)
    {
        if (percent == null) return;
        try
        {
            await using var c = await OpenAsync(j, ct);
            await using var cmd = new SqlCommand("SELECT percent_complete FROM sys.dm_exec_requests WHERE session_id = @s", c);
            cmd.Parameters.AddWithValue("@s", spid);
            while (!ct.IsCancellationRequested)
            {
                if (await cmd.ExecuteScalarAsync(ct) is float value) percent((int)value);
                await Task.Delay(700, ct);
            }
        }
        catch (SqlException) { }   // Yüzde bilgisi yoksa yedek yine sürer; pano kayan çubuk gösterir.
    }

    public static Task BackupAsync(Job j, DbInfo db, string type, bool copyOnly, string path, Action<int>? percent, CancellationToken ct)
    {
        var kind = type == "log" ? "LOG" : "DATABASE";
        var options = new List<string> { "INIT", "FORMAT", "CHECKSUM" };
        if (type == "diff") options.Add("DIFFERENTIAL");
        if (copyOnly) options.Add("COPY_ONLY");
        // Sunucu tarafında sıkıştırılmaz: asıl sıkıştırmayı seçilen arşiv formatı en yüksek oranla yapar.
        return RunWithProgressAsync(j, $"BACKUP {kind} {Quote(db.Name)} TO DISK = @p WITH {string.Join(", ", options)}", path, percent, ct);
    }

    /// <summary>
    /// Yedeğin gerçekten açıldığını kanıtlar: geçici veritabanına geri yükler, DBCC CHECKDB çalıştırır ve siler.
    /// Üretim veritabanına dokunmaz; geçici olarak veritabanı kadar disk alanı kullanır.
    /// </summary>
    public static async Task RestoreTestAsync(Job j, string path, Action<int>? percent, CancellationToken ct)
    {
        var temp = "Backup360_Verify_" + Guid.NewGuid().ToString("N")[..10];
        await using var c = await OpenAsync(j, ct);

        string dataDir, logDir;
        await using (var folders = new SqlCommand("SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000)), CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000))", c))
        await using (var r = await folders.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            var fallback = Path.GetDirectoryName(path)!;
            dataDir = r.IsDBNull(0) || r.GetString(0).Length == 0 ? fallback : r.GetString(0);
            logDir = r.IsDBNull(1) || r.GetString(1).Length == 0 ? fallback : r.GetString(1);
        }

        var files = new List<(string Logical, string Type)>();
        await using (var list = new SqlCommand("RESTORE FILELISTONLY FROM DISK = @p", c) { CommandTimeout = 0 })
        {
            list.Parameters.AddWithValue("@p", path);
            await using var r = await list.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) files.Add(((string)r["LogicalName"], (string)r["Type"]));
        }

        var moves = new List<string>();
        await using var restore = new SqlCommand { Connection = c, CommandTimeout = 0 };
        restore.Parameters.AddWithValue("@p", path);
        for (var i = 0; i < files.Count; i++)
        {
            var (logical, type) = files[i];
            var target = type == "L" ? Path.Combine(logDir, $"{temp}_{i}.ldf") : Path.Combine(dataDir, type == "D" ? $"{temp}_{i}.mdf" : $"{temp}_{i}");
            restore.Parameters.AddWithValue($"@l{i}", logical);
            restore.Parameters.AddWithValue($"@t{i}", target);
            moves.Add($"MOVE @l{i} TO @t{i}");
        }
        restore.CommandText = $"RESTORE DATABASE {Quote(temp)} FROM DISK = @p WITH CHECKSUM, RECOVERY, {string.Join(", ", moves)}";

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = MonitorAsync(j, c.ServerProcessId, percent, stop.Token);
        try
        {
            await restore.ExecuteNonQueryAsync(ct);
            await using var check = new SqlCommand($"DBCC CHECKDB ({Quote(temp)}) WITH NO_INFOMSGS, ALL_ERRORMSGS", c) { CommandTimeout = 0 };
            await check.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            stop.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
            await DropTempAsync(j, temp);
        }
    }

    /// <summary>Geçici veritabanını her durumda kaldırır; iptal edilse de sunucuda iz bırakmaz.</summary>
    private static async Task DropTempAsync(Job j, string temp)
    {
        try
        {
            await using var c = await OpenAsync(j, CancellationToken.None);
            await using var drop = new SqlCommand($"IF DB_ID(N'{temp}') IS NOT NULL BEGIN ALTER DATABASE {Quote(temp)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Quote(temp)}; END", c) { CommandTimeout = 120 };
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (SqlException) { }
    }

    public static Task VerifyAsync(Job j, string path, Action<int>? percent, CancellationToken ct) =>
        RunWithProgressAsync(j, "RESTORE VERIFYONLY FROM DISK = @p WITH CHECKSUM", path, percent, ct);
}

public static class MySqlEngine
{
    private static readonly string[] SystemDbs = { "information_schema", "performance_schema", "mysql", "sys" };

    public static string? BinDir()
    {
        var env = Environment.GetEnvironmentVariable("BACKUP360_MYSQL_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "mysqldump.exe"))) return env;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir.Trim(), "mysqldump.exe"))) return dir.Trim();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var roots = new[] { Path.Combine(pf, "MySQL"), pf, @"C:\xampp", @"C:\wamp64\bin\mysql" };
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var dir in Directory.GetDirectories(root).Where(d => Path.GetFileName(d).StartsWith("MySQL", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(d).StartsWith("MariaDB", StringComparison.OrdinalIgnoreCase)).OrderByDescending(d => d))
                if (File.Exists(Path.Combine(dir, "bin", "mysqldump.exe"))) return Path.Combine(dir, "bin");
        return null;
    }

    private static string Bin(string exe)
    {
        var dir = BinDir() ?? throw new InvalidOperationException("mysqldump bulunamadı. MySQL veya MariaDB istemci araçlarını kurun.");
        return Path.Combine(dir, exe);
    }

    private static string Escape(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Parola komut satırında görünmesin diye geçici seçenek dosyasıyla verilir.</summary>
    private static string WriteOptions(Job j, string work)
    {
        var path = Path.Combine(work, $"my-{Guid.NewGuid():N}.cnf");
        var text = new StringBuilder("[client]\n");
        text.Append($"host={Escape(j.DbServer)}\n");
        if (j.DbPort > 0) text.Append($"port={j.DbPort}\n");
        text.Append($"user={Escape(j.DbUser)}\n");
        var password = Secrets.Unprotect(j.DbPasswordEnc);
        if (!string.IsNullOrEmpty(password)) text.Append($"password={Escape(password)}\n");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return path;
    }

    public static async Task<List<string>> ListAsync(Job j, CancellationToken ct)
    {
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Backup360", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var options = WriteOptions(j, work);
            var (code, text) = await Proc.RunAsync(Bin("mysql.exe"), new[] { "--defaults-extra-file=" + options, "-N", "-B", "-e", "SHOW DATABASES" }, null, ct);
            if (code != 0) throw new IOException("MySQL bağlantısı kurulamadı: " + text.Trim());
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => !SystemDbs.Contains(n, StringComparer.OrdinalIgnoreCase)).OrderBy(n => n).ToList();
        }
        finally { Directory.Delete(work, true); }
    }

    public static async Task DumpAsync(Job j, string database, string path, string work, CancellationToken ct)
    {
        var options = WriteOptions(j, work);
        try
        {
            var (code, text) = await Proc.RunAsync(Bin("mysqldump.exe"), new[]
            {
                "--defaults-extra-file=" + options, "--single-transaction", "--routines", "--events", "--triggers", "--hex-blob",
                "--default-character-set=utf8mb4", "--result-file=" + path, "--databases", database,
            }, null, ct);
            if (code != 0) throw new IOException($"mysqldump hata verdi (kod {code}): {text.Trim()}");
        }
        finally { File.Delete(options); }

        // mysqldump yarıda kalırsa bu satır yazılmaz.
        using var f = File.OpenRead(path);
        f.Seek(Math.Max(0, f.Length - 256), SeekOrigin.Begin);
        using var r = new StreamReader(f);
        if (!r.ReadToEnd().Contains("Dump completed")) throw new BackupCorruptException("MySQL dökümü eksik: tamamlanma satırı yok.");
    }
}

public static class SqliteEngine
{
    /// <summary>SQLite çevrimiçi yedek API'si kullanılır: uygulama veritabanına yazarken de tutarlı kopya alınır.</summary>
    public static void Backup(string source, string destination)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("SQLite dosyası bulunamadı: " + source);
        try { Copy(source, destination); }
        catch (SqliteException e) when (e.SqliteErrorCode is 11 or 26)
        {
            throw new BackupCorruptException($"SQLite dosyası bozuk: {source}", e);
        }
    }

    private static void Copy(string source, string destination)
    {
        using (var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString()))
        {
            from.Open();
            to.Open();
            from.BackupDatabase(to);
        }
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        check.Open();
        using var cmd = check.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        var result = cmd.ExecuteScalar() as string;
        if (result != "ok") throw new BackupCorruptException("SQLite bütünlük denetimi başarısız: " + result);
    }
}
