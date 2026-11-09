using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Backup360.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Backup360.Tests;

public class DatabaseTests
{
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private static string Cs(string db) => $"Data Source={LocalDb};Initial Catalog={db};Integrated Security=true;TrustServerCertificate=true;Pooling=false";

    private static DbInfo Ag(string role, bool preferred = true, string sync = "SYNCHRONIZED") =>
        new("ERP", "ONLINE", "FULL", true, "AG1", role, sync, preferred);

    [Fact]
    public void Always_on_rules()
    {
        Assert.Equal(DecisionKind.Skip, AlwaysOn.Decide(Ag("SECONDARY", preferred: false), "full", "preferred", false).Kind);

        var secondaryFull = AlwaysOn.Decide(Ag("SECONDARY"), "full", "preferred", false);
        Assert.Equal(DecisionKind.Run, secondaryFull.Kind);
        Assert.True(secondaryFull.CopyOnly);

        var secondaryLog = AlwaysOn.Decide(Ag("SECONDARY"), "log", "preferred", true);
        Assert.Equal(DecisionKind.Run, secondaryLog.Kind);
        Assert.False(secondaryLog.CopyOnly);

        Assert.Equal(DecisionKind.Fail, AlwaysOn.Decide(Ag("SECONDARY"), "diff", "preferred", false).Kind);
        Assert.Equal(DecisionKind.Fail, AlwaysOn.Decide(Ag("SECONDARY", sync: "NOT SYNCHRONIZING"), "full", "preferred", false).Kind);
        Assert.Equal(DecisionKind.Fail, AlwaysOn.Decide(Ag("RESOLVING"), "full", "preferred", false).Kind);

        var forced = AlwaysOn.Decide(Ag("SECONDARY", preferred: false), "full", "current", false);
        Assert.Equal(DecisionKind.Run, forced.Kind);
        Assert.True(forced.CopyOnly);

        var primary = AlwaysOn.Decide(Ag("PRIMARY"), "full", "preferred", false);
        Assert.Equal(DecisionKind.Run, primary.Kind);
        Assert.False(primary.CopyOnly);
        Assert.Equal(DecisionKind.Run, AlwaysOn.Decide(Ag("PRIMARY"), "diff", "preferred", false).Kind);

        var standalone = new DbInfo("X", "ONLINE", "SIMPLE", false, null, null, null, true);
        Assert.Equal(DecisionKind.Warn, AlwaysOn.Decide(standalone, "log", "preferred", false).Kind);
        Assert.Equal(DecisionKind.Warn, AlwaysOn.Decide(standalone with { State = "OFFLINE" }, "full", "preferred", false).Kind);
    }

    private static async Task Sql(string text, string db = "master")
    {
        await using var c = new SqlConnection(Cs(db));
        await c.OpenAsync();
        await using var cmd = new SqlCommand(text, c) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> Count(string text, string db)
    {
        await using var c = new SqlConnection(Cs(db));
        await c.OpenAsync();
        await using var cmd = new SqlCommand(text, c);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string> Extract(string archive, string dest)
    {
        Directory.CreateDirectory(dest);
        var (code, text) = await Proc.RunAsync(Tools.SevenZip()!, new[] { "x", "-y", "-o" + dest, archive }, null, default);
        Assert.True(code == 0, text);
        return Directory.GetFiles(dest).Single();
    }

    private static Job DbJob(string name, string engine) =>
        new() { Name = name, Mode = "database", DbEngine = engine, Formats = new() { "7z" }, Targets = new() { "gdrive" }, DbAll = false };

    private static async Task Drop(string name) =>
        await Sql($"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}] END");

    [Fact]
    public async Task Sql_server_full_diff_log_and_restore()
    {
        using var s = new Sandbox();
        var name = "b360_" + Guid.NewGuid().ToString("N")[..8];
        var restored = name + "_r";
        var bak = Directory.CreateDirectory(Path.Combine(s.Root, "bak")).FullName;
        var files = Directory.CreateDirectory(Path.Combine(s.Root, "mdf")).FullName;
        await Sql($"CREATE DATABASE [{name}] ON (NAME=d, FILENAME='{files}\\{name}.mdf') LOG ON (NAME=l, FILENAME='{files}\\{name}.ldf'); ALTER DATABASE [{name}] SET RECOVERY FULL");
        try
        {
            await Sql("CREATE TABLE t(id int); INSERT t VALUES (1),(2),(3)", name);
            var job = DbJob("SQL", "mssql");
            job.DbServer = LocalDb;
            job.DbNames = new() { name };
            job.DbBackupDir = bak;
            job.DbCopyOnly = false;
            Db.Save(job);

            var full = await BackupRunner.RunAsync(job.Id, null, default);
            Assert.True(full.Status == "success", full.Detail);
            Assert.Empty(Directory.GetFiles(bak));
            var archive = Directory.GetFiles(s.Cloud1, $"{name}_FULL_*.7z", SearchOption.AllDirectories).Single();
            Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), archive);

            await Sql("INSERT t VALUES (4)", name);
            foreach (var type in new[] { "diff", "log" })
            {
                job.DbType = type;
                Db.Save(job);
                await Task.Delay(1100);
                var r = await BackupRunner.RunAsync(job.Id, null, default);
                Assert.True(r.Status == "success", r.Detail);
                Assert.Single(Directory.GetFiles(s.Cloud1, $"{name}_{type.ToUpperInvariant()}_*.7z", SearchOption.AllDirectories));
            }

            var extracted = await Extract(archive, Path.Combine(s.Root, "acik"));
            await Sql($"RESTORE DATABASE [{restored}] FROM DISK='{extracted}' WITH MOVE 'd' TO '{files}\\{restored}.mdf', MOVE 'l' TO '{files}\\{restored}.ldf'");
            Assert.Equal(3, await Count("SELECT COUNT(*) FROM t", restored));
        }
        finally
        {
            await Drop(restored);
            await Drop(name);
        }
    }

    [Fact]
    public async Task Sql_server_copy_only_flag_and_listing()
    {
        using var s = new Sandbox();
        var name = "b360_" + Guid.NewGuid().ToString("N")[..8];
        var bak = Directory.CreateDirectory(Path.Combine(s.Root, "bak")).FullName;
        var files = Directory.CreateDirectory(Path.Combine(s.Root, "mdf")).FullName;
        await Sql($"CREATE DATABASE [{name}] ON (NAME=d, FILENAME='{files}\\{name}.mdf') LOG ON (NAME=l, FILENAME='{files}\\{name}.ldf')");
        try
        {
            var job = DbJob("COPY", "mssql");
            job.DbServer = LocalDb;
            job.DbNames = new() { name };
            job.DbBackupDir = bak;
            var all = await MsSql.ListAsync(job, default);
            Assert.Contains(all, d => d.Name == name && !d.InAg);
            Assert.DoesNotContain(all, d => d.Name == "tempdb");
            job.DbAll = true;
            Assert.DoesNotContain(MsSql.Select(job, all), d => d.Name == "master");
            job.DbAll = false;

            var path = Path.Combine(bak, "x.bak");
            await MsSql.BackupAsync(job, all.Single(d => d.Name == name), "full", true, path, null, default);
            await MsSql.VerifyAsync(job, path, null, default);
            await using var c = new SqlConnection(Cs("master"));
            await c.OpenAsync();
            await using var cmd = new SqlCommand($"RESTORE HEADERONLY FROM DISK='{path}'", c);
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                Assert.True(await r.ReadAsync());
                Assert.True(Convert.ToBoolean(r["IsCopyOnly"]));
            }
        }
        finally { await Drop(name); }
    }

    [Fact]
    public async Task Always_on_query_is_valid_sql_with_expected_shape()
    {
        await using var c = new SqlConnection(Cs("master"));
        await c.OpenAsync();
        await using var cmd = new SqlCommand(MsSql.AgQuery, c);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(8, r.FieldCount);
        Assert.False(r.GetBoolean(3));
        Assert.True(r.GetBoolean(7));
    }

    [Fact]
    public async Task Sqlite_backup_while_open_and_restorable()
    {
        using var s = new Sandbox();
        var source = Path.Combine(s.Root, "uygulama.db");
        using var open = new SqliteConnection($"Data Source={source};Pooling=False");
        open.Open();
        using (var cmd = open.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t(a); INSERT INTO t VALUES (7);";
            cmd.ExecuteNonQuery();
        }
        var job = DbJob("Lite", "sqlite");
        job.DbNames = new() { source };
        Db.Save(job);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.True(r.Status == "success", r.Detail);
        var extracted = await Extract(Directory.GetFiles(s.Cloud1, "*.7z", SearchOption.AllDirectories).Single(), Path.Combine(s.Root, "acik"));
        using var copy = new SqliteConnection($"Data Source={extracted};Pooling=False");
        copy.Open();
        using var q = copy.CreateCommand();
        q.CommandText = "SELECT a FROM t";
        Assert.Equal(7L, q.ExecuteScalar());
    }

    [Fact]
    public async Task Bad_sqlite_file_fails_job()
    {
        using var s = new Sandbox();
        var fake = Path.Combine(s.Root, "bozuk.db");
        File.WriteAllText(fake, "bu bir veritabani degil");
        var job = DbJob("Bozuk", "sqlite");
        job.DbNames = new() { fake };
        Db.Save(job);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.Equal("corrupt", r.Status);
        Assert.StartsWith("YEDEK BOZUK", r.Detail);
        Assert.Equal("corrupt", Db.LastRun("job", job.Id)!.Status);
        Assert.Empty(Directory.GetFiles(s.Cloud1, "*", SearchOption.AllDirectories));
    }

    private static async Task<int> TempDatabases() => await Count("SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'Backup360_Verify_%'", "master");

    [Fact]
    public async Task Restore_test_proves_backup_and_leaves_nothing_behind()
    {
        using var s = new Sandbox();
        var name = "b360_" + Guid.NewGuid().ToString("N")[..8];
        var bak = Directory.CreateDirectory(Path.Combine(s.Root, "bak")).FullName;
        var files = Directory.CreateDirectory(Path.Combine(s.Root, "mdf")).FullName;
        await Sql($"CREATE DATABASE [{name}] ON (NAME=d, FILENAME='{files}\\{name}.mdf') LOG ON (NAME=l, FILENAME='{files}\\{name}.ldf')");
        try
        {
            await Sql("CREATE TABLE t(a char(4000)); INSERT t VALUES ('x'),('y')", name);
            var job = DbJob("Sinav", "mssql");
            job.DbServer = LocalDb;
            job.DbNames = new() { name };
            job.DbBackupDir = bak;
            job.DbRestoreTest = true;
            Db.Save(job);
            var lines = new List<string>();
            var r = await BackupRunner.RunAsync(job.Id, l => { lock (lines) lines.Add(l); }, default);
            Assert.True(r.Status == "success", r.Detail);
            lock (lines) Assert.Contains(lines, l => l.Contains("CHECKDB temiz"));
            Assert.Equal(0, await TempDatabases());
            Assert.Single(Directory.GetFiles(s.Cloud1, $"{name}_FULL_*.7z", SearchOption.AllDirectories));

            var db = (await MsSql.ListAsync(job, default)).Single(d => d.Name == name);
            var path = Path.Combine(bak, "h.bak");
            await MsSql.BackupAsync(job, db, "full", true, path, null, default);
            var bytes = File.ReadAllBytes(path);
            for (var i = bytes.Length / 3; i < bytes.Length * 2 / 3; i += 512) bytes[i] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
            var error = await Assert.ThrowsAsync<SqlException>(() => MsSql.RestoreTestAsync(job, path, null, default));
            Assert.True(BackupHealth.IsCorruption(error), string.Join(",", error.Errors.Cast<SqlError>().Select(x => x.Number)));
            Assert.Equal(0, await TempDatabases());
        }
        finally { await Drop(name); }
    }

    [Fact]
    public async Task Security_notes_flag_sa_without_changing_server()
    {
        var job = DbJob("Guv", "mssql");
        job.DbServer = LocalDb;
        var windows = await MsSql.SecurityNotesAsync(job, default);
        Assert.DoesNotContain(windows, n => n.StartsWith("sa "));
        job.DbAuth = "sql";
        job.DbUser = "SA";
        Assert.Contains(await MsSql.SecurityNotesAsync(job, default), n => n.StartsWith("sa "));
    }

    [Fact]
    public async Task Damaged_sql_server_backup_is_recognized_as_corrupt()
    {
        using var s = new Sandbox();
        var name = "b360_" + Guid.NewGuid().ToString("N")[..8];
        var bak = Directory.CreateDirectory(Path.Combine(s.Root, "bak")).FullName;
        var files = Directory.CreateDirectory(Path.Combine(s.Root, "mdf")).FullName;
        await Sql($"CREATE DATABASE [{name}] ON (NAME=d, FILENAME='{files}\\{name}.mdf') LOG ON (NAME=l, FILENAME='{files}\\{name}.ldf')");
        try
        {
            await Sql("CREATE TABLE t(a char(4000)); INSERT t VALUES ('x'),('y'),('z')", name);
            var job = DbJob("Hasar", "mssql");
            job.DbServer = LocalDb;
            job.DbNames = new() { name };
            var db = (await MsSql.ListAsync(job, default)).Single(d => d.Name == name);
            var path = Path.Combine(bak, "x.bak");
            await MsSql.BackupAsync(job, db, "full", true, path, null, default);
            await MsSql.VerifyAsync(job, path, null, default);

            var bytes = File.ReadAllBytes(path);
            for (var i = bytes.Length / 3; i < bytes.Length * 2 / 3; i += 512) bytes[i] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            var error = await Assert.ThrowsAsync<SqlException>(() => MsSql.VerifyAsync(job, path, null, default));
            Assert.True(BackupHealth.IsCorruption(error), string.Join(",", error.Errors.Cast<SqlError>().Select(x => x.Number)));
        }
        finally { await Drop(name); }
    }

    [Fact]
    public async Task Mysql_dump_against_temporary_server()
    {
        var port = Environment.GetEnvironmentVariable("BACKUP360_MYSQL_TEST_PORT");
        Assert.False(string.IsNullOrEmpty(port), "BACKUP360_MYSQL_TEST_PORT ayarlanmadı");
        using var s = new Sandbox();
        var job = DbJob("My", "mysql");
        job.DbServer = "127.0.0.1";
        job.DbPort = int.Parse(port!);
        job.DbUser = "b360";
        job.DbPasswordEnc = Secrets.Protect("Pa\"ss\\w0rd!");
        job.DbAll = true;
        Assert.Contains("b360test", await MySqlEngine.ListAsync(job, default));
        Db.Save(job);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.True(r.Status == "success", r.Detail);
        var dump = await Extract(Directory.GetFiles(s.Cloud1, "b360test_*.7z", SearchOption.AllDirectories).Single(), Path.Combine(s.Root, "acik"));
        var text = File.ReadAllText(dump);
        Assert.Contains("Dump completed", text);
        Assert.Contains("ğüş", text);
    }
}

public class ServiceTests
{
    private static Job Dated(Sandbox s, string root) => new()
    {
        Name = "Gun", Mode = "dated", Sources = new() { root }, Formats = new() { "7z" }, Targets = new() { "gdrive" },
    };

    private static string[] Files(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).OrderBy(x => x).ToArray();

    private static string Root(Sandbox s, params string[] subs)
    {
        var root = Directory.CreateDirectory(Path.Combine(s.Root, "Ana")).FullName;
        foreach (var n in subs)
        {
            Directory.CreateDirectory(Path.Combine(root, n));
            File.WriteAllText(Path.Combine(root, n, "a.txt"), n);
        }
        return root;
    }

    [Fact]
    public async Task Folder_output_is_uncompressed_in_date_folder()
    {
        using var s = new Sandbox();
        var root = Root(s, "Satis");
        File.WriteAllText(Path.Combine(root, "kok.txt"), "kok");
        var job = Dated(s, root);
        job.DatedOutput = "folder";
        job.Formats.Clear();
        Db.Save(job);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.True(r.Status == "success", r.Detail);
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        Assert.Equal(new[] { $"Backup360/Gun/{today}/Satis/a.txt", $"Backup360/Gun/{today}/_KokDosyalar/kok.txt" }.OrderBy(x => x), Files(s.Cloud1));
    }

    [Fact]
    public async Task Multiple_sources_become_subfolders_of_the_day_folder()
    {
        using var s = new Sandbox();
        var a = Directory.CreateDirectory(Path.Combine(s.Root, "Muhasebe", "Faturalar")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(s.Root, "Satis", "Teklifler")).FullName;
        File.WriteAllText(Path.Combine(a, "a.txt"), "a");
        File.WriteAllText(Path.Combine(b, "b.txt"), "b");
        var job = new Job { Name = "Gun", Mode = "dated", DatedOutput = "folder", Sources = new() { Path.Combine(s.Root, "Muhasebe"), Path.Combine(s.Root, "Satis") }, Targets = new() { "gdrive" } };
        Db.Save(job);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.True(r.Status == "success", r.Detail);
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        Assert.Equal(new[] { $"Backup360/Gun/{today}/Muhasebe/Faturalar/a.txt", $"Backup360/Gun/{today}/Satis/Teklifler/b.txt" }, Files(s.Cloud1));

        var baseDir = Path.Combine(s.Cloud1, "J");
        foreach (var p in new[] { "2020-01-01/Muhasebe/Faturalar", "2020-01-02/Muhasebe/Faturalar", "2020-01-01/Satis/Teklifler" })
        {
            Directory.CreateDirectory(Path.Combine(baseDir, p));
            File.WriteAllText(Path.Combine(baseDir, p, "a.txt"), "x");
        }
        Assert.Equal(1, await Cloud.ApplyFolderRetentionAsync("gdrive", "J", 0, 5, null, default, 3));
        Assert.False(Directory.Exists(Path.Combine(baseDir, "2020-01-01", "Muhasebe", "Faturalar")));
        Assert.True(Directory.Exists(Path.Combine(baseDir, "2020-01-01", "Satis", "Teklifler")));
    }

    [Fact]
    public async Task Days_filter_skips_old_folders()
    {
        using var s = new Sandbox();
        var root = Root(s, "Yeni", "Eski");
        File.SetLastWriteTime(Path.Combine(root, "Eski", "a.txt"), DateTime.Now.AddDays(-30));
        var job = Dated(s, root);
        job.DaysFilter = 7;
        Db.Save(job);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        Assert.Contains("/Yeni_", Files(s.Cloud1).Single());
    }

    [Fact]
    public async Task Keep_days_removes_old_but_never_the_newest()
    {
        using var s = new Sandbox();
        var root = Root(s, "Satis");
        var job = Dated(s, root);
        job.Keep = 0;
        job.KeepDays = 10;
        Db.Save(job);
        var folder = Directory.CreateDirectory(Path.Combine(s.Cloud1, "Backup360", "Gun", "2020-01-01")).FullName;
        File.WriteAllText(Path.Combine(folder, "Satis_20200101-000000.7z"), "eski");
        var lone = Directory.CreateDirectory(Path.Combine(s.Cloud1, "Backup360", "Gun", "2020-02-02")).FullName;
        File.WriteAllText(Path.Combine(lone, "Muhasebe_20200202-000000.7z"), "tek kopya");
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        var names = Files(s.Cloud1);
        Assert.DoesNotContain(names, n => n.Contains("Satis_20200101"));
        Assert.Contains(names, n => n.Contains("Muhasebe_20200202"));
        Assert.Equal(2, names.Length);
    }

    [Fact]
    public async Task Folder_retention_purges_only_old_date_item_folders()
    {
        using var s = new Sandbox();
        var baseDir = Path.Combine(s.Cloud1, "J");
        foreach (var p in new[] { "2020-01-01/Satis", "2020-01-02/Satis", "2020-01-01/Tek", "_baska/klasor" })
        {
            Directory.CreateDirectory(Path.Combine(baseDir, p));
            File.WriteAllText(Path.Combine(baseDir, p, "a.txt"), "x");
        }
        Assert.Equal(1, await Cloud.ApplyFolderRetentionAsync("gdrive", "J", 0, 5, null, default));
        Assert.False(Directory.Exists(Path.Combine(baseDir, "2020-01-01", "Satis")));
        Assert.True(Directory.Exists(Path.Combine(baseDir, "2020-01-02", "Satis")));
        Assert.True(Directory.Exists(Path.Combine(baseDir, "2020-01-01", "Tek")));
        Assert.True(Directory.Exists(Path.Combine(baseDir, "_baska", "klasor")));
    }

    [Fact]
    public async Task Connection_test_measures_and_cleans_up()
    {
        using var s = new Sandbox();
        var ok = await Cloud.TestAsync("gdrive", default);
        Assert.True(ok.Ok, ok.Message);
        Assert.True(ok.UploadBytesPerSecond > 0);
        Assert.Equal(ok.UploadBytesPerSecond, Estimator.KnownSpeed("gdrive"));
        Assert.Empty(Files(s.Cloud1));
        Assert.False((await Cloud.TestAsync("bilinmeyen", default)).Ok);
    }

    [Fact]
    public void Estimate_uses_measured_speed()
    {
        using var s = new Sandbox();
        var src = s.Tree("K");
        Estimator.StoreSpeed("gdrive", 1048576);
        var job = new Job { Name = "T", Mode = "mirror", Sources = new() { src }, Targets = new() { "gdrive", "nas" } };
        var text = Estimator.Describe(job);
        Assert.Contains("Kaynak boyutu", text);
        Assert.Contains("gdrive: 1 MB/sn", text);
        Assert.Contains("nas: hız bilinmiyor", text);
    }

    [Fact]
    public async Task Compression_reports_percent()
    {
        using var s = new Sandbox();
        var dir = Directory.CreateDirectory(Path.Combine(s.Root, "b")).FullName;
        File.WriteAllBytes(Path.Combine(dir, "b.bin"), RandomNumberGenerator.GetBytes(60_000_000));
        var seen = new List<int>();
        await Archiver.CreateAsync(Formats.Get("7z"), new ArchiveRequest(new[] { dir }, Array.Empty<string>(), Path.Combine(s.Root, "c"), 9, null), null, default, seen.Add);
        Assert.Contains(seen, p => p > 0);
    }

    internal sealed class FakeSmtp : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        public readonly List<string> Lines = new();
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;

        public FakeSmtp()
        {
            _l.Start();
            _ = Task.Run(Serve);
        }

        private async Task Serve()
        {
            using var c = await _l.AcceptTcpClientAsync();
            using var stream = c.GetStream();
            using var r = new StreamReader(stream, Encoding.UTF8);
            using var w = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            await w.WriteLineAsync("220 test");
            string? line;
            var data = false;
            while ((line = await r.ReadLineAsync()) != null)
            {
                lock (Lines) Lines.Add(line);
                if (data)
                {
                    if (line == ".") { data = false; await w.WriteLineAsync("250 ok"); }
                    continue;
                }
                if (line.StartsWith("DATA")) { data = true; await w.WriteLineAsync("354 go"); }
                else if (line.StartsWith("QUIT")) { await w.WriteLineAsync("221 bye"); return; }
                else await w.WriteLineAsync("250 ok");
            }
        }

        public void Dispose() => _l.Stop();
    }

    [Fact]
    public async Task Failed_job_sends_mail_and_success_does_not()
    {
        using var s = new Sandbox();
        using var smtp = new FakeSmtp();
        Notifier.Save(new SmtpSettings { Enabled = true, Host = "127.0.0.1", Port = smtp.Port, Security = "none", From = "yedek@ornek.com", To = "bt@ornek.com" });

        var good = new Job { Name = "Iyi", Mode = "archive", Sources = new() { s.Tree("K") }, Formats = new() { "7z" }, Targets = new() { "gdrive" } };
        Db.Save(good);
        Assert.Equal("success", (await BackupRunner.RunAsync(good.Id, null, default)).Status);
        Assert.Empty(smtp.Lines);

        var bad = new Job { Name = "Kotu", Mode = "archive", Sources = new() { s.Tree("L") }, Formats = new() { "7z" }, Targets = new() { "yok" } };
        Db.Save(bad);
        Assert.Equal("failed", (await BackupRunner.RunAsync(bad.Id, null, default)).Status);
        lock (smtp.Lines)
        {
            Assert.Contains(smtp.Lines, l => l.StartsWith("RCPT TO:<bt@ornek.com>"));
            Assert.Contains(smtp.Lines, l => l.StartsWith("Subject:") && l.Contains("Kotu"));
        }
    }

    [Fact]
    public async Task Corrupt_backup_sends_distinct_mail()
    {
        using var s = new Sandbox();
        using var smtp = new FakeSmtp();
        Notifier.Save(new SmtpSettings { Enabled = true, Host = "127.0.0.1", Port = smtp.Port, Security = "none", From = "yedek@ornek.com", To = "bt@ornek.com", OnWarning = false });
        var fake = Path.Combine(s.Root, "bozuk.db");
        File.WriteAllText(fake, "bu bir veritabani degil, sadece metin dosyasi");
        var job = new Job { Name = "Kritik", Mode = "database", DbEngine = "sqlite", DbNames = new() { fake }, DbAll = false, Formats = new() { "7z" }, Targets = new() { "gdrive" } };
        Db.Save(job);
        Assert.Equal("corrupt", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        lock (smtp.Lines) Assert.Contains(smtp.Lines, l => l.StartsWith("Subject:") && l.Contains("YEDEK BOZUK") && l.Contains("Kritik"));
    }

    [Fact]
    public async Task Mail_failure_does_not_change_backup_result()
    {
        using var s = new Sandbox();
        Notifier.Save(new SmtpSettings { Enabled = true, Host = "127.0.0.1", Port = 1, Security = "none", To = "a@b.com", From = "c@d.com", OnSuccess = true });
        var job = new Job { Name = "J", Mode = "archive", Sources = new() { s.Tree("K") }, Formats = new() { "7z" }, Targets = new() { "gdrive" } };
        Db.Save(job);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
    }
}
