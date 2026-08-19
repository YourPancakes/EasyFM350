using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EasyFM350.Wpf.Backend.Network;

namespace EasyFM350.Wpf.Backend.Radio;

public static partial class InfoDecode
{
    [GeneratedRegex(@"\d{14,15}")]
    private static partial Regex IdentityNumberRegex();

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex QuotedValueRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\bV\d+(?:\.\d+)+\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\bP\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex PatchRegex();

    [GeneratedRegex(@"(\d{2})/(\d{2})/(\d{2}),(\d{2}):(\d{2}):(\d{2})([+-]\d+)?")]
    private static partial Regex ClockRegex();

    public static string Human(string cmd, List<string>? lines)
    {
        if (lines == null) return ErrorText(cmd);
        var body = string.Join("\n", lines);
        var f = Modem.Modem.Fields(body, PrefixOf(cmd));
        switch (cmd)
        {
            case "ATI": return Ati(lines, body, false);
            case "AT+CGSN": return HasAtiFields(lines) ? Ati(lines, body, true) : Identity(body);
            case "AT+CPIN?":
                if (f.Length > 0 && f[0] == "READY") return "READY";
                if (f.Length > 0 && f[0] == "SIM PIN") return "PIN required";
                if (f.Length > 0 && f[0] == "SIM PUK") return "PUK required";
                break;

            case "AT+GTSIMSELECT?": return f.Length > 0 ? f[0] == "0" ? "Slot 1" : f[0] == "1" ? "Slot 2" : body : body;
            case "AT+ESLOTSINFO?": return SimSlots(f, body);
            case "AT+ESIMS?": return f.Length > 0 ? f[0] == "0" ? "Disabled" : f[0] == "1" ? "Enabled" : body : body;
            case "AT+GTAPPVER?":
            case "AT+GTPKGVER?":
            case "AT+GTRFHWVER?": return f.Length > 0 ? string.Join(" ", f) : body;
            case "AT+GTBASELINEVER?": return Baseline(body);
            case "AT+ECAL?":
                return f.Length > 0 ? f[0] == "1" ? "Calibrated" : f[0] == "0" ? "Not calibrated" : body : body;
            case "AT+GTQUERYCALI?":
                return f.Length > 0
                    ? f[0] == "0" ? "Calibration check passed" : "Calibration diagnostic code " + f[0]
                    : body;
            case "AT+GTCURCAR?": return CarrierProfile(f, body);
            case "AT+GTLOCKCAR?": return CarrierLock(f, body);
            case "AT+GTUSBMODE?": return UsbMode(f, body);
            case "AT+EHVOLTE?": return f.Length > 0 ? f[0] == "0" ? "Off" : f[0] == "1" ? "On" : body : body;
            case "AT+COPS?": return Cops(f, body);
            case "AT+CEREG?":
            case "AT+CREG?":
            case "AT+CGREG?": return Registration(f, body);
            case "AT+CGATT?": return f.Length > 0 ? f[0] == "1" ? "Attached" : "Detached" : body;
            case "AT+ERAT?": return Erat(f, body);
            case "AT+CFUN?": return f.Length > 0 ? Cfun(f[0]) : body;
            case "AT+CSQ": return Csq(f, body);
            case "AT+RSRP?": return f.Length > 0 ? f[0] + " dBm" : body;
            case "AT+CESQ": return Cesq(f, body);
            case "AT+GTACT?": return Gtact(f, body);
            case "AT+E5GOPT?": return E5gopt(f, body);
            case "AT+GTCAINFO?": return f.Length == 0 ? "No CA active" : body;
            case "AT+GTCCINFO?": return GtccInfo(lines, body);
            case "AT+GTBANDCFG?": return BandCfg(lines, body);
            case "AT+GTDUALSIM?": return DualSim(f, body);
            case "AT+GTSENRDTEMP?": return SensorTemperature(f, body);
            case "AT+GTSHUTDOWNTEMP?": return ShutdownTemp(lines, body);
            case "AT+GTTXPOWER?": return TxPower(lines, body);
            case "AT+CBC": return Cbc(f, body);
            case "AT+CCLK?": return Cclk(f, body);
            case "AT+CGDCONT?":
                return lines.Count == 0
                    ? "Empty"
                    : string.Join("; ", lines.Select(l =>
                    {
                        var d = Modem.Modem.Fields(l, "+CGDCONT");
                        return d.Length > 2 ? d[0] + ": " + d[1] + " " + d[2] : l;
                    }));
            case "AT+CGPADDR": return Cgpaddr(lines, body);
            case "AT+CFSN": return f.Length > 0 && f[0].Length > 0 ? string.Join(" ", f) : "Empty";
            case "AT+EGMR=0,10":
                return Identity(body);
        }

        return body.Length > 0 ? body : "OK";
    }

    private static string ErrorText(string cmd)
    {
        switch (cmd)
        {
            case "AT+CIMI":
            case "AT+CCID":
            case "AT+CPIN?": return "No SIM";
            case "AT+RSRP?":
            case "AT+GTSENRDTEMP?":
            case "AT+GTCCINFO?": return "N/A";
            case "AT+CGPADDR": return "No connection";
            default: return "ERROR";
        }
    }

    private static string PrefixOf(string cmd)
    {
        if (cmd.Length < 3) return string.Empty;
        return "+" + cmd.Substring(3).TrimEnd('?', '=');
    }

    private static string Identity(string body)
    {
        var number = Modem.Modem.Number(body);
        if (number != null) return number;
        var match = IdentityNumberRegex().Match(body);
        return match.Success ? match.Value : body;
    }

    private static string Baseline(string body)
    {
        var parts = new List<string>();
        foreach (Match m in QuotedValueRegex().Matches(body))
        {
            var v = WhitespaceRegex().Replace(m.Groups[1].Value, " ").Trim();
            var sp = v.IndexOf(' ');
            if (sp > 0 && sp + 2 < v.Length && v[sp + 1] == '_') v = v.Substring(sp + 2);
            if (v.Length > 0) parts.Add(v);
        }

        if (parts.Count == 0) return body;
        string? version = null, patch = null;
        foreach (var part in parts)
        {
            if (version == null)
            {
                var match = VersionRegex().Match(part);
                if (match.Success) version = match.Value;
            }

            if (patch == null)
            {
                var match = PatchRegex().Match(part);
                if (match.Success) patch = match.Value;
            }
        }

        if (version != null && patch != null) return version + " (" + patch + ")";
        return version ?? patch ?? parts[0];
    }

    private static string SimSlots(string[] fields, string body)
    {
        if (fields.Length == 0 || !int.TryParse(fields[0], out var count) || count < 1) return body;
        var parts = new List<string> { count + (count == 1 ? " SIM slot" : " SIM slots") };
        const int fieldsPerSlot = 6;
        for (var slot = 0; slot < count; slot++)
        {
            var start = 1 + slot * fieldsPerSlot;
            if (start >= fields.Length) break;
            var status = fields[start];
            string text;
            if (status.Contains("CME ERROR: 10", StringComparison.OrdinalIgnoreCase))
                text = "Slot " + (slot + 1) + ": no SIM";
            else if (status.Contains("EMPTY_EUICC", StringComparison.OrdinalIgnoreCase))
                text = "Slot " + (slot + 1) + " (eSIM): no active profile";
            else if (status.StartsWith("+CPIN:", StringComparison.OrdinalIgnoreCase))
                text = "Slot " + (slot + 1) + ": " + status.Substring(6).Trim();
            else
                text = "Slot " + (slot + 1) + ": " + (status.Length == 0 ? "Empty" : status);

            var iccidIndex = start + 4;
            if (iccidIndex < fields.Length && fields[iccidIndex].Length > 0)
                text += ", ICCID " + fields[iccidIndex];
            parts.Add(text);
        }

        return parts.Count == 1 ? parts[0] : parts[0] + ": " + string.Join("; ", parts.Skip(1));
    }

    private static string CarrierProfile(string[] fields, string body)
    {
        if (fields.Length == 0) return body;
        if (fields[0] == "65535") return "Operator firmware profile: none";
        if (fields.Length > 1 && fields[1].Length > 0) return fields[1] + " (profile " + fields[0] + ")";
        return "Carrier profile " + fields[0];
    }

    private static string CarrierLock(string[] fields, string body)
    {
        if (fields.Length == 0) return body;
        if (fields[0] == "0") return "Carrier lock disabled";
        return fields.Length > 1 ? "Carrier lock enabled (profile " + fields[1] + ")" : "Carrier lock enabled";
    }

    private static string UsbMode(string[] fields, string body)
    {
        if (fields.Length == 0) return body;
        return fields[0] switch
        {
            "41" => "RNDIS network interface",
            "40" => "USB profile 40",
            _ => "USB profile " + fields[0]
        };
    }

    private static string DualSim(string[] fields, string body)
    {
        if (fields.Length == 0) return body;
        var mode = fields[0] == "0" ? "Dual-SIM disabled" :
            fields[0] == "1" ? "Dual-SIM enabled" : "Dual-SIM mode " + fields[0];
        if (fields.Length > 2) mode += " (" + fields[1] + ": " + HumanServiceState(fields[2]) + ")";
        return mode;
    }

    private static string HumanServiceState(string value)
    {
        return value.Equals("NO SERVICE", StringComparison.OrdinalIgnoreCase) ? "no service" : value;
    }

    private static string Ati(List<string> lines, string body, bool includeIdentity)
    {
        string? man = null, model = null, rev = null, svn = null, imei = null;
        foreach (var l in lines)
        {
            var i = l.IndexOf(':');
            if (i < 0) continue;
            var k = l.Substring(0, i).Trim();
            var v = l.Substring(i + 1).Trim();
            if (k.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase)) man = v;
            else if (k.Equals("Model", StringComparison.OrdinalIgnoreCase)) model = v;
            else if (k.Equals("Revision", StringComparison.OrdinalIgnoreCase)) rev = v;
            else if (k.Equals("SVN", StringComparison.OrdinalIgnoreCase)) svn = v;
            else if (k.Equals("IMEI", StringComparison.OrdinalIgnoreCase)) imei = Modem.Modem.Number(v) ?? v;
        }

        if (model == null && rev == null && imei == null) return body;
        if (man != null)
            man = man.Replace(" Wireless Inc.", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(" Wireless", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var parts = new List<string>(4);
        var product = ((man != null ? man + " " : string.Empty) + model).Trim();
        if (product.Length > 0) parts.Add(product);
        if (rev != null) parts.Add("firmware " + rev);
        if (includeIdentity && svn != null) parts.Add("SVN " + svn);
        if (includeIdentity && imei != null) parts.Add("IMEI " + imei);
        return parts.Count > 0 ? string.Join(", ", parts) : body;
    }

    private static bool HasAtiFields(List<string> lines)
    {
        return lines.Any(static line => line.StartsWith("Manufacturer:", StringComparison.OrdinalIgnoreCase)
                                        || line.StartsWith("Model:", StringComparison.OrdinalIgnoreCase)
                                        || line.StartsWith("Revision:", StringComparison.OrdinalIgnoreCase)
                                        || line.StartsWith("SVN:", StringComparison.OrdinalIgnoreCase));
    }

    private static string Cops(string[] f, string body)
    {
        if (f.Length < 4) return body;
        if (f[2].Length == 0) return "Not registered";
        return f[2] + " (" + ActName(f[3]) + ")";
    }

    private static string RegStat(string s)
    {
        switch (s)
        {
            case "0": return "Not registered";
            case "1": return "Registered (home)";
            case "2": return "Searching…";
            case "3": return "Denied";
            case "4": return "No service";
            case "5": return "Registered (roaming)";
            default: return s;
        }
    }

    private static string ActName(string a)
    {
        switch (a)
        {
            case "0": return "GSM";
            case "2": return "UTRAN";
            case "4": return "UTRAN+HSDPA";
            case "5": return "UTRAN+HSUPA";
            case "6": return "HSPA";
            case "7": return "LTE";
            case "9": return "LTE (NB-S1)";
            case "10": return "LTE→5GCN";
            case "11": return "NR SA";
            case "12": return "NR→EPS";
            case "13": return "NG-RAN";
            case "14": return "5G NSA (EN-DC)";
            default: return "ACT " + a;
        }
    }

    private static string Registration(string[] fields, string body)
    {
        if (fields.Length == 0) return body;
        var status = fields.Length > 1 ? fields[1] : fields[0];
        return RegStat(status);
    }

    private static string Erat(string[] f, string body)
    {
        if (f.Length < 1) return body;
        var s = f[0] == "255" ? "No service" : ActName(f[0]);
        if (f.Length > 2)
        {
            string m;
            switch (f[2])
            {
                case "1": m = "3G only"; break;
                case "3": m = "LTE only"; break;
                case "5": m = "3G and LTE"; break;
                case "15": m = "NR only"; break;
                case "17": m = "3G and 5G"; break;
                case "19": m = "LTE and 5G"; break;
                case "21": m = "3G, LTE and 5G"; break;
                default: m = "mode " + f[2]; break;
            }

            s += " (enabled: " + m + ")";
        }

        return s;
    }

    private static string Cfun(string v)
    {
        switch (v)
        {
            case "0": return "Minimum functionality";
            case "1": return "Full modem functionality";
            case "4": return "Airplane mode";
            default: return v;
        }
    }

    private static string Csq(string[] f, string body)
    {
        int rssi;
        if (f.Length == 0 || !int.TryParse(f[0], out rssi)) return body;
        if (rssi == 99) return "No signal";
        if (rssi is < 0 or > 31) return body;
        return (-113 + 2 * rssi).ToString(CultureInfo.InvariantCulture) + " dBm (" + rssi + "/31)";
    }

    private static string Cesq(string[] f, string body)
    {
        if (f.Length < 6) return body;
        int rsrq, rsrp;
        var parts = new List<string>();
        if (int.TryParse(f[5], out rsrp) && rsrp != 255 && rsrp != 99)
            parts.Add("RSRP " + (rsrp - 140) + " dBm");
        if (int.TryParse(f[4], out rsrq) && rsrq != 255 && rsrq != 99)
            parts.Add("RSRQ " + (rsrq / 2.0 - 19.5).ToString("0.#", CultureInfo.InvariantCulture) + " dB");
        return parts.Count > 0 ? string.Join(", ", parts) : "No measurement";
    }

    private static string Gtact(string[] fields, string body)
    {
        BandPlan.ParseGtact(fields, out var rat, out var umts, out var lte, out var nr);
        if (fields.Length < 4) return body;
        string mode;
        if (MatchesRat(rat, BandPlan.RAT_AUTO)) mode = "Auto (5G NSA/SA + LTE + 3G)";
        else if (MatchesRat(rat, BandPlan.RAT_5G4G)) mode = "5G NSA + LTE";
        else if (MatchesRat(rat, BandPlan.RAT_LTE)) mode = "LTE only";
        else if (MatchesRat(rat, BandPlan.RAT_3G)) mode = "3G only";
        else if (MatchesRat(rat, BandPlan.RAT_5GSA)) mode = "5G SA only";
        else mode = "RAT " + string.Join(",", rat);
        var result = new StringBuilder(mode, mode.Length + 64).Append(", bands:");
        AppendBandCount(result, "3G", umts.Count);
        AppendBandCount(result, "LTE", lte.Count);
        AppendBandCount(result, "5G NR", nr.Count);
        if (result[result.Length - 1] == ',') result.Length--;
        return result.ToString();
    }

    private static bool MatchesRat(int[] value, int[] expected)
    {
        return value.Length == 3 && value[0] == expected[0] && value[1] == expected[1] && value[2] == expected[2];
    }

    private static void AppendBandCount(StringBuilder output, string label, int count)
    {
        if (count == 0) return;
        output.Append(' ').Append(label).Append(' ').Append(count).Append(count == 1 ? " band" : " bands").Append(',');
    }

    private static string Cgpaddr(List<string> lines, string body)
    {
        foreach (var line in lines)
        {
            var f = Modem.Modem.Fields(line, "+CGPADDR");
            if (f.Length < 2) continue;
            var parts = new List<string>();
            if (f[1].Length > 0) parts.Add(f[1]);
            if (f.Length > 2 && f[2].Length > 0)
                parts.Add("IPv6 " + (NetConfig.DottedToIpv6(f[2]) ?? f[2]));
            if (parts.Count > 0) return string.Join(", ", parts);
        }

        return body;
    }

    private static string GtccInfo(List<string> lines, string body)
    {
        var cells = new List<string>(lines.Count);
        var sawData = false;
        foreach (var raw in lines)
        {
            var line = raw;
            if (line.StartsWith("+GTCCINFO", StringComparison.Ordinal))
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                line = line.Substring(colon + 1).Trim();
            }

            if (line.Length == 0) continue;
            sawData = true;
            var cell = GtccCell(line.Split(','));
            if (cell != null) cells.Add(cell);
        }

        return cells.Count > 0 ? string.Join("\n", cells) : sawData ? body : "No cells";
    }

    private static string? GtccCell(string[] f)
    {
        for (var i = 0; i < f.Length; i++) f[i] = f[i].Trim().Trim('"');
        if (f.Length < 8) return null;
        var rat = f[1];
        var sb = new StringBuilder(96);
        sb.Append(f[0] == "1" ? "Serving" : f[0] == "2" ? "Neighbor" : "Cell " + f[0]);
        sb.Append(": ").Append(rat == "2" ? "3G" : rat == "4" ? "LTE" : rat == "9" ? "NR" : "RAT " + rat);

        string? band = null;
        var hasBandCode = false;
        if (f.Length > 8 && int.TryParse(f[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bc))
        {
            if (bc >= 100 && bc < 500)
            {
                band = "B" + (bc - 100);
                hasBandCode = true;
            }
            else if (bc >= 501 && bc <= 509)
            {
                band = "n" + (bc - 500);
                hasBandCode = true;
            }
            else if (bc >= 5010)
            {
                band = "n" + (bc - 5000);
                hasBandCode = true;
            }
        }

        if (band == null && rat == "4"
                         && int.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var arfcn))
            band = BandPlan.BandFromEarfcn(arfcn);
        if (band != null && (band == "B?" || band[0] == 'E')) band = null;
        if (band != null && band.Length > 1 && band[0] == 'B'
            && int.TryParse(band.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lteBand))
            band = BandPlan.BandLabel(lteBand);
        if (band != null) sb.Append(' ').Append(band);

        if (int.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
            sb.Append(rat == "2" ? ", UARFCN " : rat == "9" ? ", ARFCN " : ", EARFCN ").Append(channel);
        if (rat == "4" && hasBandCode && f.Length > 9
            && int.TryParse(f[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rb) && rb > 0)
            sb.Append(", ").Append(rb == 6 ? "1.4" : (rb / 5).ToString(CultureInfo.InvariantCulture)).Append(" MHz");
        if (f[7].Length > 0) sb.Append(rat == "2" ? ", PSC " : ", PCI ").Append(f[7]);

        if (f[2].Length > 0 && f[3].Length > 0)
        {
            var mnc = f[3].Length == 1 ? "0" + f[3] : f[3];
            sb.Append(", ").Append(f[2]).Append("-").Append(mnc);
        }

        if (f.Length > 4 && IsHexData(f[4])
                         && long.TryParse(f[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var tac))
            sb.Append(", TAC ").Append(tac);
        if (f.Length > 5 && IsHexData(f[5])
                         && long.TryParse(f[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cellId))
            sb.Append(", CellID ").Append(cellId);

        if (rat != "2" && f.Length >= 12)
        {
            var rsrpOffset = rat == "9" ? 157 : 140;
            if (int.TryParse(f[f.Length - 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rx0)
                && int.TryParse(f[f.Length - 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rx1)
                && rx0 > 0 && rx0 < 255 && rx1 > 0 && rx1 < 255)
                sb.Append(", RSRP ").Append(rx0 == rx1
                        ? (rx0 - rsrpOffset).ToString(CultureInfo.InvariantCulture)
                        : (rx0 - rsrpOffset).ToString(CultureInfo.InvariantCulture) + "/" +
                          (rx1 - rsrpOffset).ToString(CultureInfo.InvariantCulture))
                    .Append(" dBm");
            if (int.TryParse(f[f.Length - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rq) &&
                rq != 255)
                sb.Append(", RSRQ ").Append((rat == "9" ? rq * 0.5 - 43.5 : rq * 0.5 - 19.5)
                    .ToString("0.#", CultureInfo.InvariantCulture)).Append(" dB");
            if (int.TryParse(f[f.Length - 4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv) &&
                sv != 255)
                sb.Append(", SINR ").Append((rat == "9" ? sv * 0.5 - 23 : sv * 0.5)
                    .ToString("0.#", CultureInfo.InvariantCulture)).Append(" dB");
        }

        return sb.ToString();
    }

    private static bool IsHexData(string value)
    {
        var trimmed = value.TrimStart('0');
        if (trimmed.Length == 0) return false;
        foreach (var c in trimmed)
            if (c != 'F' && c != 'f')
                return true;
        return false;
    }

    private static string E5gopt(string[] f, string body)
    {
        int v;
        if (f.Length == 0 || !int.TryParse(f[0], out v)) return body;
        var parts = new List<string>();
        if ((v & 1) != 0) parts.Add("LTE");
        if ((v & 2) != 0) parts.Add("5G SA");
        if ((v & 4) != 0) parts.Add("5G NSA");
        if ((v & ~7) != 0 || parts.Count == 0) return body;
        return string.Join(" + ", parts);
    }

    private static string BandCfg(List<string> lines, string body)
    {
        var off = new List<string>();
        var uplinkOnlyOff = new List<int>();
        foreach (var l in lines)
        {
            var p = l.Split(',');
            if (p.Length < 3) continue;
            if (p[2].Trim() == "1") continue;
            if (!int.TryParse(p[1].Trim(), out var band)) continue;
            switch (p[0].Trim())
            {
                case "0" when Array.IndexOf(BandPlan.UmtsAll, band) >= 0: off.Add("3G B" + band); break;
                case "1" when Array.IndexOf(BandPlan.LteAll, band) >= 0: off.Add("B" + band); break;
                case "2" when Array.IndexOf(BandPlan.NrAll, band) >= 0: off.Add("n" + band); break;
                case "2" when band is >= 80 and <= 84: uplinkOnlyOff.Add(band); break;
            }
        }

        if (off.Count == 0 && uplinkOnlyOff.Count == 0) return "All supported bands enabled";
        if (off.Count == 0) return "All regular bands enabled (5G uplink-only n80–n84 disabled)";
        var value = "Disabled: " + string.Join(", ", off);
        return uplinkOnlyOff.Count == 0 ? value : value + " (5G uplink-only n80–n84 also disabled)";
    }

    private static string ShutdownTemp(List<string> lines, string body)
    {
        var cutoffs = new List<int>();
        foreach (var l in lines)
        {
            var f = Modem.Modem.Fields(l, "+GTSHUTDOWNTEMP");
            if (f.Length > 1 && int.TryParse(f[1], out var millidegrees))
                cutoffs.Add(millidegrees / 1000);
        }

        if (cutoffs.Count == 0) return body;
        var minimum = cutoffs.Min();
        var maximum = cutoffs.Max();
        return minimum == maximum
            ? minimum.ToString(CultureInfo.InvariantCulture) + " °C"
            : minimum.ToString(CultureInfo.InvariantCulture) + "–"
                                                             + maximum.ToString(CultureInfo.InvariantCulture) + " °C";
    }

    private static string TxPower(List<string> lines, string body)
    {
        var parts = new List<string>();
        foreach (var l in lines)
        {
            var f = Modem.Modem.Fields(l, "+GTTXPOWER");
            if (f.Length == 0) continue;
            parts.Add(f[0] == "-127" ? "Not transmitting" : f[0] + " dBm");
        }

        if (parts.Count == 0) return body;
        return parts.All(p => p == parts[0]) ? parts[0] : string.Join(", ", parts);
    }

    private static string Cbc(string[] f, string body)
    {
        int mv;
        if (f.Length > 1 && int.TryParse(f[1], out mv))
            return (mv / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " V";
        return body;
    }

    private static string Cclk(string[] f, string body)
    {
        var s = body.Replace("\"", "");
        var m = ClockRegex().Match(s);
        if (!m.Success) return body;
        var tz = ClockTimeZone(m.Groups[7].Value);
        return "20" + m.Groups[1].Value + "-" + m.Groups[2].Value + "-" + m.Groups[3].Value
               + " " + m.Groups[4].Value + ":" + m.Groups[5].Value + ":" + m.Groups[6].Value + tz;
    }

    private static string ClockTimeZone(string value)
    {
        if (value.Length == 0 || !int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                out var quarters))
            return string.Empty;
        var offset = TimeSpan.FromMinutes(quarters * 15);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return " UTC" + sign + offset.Hours.ToString("00", CultureInfo.InvariantCulture)
               + ":" + offset.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    private static string SensorTemperature(string[] fields, string body)
    {
        if (fields.Length < 2 || !int.TryParse(fields[1], out var celsius)) return body;
        return "Sensor " + fields[0] + ": " + celsius.ToString(CultureInfo.InvariantCulture) + " C";
    }
}