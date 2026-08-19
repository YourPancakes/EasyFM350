using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EasyFM350.Wpf.Backend.Radio;

internal static partial class SignalParser
{
    [GeneratedRegex(@"(PCC|SCC \d+):([\d,]+)")]
    private static partial Regex CaRe();

    public static Snapshot Parse(
        string? rsrpResponse,
        string? csqResponse,
        string? cellResponse,
        string? carrierAggregationResponse,
        string? temperatureResponse)
    {
        var snapshot = new Snapshot();
        if (TryReadIntField(rsrpResponse, "+RSRP", 0, out var pci)
            && TryReadIntField(rsrpResponse, "+RSRP", 1, out var earfcn)
            && TryReadIntField(rsrpResponse, "+RSRP", 2, out var rsrp))
        {
            snapshot.HasSignal = true;
            snapshot.Pci = pci;
            snapshot.Earfcn = earfcn;
            snapshot.Rsrp = rsrp;
            snapshot.Band = BandPlan.BandFromEarfcn(earfcn);
        }

        if (TryReadIntField(csqResponse, "+CSQ", 0, out var csq)) snapshot.Csq = csq == 99 ? -1 : csq;

        if (TryReadField(cellResponse, "+GTCCINFO", 1, out var rat)
            && TryReadField(cellResponse, "+GTCCINFO", 10, out var sinr))
        {
            var hasRsrq = TryReadIntField(cellResponse, "+GTCCINFO", 13, out var rawRsrq);
            if (rat.SequenceEqual("9"))
            {
                if (hasRsrq && rawRsrq != 255) snapshot.RsrqDb = rawRsrq * 0.5 - 43.5;
                snapshot.SinrIdx = sinr.SequenceEqual("255") ? null : sinr.ToString();
                if (int.TryParse(sinr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawSinr) && rawSinr != 255)
                    snapshot.SinrDb = rawSinr * 0.5 - 23;
            }
            else if (!rat.SequenceEqual("2"))
            {
                if (hasRsrq && rawRsrq != 255) snapshot.RsrqDb = rawRsrq * 0.5 - 19.5;
                snapshot.SinrIdx = sinr.SequenceEqual("255") ? null : sinr.ToString();
                if (int.TryParse(sinr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawSinr) && rawSinr != 255)
                    snapshot.SinrDb = rawSinr * 0.5;
            }
        }

        if (TryReadField(temperatureResponse, "+GTSENRDTEMP", 1, out var temperature)) snapshot.TempC = temperature.ToString();

        if (!string.IsNullOrEmpty(carrierAggregationResponse) && carrierAggregationResponse.Contains("PCC", StringComparison.Ordinal))
            foreach (Match carrierMatch in CaRe().Matches(carrierAggregationResponse))
            {
                var fields = carrierMatch.Groups[2].Value.Split(',');
                var carrierKind = carrierMatch.Groups[1].Value;
                var isPrimaryCarrier = carrierKind == "PCC";
                if (!isPrimaryCarrier && (fields.Length == 0 || fields[0] != "2")) continue;

                var bandCodeIndex = isPrimaryCarrier ? 0 : 2;
                var resourceBlocksIndex = isPrimaryCarrier ? 3 : 5;
                if (fields.Length <= Math.Max(bandCodeIndex, resourceBlocksIndex)
                    || !int.TryParse(
                        fields[bandCodeIndex],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var bandCode)
                    || !int.TryParse(
                        fields[resourceBlocksIndex],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var resourceBlocks))
                    continue;

                if (resourceBlocks <= 0) continue;

                string bandLabel;
                if (bandCode >= 100 && bandCode < 500) bandLabel = "B" + (bandCode - 100);
                else if (bandCode >= 501 && bandCode <= 509) bandLabel = "n" + (bandCode - 500);
                else if (bandCode >= 5010) bandLabel = "n" + (bandCode - 5000);
                else continue;

                var bandwidthMhz = resourceBlocks == 6
                    ? "1.4"
                    : (resourceBlocks / 5).ToString(CultureInfo.InvariantCulture);
                snapshot.Carriers.Add(carrierKind + " " + bandLabel + " " + bandwidthMhz + "MHz");
            }

        return snapshot;
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