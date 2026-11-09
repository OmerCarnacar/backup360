using System.Diagnostics;
using System.Text.Json;

namespace Backup360.Core;

public sealed record RemoteEntry(string Name, string Path, long Size, bool IsDir, DateTime? Modified);

public static partial class CloudExplorer
{
    private static string Exe => Tools.Require(Tools.Rclone(), "rclone");

    private static List<string> Args(params string[] args) => new(args) { "--config", AppPaths.RcloneConfig };

    public static string Remote(string target, string path) => $"{target}:{path.Replace('\\', '/').Trim('/')}";

    /// <summary>Hedefteki bir klasörün içeriğini listeler: önce klasörler, sonra dosyalar.</summary>
    public static async Task<List<RemoteEntry>> ListAsync(string target, string path, CancellationToken ct)
    {
        var (code, output, error) = await Proc.CaptureAsync(Exe, Args("lsjson", Remote(target, path), "--no-mimetype", "--retries", "1", "--low-level-retries", "2"), ct);
        if (code != 0) throw new IOException(Cloud.Explain(error));
        using var doc = JsonDocument.Parse(output);
        var prefix = path.Replace('\\', '/').Trim('/');
        var list = new List<RemoteEntry>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var name = e.GetProperty("Name").GetString()!;
            var size = e.GetProperty("Size").GetInt64();
            var isDir = e.GetProperty("IsDir").GetBoolean();
            DateTime? modified = e.TryGetProperty("ModTime", out var m) && m.TryGetDateTime(out var d) ? d.ToLocalTime() : null;
            list.Add(new RemoteEntry(name, prefix.Length == 0 ? name : $"{prefix}/{name}", isDir ? 0 : size, isDir, modified));
        }
        return list.OrderByDescending(x => x.IsDir).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Dosya veya klasörü yerel klasöre indirir.</summary>
    public static async Task DownloadAsync(string target, RemoteEntry entry, string localFolder, Action<string>? log, Action<TransferStats>? stats, CancellationToken ct)
    {
        var destination = Path.Combine(localFolder, entry.Name);
        var args = Args(entry.IsDir ? "copy" : "copyto", Remote(target, entry.Path), destination);
        await Cloud.RunTransferAsync(args, log, stats, ct);
    }
}

/// <summary>Bulut hedefini Windows'ta sürücü harfi olarak bağlar. WinFsp gerektirir.</summary>
public static class DriveMounter
{
    public const string WinFspUrl = "https://winfsp.dev/rel/";
    private static readonly Dictionary<string, Process> Mounts = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsWinFspInstalled()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return File.Exists(Path.Combine(pf86, "WinFsp", "bin", "winfsp-x64.dll"));
    }

    public static List<string> FreeLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        return Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Where(c => !used.Contains(c)).Reverse().Select(c => c + ":").ToList();
    }

    public static IReadOnlyDictionary<string, string> Active
    {
        get
        {
            lock (Mounts)
            {
                foreach (var dead in Mounts.Where(m => m.Value.HasExited).Select(m => m.Key).ToList()) Mounts.Remove(dead);
                return Mounts.ToDictionary(m => m.Key, m => m.Value.StartInfo.ArgumentList[1]);
            }
        }
    }

    public static async Task MountAsync(string target, string letter, CancellationToken ct)
    {
        if (!IsWinFspInstalled()) throw new InvalidOperationException("Disk olarak bağlamak için WinFsp kurulu olmalı.");
        var psi = new ProcessStartInfo(Tools.Require(Tools.Rclone(), "rclone")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "mount", target + ":", letter, "--vfs-cache-mode", "writes", "--volname", target, "--config", AppPaths.RcloneConfig }) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("rclone başlatılamadı.");
        var errors = p.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500, ct);
            if (p.HasExited) throw new IOException("Disk bağlanamadı: " + Cloud.Explain(await errors));
            if (Directory.Exists(letter + "\\"))
            {
                lock (Mounts) Mounts[letter] = p;
                return;
            }
        }
        Proc.TryKill(p);
        throw new IOException("Disk 20 saniye içinde bağlanamadı.");
    }

    public static void Unmount(string letter)
    {
        lock (Mounts)
        {
            if (!Mounts.Remove(letter, out var p)) return;
            Proc.TryKill(p);
        }
    }

    public static void UnmountAll()
    {
        lock (Mounts)
        {
            foreach (var p in Mounts.Values) Proc.TryKill(p);
            Mounts.Clear();
        }
    }
}
