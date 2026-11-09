using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Backup360.Core;

public enum Engine { SevenZip, TarThen7z, TarZstd, Rar }

public sealed record FormatDef(string Key, string Label, Engine Engine, string SevenZipType, bool Password, bool Level)
{
    public string Extension => Key;
}

public static class Formats
{
    public static readonly IReadOnlyList<FormatDef> All = new FormatDef[]
    {
        new("7z",      "7z (en iyi oran, parola destekli)", Engine.SevenZip,  "7z",    true,  true),
        new("zip",     "zip (her yerde açılır, parola destekli)", Engine.SevenZip, "zip", true, true),
        new("rar",     "rar (WinRAR gerekir, parola destekli)", Engine.Rar,   "",      true,  true),
        new("tar",     "tar (sıkıştırmasız paket)",         Engine.SevenZip,  "tar",   false, false),
        new("tar.gz",  "tar.gz (gzip)",                     Engine.TarThen7z, "gzip",  false, true),
        new("tar.bz2", "tar.bz2 (bzip2)",                   Engine.TarThen7z, "bzip2", false, true),
        new("tar.xz",  "tar.xz (xz)",                       Engine.TarThen7z, "xz",    false, true),
        new("tar.zst", "tar.zst (zstandard, çok hızlı)",    Engine.TarZstd,   "",      false, true),
        new("wim",     "wim (Windows imajı)",               Engine.SevenZip,  "wim",   false, false),
    };

    public static FormatDef Get(string key) =>
        All.FirstOrDefault(f => f.Key == key) ?? throw new ArgumentException($"Bilinmeyen format: {key}");

    public static bool IsAvailable(FormatDef f) => f.Engine != Engine.Rar || Tools.Rar() != null;
}

public sealed record ArchiveRequest(IReadOnlyList<string> Sources, IReadOnlyList<string> Excludes, string OutputBase, int Level, string? Password);

public sealed record ArchiveResult(string Path, bool HadWarnings);

public static class Archiver
{
    private static readonly string[] Common = { "-y", "-ssw", "-scsUTF-8", "-sccUTF-8" };

    private static string SevenZ => Tools.Require(Tools.SevenZip(), "7-Zip");

    /// <summary>Kök sürücü verilirse içeriği alınır; klasörlerde sondaki ayraç atılır ki klasör adı arşivde kalsın.</summary>
    private static string Normalize(string source)
    {
        var full = Path.GetFullPath(source);
        return Path.GetPathRoot(full) == full ? full + "*" : Path.TrimEndingDirectorySeparator(full);
    }

    private static string WriteList(ArchiveRequest req, Encoding encoding)
    {
        var path = req.OutputBase + ".list";
        File.WriteAllLines(path, req.Sources.Select(Normalize), encoding);
        return path;
    }

    public static async Task<ArchiveResult> CreateAsync(FormatDef f, ArchiveRequest req, Action<string>? log, CancellationToken ct, Action<int>? percent = null)
    {
        if (req.Password != null && !f.Password)
            throw new InvalidOperationException($"{f.Key} formatı parolayı desteklemiyor.");

        var output = $"{req.OutputBase}.{f.Extension}";
        var list = WriteList(req, new UTF8Encoding(false));
        try
        {
            var warned = f.Engine switch
            {
                Engine.SevenZip => await SevenZipAsync(f, req, list, output, log, ct, percent),
                Engine.TarThen7z => await TarPipeAsync(req, list, s => CompressWith7zAsync(f, req, output, s, ct), ct, percent),
                Engine.TarZstd => await TarPipeAsync(req, list, s => CompressZstdAsync(req, output, s, ct), ct, percent),
                Engine.Rar => await RarAsync(req, list, output, log, ct),
                _ => throw new NotSupportedException(),
            };
            return new ArchiveResult(output, warned);
        }
        catch
        {
            TryDelete(output);
            throw;
        }
        finally
        {
            TryDelete(list);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static IEnumerable<string> ExcludeArgs(ArchiveRequest req) => req.Excludes.Select(x => "-xr!" + x);

    /// <summary>7-Zip çıkış kodları: 0 başarılı, 1 uyarı (kilitli/eksik dosya), 2+ hata.</summary>
    private static bool Check(int code, string tool, string output)
    {
        if (code >= 2) throw new IOException($"{tool} hata verdi (kod {code}): {LastLines(output)}");
        return code == 1;
    }

    private static string LastLines(string text) =>
        string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(4));

    private static async Task<bool> SevenZipAsync(FormatDef f, ArchiveRequest req, string list, string output, Action<string>? log, CancellationToken ct, Action<int>? percent)
    {
        var args = new List<string> { "a", "-t" + f.SevenZipType, "-bsp1" };
        args.AddRange(Common);
        if (f.Level) args.Add("-mx=" + req.Level);
        if (req.Password != null)
        {
            args.Add("-p" + req.Password);
            if (f.Key == "7z") args.Add("-mhe=on");
            if (f.Key == "zip") args.Add("-mem=AES256");
        }
        args.AddRange(ExcludeArgs(req));
        args.Add(output);
        args.Add("@" + list);
        var (code, text) = await Proc.RunAsync(SevenZ, args, null, ct, percent);
        ReportWarnings(code, text, log);
        return Check(code, "7-Zip", text);
    }

    private static void ReportWarnings(int code, string text, Action<string>? log)
    {
        if (code != 1 || log == null) return;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(l => l.Contains("WARNING", StringComparison.OrdinalIgnoreCase) || l.Contains("cannot", StringComparison.OrdinalIgnoreCase)).Take(20))
            log("  uyarı: " + line);
    }

    /// <summary>Tar akışını geçici dosya yazmadan doğrudan sıkıştırıcıya aktarır.</summary>
    private static async Task<bool> TarPipeAsync(ArchiveRequest req, string list, Func<Stream, Task> consume, CancellationToken ct, Action<int>? percent)
    {
        var args = new List<string> { "a", "-ttar", "-so", "-bsp2" };
        args.AddRange(Common);
        args.AddRange(ExcludeArgs(req));
        args.Add("dummy.tar");
        args.Add("@" + list);

        using var tar = Proc.Start(SevenZ, args);
        using var reg = ct.Register(() => Proc.TryKill(tar));
        var messages = new StringBuilder();
        var stderr = Proc.PumpAsync(tar.StandardError, messages, null, percent);
        try
        {
            await consume(tar.StandardOutput.BaseStream);
        }
        catch
        {
            Proc.TryKill(tar);
            throw;
        }
        await tar.WaitForExitAsync(CancellationToken.None);
        await stderr;
        ct.ThrowIfCancellationRequested();
        return Check(tar.ExitCode, "7-Zip (tar)", messages.ToString());
    }

    private static async Task CompressWith7zAsync(FormatDef f, ArchiveRequest req, string output, Stream source, CancellationToken ct)
    {
        var args = new List<string> { "a", "-t" + f.SevenZipType, "-bd", "-si" + Path.GetFileNameWithoutExtension(output) };
        args.AddRange(Common);
        args.Add("-mx=" + req.Level);
        args.Add(output);

        using var p = Proc.Start(SevenZ, args, pipeIn: true);
        using var reg = ct.Register(() => Proc.TryKill(p));
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await source.CopyToAsync(p.StandardInput.BaseStream, 1 << 20, ct);
            p.StandardInput.Close();
        }
        catch (IOException)
        {
            // Sıkıştırıcı erken kapandıysa asıl neden aşağıdaki çıkış kodundadır.
        }
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0) throw new IOException($"7-Zip ({f.Key}) hata verdi (kod {p.ExitCode}): {LastLines(await stdout + await stderr)}");
    }

    private static async Task CompressZstdAsync(ArchiveRequest req, string output, Stream source, CancellationToken ct)
    {
        await using var file = File.Create(output);
        // En yüksek seviyede zstd'nin "ultra" düzeyi (22) kullanılır.
        await using var zstd = new CompressionStream(file, req.Level >= 9 ? 22 : req.Level * 2 + 1);
        zstd.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        await source.CopyToAsync(zstd, 1 << 20, ct);
    }

    private static async Task<bool> RarAsync(ArchiveRequest req, string list, string output, Action<string>? log, CancellationToken ct)
    {
        var args = new List<string> { "a", "-r", "-ep1", "-idq", "-y", "-dh", "-scfl", "-m" + Math.Clamp((req.Level + 1) / 2, 1, 5) };
        if (req.Password != null) args.Add("-hp" + req.Password);
        args.AddRange(req.Excludes.Select(x => "-x" + x));
        args.Add(output);
        args.Add("@" + list);
        var (code, text) = await Proc.RunAsync(Tools.Require(Tools.Rar(), "WinRAR"), args, null, ct);
        if (code == 1) log?.Invoke("  uyarı: WinRAR bazı dosyaları alamadı: " + LastLines(text));
        if (code >= 2) throw new IOException($"WinRAR hata verdi (kod {code}): {LastLines(text)}");
        return code == 1;
    }

    /// <summary>Arşivi seçilen klasöre açar.</summary>
    public static async Task ExtractAsync(FormatDef f, string path, string destination, string? password, CancellationToken ct, Action<int>? percent = null)
    {
        Directory.CreateDirectory(destination);
        if (f.Engine == Engine.TarZstd)
        {
            await Task.Run(() =>
            {
                using var file = File.OpenRead(path);
                using var zstd = new DecompressionStream(file);
                TarFile.ExtractToDirectory(zstd, destination, overwriteFiles: false);
            }, ct);
            return;
        }
        if (f.Engine == Engine.Rar)
        {
            var (rarCode, rarText) = await Proc.RunAsync(Tools.Require(Tools.Rar(), "WinRAR"), new[] { "x", "-idq", "-y", password != null ? "-p" + password : "-p-", path, destination + "\\" }, null, ct);
            if (rarCode != 0) throw new IOException($"Arşiv açılamadı (kod {rarCode}): {LastLines(rarText)}");
            return;
        }

        async Task Open(string archive)
        {
            var args = new List<string> { "x", "-y", "-bsp1", "-sccUTF-8", "-o" + destination };
            if (password != null && archive == path) args.Add("-p" + password);
            args.Add(archive);
            var (code, text) = await Proc.RunAsync(SevenZ, args, null, ct, percent);
            if (code != 0) throw new IOException($"Arşiv açılamadı (kod {code}): {LastLines(text)}");
        }

        await Open(path);
        if (f.Engine != Engine.TarThen7z) return;
        foreach (var inner in Directory.GetFiles(destination, "*.tar"))
        {
            await Open(inner);
            File.Delete(inner);
        }
    }

    /// <summary>Arşivin baştan sona okunabildiğini doğrular; bozuk arşiv buluta gönderilmez.</summary>
    public static async Task VerifyAsync(FormatDef f, string path, string? password, CancellationToken ct, Action<int>? percent = null)
    {
        switch (f.Engine)
        {
            case Engine.TarZstd:
                await Task.Run(() => VerifyZstd(path, ct), ct);
                return;
            case Engine.Rar:
            {
                var args = new List<string> { "t", "-idq" };
                args.Add(password != null ? "-p" + password : "-p-");
                args.Add(path);
                var (code, text) = await Proc.RunAsync(Tools.Require(Tools.Rar(), "WinRAR"), args, null, ct);
                if (code != 0) throw new IOException($"Arşiv doğrulanamadı (kod {code}): {LastLines(text)}");
                return;
            }
            default:
            {
                var args = new List<string> { "t", "-bsp1", "-sccUTF-8" };
                if (password != null) args.Add("-p" + password);
                args.Add(path);
                var (code, text) = await Proc.RunAsync(SevenZ, args, null, ct, percent);
                if (code != 0) throw new IOException($"Arşiv doğrulanamadı (kod {code}): {LastLines(text)}");
                return;
            }
        }
    }

    private static void VerifyZstd(string path, CancellationToken ct)
    {
        using var file = File.OpenRead(path);
        using var zstd = new DecompressionStream(file);
        using var tar = new TarReader(zstd);
        while (tar.GetNextEntry(copyData: false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            entry.DataStream?.CopyTo(Stream.Null);
        }
        // Sağlama toplamı akışın en sonunda: okunmazsa bozulma fark edilmez.
        zstd.CopyTo(Stream.Null);
    }
}
