using System;
using System.Collections.Generic;
using System.Linq;
using EasyFM350.Wpf.Backend.Modem;

namespace EasyFM350.Wpf.Backend.Radio;

public sealed class DeviceInfoService
{
    private static readonly string[] LineSeparators = { "\r", "\n" };
    private readonly Modem.Modem _modem;

    public DeviceInfoService(Modem.Modem modem)
    {
        _modem = modem ?? throw new ArgumentNullException(nameof(modem));
    }

    public Result Query(string command, string label, bool quiet)
    {
        try
        {
            var response = _modem.Send(command, 3000, quiet);
            var raw = CompactRaw(command, response);
            if (response.Length == 0) return new Result(label, "Empty", raw, 0);

            var prefix = command.TrimEnd('?', '=');
            var lines = new List<string>();
            foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                var value = AtInput.Sanitize(line.Trim());
                if (value.Length == 0 || value == command || value == "OK" ||
                    value.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (value == "ERROR" || value == "NO CARRIER"
                                     || value.StartsWith("+CME ERROR:", StringComparison.Ordinal)
                                     || value.StartsWith("+CMS ERROR:", StringComparison.Ordinal))
                    return new Result(label, InfoDecode.Human(command, null), raw, 0);
                lines.Add(value);
            }

            var human = InfoDecode.Human(command, lines);
            var slot = command == "AT+CGSN" ? 7 : command == "AT+EGMR=0,10" ? 10 : command == "AT+CFSN" ? 5 : 0;
            return new Result(label, human.Length == 0 ? "Empty" : human, raw, slot);
        }
        catch (Exception exception)
        {
            return new Result(label, "—", command + "\n" + exception.Message, 0);
        }
    }

    private static string CompactRaw(string command, string response)
    {
        var compact = string.Join(" | ", response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim()).Where(static line => line.Length > 0));
        if (compact.Length > 1800) compact = compact.Substring(0, 1800) + "...";
        return command + (compact.Length == 0 ? string.Empty : " | " + compact);
    }

    public sealed class Result
    {
        internal Result(string label, string value, string raw, int editSlot)
        {
            Label = label;
            Value = value;
            Raw = raw;
            EditSlot = editSlot;
        }

        public string Label { get; }
        public string Value { get; }
        public string Raw { get; }
        public int EditSlot { get; }
    }
}