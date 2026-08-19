using System;

namespace EasyFM350.Wpf.Backend.Network;

internal static class ProxyEndpoint
{
    public static bool TryParse(string? value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var input = value?.Trim();
        if (string.IsNullOrEmpty(input)) return false;

        var uriText = input.Contains("://", StringComparison.Ordinal) ? input : "tcp://" + input;
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var endpoint)
            || !(endpoint.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                 || endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(endpoint.Host)
            || endpoint.Port is < 1 or > 65535
            || (endpoint.AbsolutePath.Length > 0 && endpoint.AbsolutePath != "/")
            || endpoint.Query.Length != 0)
            return false;

        if (endpoint.HostNameType is UriHostNameType.Unknown or UriHostNameType.IPv6
            || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            return false;

        host = endpoint.IdnHost;
        port = endpoint.Port;
        return true;
    }
}