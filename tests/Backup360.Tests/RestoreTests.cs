using Backup360.Core;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Backup360.Tests;

public class RestoreTests
{
    private const string LocalDb = @"(localdb)\MSSQLLocalDB";
    private static string Cs(string db) => $"Data Source={LocalDb};Initial Catalog={db};Integrated Security=true;TrustServerCertificate=true;Pooling=false";

    private static async Task Sql(string text, string db = "master")
    {
        await using var c = new SqlConnection(Cs(db));
        await c.OpenAsync();
        await using var cmd = new SqlCommand(text, c) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> Count(string db)
    {
        await using var c = new SqlConnection(Cs(db));
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM t", c);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task Drop(string name) =>
        await Sql($"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}] END");

    private static void Log(string _) { }

    public static IEnumerable<object[]> AllFormats() => Formats.All.Select(f => new object[] { f.Key });

    [Theory, MemberData(nameof(AllFormats))]
    public async Task Files_restore_from_every_format_into_new_folder(string key)
    {
        using var s = new Sandbox();
        var src = s.Tree("Kaynak");
        var job = new Job { Name = "Geri", Mode = "archive", Sources = new() { src }, Formats = new() { key }, Targets = new() { "gdrive" } };
        Db.Save(job);
        var r = await BackupRunner.RunAsync(job.Id, null, default);
        Assert.True(r.Status == "success", r.Detail);

        var point = (await RestoreCatalog.ListAsync(job, default)).Single();
        var root = Directory.CreateDirectory(Path.Combine(s.Root, "don")).FullName;
        File.WriteAllText(Path.Combine(root, "mevcut.txt"), "dokunma");
        var dest = await RestoreEngine.RestoreFilesAsync(point, root, Log, null, default);
        Assert.Equal(Sandbox.Hashes(src), Sandbox.Hashes(Path.Combine(dest, "Kaynak")));
        Assert.Equal("dokunma", File.ReadAllText(Path.Combine(root, "mevcut.txt")));
    }

    [Fact]
    public async Task Encrypted_archive_restores_and_damaged_one_is_refused()
    {
        using var s = new Sandbox();
        var src = s.Tree("Sir");
        var job = new Job { Name = "Par", Mode = "archive", Sources = new() { src }, Formats = new() { "7z" }, Targets = new() { "gdrive" }, PasswordEnc = Secrets.Protect("Gizli 1!") };
        Db.Save(job);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        var point = (await RestoreCatalog.ListAsync(job, default)).Single();
        var root = Directory.CreateDirectory(Path.Combine(s.Root, "don")).FullName;
        var dest = await RestoreEngine.RestoreFilesAsync(point, root, Log, null, default);
        Assert.Equal(Sandbox.Hashes(src), Sandbox.Hashes(Path.Combine(dest, "Sir")));

        var file = Directory.GetFiles(s.Cloud1, "*.7z", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(file);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(file, bytes);
        var bad = (await RestoreCatalog.ListAsync(job, default)).Single();
        var before = Directory.GetDirectories(root).Length;
        await Assert.ThrowsAsync<BackupCorruptException>(() => RestoreEngine.RestoreFilesAsync(bad with { Md5 = "" }, root, Log, null, default));
        Assert.Equal(before, Directory.GetDirectories(root).Length);
    }

    [Fact]
    public async Task Folder_based_mirror_and_each_subfolder_restore()
    {
        using var s = new Sandbox();
        var src = s.Tree("Belge");
        var job = new Job { Name = "Ayna", Mode = "mirror", Sources = new() { src }, Targets = new() { "gdrive" } };
        Db.Save(job);
        Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
        var point = (await RestoreCatalog.ListAsync(job, default)).Single();
        var root = Directory.CreateDirectory(Path.Combine(s.Root, "don")).FullName;
        var dest = await RestoreEngine.RestoreFilesAsync(point, root, Log, null, default);
        Assert.Equal(Sandbox.Hashes(src), Sandbox.Hashes(dest));

        var parent = Directory.CreateDirectory(Path.Combine(s.Root, "Ana", "P1")).FullName;
        File.WriteAllText(Path.Combine(parent, "a.txt"), "p1");
        var each = new Job { Name = "Her", Mode = "dated", UseDateFolder = false, DatedOutput = "archive", Sources = new() { Path.Combine(s.Root, "Ana") }, Formats = new() { "zip" }, Targets = new() { "gdrive" } };
        Db.Save(each);
        Assert.Equal("success", (await BackupRunner.RunAsync(each.Id, null, default)).Status);
        var p = (await RestoreCatalog.ListAsync(each, default)).Single();
        Assert.Equal("P1", p.Item);
        var d2 = await RestoreEngine.RestoreFilesAsync(p, root, Log, null, default);
        Assert.Equal("p1", File.ReadAllText(Path.Combine(d2, "P1", "a.txt")));
    }

    [Fact]
    public void Chain_builds_full_diff_and_logs_in_order()
    {
        var job = new Job { Mode = "database", DbEngine = "mssql" };
        RestorePoint P(string item, string stamp) => new(job, "g", "f", item + stamp, true, item, stamp, "7z", 1, "");
        var all = new[]
        {
            P("E_FULL", "20260101-010000"), P("E_FULL", "20260103-010000"), P("E_LOG", "20260103-020000"), P("E_DIFF", "20260103-030000"),
            P("E_LOG", "20260103-040000"), P("E_LOG", "20260103-050000"), P("E_LOG", "20260103-060000"),
        };
        var chain = RestoreCatalog.Chain(all[5], all);
        Assert.Equal(new[] { "20260103-010000", "20260103-030000", "20260103-040000", "20260103-050000" }, chain.Select(c => c.Stamp));
        Assert.Equal(new[] { "20260103-010000", "20260103-030000" }, RestoreCatalog.Chain(all[3], all).Select(c => c.Stamp));
        Assert.Throws<InvalidOperationException>(() => RestoreCatalog.Chain(P("X_LOG", "20260101-000000"), new[] { P("X_LOG", "20260101-000000") }));
    }

    [Fact]
    public async Task Sql_server_chain_restores_to_new_name_and_refuses_existing()
    {
        using var s = new Sandbox();
        var name = "b360_" + Guid.NewGuid().ToString("N")[..8];
        var target = name + "_geri";
        var bak = Directory.CreateDirectory(Path.Combine(s.Root, "bak")).FullName;
        var files = Directory.CreateDirectory(Path.Combine(s.Root, "mdf")).FullName;
        await Sql($"CREATE DATABASE [{name}] ON (NAME=d, FILENAME='{files}\\{name}.mdf') LOG ON (NAME=l, FILENAME='{files}\\{name}.ldf'); ALTER DATABASE [{name}] SET RECOVERY FULL");
        try
        {
            await Sql("CREATE TABLE t(id int); INSERT t VALUES (1),(2)", name);
            var job = new Job { Name = "Zincir", Mode = "database", DbEngine = "mssql", DbServer = LocalDb, DbNames = new() { name }, DbAll = false, DbBackupDir = bak, DbCopyOnly = false, Formats = new() { "7z" }, Targets = new() { "gdrive" } };
            Db.Save(job);
            Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
            await Sql("INSERT t VALUES (3)", name);
            foreach (var type in new[] { "diff", "log" })
            {
                await Task.Delay(1100);
                job.DbType = type;
                Db.Save(job);
                Assert.Equal("success", (await BackupRunner.RunAsync(job.Id, null, default)).Status);
                await Sql($"INSERT t VALUES ({(type == "diff" ? 4 : 5)})", name);
            }

            var points = await RestoreCatalog.ListAsync(job, default);
            var log = points.Single(p => p.Item.EndsWith("_LOG"));
            await RestoreEngine.RestoreDatabaseAsync(log, points, target, Log, null, default);
            Assert.Equal(4, await Count(target));
            Assert.Equal(5, await Count(name));
            Assert.Empty(Directory.GetFileSystemEntries(bak));

            await Assert.ThrowsAsync<ArgumentException>(() => RestoreEngine.RestoreDatabaseAsync(log, points, name, Log, null, default));
            await Assert.ThrowsAsync<ArgumentException>(() => RestoreEngine.RestoreDatabaseAsync(log, points, "kotu;ad", Log, null, default));
        }
        finally
        {
            await Drop(target);
            await Drop(name);
        }
    }

    private static string Env(string n)
    {
        var v = Environment.GetEnvironmentVariable(n);
        Assert.False(string.IsNullOrEmpty(v), n + " ayarlanmadı");
        return v!;
    }

    [Fact]
    public async Task Mysql_postgres_firebird_restore_to_new_databases()
    {
        Env("BACKUP360_MYSQL_BIN");
        Env("BACKUP360_PG_BIN");
        Env("BACKUP360_FB_BIN");
        using var s = new Sandbox();

        var my = new Job { Name = "My", Mode = "database", DbEngine = "mysql", DbServer = "127.0.0.1", DbPort = int.Parse(Env("BACKUP360_MYSQL_TEST_PORT")), DbUser = "b360", DbPasswordEnc = Secrets.Protect("Pa\"ss\\w0rd!"), DbAll = true, Formats = new() { "7z" }, Targets = new() { "gdrive" } };
        Db.Save(my);
        Assert.Equal("success", (await BackupRunner.RunAsync(my.Id, null, default)).Status);
        var myPoints = await RestoreCatalog.ListAsync(my, default);
        await RestoreEngine.RestoreDatabaseAsync(myPoints.Single(p => p.Item == "b360test"), myPoints, "b360_geri", Log, null, default);
        Assert.Contains("b360_geri", await MySqlEngine.ListAsync(my, default));
        await Assert.ThrowsAsync<ArgumentException>(() => RestoreEngine.RestoreDatabaseAsync(myPoints.Single(p => p.Item == "b360test"), myPoints, "b360test", Log, null, default));

        var pg = new Job { Name = "Pg", Mode = "database", DbEngine = "postgres", DbServer = "127.0.0.1", DbPort = int.Parse(Env("BACKUP360_PG_TEST_PORT")), DbUser = "postgres", DbPasswordEnc = Secrets.Protect("PgGizli1!"), DbAll = false, DbNames = new() { "b360test" }, Formats = new() { "7z" }, Targets = new() { "gdrive" } };
        Db.Save(pg);
        Assert.Equal("success", (await BackupRunner.RunAsync(pg.Id, null, default)).Status);
        var pgPoints = await RestoreCatalog.ListAsync(pg, default);
        var pgName = "b360_geri_" + Guid.NewGuid().ToString("N")[..6];
        await RestoreEngine.RestoreDatabaseAsync(pgPoints.Single(), pgPoints, pgName, Log, null, default);
        Assert.Contains(pgName, await PostgresEngine.ListAsync(pg, default));
        await Assert.ThrowsAsync<ArgumentException>(() => RestoreEngine.RestoreDatabaseAsync(pgPoints.Single(), pgPoints, pgName, Log, null, default));

        var fb = new Job { Name = "Fb", Mode = "database", DbEngine = "firebird", DbServer = "localhost", DbPort = int.Parse(Env("BACKUP360_FB_TEST_PORT")), DbUser = "SYSDBA", DbPasswordEnc = Secrets.Protect("FbGizli1"), DbNames = new() { Env("BACKUP360_FB_TEST_DB") }, DbAll = false, Formats = new() { "7z" }, Targets = new() { "gdrive" } };
        Db.Save(fb);
        Assert.Equal("success", (await BackupRunner.RunAsync(fb.Id, null, default)).Status);
        var fbPoints = await RestoreCatalog.ListAsync(fb, default);
        var fbFile = Path.Combine(s.Root, "geri.fdb");
        await RestoreEngine.RestoreDatabaseAsync(fbPoints.Single(), fbPoints, fbFile, Log, null, default);
        Assert.True(File.Exists(fbFile));
        await Assert.ThrowsAsync<ArgumentException>(() => RestoreEngine.RestoreDatabaseAsync(fbPoints.Single(), fbPoints, fbFile, Log, null, default));
    }
}
