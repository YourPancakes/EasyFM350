using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EasyFM350.Wpf.Backend.Infrastructure;

namespace EasyFM350.Wpf.Backend.Esim;

public sealed class LpacSession
{
    private const string ResponseOk = "{\"ecode\":0}";
    private const string ResponseError = "{\"ecode\":-1}";
    private const int InactivityTimeoutMs = 180000;
    private const int MaxStderrChars = 65536;

    private readonly ApduOverAt _apdu;
    private readonly object _processSync = new();
    private Process? _activeProcess;
    private int _cancelRequested;

    public LpacSession(ApduOverAt apdu)
    {
        ArgumentNullException.ThrowIfNull(apdu);
        _apdu = apdu;
    }

    public event Action<string>? OnResult;

    public event Action<string>? OnTrace;

    public void Cancel()
    {
        Interlocked.Exchange(ref _cancelRequested, 1);
        Process? process;
        lock (_processSync)
        {
            process = _activeProcess;
        }

        if (process == null) return;
        TryKill(process);
    }

    public int Run(string lpacExePath, IReadOnlyList<string> args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lpacExePath);
        ArgumentNullException.ThrowIfNull(args);

        var startInfo = new ProcessStartInfo
        {
            FileName = lpacExePath,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(lpacExePath)) ?? "",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        startInfo.Environment["LPAC_APDU"] = "stdio";
        startInfo.Environment["LPAC_HTTP"] = "curl";
        startInfo.Environment["LIBEUICC_DEBUG_HTTP"] = "1";

        using var proc = Process.Start(startInfo)
                         ?? throw new InvalidOperationException("failed to start " + lpacExePath);
        lock (_processSync)
        {
            _activeProcess = proc;
        }

        if (Volatile.Read(ref _cancelRequested) != 0) TryKill(proc);

        var lastActivity = Environment.TickCount64;

        async Task<string> ReadStderrAsync()
        {
            var buffer = new StringBuilder();
            string? errorLine;
            while ((errorLine = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                Volatile.Write(ref lastActivity, Environment.TickCount64);
                if (buffer.Length >= MaxStderrChars) continue;

                var remaining = MaxStderrChars - buffer.Length;
                if (errorLine.Length + Environment.NewLine.Length <= remaining)
                    buffer.AppendLine(errorLine);
                else
                    buffer.Append(errorLine, 0, Math.Min(errorLine.Length, remaining));
            }

            return buffer.ToString();
        }

        var stderrPump = ReadStderrAsync();

        using var watchdog = new Timer(_ =>
        {
            try
            {
                if (!proc.HasExited && Environment.TickCount64 - Volatile.Read(ref lastActivity) > InactivityTimeoutMs)
                {
                    Raise(OnTrace, "lpac: no output 3 min, killed");
                    proc.Kill(true);
                }
            }
            catch
            {
            }
        }, null, 10000, 1000);

        try
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                Volatile.Write(ref lastActivity, Environment.TickCount64);
                if (line.Length == 0) continue;
                if (!TryHandleApdu(line, proc.StandardInput))
                    Raise(OnResult, line);
            }

            proc.WaitForExit();

            var stderr = stderrPump.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(stderr))
                Raise(OnTrace, "lpac stderr: " + stderr.Trim());

            return proc.ExitCode;
        }
        finally
        {
            lock (_processSync)
            {
                if (ReferenceEquals(_activeProcess, proc)) _activeProcess = null;
            }

            if (!proc.HasExited)
            {
                TryKill(proc);
                try
                {
                    proc.WaitForExit(5000);
                }
                catch
                {
                }
            }

            try
            {
                _apdu.Disconnect();
            }
            catch
            {
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch
        {
        }
    }

    private bool TryHandleApdu(string line, StreamWriter input)
    {
        if (!line.Contains("\"type\":\"apdu\"", StringComparison.Ordinal))
            return false;

        JsonNode? request;
        try
        {
            request = JsonNode.Parse(line);
        }
        catch
        {
            return false;
        }

        if ((string?)request?["type"] != "apdu")
            return false;

        var payload = request?["payload"];
        var func = (string?)payload?["func"] ?? "";
        var param = (string?)payload?["param"];

        if (OnTrace != null)
            Raise(OnTrace, "apdu " + func + (param == null ? "" : " [" + param.Length / 2 + "B]"));

        string apduPayload;
        try
        {
            apduPayload = HandleApdu(func, param);
            if (apduPayload.Contains("\"ecode\":-", StringComparison.Ordinal))
                Raise(OnTrace, "apdu " + func + " -> error");
        }
        catch (Exception ex)
        {
            Raise(OnTrace, "apdu " + func + " failed: " + ex.Message);
            apduPayload = ResponseError;
        }

        try
        {
            input.WriteLine("{\"type\":\"apdu\",\"payload\":" + apduPayload + "}");
            input.Flush();
        }
        catch (Exception ex)
        {
            Raise(OnTrace, "apdu pipe: " + ex.Message);
            throw new IOException("lpac APDU response pipe failed.", ex);
        }

        return true;
    }

    private string HandleApdu(string func, string? param)
    {
        switch (func)
        {
            case "connect":
                return _apdu.Connect() ? ResponseOk : ResponseError;

            case "disconnect":
                return _apdu.Disconnect() ? ResponseOk : ResponseError;

            case "logic_channel_open":
                if (param == null) return ResponseError;
                return "{\"ecode\":" + _apdu.LogicChannelOpen(param) + "}";

            case "logic_channel_close":
                return _apdu.LogicChannelClose() ? ResponseOk : ResponseError;

            case "transmit":
                if (param == null) return ResponseError;
                var data = _apdu.Transmit(param);
                return data != null ? "{\"ecode\":0,\"data\":\"" + data + "\"}" : ResponseError;

            default:
                return ResponseError;
        }
    }

    private static void Raise(Action<string>? handler, string message)
    {
        EventDispatch.Invoke(handler, message);
    }
}