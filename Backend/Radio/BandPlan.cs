using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EasyFM350.Wpf.Backend.Radio;

public static class BandPlan
{
    public static readonly int[] RAT_AUTO = { 20, 6, 3 };
    public static readonly int[] RAT_5G4G = { 17, 6, 3 };
    public static readonly int[] RAT_LTE = { 2, 3, 3 };
    public static readonly int[] RAT_3G = { 1, 2, 2 };
    public static readonly int[] RAT_5GSA = { 14, 6, 6 };

    public static readonly int[] UmtsAll = { 1, 2, 4, 5, 8 };

    public static readonly int[] LteAll =
    {
        1, 2, 3, 4, 5, 7, 8, 12, 13, 14, 17, 18, 19, 20, 25, 26, 28, 29, 30, 32, 34, 38, 39, 40, 41, 42, 43, 46, 48, 66,
        71
    };

    public static readonly int[] NrAll =
    {
        1, 2, 3, 5, 7, 8, 20, 25, 28, 30, 38, 40, 41, 48, 66, 71, 77, 78, 79
    };

    public static readonly int[] MtsTrioLte = { 1, 3, 7 };

    private static readonly int[] EarfcnUpperBounds =
    {
        599, 1199, 1949, 2399, 2649, 2749, 3449, 3799, 4149, 4749, 4949, 5009, 5179, 5279, 5379, 5729,
        5849, 5999, 6149, 6449, 6599, 7399, 7499, 7699, 8039, 8689, 9039, 9209, 9659, 9769, 9869, 9919,
        10359, 35999, 36199, 36349, 37749, 38249, 38649, 39649, 41589, 43589, 45589, 46789, 54539, 55239,
        56739, 66435, 67335, 68585, 68935
    };

    private static readonly string[] EarfcnLabels =
    {
        "B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8", "B9", "B10", "B11", "B?", "B12", "B13", "B14", "B?",
        "B17", "B18", "B19", "B20", "B21", "B22", "B?", "B23", "B24", "B25", "B26", "B?", "B28", "B29", "B30", "B?",
        "B32", "B?", "B33", "B34", "B?", "B38", "B39", "B40", "B41", "B42", "B43", "B?", "B46", "B?", "B48", "B?",
        "B66", "B?", "B71"
    };

    public static int LteCode(int band)
    {
        return 100 + band;
    }

    public static int NrCode(int n)
    {
        return n >= 10 ? 5000 + n : 500 + n;
    }

    public static string BuildGtact(int[] rat, IEnumerable<int> lte, IEnumerable<int> nr)
    {
        ArgumentNullException.ThrowIfNull(rat);
        ArgumentNullException.ThrowIfNull(lte);
        ArgumentNullException.ThrowIfNull(nr);

        var lteBands = lte.ToArray();
        var nrBands = nr.ToArray();
        Array.Sort(lteBands);
        Array.Sort(nrBands);

        var command = new StringBuilder(192).Append("AT+GTACT=");
        var first = true;
        foreach (var value in rat) AppendValue(command, value, ref first);
        AppendValue(command, 1, ref first);
        foreach (var value in UmtsAll)
            if (value != 1)
                AppendValue(command, value, ref first);
        foreach (var value in lteBands) AppendValue(command, LteCode(value), ref first);
        foreach (var value in nrBands) AppendValue(command, NrCode(value), ref first);
        return command.ToString();
    }

    private static void AppendValue(StringBuilder command, int value, ref bool first)
    {
        if (!first) command.Append(',');
        command.Append(value.ToString(CultureInfo.InvariantCulture));
        first = false;
    }

    public static void ParseGtact(string resp, out int[] rat, out List<int> lte, out List<int> nr)
    {
        ParseGtact(Modem.Modem.Fields(resp, "+GTACT"), out rat, out _, out lte, out nr);
    }

    public static void ParseGtact(string[] fields, out int[] rat, out List<int> umts, out List<int> lte,
        out List<int> nr)
    {
        rat = new int[3];
        umts = new List<int>(4);
        lte = new List<int>(32);
        nr = new List<int>(20);
        if (fields.Length < 4) return;
        for (var index = 0; index < 3; index++)
            if (!int.TryParse(fields[index], out rat[index]))
                rat[index] = 0;
        var start = fields[3] == "1" ? 4 : 3;
        for (var index = start; index < fields.Length; index++)
        {
            if (!int.TryParse(fields[index], out var value)) continue;
            if (value >= 5000) nr.Add(value - 5000);
            else if (value >= 500) nr.Add(value - 500);
            else if (value >= 100) lte.Add(value - 100);
            else if (value > 0) umts.Add(value);
        }
    }

    public static string BandLabel(int lteBand)
    {
        switch (lteBand)
        {
            case 1: return "B1 (2100)";
            case 3: return "B3 (1800)";
            case 7: return "B7 (2600)";
            case 8: return "B8 (900)";
            case 20: return "B20 (800)";
            case 38: return "B38 (2600 TDD)";
            case 40: return "B40 (2300 TDD)";
            case 41: return "B41 (2500 TDD)";
            default: return "B" + lteBand;
        }
    }

    public static string BandFromEarfcn(int earfcn)
    {
        if (earfcn < 0) return "E" + earfcn;
        var index = Array.BinarySearch(EarfcnUpperBounds, earfcn);
        if (index < 0) index = ~index;
        return index < EarfcnLabels.Length ? EarfcnLabels[index] : "E" + earfcn;
    }
}