using System;

namespace EasyFM350.Wpf.Backend.Modem;

internal sealed class IdentityService
{
    private static readonly string[] LineSeparators = { "\r", "\n" };
    private readonly Modem _modem;

    public IdentityService(Modem modem)
    {
        _modem = modem ?? throw new ArgumentNullException(nameof(modem));
    }

    public string? Read(int slot)
    {
        return slot switch
        {
            7 => Modem.Number(_modem.Send("AT+CGSN", 3000, true)),
            10 => ExtractValue(_modem.Send("AT+EGMR=0,10", 3000, true))
                  ?? ExtractValue(_modem.Send("AT+EGMREXT=0,10", 3000, true)),
            5 => ExtractValue(_modem.Send("AT+CFSN", 3000, true))
                 ?? ExtractValue(_modem.Send("AT+EGMR=0,5", 3000, true)),
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }

    public WriteResult Write(int slot, string value)
    {
        if (slot is not (5 or 7 or 10)) throw new ArgumentOutOfRangeException(nameof(slot));
        if (!AtInput.IsSafeValue(value)) throw new ArgumentException("Unsafe identity value.", nameof(value));

        var commands = BuildWriteCommands(slot, value);
        var accepted = false;
        foreach (var command in commands)
        {
            if (!Modem.IsOk(_modem.Send(command))) continue;
            accepted = true;
            break;
        }

        return accepted ? new WriteResult(true, Read(slot)) : default;
    }

    private static string[] BuildWriteCommands(int slot, string value)
    {
        return slot == 5
            ? new[]
            {
                "AT+EGMREXT=1,5, \"" + value + "\"", "AT+EGMREXT=1,5,\"" + value + "\"",
                "AT+EGMR=1,5, \"" + value + "\"", "AT+EGMR=1,5,\"" + value + "\"",
                "AT+GTSN=\"" + value + "\""
            }
            : new[]
            {
                "AT+EGMR=1," + slot + ",\"" + value + "\"", "AT+EGMR=1," + slot + ", \"" + value + "\"",
                "AT+EGMREXT=1," + slot + ",\"" + value + "\"", "AT+EGMREXT=1," + slot + ", \"" + value + "\""
            };
    }

    private static string? ExtractValue(string response)
    {
        foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.Trim();
            if (value.StartsWith("AT+", StringComparison.OrdinalIgnoreCase)
                || value == "OK" || value == "ERROR" || value == "NO CARRIER"
                || value.StartsWith("+CME ERROR:", StringComparison.Ordinal)
                || value.StartsWith("+CMS ERROR:", StringComparison.Ordinal)) continue;
            var firstQuote = value.IndexOf('"');
            if (firstQuote >= 0)
            {
                var secondQuote = value.IndexOf('"', firstQuote + 1);
                if (secondQuote > firstQuote) return value.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
            }

            var separator = value.IndexOf(':');
            if (separator >= 0) value = value.Substring(separator + 1).Trim();
            if (value.Length > 0) return value;
        }

        return null;
    }

    public readonly record struct WriteResult(bool Accepted, string? VerifiedValue);
}