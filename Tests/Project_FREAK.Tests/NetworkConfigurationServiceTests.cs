using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Project_FREAK.Controllers;
using Xunit;

namespace Project_FREAK.Tests;

public class NetworkConfigurationServiceTests
{
    private readonly NetworkConfigurationService _service = new();

    [Fact]
    public void EscapeAdapterName_RemovesQuotesAndTrims()
    {
        var result = NetworkConfigurationService.EscapeAdapterName("  \"Ethernet 2\"  ");
        Assert.Equal("Ethernet 2", result);
    }

    [Theory]
    [InlineData("Ethernet & whoami")]
    [InlineData("Ethernet|whoami")]
    [InlineData("Ethernet<file")]
    [InlineData("Ethernet>file")]
    [InlineData("Ethernet%PATH%")]
    [InlineData("Ethernet^whoami")]
    [InlineData("Ethernet!whoami")]
    [InlineData("Ethernet\r\nwhoami")]
    public void EscapeAdapterName_RejectsCommandMetacharacters(string adapterName)
    {
        Assert.Throws<System.ArgumentException>(() => NetworkConfigurationService.EscapeAdapterName(adapterName));
    }

    [Fact]
    public void BuildSetStaticIpCommand_DefaultValues_ProducesCorrectSyntax()
    {
        var command = NetworkConfigurationService.BuildSetStaticIpCommand("Ethernet");
        Assert.Equal("interface ipv4 set address name=\"Ethernet\" source=static address=192.168.20.2 mask=255.255.255.0 gateway=192.168.20.1", command);
    }

    [Fact]
    public void BuildSetStaticIpCommand_WithoutGateway_SetsGatewayNone()
    {
        var command = NetworkConfigurationService.BuildSetStaticIpCommand("Ethernet", "192.168.20.5", "255.255.255.0", gateway: null);
        Assert.Equal("interface ipv4 set address name=\"Ethernet\" source=static address=192.168.20.5 mask=255.255.255.0 gateway=none", command);
    }

    [Fact]
    public void BuildSetDhcpCommand_ProducesCorrectSyntax()
    {
        var command = NetworkConfigurationService.BuildSetDhcpCommand("Ethernet");
        Assert.Equal("interface ipv4 set address name=\"Ethernet\" source=dhcp & netsh interface ipv4 set dnsservers name=\"Ethernet\" source=dhcp", command);
    }

    [Theory]
    [InlineData("192.168.20.2", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("255.255.255.0", true)]
    [InlineData("999.999.999.999", false)]
    [InlineData("not-an-ip", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidIPv4_ValidatesCorrectly(string? ip, bool expected)
    {
        Assert.Equal(expected, NetworkConfigurationService.IsValidIPv4(ip));
    }

    [Fact]
    public async Task ConfigureStaticTestStandIpAsync_EmptyAdapter_ReturnsFailure()
    {
        var result = await _service.ConfigureStaticTestStandIpAsync("");
        Assert.False(result.Success);
        Assert.Contains("No network adapter", result.Message);
    }

    [Fact]
    public async Task ConfigureStaticTestStandIpAsync_InvalidIp_ReturnsFailure()
    {
        var result = await _service.ConfigureStaticTestStandIpAsync("Ethernet", ipAddress: "invalid.ip");
        Assert.False(result.Success);
        Assert.Contains("Invalid IP address", result.Message);
    }

    [Fact]
    public async Task ConfigureStaticTestStandIpAsync_InvalidMask_ReturnsFailure()
    {
        var result = await _service.ConfigureStaticTestStandIpAsync("Ethernet", subnetMask: "300.0.0.0");
        Assert.False(result.Success);
        Assert.Contains("Invalid subnet mask", result.Message);
    }

    [Fact]
    public async Task ConfigureStaticTestStandIpAsync_InvalidGateway_ReturnsFailure()
    {
        var result = await _service.ConfigureStaticTestStandIpAsync("Ethernet", gateway: "invalid.gw");
        Assert.False(result.Success);
        Assert.Contains("Invalid gateway", result.Message);
    }

    [Fact]
    public async Task ConfigureDhcpAsync_EmptyAdapter_ReturnsFailure()
    {
        var result = await _service.ConfigureDhcpAsync("   ");
        Assert.False(result.Success);
        Assert.Contains("No network adapter", result.Message);
    }

    [Fact]
    public void GetAvailableAdapters_DoesNotThrow_AndFiltersLoopback()
    {
        var adapters = _service.GetAvailableAdapters();
        Assert.NotNull(adapters);
        Assert.DoesNotContain(adapters, a => a.InterfaceType == NetworkInterfaceType.Loopback);
        Assert.DoesNotContain(adapters, a => a.Description.Contains("Bluetooth", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AdapterInfo_IsTestStandConfigured_IdentifiesCorrectIp()
    {
        var configured = new NetworkConfigurationService.AdapterInfo
        {
            Name = "Ethernet",
            Description = "Intel",
            Id = "1",
            Status = OperationalStatus.Up,
            InterfaceType = NetworkInterfaceType.Ethernet,
            CurrentIPv4 = "192.168.20.2",
            IsDhcpEnabled = false
        };

        var unconfigured = configured with { CurrentIPv4 = "192.168.1.50" };

        Assert.True(configured.IsTestStandConfigured);
        Assert.False(unconfigured.IsTestStandConfigured);
        Assert.Contains("192.168.20.2", configured.DisplayName);
    }
}
