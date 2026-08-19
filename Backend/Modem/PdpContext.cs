using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace EasyFM350.Wpf.Backend.Modem;

internal static class PdpContext
{
    // FM350 may omit the subnet mask in +CGCONTRDP; a cellular PDN is point-to-point, so /32.
    public const string DefaultIpv4SubnetMask = "255.255.255.255";
    private static readonly char[] LineSeparators = { '\r', '\n' };

    public static IReadOnlyList<Configured> ParseConfigured(string? response)
    {
        var contexts = new List<Configured>();
        if (string.IsNullOrWhiteSpace(response)) return contexts;

        foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = Modem.Fields(line, "+CGDCONT");
            if (fields.Length < 2 || !TryCid(fields[0], out var cid)) continue;
            var pdpType = fields[1];
            var apn = fields.Length > 2 ? fields[2] : string.Empty;
            bool? ims = fields.Length > 9 && TryFlag(fields[9], out var flag) ? flag : null;
            contexts.Add(new Configured(cid, pdpType, apn, ims));
        }

        return contexts;
    }

    public static IReadOnlyList<Active> ParseActive(string? response)
    {
        var builders = new Dictionary<int, ActiveBuilder>();
        if (string.IsNullOrWhiteSpace(response)) return Array.Empty<Active>();

        foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = Modem.Fields(line, "+CGCONTRDP");
            if (fields.Length < 3 || !TryCid(fields[0], out var cid)) continue;

            if (!builders.TryGetValue(cid, out var builder))
            {
                builder = new ActiveBuilder(cid);
                builders.Add(cid, builder);
            }

            if (builder.BearerId == null && int.TryParse(fields[1], out var bearerId)) builder.BearerId = bearerId;
            if (builder.Apn.Length == 0 && fields[2].Length > 0) builder.Apn = fields[2];

            if (fields.Length > 3)
            {
                if (builder.LocalIpv4 == null
                    && TryLocalIpv4(fields[3], out var localIpv4, out var subnetMask))
                {
                    builder.LocalIpv4 = localIpv4;
                    builder.LocalIpv4SubnetMask = subnetMask;
                }

                if (builder.LocalIpv6 == null && TryIpv6(fields[3], out var localIpv6)) builder.LocalIpv6 = localIpv6;
            }

            if (fields.Length > 4 && builder.GatewayIpv4 == null && TryIpv4(fields[4], out var gateway))
                builder.GatewayIpv4 = gateway;
            if (fields.Length > 5 && builder.PrimaryDns == null && TryIpv4(fields[5], out var primaryDns))
                builder.PrimaryDns = primaryDns;
            if (fields.Length > 6 && builder.SecondaryDns == null && TryIpv4(fields[6], out var secondaryDns))
                builder.SecondaryDns = secondaryDns;
            if (fields.Length > 9 && TryFlag(fields[9], out var ims)) builder.MergeImsFlag(ims);
            if (builder.PdpType == null && fields.Length > 27 && IsPacketDataType(fields[27]))
                builder.PdpType = fields[27].ToUpperInvariant();
        }

        var result = new List<Active>(builders.Count);
        foreach (var builder in builders.Values) result.Add(builder.Build());
        result.Sort((left, right) => left.Cid.CompareTo(right.Cid));
        return result;
    }

    public static Configured? FindConfigured(IReadOnlyList<Configured> configured, int cid)
    {
        for (var index = 0; index < configured.Count; index++)
            if (configured[index].Cid == cid)
                return configured[index];
        return null;
    }

    public static Active? FindActive(IReadOnlyList<Active> active, int cid)
    {
        for (var index = 0; index < active.Count; index++)
            if (active[index].Cid == cid)
                return active[index];
        return null;
    }

    public static int SelectDataContext(IReadOnlyList<Configured> configured, IReadOnlyList<Active> active)
    {
        var bestCid = 0;
        var bestScore = int.MinValue;

        for (var index = 0; index < active.Count; index++)
        {
            var current = active[index];
            var definition = FindConfigured(configured, current.Cid);
            var ims = current.IsImsSignalling ?? definition?.IsImsSignalling;
            if (ims == true) continue;

            var effectiveType = definition?.PdpType ?? current.PdpType;
            if (effectiveType != null && !IsPacketDataType(effectiveType)) continue;
            if (definition == null && effectiveType == null && !current.HasAddress) continue;

            var score = current.HasAddress ? 100 : 0;
            if (current.Apn.Length > 0) score += 20;
            if (definition != null && IsPacketDataType(definition.PdpType)) score += 10;
            if (score > bestScore || (score == bestScore && (bestCid == 0 || current.Cid < bestCid)))
            {
                bestScore = score;
                bestCid = current.Cid;
            }
        }

        if (bestCid > 0) return bestCid;

        bestScore = int.MinValue;
        for (var index = 0; index < configured.Count; index++)
        {
            var current = configured[index];
            if (current.IsImsSignalling == true || !IsPacketDataType(current.PdpType)) continue;
            var score = current.Apn.Length > 0 ? 20 : 0;
            if (score > bestScore || (score == bestScore && (bestCid == 0 || current.Cid < bestCid)))
            {
                bestScore = score;
                bestCid = current.Cid;
            }
        }

        return bestCid;
    }

    public static bool TryParseAddresses(string? response, int contextId, out string? ipv4, out string? ipv6)
    {
        ipv4 = null;
        ipv6 = null;
        if (string.IsNullOrWhiteSpace(response) || contextId < 1) return false;

        foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = Modem.Fields(line, "+CGPADDR");
            if (fields.Length < 2 || !int.TryParse(fields[0], out var cid) || cid != contextId) continue;
            for (var fieldIndex = 1; fieldIndex < fields.Length; fieldIndex++)
            {
                if (ipv4 == null && TryIpv4(fields[fieldIndex], out var v4)) ipv4 = v4;
                if (ipv6 == null && TryIpv6(fields[fieldIndex], out var v6)) ipv6 = v6;
            }
        }

        return ipv4 != null || ipv6 != null;
    }

    public static bool TryParseDns(string? response, int contextId, out string? primaryDns, out string? secondaryDns)
    {
        primaryDns = null;
        secondaryDns = null;
        if (string.IsNullOrWhiteSpace(response) || contextId < 1) return false;

        foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = Modem.Fields(line, "+GTDNS");
            if (fields.Length < 2 || !int.TryParse(fields[0], out var cid) || cid != contextId) continue;
            if (fields.Length > 1 && TryIpv4(fields[1], out var first)) primaryDns = first;
            if (fields.Length > 2 && TryIpv4(fields[2], out var second)) secondaryDns = second;
            return primaryDns != null || secondaryDns != null;
        }

        return false;
    }

    public static bool TryParseIpv4(string? response, int contextId, out Ipv4Parameters? parameters)
    {
        parameters = null;
        var active = ParseActive(response);
        var context = FindActive(active, contextId);
        if (context?.LocalIpv4 == null || context.GatewayIpv4 == null)
            return false;
        parameters = new Ipv4Parameters(
            context.LocalIpv4,
            IsUsableSubnetMask(context.LocalIpv4SubnetMask) ? context.LocalIpv4SubnetMask! : DefaultIpv4SubnetMask,
            context.GatewayIpv4,
            context.PrimaryDns,
            context.SecondaryDns);
        return true;
    }

    private static bool IsUsableSubnetMask(string? mask)
    {
        return mask != null && !mask.Equals("0.0.0.0", StringComparison.Ordinal);
    }

    public static bool HasUsableAddress(string? response, int contextId)
    {
        return TryParseAddresses(response, contextId, out _, out _);
    }

    public static bool TryParsePdnDeactivationCid(string? message, out int contextId)
    {
        contextId = 0;
        if (string.IsNullOrWhiteSpace(message)) return false;

        var marker = message.IndexOf("PDN DEACT", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
            return TryReadPositiveInteger(message, marker + "PDN DEACT".Length, out contextId);

        marker = message.IndexOf("NW DEACT", StringComparison.OrdinalIgnoreCase);
        var markerLength = "NW DEACT".Length;
        if (marker < 0)
        {
            marker = message.IndexOf("ME DEACT", StringComparison.OrdinalIgnoreCase);
            markerLength = "ME DEACT".Length;
        }

        if (marker < 0) return false;

        var tail = message.Substring(marker + markerLength).Trim().TrimStart(':').Trim();
        var parts = tail.Split(',');
        if (parts.Length < 2) return false;

        if (int.TryParse(parts[0].Trim(), out var primaryCid) && primaryCid > 0)
            return int.TryParse(parts[1].Trim(), out contextId) && contextId > 0;

        return parts.Length >= 3
               && int.TryParse(parts[^1].Trim(), out contextId)
               && contextId > 0;
    }

    private static bool TryReadPositiveInteger(string text, int offset, out int value)
    {
        value = 0;
        var index = offset;
        while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] is ':' or ',')) index++;
        var start = index;
        while (index < text.Length && char.IsDigit(text[index])) index++;
        return index > start && int.TryParse(text.Substring(start, index - start), out value) && value > 0;
    }

    private static bool IsPacketDataType(string value)
    {
        return value.Equals("IP", StringComparison.OrdinalIgnoreCase)
               || value.Equals("IPV6", StringComparison.OrdinalIgnoreCase)
               || value.Equals("IPV4V6", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCid(string value, out int cid)
    {
        return int.TryParse(value, out cid) && cid > 0;
    }

    private static bool TryFlag(string value, out bool flag)
    {
        if (value == "0")
        {
            flag = false;
            return true;
        }

        if (value == "1")
        {
            flag = true;
            return true;
        }

        flag = false;
        return false;
    }

    private static bool TryLocalIpv4(string? value, out string address, out string? subnetMask)
    {
        address = string.Empty;
        subnetMask = null;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var octets = value.Split('.');
        if (octets.Length == 4)
            return TryIpv4(value, out address);
        if (octets.Length != 8) return false;

        if (!TryIpv4(string.Join(".", octets, 0, 4), out address)) return false;
        if (!TrySubnetMask(string.Join(".", octets, 4, 4), out var parsedMask))
        {
            address = string.Empty;
            return false;
        }

        subnetMask = parsedMask;
        return true;
    }

    private static bool TrySubnetMask(string value, out string subnetMask)
    {
        subnetMask = string.Empty;
        if (!IPAddress.TryParse(value, out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork) return false;

        var bytes = parsed.GetAddressBytes();
        var seenZero = false;
        foreach (var octet in bytes)
            for (var bit = 7; bit >= 0; bit--)
            {
                var set = (octet & (1 << bit)) != 0;
                if (seenZero && set) return false;
                if (!set) seenZero = true;
            }

        subnetMask = parsed.ToString();
        return true;
    }

    private static bool TryIpv4(string? value, out string address)
    {
        address = string.Empty;
        if (!IPAddress.TryParse(value, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = parsed.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] == 127 || bytes[0] >= 224) return false;
        address = parsed.ToString();
        return true;
    }

    private static bool TryIpv6(string? value, out string address)
    {
        address = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (IPAddress.TryParse(value, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6
                                                      && !parsed.Equals(IPAddress.IPv6Any))
        {
            address = parsed.ToString();
            return true;
        }

        var octets = value.Split('.');
        if (octets.Length is not (16 or 32)) return false;
        var bytes = new byte[16];
        for (var index = 0; index < bytes.Length; index++)
            if (!byte.TryParse(octets[index], out bytes[index]))
                return false;
        parsed = new IPAddress(bytes);
        if (parsed.Equals(IPAddress.IPv6Any)) return false;
        address = parsed.ToString();
        return true;
    }

    internal sealed record Configured(
        int Cid,
        string PdpType,
        string Apn,
        bool? IsImsSignalling);

    internal sealed record Active(
        int Cid,
        int? BearerId,
        string Apn,
        string? LocalIpv4,
        string? LocalIpv4SubnetMask,
        string? LocalIpv6,
        string? GatewayIpv4,
        string? PrimaryDns,
        string? SecondaryDns,
        string? PdpType,
        bool? IsImsSignalling)
    {
        public bool HasAddress => LocalIpv4 != null || LocalIpv6 != null;
    }

    internal sealed record Ipv4Parameters(
        string? LocalAddress,
        string SubnetMask,
        string Gateway,
        string? PrimaryDns,
        string? SecondaryDns);

    private sealed class ActiveBuilder
    {
        private bool? _ims;

        public ActiveBuilder(int cid)
        {
            Cid = cid;
        }

        public int Cid { get; }
        public int? BearerId { get; set; }
        public string Apn { get; set; } = string.Empty;
        public string? LocalIpv4 { get; set; }
        public string? LocalIpv4SubnetMask { get; set; }
        public string? LocalIpv6 { get; set; }
        public string? GatewayIpv4 { get; set; }
        public string? PrimaryDns { get; set; }
        public string? SecondaryDns { get; set; }
        public string? PdpType { get; set; }

        public void MergeImsFlag(bool value)
        {
            if (value) _ims = true;
            else if (_ims == null) _ims = false;
        }

        public Active Build()
        {
            return new Active(Cid, BearerId, Apn, LocalIpv4, LocalIpv4SubnetMask, LocalIpv6, GatewayIpv4, PrimaryDns,
                SecondaryDns, PdpType, _ims);
        }
    }
}