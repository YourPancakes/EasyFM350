using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using EasyFM350.Wpf.Backend.Config;
using EasyFM350.Wpf.Backend.Infrastructure;

namespace EasyFM350.Wpf.Backend.Esim;

public sealed class EsimService
{
    private readonly ApduOverAt _apdu;
    private readonly Modem.Modem _modem;
    private readonly object _sessionSync = new();
    private LpacSession? _activeSession;

    private int _euiccInitFailures;
    private long _lastModemResetTick;

    public EsimService(Modem.Modem modem)
    {
        ArgumentNullException.ThrowIfNull(modem);
        _modem = modem;
        _apdu = new ApduOverAt(modem);
        _apdu.OnRetry += _ => Progress(Lang.T("esim_euicc_wait"));
    }

    public event Action<string>? OnTrace;

    public event Action<string>? OnProgress;

    public event Action<long>? OnWriteBytes;

    public bool EnsureEsimSlot()
    {
        var current = Modem.Modem.Fields(_modem.Send("AT+GTDUALSIM?", 4000), "+GTDUALSIM");
        if (current.Length > 0 && current[0] == "1") return true;
        return Modem.Modem.IsOk(_modem.Send("AT+GTDUALSIM=1", 8000));
    }

    public bool CycleSimSlot()
    {
        if (!Modem.Modem.IsOk(_modem.Send("AT+GTDUALSIM=0", 8000))) return false;
        Thread.Sleep(2000);
        if (!Modem.Modem.IsOk(_modem.Send("AT+GTDUALSIM=1", 8000))) return false;
        Thread.Sleep(2000);
        return true;
    }

    public void CancelActiveOperation()
    {
        LpacSession? session;
        lock (_sessionSync)
        {
            session = _activeSession;
        }

        session?.Cancel();
    }

    private static string? ResolveLpacExe()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "lpac");
        var exe = Path.Combine(dir, "lpac.orig.exe");
        var curl = Path.Combine(dir, "libcurl.dll");
        if (File.Exists(exe) && File.Exists(curl)) return exe;

        var assembly = typeof(EsimService).Assembly;
        if (!ExtractResource(assembly, "lpac.orig.exe", exe) || !ExtractResource(assembly, "libcurl.dll", curl))
            return null;
        return exe;
    }

    private static bool ExtractResource(System.Reflection.Assembly assembly, string suffix, string destination)
    {
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = assembly.GetManifestResourceStream(name);
                if (input == null) return false;
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write);
                input.CopyTo(output);
                return true;
            }
            catch (Exception) { return false; }
        }
        return false;
    }

    public LpacResult RunLpac(params string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var exe = ResolveLpacExe();
        if (exe == null)
            return LpacResult.Error("lpac.orig.exe is missing and cannot be unpacked beside the app (write access denied)");

        var key = OperationKey(args);
        if (key != null) Progress(Lang.T(key));

        var maxAttempts = IsReadOnly(args) ? 3 : 1;
        for (var attempt = 1;; attempt++)
        {
            var result = RunLpacOnce(exe, args);
            if (result.Ok)
            {
                _euiccInitFailures = 0;
                return result;
            }

            if (attempt >= maxAttempts)
            {
                MaybeResetWedgedEuicc(result);
                return result;
            }

            Trace("lpac " + string.Join(" ", args) + " failed: " + result.Message + " — retrying");
            Thread.Sleep(8000);
        }
    }

    private void MaybeResetWedgedEuicc(LpacResult result)
    {
        if (!string.Equals(result.Message, "euicc_init", StringComparison.OrdinalIgnoreCase)) return;
        _euiccInitFailures++;
        if (_euiccInitFailures < 2) return;
        if (Environment.TickCount64 - _lastModemResetTick < 600000) return;
        _lastModemResetTick = Environment.TickCount64;
        _euiccInitFailures = 0;
        Progress(Lang.T("esim_modem_reset"));
        try
        {
            _modem.Send("AT+CFUN=1,1", 5000, true);
        }
        catch (Exception)
        {
        }
    }

    private LpacResult RunLpacOnce(string exe, string[] args)
    {
        var session = new LpacSession(_apdu);
        lock (_sessionSync)
        {
            if (_activeSession != null)
                return LpacResult.Error("another lpac operation is already running");
            _activeSession = session;
        }

        LpacResult? result = null;
        long transmitBytes = 0;
        var transmitCount = 0;
        session.OnTrace += line =>
        {
            if (line.StartsWith("apdu transmit [", StringComparison.Ordinal))
            {
                transmitCount++;
                var open = line.IndexOf('[');
                var close = line.IndexOf('B', open);
                if (open >= 0 && close > open
                              && long.TryParse(line.Substring(open + 1, close - open - 1), out var bytes))
                    transmitBytes += bytes;
                if (transmitCount % 8 == 0) EventDispatch.Invoke(OnWriteBytes, transmitBytes);
            }

            Trace(line);
        };
        session.OnResult += line =>
        {
            var parsed = LpacResult.TryParse(line);
            if (parsed != null)
            {
                result = parsed;
                return;
            }

            var progress = ParseProgress(line);
            if (progress != null) Progress(progress);
            else Trace(line);
        };
        try
        {
            var exitCode = session.Run(exe, args);
            return result ?? LpacResult.Error("no result, exit " + exitCode);
        }
        finally
        {
            lock (_sessionSync)
            {
                if (ReferenceEquals(_activeSession, session)) _activeSession = null;
            }
        }
    }

    private static bool IsReadOnly(IReadOnlyList<string> args)
    {
        if (args.Count < 2) return false;
        if (args[0] == "chip") return args[1] == "info";
        if (args[0] == "profile") return args[1] == "list";
        if (args[0] == "notification") return args[1] == "list";
        return false;
    }

    private static string? ParseProgress(string line)
    {
        if (!line.Contains("\"type\":\"progress\"", StringComparison.Ordinal)) return null;
        try
        {
            var payload = JsonNode.Parse(line)?["payload"];
            if (payload == null) return null;
            var message = (string?)payload["message"];
            var data = (string?)payload["data"];
            if (string.IsNullOrEmpty(message)) return null;
            return string.IsNullOrEmpty(data) ? message : message + " " + data;
        }
        catch
        {
            return null;
        }
    }

    public LpacResult RestartSimSlot()
    {
        Progress(Lang.T("esim_resync"));
        return RestartSlotAndWait(true) ? LpacResult.Success() : LpacResult.Error("eSIM slot restart failed");
    }

    private bool RestartSlotAndWait(bool escalateToModemReset)
    {
        var start = Environment.TickCount64;
        if (!CycleSimSlot()) return false;

        var empty = true;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            Thread.Sleep(2000);
            var pin = Modem.Modem.Fields(_modem.Send("AT+CPIN?", 3000, true), "+CPIN");
            if (pin.Length > 0)
                empty = pin[0].Equals("EMPTY_EUICC", StringComparison.OrdinalIgnoreCase);
            if (Environment.TickCount64 - start < 12000) continue;
            if (!empty || !escalateToModemReset) break;
        }

        if (!empty || !escalateToModemReset) return true;

        Progress(Lang.T("esim_modem_reset"));
        try
        {
            _modem.Send("AT+CFUN=1,1", 5000, true);
        }
        catch (Exception)
        {
        }

        return true;
    }

    public LpacResult EnableProfile(string iccid)
    {
        var wasEmpty = IsEuiccEmpty();
        var result = RunLpac("profile", "enable", iccid);
        if (!result.Ok) result = RunLpac("profile", "enable", iccid, "0");
        if (!result.Ok) return result;

        if (wasEmpty)
        {
            if (!RestartSlotAndWait(true))
                return LpacResult.Error("profile enabled, but eSIM slot restart failed");
        }
        else
        {
            // A slot cycle here would race the eUICC's own REFRESH and wedge the APDU channel for minutes.
            WaitRefreshSettle();
        }

        return result;
    }

    private bool IsEuiccEmpty()
    {
        var pin = Modem.Modem.Fields(_modem.Send("AT+CPIN?", 3000, true), "+CPIN");
        return pin.Length > 0 && pin[0].Equals("EMPTY_EUICC", StringComparison.OrdinalIgnoreCase);
    }

    private void WaitRefreshSettle()
    {
        var start = Environment.TickCount64;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            Thread.Sleep(2000);
            var pin = Modem.Modem.Fields(_modem.Send("AT+CPIN?", 3000, true), "+CPIN");
            var ready = pin.Length > 0 && !pin[0].Equals("EMPTY_EUICC", StringComparison.OrdinalIgnoreCase);
            if (ready && Environment.TickCount64 - start >= 14000) return;
        }
    }

    public LpacResult DeleteProfile(string iccid)
    {
        var result = RunLpac("profile", "delete", iccid);
        if (result.Ok) return result;
        RunLpac("profile", "disable", iccid, "0");
        return RunLpac("profile", "delete", iccid);
    }

    public LpacResult ProcessAllNotifications()
    {
        var result = RunLpac("notification", "list");
        if (!result.Ok) return result;
        foreach (var notification in EsimNotification.ListFromJson(result.Data))
        {
            var seq = notification.Seq.ToString(CultureInfo.InvariantCulture);
            result = RunLpac("notification", "process", seq);
            if (!result.Ok) return result;
            result = RunLpac("notification", "remove", seq);
            if (!result.Ok) return result;
        }

        return result;
    }

    private static string? OperationKey(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return null;
        switch (args[0])
        {
            case "chip": return "esim_op_chip";
            case "notification": return "esim_op_notifications";
            case "profile":
                if (args.Count < 2) return "esim_op_profiles";
                switch (args[1])
                {
                    case "list": return "esim_op_profiles";
                    case "enable": return "esim_op_enable";
                    case "disable": return "esim_op_disable";
                    case "delete": return "esim_op_delete";
                    case "download": return "esim_op_download";
                    default: return null;
                }
            default: return null;
        }
    }

    private void Trace(string message)
    {
        EventDispatch.Invoke(OnTrace, message);
    }

    private void Progress(string message)
    {
        EventDispatch.Invoke(OnProgress, message);
    }
}