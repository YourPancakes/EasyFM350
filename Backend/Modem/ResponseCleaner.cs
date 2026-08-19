using System;
using System.Collections.Generic;
using System.Text;

namespace EasyFM350.Wpf.Backend.Modem;

public static class ResponseCleaner
{
    private static readonly string[] UrcPrefixes = { "+CGEV", "+STKPCI", "+CNEMIU", "+CIREPI" };

    public static bool IsUrc(string line)
    {
        return IsUrc(line.AsSpan());
    }

    internal static bool IsUrc(ReadOnlySpan<char> line)
    {
        foreach (var p in UrcPrefixes)
            if (line.StartsWith(p.AsSpan(), StringComparison.Ordinal))
                return true;
        return false;
    }

    public static string Clean(string raw, string sentCommand, out List<string> urcs)
    {
        urcs = new List<string>();
        var sb = new StringBuilder(raw?.Length ?? 0);
        var sent = sentCommand.AsSpan();
        var rest = raw.AsSpan();

        while (rest.Length > 0)
        {
            var sep = rest.IndexOfAny('\r', '\n');
            var seg = sep < 0 ? rest : rest.Slice(0, sep);
            rest = sep < 0 ? ReadOnlySpan<char>.Empty : rest.Slice(sep + 1);
            var line = seg.Trim();
            if (line.Length == 0) continue;
            if (line.Equals(sent, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsUrc(line))
            {
                urcs.Add(line.ToString());
                continue;
            }

            sb.Append(line).Append("\r\n");
        }

        return sb.ToString();
    }
}