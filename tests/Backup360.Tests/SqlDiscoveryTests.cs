using System.Net;
using System.Net.Sockets;
using Backup360.Core;
using Xunit;

namespace Backup360.Tests;

public class SqlDiscoveryTests
{
    [Fact]
    public void Browser_response_is_parsed()
    {
        var list = SqlDiscovery.ParseBrowserResponse("ServerName;SRV-01;InstanceName;MSSQLSERVER;IsClustered;No;Version;16.0.1000.6;tcp;1433;;ServerName;SRV-01;InstanceName;TIGER;IsClustered;No;Version;15.0.2000.5;tcp;50123;;", "SQL Browser");
        Assert.Equal(2, list.Count);
        Assert.Equal(("SRV-01", null, 1433), (list[0].Host, list[0].Instance, list[0].Port));
        Assert.Equal(@"SRV-01\TIGER", list[1].Server);
        Assert.Equal(50123, list[1].Port);
        Assert.Equal("15.0.2000.5", list[1].Version);
    }

    [Fact]
    public void Same_instance_from_several_sources_is_merged()
    {
        var merged = SqlDiscovery.Merge(new[]
        {
            new SqlInstance("SRV-01", null, 1433, "", "Ağ taraması"),
            new SqlInstance("srv-01", null, 1433, "16.0", "Bu bilgisayar"),
            new SqlInstance("SRV", "TIGER", 0, "15.0", "SQL Browser"),
            new SqlInstance("SRV", "TIGER", 50123, "", "Ağ taraması"),
        });
        Assert.Equal(2, merged.Count);
        Assert.Equal("Bu bilgisayar", merged[0].Source);
        Assert.Equal(50123, merged[1].Port);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.1.5", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    public void Only_private_networks_are_scanned(string ip, bool expected) =>
        Assert.Equal(expected, SqlDiscovery.IsPrivate(IPAddress.Parse(ip)));

    [Fact]
    public async Task Port_probe_detects_open_and_closed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(await SqlDiscovery.IsPortOpenAsync(IPAddress.Loopback, port, default));
        listener.Stop();
        Assert.False(await SqlDiscovery.IsPortOpenAsync(IPAddress.Loopback, port, default));
    }

    [Fact]
    public void Local_instances_are_read_from_this_machine()
    {
        var local = SqlDiscovery.LocalInstances();
        Assert.All(local, i => Assert.Equal(Environment.MachineName, i.Host));
    }
}
