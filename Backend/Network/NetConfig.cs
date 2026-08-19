using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace EasyFM350.Wpf.Backend.Network;

[SupportedOSPlatform("windows")]
public static class NetConfig
{
    private const int ProxyRouteMetric = 9999;
    private const int TunnelRouteMetric = 1;

    // FM350 leaves <gw_addr> empty in +CGCONTRDP; 0.0.0.0 means on-link routes.
    public const string OnLinkGateway = "0.0.0.0";

    private static readonly Encoding OemEncoding = CreateOemEncoding();

    public static string? FindNcmInterface(out int interfaceIndex)
    {
        interfaceIndex = 0;
        string? fallbackName = null;
        var fallbackIndex = 0;
        try
        {
            using (var searcher = new ManagementObjectSearcher(
                       "SELECT Name, NetConnectionID, InterfaceIndex, NetConnectionStatus " +
                       "FROM Win32_NetworkAdapter WHERE Name LIKE '%Remote NDIS based Internet Sharing%'"))
            using (var results = searcher.Get())
            {
                foreach (var item in results)
                    using (item)
                    {
                        var name = item["NetConnectionID"] as string;
                        if (string.IsNullOrWhiteSpace(name) ||
                            !TryInterfaceIndex(item["InterfaceIndex"], out var index))
                            continue;

                        fallbackName ??= name;
                        if (fallbackIndex == 0) fallbackIndex = index;

                        if (Convert.ToUInt16(item["NetConnectionStatus"] ?? 0, CultureInfo.InvariantCulture) != 2)
                            continue;
                        interfaceIndex = index;
                        return name;
                    }
            }
        }
        catch (ManagementException)
        {
        }
        catch (InvalidCastException)
        {
        }
        catch (FormatException)
        {
        }
        catch (OverflowException)
        {
        }

        interfaceIndex = fallbackIndex;
        return fallbackName;
    }

    private static bool TryInterfaceIndex(object? value, out int interfaceIndex)
    {
        try
        {
            interfaceIndex = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return interfaceIndex > 0;
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            interfaceIndex = 0;
            return false;
        }
    }

    public static string Apply(
        string iface,
        string ip,
        string subnetMask,
        string gateway,
        string? dns1,
        string? dns2)
    {
        RequireValue(iface, nameof(iface));
        ValidateIpv4(ip, nameof(ip));
        ValidateSubnetMask(subnetMask, nameof(subnetMask));
        var onLink = gateway == OnLinkGateway;
        ValidateGateway(gateway, nameof(gateway));
        if (!onLink && IPAddress.Parse(ip).Equals(IPAddress.Parse(gateway)))
            throw new ArgumentException("Gateway must differ from the local address.", nameof(gateway));
        if (!string.IsNullOrWhiteSpace(dns1)) ValidateIpv4(dns1, nameof(dns1));
        if (!string.IsNullOrWhiteSpace(dns2)) ValidateIpv4(dns2, nameof(dns2));

        var log = new StringBuilder();
        Append(log, Run("netsh", false, 15000,
            "interface", "ipv4", "set", "address", "name=" + iface, "source=static",
            "address=" + ip, "mask=" + subnetMask, "gateway=none", "store=active"));

        if (!onLink && !InSameSubnet(ip, gateway, subnetMask))
            ReplaceOnLinkRoute(log, gateway + "/32", iface, 1, 15000);

        if (!string.IsNullOrWhiteSpace(dns1))
        {
            Append(log, Run("netsh", false, 15000,
                "interface", "ipv4", "set", "dnsservers", "name=" + iface, "source=static",
                "address=" + dns1, "register=primary", "validate=no"));
            ReplaceRoute(log, dns1 + "/32", iface, gateway, 1, 15000);

            if (!string.IsNullOrWhiteSpace(dns2) && !dns2.Equals(dns1, StringComparison.Ordinal))
            {
                Append(log, Run("netsh", false, 15000,
                    "interface", "ipv4", "add", "dnsservers", "name=" + iface,
                    "address=" + dns2, "index=2", "validate=no"));
                ReplaceRoute(log, dns2 + "/32", iface, gateway, 1, 15000);
            }
        }
        else
        {
            Append(log, Run("netsh", true, 15000,
                "interface", "ipv4", "set", "dnsservers", "name=" + iface,
                "source=static", "address=none", "validate=no"));
            Append(log, "no IPv4 DNS from modem");
        }

        return log.ToString();
    }

    public static string Cleanup(
        string iface,
        string? gateway,
        string? dns1,
        string? dns2,
        int timeoutMs = 15000)
    {
        RequireValue(iface, nameof(iface));
        var log = new StringBuilder();

        DeleteRoute(log, "0.0.0.0/0", iface, null, timeoutMs);
        if (IsIpv4(dns1)) DeleteRoute(log, dns1 + "/32", iface, null, timeoutMs);
        if (IsIpv4(dns2)) DeleteRoute(log, dns2 + "/32", iface, null, timeoutMs);
        if (IsIpv4(gateway))
            DeleteRoute(log, gateway + "/32", iface, null, timeoutMs);

        Exception? restoreError = null;
        try
        {
            Append(log, Run("netsh", false, timeoutMs,
                "interface", "ipv4", "set", "dnsservers", "name=" + iface, "source=dhcp"));
        }
        catch (Exception exception)
        {
            restoreError = exception;
            Append(log, "DNS RESTORE FAILED: " + exception.Message);
        }

        try
        {
            Append(log, Run("netsh", false, timeoutMs,
                "interface", "ipv4", "set", "address", "name=" + iface, "source=dhcp", "store=active"));
        }
        catch (Exception exception)
        {
            restoreError ??= exception;
            Append(log, "ADDRESS RESTORE FAILED: " + exception.Message);
        }

        if (restoreError != null)
            throw new InvalidOperationException(
                "Failed to restore interface " + iface + " to DHCP: " + restoreError.Message, restoreError);
        return log.ToString();
    }

    public static string TunnelOn(string iface, string gateway, int timeoutMs = 15000)
    {
        return SetDefaultRoute(iface, gateway, TunnelRouteMetric, timeoutMs);
    }

    public static string TunnelOff(string iface, string gateway, bool keepProxyRoute, int timeoutMs = 15000)
    {
        return SetDefaultRoute(iface, gateway, keepProxyRoute ? ProxyRouteMetric : null, timeoutMs);
    }

    public static string ProxyRouteOn(string iface, string gateway, bool tunnelEnabled, int timeoutMs = 15000)
    {
        return SetDefaultRoute(iface, gateway, tunnelEnabled ? TunnelRouteMetric : ProxyRouteMetric, timeoutMs);
    }

    public static string ProxyRouteOff(string iface, string gateway, bool tunnelEnabled, int timeoutMs = 15000)
    {
        return SetDefaultRoute(iface, gateway, tunnelEnabled ? TunnelRouteMetric : null, timeoutMs);
    }

    public static void HostRoute(string iface, string gateway, string targetIp, bool add)
    {
        RequireValue(iface, nameof(iface));
        ValidateGateway(gateway, nameof(gateway));
        ValidateIpv4(targetIp, nameof(targetIp));
        var log = new StringBuilder();
        if (add) ReplaceRoute(log, targetIp + "/32", iface, gateway, 1, 10000);
        else DeleteRoute(log, targetIp + "/32", iface, null, 10000);
    }

    private static string SetDefaultRoute(string iface, string gateway, int? metric, int timeoutMs)
    {
        RequireValue(iface, nameof(iface));
        ValidateGateway(gateway, nameof(gateway));
        var log = new StringBuilder();
        DeleteRoute(log, "0.0.0.0/0", iface, null, timeoutMs);
        if (metric != null)
            Append(log, Run("netsh", false, timeoutMs,
                "interface", "ipv4", "add", "route", "prefix=0.0.0.0/0",
                "interface=" + iface, "nexthop=" + gateway,
                "metric=" + metric.Value.ToString(CultureInfo.InvariantCulture), "store=active"));
        return log.ToString();
    }

    private static void ReplaceRoute(
        StringBuilder log,
        string prefix,
        string iface,
        string nextHop,
        int metric,
        int timeoutMs)
    {
        DeleteRoute(log, prefix, iface, null, timeoutMs);
        Append(log, Run("netsh", false, timeoutMs,
            "interface", "ipv4", "add", "route", "prefix=" + prefix,
            "interface=" + iface, "nexthop=" + nextHop,
            "metric=" + metric.ToString(CultureInfo.InvariantCulture), "store=active"));
    }

    private static void ReplaceOnLinkRoute(
        StringBuilder log,
        string prefix,
        string iface,
        int metric,
        int timeoutMs)
    {
        DeleteRoute(log, prefix, iface, null, timeoutMs);
        Append(log, Run("netsh", false, timeoutMs,
            "interface", "ipv4", "add", "route", "prefix=" + prefix,
            "interface=" + iface, "nexthop=0.0.0.0",
            "metric=" + metric.ToString(CultureInfo.InvariantCulture), "store=active"));
    }

    private static void DeleteRoute(StringBuilder log, string prefix, string iface, string? nextHop, int timeoutMs)
    {
        var arguments = new List<string>
        {
            "interface", "ipv4", "delete", "route", "prefix=" + prefix,
            "interface=" + iface
        };
        if (!string.IsNullOrWhiteSpace(nextHop)) arguments.Add("nexthop=" + nextHop);
        arguments.Add("store=active");
        Append(log, Run("netsh", true, timeoutMs, arguments.ToArray()));
    }

    private static void RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value required.", parameterName);
    }

    private static void ValidateSubnetMask(string value, string parameterName)
    {
        if (!TrySubnetMask(value, out _))
            throw new ArgumentException("Valid contiguous IPv4 subnet mask required.", parameterName);
    }

    private static bool InSameSubnet(string address, string gateway, string subnetMask)
    {
        var addressBytes = IPAddress.Parse(address).GetAddressBytes();
        var gatewayBytes = IPAddress.Parse(gateway).GetAddressBytes();
        var maskBytes = IPAddress.Parse(subnetMask).GetAddressBytes();
        for (var index = 0; index < 4; index++)
            if ((addressBytes[index] & maskBytes[index]) != (gatewayBytes[index] & maskBytes[index]))
                return false;
        return true;
    }

    private static bool TrySubnetMask(string value, out IPAddress? mask)
    {
        mask = null;
        if (!IPAddress.TryParse(value, out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork) return false;
        var seenZero = false;
        foreach (var octet in parsed.GetAddressBytes())
            for (var bit = 7; bit >= 0; bit--)
            {
                var set = (octet & (1 << bit)) != 0;
                if (seenZero && set) return false;
                if (!set) seenZero = true;
            }

        mask = parsed;
        return true;
    }

    private static void ValidateGateway(string value, string parameterName)
    {
        if (value != OnLinkGateway) ValidateIpv4(value, parameterName);
    }

    private static void ValidateIpv4(string value, string parameterName)
    {
        if (!IsIpv4(value))
            throw new ArgumentException("Valid IPv4 address required.", parameterName);
    }

    private static bool IsIpv4(string? value)
    {
        if (!IPAddress.TryParse(value, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] != 127 && bytes[0] < 224
               && !(bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255);
    }

    private static void Append(StringBuilder log, string? value)
    {
        log.AppendLine(value ?? string.Empty);
    }

    private static Encoding CreateOemEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch (ArgumentException)
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static string Run(string executable, bool tolerateFailure, int timeoutMs, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException(executable + " did not start.");
        }
        catch (Exception ex)
        {
            if (tolerateFailure) return "START FAILED: " + ex.Message;
            throw;
        }

        using (process)
        {
            var stdout = ReadAllBytesAsync(process.StandardOutput.BaseStream);
            var stderr = ReadAllBytesAsync(process.StandardError.BaseStream);

            if (!process.WaitForExit(timeoutMs))
            {
                TryKill(process);
                WaitForOutput(stdout, stderr);
                if (!tolerateFailure) throw new TimeoutException(executable + " timed out.");
                return "TIMEOUT";
            }

            WaitForOutput(stdout, stderr);
            var output = DecodeConsoleOutput(CompletedBytes(stdout)) + DecodeConsoleOutput(CompletedBytes(stderr));
            if (!tolerateFailure && process.ExitCode != 0)
                throw new InvalidOperationException(executable + " exited with " + process.ExitCode + ": " +
                                                    output.Trim());
            return output;
        }
    }

    public static string? DottedToIpv6(string dotted)
    {
        var parts = dotted.Split('.');
        if (parts.Length != 16) return null;
        var bytes = new byte[16];
        for (var index = 0; index < bytes.Length; index++)
            if (!byte.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes[index]))
                return null;
        return new IPAddress(bytes).ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch
        {
        }

        try
        {
            process.WaitForExit(2000);
        }
        catch
        {
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static string DecodeConsoleOutput(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return OemEncoding.GetString(bytes);
        }
    }

    private static void WaitForOutput(Task<byte[]> stdout, Task<byte[]> stderr)
    {
        try
        {
            Task.WaitAll(new Task[] { stdout, stderr }, 5000);
        }
        catch
        {
        }
    }

    private static byte[] CompletedBytes(Task<byte[]> task)
    {
        return task.IsCompletedSuccessfully ? task.Result : Array.Empty<byte>();
    }
}