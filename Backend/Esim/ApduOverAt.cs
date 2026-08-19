using System;
using System.Globalization;
using System.Threading;
using EasyFM350.Wpf.Backend.Infrastructure;

namespace EasyFM350.Wpf.Backend.Esim;

public sealed class ApduOverAt
{
    private const int CommandTimeoutMs = 15000;
    private const int TransmitTimeoutMs = 60000;

    private readonly Modem.Modem _modem;
    private int _logicChannel = -1;

    public ApduOverAt(Modem.Modem modem)
    {
        ArgumentNullException.ThrowIfNull(modem);
        _modem = modem;
    }

    public event Action<string>? OnRetry;

    public bool Connect()
    {
        return _modem.IsOpen;
    }

    public bool Disconnect()
    {
        return _logicChannel < 0 || LogicChannelClose();
    }

    public int LogicChannelOpen(string aidHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aidHex);
        if (!IsHex(aidHex) || aidHex.Length is < 2 or > 32)
            throw new ArgumentException("AID must contain 1 to 16 bytes in hexadecimal format.", nameof(aidHex));

        // A crashed lpac leaves the channel open modem-side; without this close the
        // next CCHO is rejected and lpac reports "euicc_init" until the modem reboots.
        if (_logicChannel >= 0) LogicChannelClose();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (attempt > 0)
            {
                EventDispatch.Invoke(OnRetry, "logic_channel_open");
                Thread.Sleep(3000);
            }

            var response = _modem.Send("AT+CCHO=\"" + aidHex + "\"", CommandTimeoutMs, true);

            var fields = Modem.Modem.Fields(response, "+CCHO");
            if (fields.Length > 0 && int.TryParse(fields[0], out _logicChannel))
                return _logicChannel;

            var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = lines.Length - 1; i >= 0; i--)
                if (int.TryParse(lines[i], NumberStyles.None, CultureInfo.InvariantCulture, out _logicChannel))
                    return _logicChannel;
        }

        _logicChannel = -1;
        return -1;
    }

    public bool LogicChannelClose()
    {
        if (_logicChannel < 0) return true;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) Thread.Sleep(1000);
            var response = _modem.Send("AT+CCHC=" + _logicChannel, CommandTimeoutMs, true);
            if (Modem.Modem.IsOk(response))
            {
                _logicChannel = -1;
                return true;
            }
        }

        _logicChannel = -1;
        return false;
    }

    public string? Transmit(string apduHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apduHex);
        if (!IsHex(apduHex))
            throw new ArgumentException("APDU must contain an even number of hexadecimal characters.", nameof(apduHex));
        if (_logicChannel < 0) return null;

        var response = _modem.Send(
            "AT+CGLA=" + _logicChannel + "," + apduHex.Length + ",\"" + apduHex + "\"",
            TransmitTimeoutMs, true, true);

        var fields = Modem.Modem.Fields(response, "+CGLA");
        if (fields.Length < 2
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var responseLength)
            || responseLength <= 0
            || fields[1].Length != responseLength
            || !IsHex(fields[1]))
            return null;
        return fields[1];
    }

    private static bool IsHex(string value)
    {
        if (value.Length == 0 || (value.Length & 1) != 0) return false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f') continue;
            return false;
        }

        return true;
    }
}