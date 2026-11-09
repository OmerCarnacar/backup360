using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

namespace Backup360.Core;

public sealed record SqlInstance(string Host, string? Instance, int Port, string Version, string Source)
{
    public string Server => Instance == null ? Host : $@"{Host}\{Instance}";
    public string InstanceLabel => Instance ?? "(varsayılan)";
    public string PortText => Port > 0 ? Port.ToString() : "dinamik";
}

/// <summary>Bu bilgisayardaki ve yerel ağdaki SQL Server örneklerini bulur.</summary>
public static class SqlDiscovery
{
    private const int SqlPort = 1433;
    private const int BrowserPort = 1434;

    /// <summary>SQL Browser yanıtını çözer: ServerName;X;InstanceName;Y;...;Version;16.0;tcp;1433;;</summary>
    public static List<SqlInstance> ParseBrowserResponse(string text, string source)
    {
        var list = new List<SqlInstance>();
        foreach (var block in text.Split(";;", StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = block.Split(';');
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < parts.Length; i += 2) map[parts[i]] = parts[i + 1];
            if (!map.TryGetValue("ServerName", out var host) || host.Length == 0) continue;
            map.TryGetValue("InstanceName", out var name);
            map.TryGetValue("Version", out var version);
            var port = map.TryGetValue("tcp", out var tcp) && int.TryParse(tcp, out var p) ? p : 0;
            list.Add(new SqlInstance(host, string.Equals(name, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? null : name, port, version ?? "", source));
        }
        return list;
    }

    /// <summary>Kurulu örnekleri kayıt defterinden okur.</summary>
    public static List<SqlInstance> LocalInstances()
    {
        var list = new List<SqlInstance>();
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var names = hive.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
        if (names == null) return list;
        foreach (var instance in names.GetValueNames())
        {
            var id = names.GetValue(instance) as string;
            var root = $@"SOFTWARE\Microsoft\Microsoft SQL Server\{id}";
            using var tcp = hive.OpenSubKey($@"{root}\MSSQLServer\SuperSocketNetLib\Tcp\IPAll");
            var text = (tcp?.GetValue("TcpPort") as string ?? "").Split(',')[0].Trim();
            if (text.Length == 0) text = (tcp?.GetValue("TcpDynamicPorts") as string ?? "").Trim();
            var port = int.TryParse(text, out var p) ? p : instance == "MSSQLSERVER" ? SqlPort : 0;
            using var setup = hive.OpenSubKey($@"{root}\Setup");
            var version = setup?.GetValue("Version") as string ?? "";
            list.Add(new SqlInstance(Environment.MachineName, instance == "MSSQLSERVER" ? null : instance, port, version, "Bu bilgisayar"));
        }
        return list;
    }

    private static async Task<List<SqlInstance>> BrowserAsync(CancellationToken ct)
    {
        var list = new List<SqlInstance>();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        try
        {
            await udp.SendAsync(new byte[] { 0x02 }, 1, new IPEndPoint(IPAddress.Broadcast, BrowserPort));
            await udp.SendAsync(new byte[] { 0x03 }, 1, new IPEndPoint(IPAddress.Loopback, BrowserPort));
        }
        catch (SocketException) { return list; }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(window.Token);
                if (result.Buffer.Length > 3 && result.Buffer[0] == 0x05)
                    list.AddRange(ParseBrowserResponse(Encoding.ASCII.GetString(result.Buffer, 3, result.Buffer.Length - 3), "SQL Browser"));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (SocketException) { }
        return list;
    }

    private static IEnumerable<IPAddress> Subnet()
    {
        var seen = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            foreach (var u in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a.Address)))
            {
                var b = u.Address.GetAddressBytes();
                if (!seen.Add($"{b[0]}.{b[1]}.{b[2]}")) continue;
                for (var host = 1; host < 255; host++) yield return new IPAddress(new[] { b[0], b[1], b[2], (byte)host });
            }
    }

    /// <summary>Yalnızca özel ağ adresleri taranır; internete tarama yapılmaz.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public static async Task<bool> IsPortOpenAsync(IPAddress ip, int port, CancellationToken ct)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
        try
        {
            await client.ConnectAsync(ip, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    private static async Task<string> NameOfAsync(IPAddress ip)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(lookup, Task.Delay(800)) == lookup) return lookup.Result.HostName.Split('.')[0].ToUpperInvariant();
        }
        catch (SocketException) { }
        return ip.ToString();
    }

    private static async Task<List<SqlInstance>> SweepAsync(Action<int>? progress, CancellationToken ct)
    {
        var addresses = Subnet().ToList();
        var found = new List<SqlInstance>();
        var done = 0;
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct }, async (ip, token) =>
        {
            if (await IsPortOpenAsync(ip, SqlPort, token))
            {
                var instance = new SqlInstance(await NameOfAsync(ip), null, SqlPort, "", "Ağ taraması");
                lock (found) found.Add(instance);
            }
            progress?.Invoke(Interlocked.Increment(ref done) * 100 / Math.Max(1, addresses.Count));
        });
        return found;
    }

    /// <summary>Aynı örneği tek satıra indirir; ayrıntısı en fazla olan kayıt kalır.</summary>
    public static List<SqlInstance> Merge(IEnumerable<SqlInstance> all) =>
        all.GroupBy(i => $"{i.Host.Split('.')[0]}|{i.Instance}".ToUpperInvariant())
            .Select(g => g.OrderByDescending(i => i.Source == "Bu bilgisayar").ThenByDescending(i => i.Version.Length).First() is var best && best.Port == 0
                ? best with { Port = g.Max(i => i.Port) } : g.OrderByDescending(i => i.Source == "Bu bilgisayar").ThenByDescending(i => i.Version.Length).First())
            .OrderBy(i => i.Source == "Bu bilgisayar" ? 0 : 1).ThenBy(i => i.Server, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static async Task<List<SqlInstance>> ScanAsync(Action<int>? progress, CancellationToken ct)
    {
        var local = LocalInstances();
        var browser = BrowserAsync(ct);
        var sweep = SweepAsync(progress, ct);
        await Task.WhenAll(browser, sweep);
        return Merge(local.Concat(await browser).Concat(await sweep));
    }
}
