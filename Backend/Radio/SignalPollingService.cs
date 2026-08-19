using System;
using EasyFM350.Wpf.Backend.Modem;

namespace EasyFM350.Wpf.Backend.Radio;

internal sealed class SignalPollingService
{
    private readonly Modem.Modem _modem;

    public SignalPollingService(Modem.Modem modem)
    {
        _modem = modem ?? throw new ArgumentNullException(nameof(modem));
    }

    public PollResult Read(bool includeTemperature, int pdpContextId, bool includePdn)
    {
        var rsrp = _modem.Send("AT+RSRP?", 2000, true);
        var csq = _modem.Send("AT+CSQ", 1500, true);
        var cell = _modem.Send("AT+GTCCINFO?", 3000, true);
        var carriers = _modem.Send("AT+GTCAINFO?", 2000, true);
        var temperature = includeTemperature ? _modem.Send("AT+GTSENRDTEMP?", 1500, true) : null;
        var pdn = includePdn && pdpContextId > 0
            ? _modem.Send(ModemCommands.ReadPdpAddress(pdpContextId), 1500, true)
            : null;
        return new PollResult(SignalParser.Parse(rsrp, csq, cell, carriers, temperature), pdn);
    }

    public string ReadHealth(int pdpContextId, bool includePdn)
    {
        return _modem.Send(includePdn && pdpContextId > 0 ? ModemCommands.ReadPdpAddress(pdpContextId) : "AT", 1500,
            true);
    }

    internal sealed class PollResult
    {
        internal PollResult(SignalParser.Snapshot signal, string? pdnResponse)
        {
            Signal = signal;
            PdnResponse = pdnResponse;
        }

        public SignalParser.Snapshot Signal { get; }
        public string? PdnResponse { get; }
    }
}