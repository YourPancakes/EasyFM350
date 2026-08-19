using System;
using System.Collections.Generic;
using EasyFM350.Wpf.Backend.Modem;

namespace EasyFM350.Wpf.Backend.Radio;

internal sealed class ModemSettingsService
{
    private readonly Modem.Modem _modem;

    public ModemSettingsService(Modem.Modem modem)
    {
        _modem = modem ?? throw new ArgumentNullException(nameof(modem));
    }

    public InitialSettings ReadInitial()
    {
        var bands = ReadBands();
        var e5gOption = ReadE5gOption();
        var pdp = ReadPdp();
        return new InitialSettings(bands, e5gOption, pdp);
    }

    public BandSettings ReadBands()
    {
        var fields = Modem.Modem.Fields(_modem.Send("AT+GTACT?"), "+GTACT");
        BandPlan.ParseGtact(fields, out var rat, out _, out var lte, out var nr);
        return new BandSettings(fields.Length >= 4 && Array.TrueForAll(rat, value => value > 0),
            rat, lte.ToArray(), nr.ToArray());
    }

    public int ReadE5gOption()
    {
        var fields = Modem.Modem.Fields(_modem.Send("AT+E5GOPT?"), "+E5GOPT");
        return fields.Length > 0 && int.TryParse(fields[0], out var value) ? value : -1;
    }

    public PdpSettings ReadPdp()
    {
        return ReadSnapshot().Selected;
    }

    public PdpSettings ResolvePdpForConfiguration(string desiredApn, string desiredPdpType)
    {
        desiredApn = ApnPolicy.NormalizeForConfiguration(desiredApn);
        var desiredType = PdpProtocol.ToModemValue(desiredPdpType);
        var snapshot = ReadSnapshot();

        for (var index = 0; index < snapshot.Active.Count; index++)
        {
            var active = snapshot.Active[index];
            var configured = PdpContext.FindConfigured(snapshot.Configured, active.Cid);
            if (IsIms(active, configured)) continue;
            var effectiveType = configured?.PdpType ?? active.PdpType;
            if (string.Equals(active.Apn, desiredApn, StringComparison.OrdinalIgnoreCase)
                && string.Equals(effectiveType ?? string.Empty, desiredType, StringComparison.OrdinalIgnoreCase))
                return CreateSettings(active.Cid, configured, active);
        }

        for (var index = 0; index < snapshot.Configured.Count; index++)
        {
            var configured = snapshot.Configured[index];
            if (configured.IsImsSignalling == true) continue;
            if (!string.Equals(configured.PdpType, desiredType, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(configured.Apn, desiredApn, StringComparison.OrdinalIgnoreCase)) continue;
            return CreateSettings(configured.Cid, configured, PdpContext.FindActive(snapshot.Active, configured.Cid));
        }

        var availableCid = ReadFirstAvailableCid(snapshot.Configured, snapshot.Active);
        if (availableCid > 0)
            return new PdpSettings(availableCid, null, null, null, false);

        if (snapshot.Selected.Cid > 0 && !snapshot.Selected.IsActive)
            return snapshot.Selected;

        throw new InvalidOperationException(
            "CGDCONT: no free PDP context id; refusing to overwrite an unrelated active context");
    }

    private PdpSnapshot ReadSnapshot()
    {
        var activeResponse = _modem.Send("AT+CGCONTRDP", 3000, true);
        var configuredResponse = _modem.Send("AT+CGDCONT?");
        var active = PdpContext.ParseActive(activeResponse);
        var configured = PdpContext.ParseConfigured(configuredResponse);
        var cid = PdpContext.SelectDataContext(configured, active);
        var selected = cid > 0
            ? CreateSettings(cid, PdpContext.FindConfigured(configured, cid), PdpContext.FindActive(active, cid))
            : new PdpSettings(0, null, null, null, false);
        return new PdpSnapshot(configured, active, selected);
    }

    private int ReadFirstAvailableCid(
        IReadOnlyList<PdpContext.Configured> configured,
        IReadOnlyList<PdpContext.Active> active)
    {
        var used = new HashSet<int>();
        for (var index = 0; index < configured.Count; index++) used.Add(configured[index].Cid);
        for (var index = 0; index < active.Count; index++) used.Add(active[index].Cid);

        var response = _modem.Send("AT+CGDCONT=?", 3000, true);
        if (string.IsNullOrEmpty(response)) return 0;
        foreach (var line in response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("+CGDCONT", StringComparison.Ordinal)) continue;
            var open = line.IndexOf('(');
            var close = open < 0 ? -1 : line.IndexOf(')', open + 1);
            if (open < 0 || close <= open + 1) continue;

            var cidSpec = line.Substring(open + 1, close - open - 1);
            foreach (var rawPart in cidSpec.Split(','))
            {
                var part = rawPart.Trim();
                if (part.Length == 0) continue;
                var dash = part.IndexOf('-');
                if (dash < 0)
                {
                    if (int.TryParse(part, out var single) && single > 0 && !used.Contains(single)) return single;
                    continue;
                }

                if (!int.TryParse(part.Substring(0, dash).Trim(), out var first)
                    || !int.TryParse(part.Substring(dash + 1).Trim(), out var last)
                    || last < first) continue;
                for (var cid = Math.Max(1, first); cid <= last; cid++)
                    if (!used.Contains(cid))
                        return cid;
            }
        }

        return 0;
    }

    private static bool IsIms(PdpContext.Active active, PdpContext.Configured? configured)
    {
        return (active.IsImsSignalling ?? configured?.IsImsSignalling) == true;
    }

    private static PdpSettings CreateSettings(int cid, PdpContext.Configured? configured, PdpContext.Active? active)
    {
        var configuredApn = configured?.Apn;
        var activeApn = active?.Apn;
        // The editable APN is a configuration value. Never feed a network-expanded
        // active APN from CGCONTRDP back into CGDCONT on a later reconnect.
        var displayApn = configured != null ? configuredApn : activeApn;
        return new PdpSettings(cid, configured?.PdpType ?? active?.PdpType, displayApn, configuredApn, active != null)
        {
            ActiveApn = activeApn
        };
    }

    private sealed record PdpSnapshot(
        IReadOnlyList<PdpContext.Configured> Configured,
        IReadOnlyList<PdpContext.Active> Active,
        PdpSettings Selected);

    internal sealed record InitialSettings(BandSettings Bands, int E5gOption, PdpSettings Pdp);

    internal sealed record BandSettings(bool HasValues, int[] Rat, int[] Lte, int[] Nr);

    internal sealed record PdpSettings(int Cid, string? Type, string? Apn, string? ConfiguredApn, bool IsActive)
    {
        public string? ActiveApn { get; init; }
    }
}