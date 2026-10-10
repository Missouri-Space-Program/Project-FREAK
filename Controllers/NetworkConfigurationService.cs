using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Project_FREAK.Controllers;

public class NetworkConfigurationService
{
    public const string DefaultTestStandIp = "192.168.20.2";
    public const string DefaultSubnetMask = "255.255.255.0";
    public const string DefaultGateway = "192.168.20.1";

    public record AdapterInfo
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required string Id { get; init; }
        public required OperationalStatus Status { get; init; }
        public required NetworkInterfaceType InterfaceType { get; init; }
        public string? CurrentIPv4 { get; init; }
        public string? CurrentSubnetMask { get; init; }
        public bool IsDhcpEnabled { get; init; }

        public bool IsConnected => Status == OperationalStatus.Up;
        public bool IsTestStandConfigured => string.Equals(CurrentIPv4, DefaultTestStandIp, StringComparison.OrdinalIgnoreCase);

        public string DisplayName
        {
            get
            {
                var statusLabel = IsConnected ? "Connected" : "Disconnected";
                var ipLabel = string.IsNullOrEmpty(CurrentIPv4) ? "No IPv4" : CurrentIPv4;
                var modeLabel = IsDhcpEnabled ? "DHCP" : "Static";
                return $"{Name} [{statusLabel}] - {ipLabel} ({modeLabel}) - {Description}";
            }
        }
    }

    public record NetworkConfigResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public bool CancelledByUser { get; init; }
    }

    public virtual IReadOnlyList<AdapterInfo> GetAvailableAdapters()
    {
        var adapters = new List<AdapterInfo>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var nic in interfaces)
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                // Filter out Bluetooth adapters
                if (nic.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? ipv4 = null;
                string? mask = null;
                bool isDhcp = false;

                try
                {
                    var ipProps = nic.GetIPProperties();
                    var unicast = ipProps.UnicastAddresses
                        .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);

                    if (unicast != null)
                    {
                        ipv4 = unicast.Address.ToString();
                        mask = unicast.IPv4Mask?.ToString();
                    }

                    var v4Props = ipProps.GetIPv4Properties();
                    if (v4Props != null)
                    {
                        isDhcp = v4Props.IsDhcpEnabled;
                    }
                }
                catch
                {
                    // Ignore errors reading specific IP properties
                }

                adapters.Add(new AdapterInfo
                {
                    Name = nic.Name,
                    Description = nic.Description,
                    Id = nic.Id,
                    Status = nic.OperationalStatus,
                    InterfaceType = nic.NetworkInterfaceType,
                    CurrentIPv4 = ipv4,
                    CurrentSubnetMask = mask,
                    IsDhcpEnabled = isDhcp
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error enumerating network adapters: {ex.Message}");
        }

        // Sort: Connected Ethernet adapters first, then other connected, then disconnected Ethernet, then others
        return adapters
            .OrderByDescending(a => a.InterfaceType == NetworkInterfaceType.Ethernet && a.IsConnected)
            .ThenByDescending(a => a.IsConnected)
            .ThenByDescending(a => a.InterfaceType == NetworkInterfaceType.Ethernet)
            .ThenBy(a => a.Name)
            .ToList();
    }

    public static string EscapeAdapterName(string adapterName)
    {
        return adapterName.Replace("\"", string.Empty).Trim();
    }

    public static string BuildSetStaticIpCommand(
        string adapterName,
        string ipAddress = DefaultTestStandIp,
        string subnetMask = DefaultSubnetMask,
        string? gateway = DefaultGateway)
    {
        var cleanName = EscapeAdapterName(adapterName);
        var gwParam = string.IsNullOrWhiteSpace(gateway) ? "gateway=none" : $"gateway={gateway}";
        return $"interface ipv4 set address name=\"{cleanName}\" source=static address={ipAddress} mask={subnetMask} {gwParam}";
    }

    public static string BuildSetDhcpCommand(string adapterName)
    {
        var cleanName = EscapeAdapterName(adapterName);
        return $"interface ipv4 set address name=\"{cleanName}\" source=dhcp & netsh interface ipv4 set dnsservers name=\"{cleanName}\" source=dhcp";
    }

    public static bool IsValidIPv4(string? ip)
    {
        return IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork;
    }

    public async Task<NetworkConfigResult> ConfigureStaticTestStandIpAsync(
        string adapterName,
        string ipAddress = DefaultTestStandIp,
        string subnetMask = DefaultSubnetMask,
        string? gateway = DefaultGateway)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return new NetworkConfigResult { Success = false, Message = "No network adapter selected." };
        }

        if (!IsValidIPv4(ipAddress))
        {
            return new NetworkConfigResult { Success = false, Message = $"Invalid IP address: '{ipAddress}'." };
        }

        if (!IsValidIPv4(subnetMask))
        {
            return new NetworkConfigResult { Success = false, Message = $"Invalid subnet mask: '{subnetMask}'." };
        }

        if (!string.IsNullOrWhiteSpace(gateway) && !IsValidIPv4(gateway))
        {
            return new NetworkConfigResult { Success = false, Message = $"Invalid gateway: '{gateway}'." };
        }

        var command = BuildSetStaticIpCommand(adapterName, ipAddress, subnetMask, gateway);
        return await ExecuteElevatedNetshAsync(command, $"Configured {adapterName} to {ipAddress}.");
    }

    public async Task<NetworkConfigResult> ConfigureDhcpAsync(string adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return new NetworkConfigResult { Success = false, Message = "No network adapter selected." };
        }

        var command = BuildSetDhcpCommand(adapterName);
        return await ExecuteElevatedNetshAsync(command, $"Restored {adapterName} to automatic DHCP configuration.");
    }

    protected virtual async Task<NetworkConfigResult> ExecuteElevatedNetshAsync(string netshArguments, string successMessage)
    {
        var tempLogPath = Path.Combine(Path.GetTempPath(), $"netsh_{Guid.NewGuid():N}.log");

        try
        {
            // Execute via cmd.exe so we can redirect output to a temp log file while running elevated
            var cmdArguments = $"/c \"netsh {netshArguments} > \"{tempLogPath}\" 2>&1\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArguments,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };

            int exitCode;
            try
            {
                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return new NetworkConfigResult
                    {
                        Success = false,
                        Message = "Failed to launch network configuration process."
                    };
                }

                await process.WaitForExitAsync();
                exitCode = process.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (User cancelled UAC)
            {
                return new NetworkConfigResult
                {
                    Success = false,
                    CancelledByUser = true,
                    Message = "Operation cancelled: Administrator permission was denied."
                };
            }
            catch (Exception ex)
            {
                return new NetworkConfigResult
                {
                    Success = false,
                    Message = $"Failed to execute configuration: {ex.Message}"
                };
            }

            string output = string.Empty;
            if (File.Exists(tempLogPath))
            {
                try
                {
                    output = await File.ReadAllTextAsync(tempLogPath);
                }
                catch { }
            }

            if (exitCode == 0)
            {
                return new NetworkConfigResult
                {
                    Success = true,
                    Message = successMessage
                };
            }

            var trimmedOutput = output.Trim();
            var detail = string.IsNullOrWhiteSpace(trimmedOutput)
                ? $"Process exited with code {exitCode}."
                : trimmedOutput;

            return new NetworkConfigResult
            {
                Success = false,
                Message = $"Configuration failed: {detail}"
            };
        }
        finally
        {
            if (File.Exists(tempLogPath))
            {
                try { File.Delete(tempLogPath); } catch { }
            }
        }
    }
}
