using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EasyFM350.Wpf.Backend.Radio;

internal static partial class SignalParser
{
    [GeneratedRegex(@"(PCC|SCC \d+):([\d,]+)")]
    private static partial Regex CaRe();

    public static Snapshot Parse(string? rsrpR, string? csqR, string? cellR, string? caR, string? tempR)
    {
        var s = new Snapshot();
        if (TryReadIntField(rsrpR, "+RSRP", 0, out var pci)
            && TryReadIntField(rsrpR, "+RSRP", 1, out var earfcn)
            && TryReadIntField(rsrpR, "+RSRP", 2, out var rsrp))
        {
            s.HasSignal = true;
            s.Pci = pci;
            s.Earfcn = earfcn;
            s.Rsrp = rsrp;
            s.Band = BandPlan.BandFromEarfcn(earfcn);
        }

        if (TryReadIntField(csqR, "+CSQ", 0, out var csq)) s.Csq = csq == 99 ? -1 : csq;

        if (TryReadField(cellR, "+GTCCINFO", 1, out var rat)
            && TryReadField(cellR, "+GTCCINFO", 10, out var sinr))
        {
            var hasRsrq = TryReadIntField(cellR, "+GTCCINFO", 13, out var rq);
            if (rat.SequenceEqual("9"))
            {
                if (hasRsrq && rq != 255) s.RsrqDb = rq * 0.5 - 43.5;
                s.SinrIdx = sinr.SequenceEqual("255") ? null : sinr.ToString();
                if (int.TryParse(sinr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv) && sv != 255)
                    s.SinrDb = sv * 0.5 - 23;
            }
            else if (!rat.SequenceEqual("2"))
            {
                if (hasRsrq && rq != 255) s.RsrqDb = rq * 0.5 - 19.5;
                s.SinrIdx = sinr.SequenceEqual("255") ? null : sinr.ToString();
                if (int.TryParse(sinr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv) && sv != 255)
                    s.SinrDb = sv * 0.5;
            }
        }

        if (TryReadField(tempR, "+GTSENRDTEMP", 1, out var temperature)) s.TempC = temperature.ToString();

        if (!string.IsNullOrEmpty(caR) && caR.Contains("PCC", StringComparison.Ordinal))
            foreach (Match x in CaRe().Matches(caR))
            {
                var f = x.Groups[2].Value.Split(',');
                var isPcc = x.Groups[1].Value == "PCC";
                if (!isPcc && (f.Length == 0 || f[0] != "2")) continue;

                var bi = isPcc ? 0 : 2;
                var ri = isPcc ? 3 : 5;
                if (f.Length <= Math.Max(bi, ri)
                    || !int.TryParse(f[bi], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bc)
                    || !int.TryParse(f[ri], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rb)) continue;
                if (rb > 0)
                {
                    string bl;
                    if (bc >= 100 && bc < 500) bl = "B" + (bc - 100);
                    else if (bc >= 501 && bc <= 509) bl = "n" + (bc - 500);
                    else if (bc >= 5010) bl = "n" + (bc - 5000);
                    else continue;
                    var mhz = rb == 6 ? "1.4" : (rb / 5).ToString(CultureInfo.InvariantCulture);
                    s.Carriers.Add(x.Groups[1].Value + " " + bl + " " + mhz + "MHz");
                }
            }

        return s;
    }

    private static bool TryReadIntField(string? response, string prefix, int fieldIndex, out int value)
    {
        if (TryReadField(response, prefix, fieldIndex, out var field))
            return int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        value = 0;
        return false;
    }

    private static bool TryReadField(string? response, string prefix, int fieldIndex, out ReadOnlySpan<char> field)
    {
        field = default;
        if (string.IsNullOrEmpty(response)) return false;
        var source = response.AsSpan();
        var prefixIndex = source.IndexOf(prefix.AsSpan(), StringComparison.Ordinal);
        if (prefixIndex < 0) return false;
        source = source.Slice(prefixIndex + prefix.Length);
        if (!source.IsEmpty && source[0] == ':') source = source.Slice(1);
        source = source.TrimStart();
        for (var index = 0;; index++)
        {
            var separator = source.IndexOfAny(',', '\r', '\n');
            var candidate = separator < 0 ? source : source.Slice(0, separator);
            candidate = candidate.Trim().Trim('"');
            if (index == fieldIndex)
            {
                field = candidate;
                return true;
            }

            if (separator < 0 || source[separator] != ',') return false;
            source = source.Slice(separator + 1);
        }
    }

    internal sealed class Snapshot
    {
        public bool HasSignal { get; internal set; }
        public int Pci { get; internal set; }
        public int Earfcn { get; internal set; }
        public int Rsrp { get; internal set; }
        public int Csq { get; internal set; } = -1;
        public double RsrqDb { get; internal set; } = double.NaN;
        public string? SinrIdx { get; internal set; }
        public double SinrDb { get; internal set; } = double.NaN;
        public string? TempC { get; internal set; }
        public string Band { get; internal set; } = "--";
        public List<string> Carriers { get; } = new(3);
    }
}