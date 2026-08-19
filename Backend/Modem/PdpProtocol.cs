using System;

namespace EasyFM350.Wpf.Backend.Modem;

internal static class PdpProtocol
{
    public static readonly string[] DisplayValues = { "IPv4", "IPv6", "IPv4/IPv6" };

    public static string ToModemValue(string? displayValue)
    {
        return displayValue switch
        {
            "IPv4" or "IP" => "IP",
            "IPv6" or "IPV6" => "IPV6",
            "IPv4/IPv6" or "IPV4V6" => "IPV4V6",
            _ => throw new ArgumentException("Unsupported PDP protocol.", nameof(displayValue))
        };
    }

    public static string ToDisplayValue(string? modemValue)
    {
        return modemValue switch
        {
            "IP" => "IPv4",
            "IPV6" => "IPv6",
            "IPV4V6" => "IPv4/IPv6",
            _ => "IPv4"
        };
    }
}