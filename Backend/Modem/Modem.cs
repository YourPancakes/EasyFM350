using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using EasyFM350.Wpf.Backend.Infrastructure;

namespace EasyFM350.Wpf.Backend.Modem;

public interface ITransport
{
    bool IsOpen { get; }

    void Open();

    void Close();

    void Write(string s);

    string ReadAvailable();
}

public partial class Modem
{
    private const int MaxTimeouts = 3;
    private const int MaxResponseChars = 1024 * 1024;
    private readonly StringBuilder _rxBuf = new();
    private readonly object _stateSync = new();
    private readonly object _sync = new();
    private readonly StringBuilder _urcBuf = new();
    private int _closeRequested;
    private int _consecutiveTimeouts;

    private volatile string? _currentPort;
    private volatile ITransport? _t;

    private string? CurrentPort
    {
        get => _currentPort;
        set => _currentPort = value;
    }

    public bool IsOpen
    {
        get
        {
            var t = _t;
            return t != null && t.IsOpen;
        }
    }

    public event Action<string>? OnLog;

    public event Action<string>? OnUrc;

    public event Action? OnPortLost;

    private void Log(string s)
    {
        EventDispatch.Invoke(OnLog, s);
    }

    [GeneratedRegex(@"\((COM\d+)\)")]
    private static partial Regex ComPortRe();

    public string? FindAtPort()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher(
                       "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%MD AT Port%(COM%'"))
            using (var results = searcher.Get())
            {
                foreach (var o in results)
                    using (o)
                    {
                        var name = o["Name"] as string;
                        var m = ComPortRe().Match(name ?? "");
                        if (m.Success) return m.Groups[1].Value;
                    }
            }
        }
        catch (Exception ex)
        {
            Log("wmi: " + ex.Message);
        }

        return null;
    }

    public bool Open(string portName)
    {
        return Open(new SerialTransport(portName), portName);
    }

    public bool Open(ITransport transport, string label)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        lock (_sync)
        {
            Close();
            Volatile.Write(ref _closeRequested, 0);
            try
            {
                lock (_stateSync)
                {
                    _t = transport;
                }

                transport.Open();

                _consecutiveTimeouts = 0;
                var r = Send("AT", 1500);
                if (!IsOk(r))
                {
                    Close();
                    return false;
                }

                CurrentPort = label;
                Log("<<< " + label + " opened");
                return true;
            }
            catch (Exception ex)
            {
                Log("!!! open " + label + ": " + ex.Message);
                Close();
                return false;
            }
        }
    }

    public void Close()
    {
        Interlocked.Exchange(ref _closeRequested, 1);
        lock (_sync)
        {
            ITransport? t;
            lock (_stateSync)
            {
                t = _t;
                _t = null;
                CurrentPort = null;
            }

            _rxBuf.Clear();
            _urcBuf.Clear();

            try
            {
                if (t != null) t.Close();
            }
            catch
            {
            }
        }
    }

    [GeneratedRegex(
        @"(\r\n|\n|^)(OK|ERROR)(\r\n|\n|$)|(\r\n|\n|^)(\+CME ERROR:[^\r\n]*|\+CMS ERROR:[^\r\n]*)(\r\n|\n)|(\r\n|\n|^)(NO CARRIER)(\r\n|\n)")]
    private static partial Regex FinalCode();

    [GeneratedRegex(@"^(AT\+CGAUTH=\d+,\d+,)""[^""]*"",""[^""]*""")]
    private static partial Regex CgAuthMask();

    [GeneratedRegex(@"(?:^|\r\n|\n)\s*(\d{6,})\s*(?:\r\n|\n|$)")]
    private static partial Regex NumberRe();

    [GeneratedRegex(@"\b(?:IMEI|IMSI)\s*[:=]\s*(\d{6,})\b", RegexOptions.IgnoreCase)]
    private static partial Regex LabeledNumberRe();

    public string Send(string cmd, int timeoutMs = 3000, bool quiet = false, bool slowCommand = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cmd);
        if (timeoutMs < 1) throw new ArgumentOutOfRangeException(nameof(timeoutMs));

        var lost = false;
        string response;
        lock (_sync)
        {
            var transport = GetOpenTransport();
            try
            {
                response = ExecuteCommand(transport, cmd, timeoutMs, quiet, slowCommand, out lost);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                Log("!!! io: " + ex.Message);
                lost = true;
                response = string.Empty;
            }
        }

        if (lost) HandleLostPort();
        return response;
    }

    private ITransport GetOpenTransport()
    {
        ITransport? transport;
        lock (_stateSync)
        {
            transport = _t;
        }

        if (transport == null || !transport.IsOpen) throw new InvalidOperationException("port closed");
        return transport;
    }

    private string ExecuteCommand(
        ITransport transport,
        string command,
        int timeoutMs,
        bool quiet,
        bool slowCommand,
        out bool lost)
    {
        lost = false;

        var pendingInput = transport.ReadAvailable();
        if (pendingInput.Length > 0) RouteUrcs(pendingInput);

        transport.Write(command + "\r");
        if (!quiet) Log(">> " + CgAuthMask().Replace(command, "$1\"***\",\"***\""));

        var responseBuffer = _rxBuf;
        responseBuffer.Length = 0;
        if (responseBuffer.Capacity > 65536) responseBuffer.Capacity = 4096;

        var deadline = Environment.TickCount64 + timeoutMs;
        var lastNewline = -1;
        Span<char> stackTail = stackalloc char[256];

        while (Volatile.Read(ref _closeRequested) == 0 && Environment.TickCount64 < deadline)
        {
            var chunk = transport.ReadAvailable();
            if (chunk.Length > 0)
            {
                if (TryCompleteResponse(
                        transport,
                        command,
                        quiet,
                        chunk,
                        responseBuffer,
                        stackTail,
                        ref lastNewline,
                        out var retryImmediately,
                        out var completedResponse))
                    return completedResponse;

                if (retryImmediately) continue;
            }

            Thread.Sleep(20);
        }

        if (Volatile.Read(ref _closeRequested) != 0) return string.Empty;

        var partialResponse = responseBuffer.ToString();
        if (partialResponse.Length > 0) RouteUrcs(partialResponse);
        Log("<< (timeout) " + partialResponse.Trim());

        if (!slowCommand && ++_consecutiveTimeouts >= MaxTimeouts)
        {
            Log("!!! modem silent x" + _consecutiveTimeouts + " — считаю порт мёртвым");
            lost = true;
        }

        return string.Empty;
    }

    private bool TryCompleteResponse(
        ITransport transport,
        string command,
        bool quiet,
        string chunk,
        StringBuilder responseBuffer,
        Span<char> stackTail,
        ref int lastNewline,
        out bool retryImmediately,
        out string completedResponse)
    {
        retryImmediately = false;
        completedResponse = string.Empty;

        var previousLength = responseBuffer.Length;
        responseBuffer.Append(chunk);
        TrimResponseBuffer(responseBuffer, ref previousLength, ref lastNewline);

        var scanFrom = lastNewline + 1;
        if (responseBuffer.Length - scanFrom > 4096) scanFrom = responseBuffer.Length - 4096;

        var newlineInChunk = chunk.LastIndexOf('\n');
        if (newlineInChunk >= 0)
            lastNewline = Math.Min(responseBuffer.Length - 1, previousLength + newlineInChunk);

        if (!HasFinalCode(responseBuffer, scanFrom, stackTail)) return false;

        if (!PrepareCorrelatedResponse(responseBuffer, command))
        {
            lastNewline = LastIndexOf(responseBuffer, '\n');
            retryImmediately = true;
            return false;
        }

        var cleanedResponse = ResponseCleaner.Clean(responseBuffer.ToString(), command, out var urcs);
        if (!quiet) Log("<< " + cleanedResponse.Trim());
        foreach (var urc in urcs) FireUrc(urc);

        var trailingInput = transport.ReadAvailable();
        if (trailingInput.Length > 0) RouteUrcs(trailingInput);

        _consecutiveTimeouts = 0;
        completedResponse = cleanedResponse;
        return true;
    }

    private static void TrimResponseBuffer(StringBuilder responseBuffer, ref int previousLength, ref int lastNewline)
    {
        if (responseBuffer.Length <= MaxResponseChars) return;

        var removeCount = responseBuffer.Length - MaxResponseChars;
        responseBuffer.Remove(0, removeCount);
        lastNewline = Math.Max(-1, lastNewline - removeCount);
        previousLength = Math.Max(0, previousLength - removeCount);
    }

    private static bool HasFinalCode(StringBuilder responseBuffer, int scanFrom, Span<char> stackTail)
    {
        var tailLength = responseBuffer.Length - scanFrom;
        if (tailLength <= stackTail.Length)
        {
            var tail = stackTail.Slice(0, tailLength);
            responseBuffer.CopyTo(scanFrom, tail, tailLength);
            return FinalCode().IsMatch(tail);
        }

        var rentedTail = ArrayPool<char>.Shared.Rent(tailLength);
        try
        {
            responseBuffer.CopyTo(scanFrom, rentedTail, 0, tailLength);
            return FinalCode().IsMatch(new ReadOnlySpan<char>(rentedTail, 0, tailLength));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rentedTail);
        }
    }

    private static bool IsTransportFailure(Exception exception)
    {
        return exception is IOException
            or TimeoutException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ObjectDisposedException;
    }

    private void HandleLostPort()
    {
        var wasOpen = CurrentPort != null;
        try
        {
            Close();
        }
        catch
        {
        }

        if (wasOpen) EventDispatch.Invoke(OnPortLost, ex => Log("!!! portlost subscriber: " + ex.Message));
    }

    private static int LastIndexOf(StringBuilder builder, char value)
    {
        for (var index = builder.Length - 1; index >= 0; index--)
            if (builder[index] == value)
                return index;
        return -1;
    }

    private static bool PrepareCorrelatedResponse(StringBuilder response, string command)
    {
        var text = response.ToString();
        var currentEcho = -1;
        var foreignEcho = false;
        var offset = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
            {
                if (line.Equals(command, StringComparison.OrdinalIgnoreCase)) currentEcho = offset;
                else foreignEcho = true;
            }

            offset += rawLine.Length + 1;
        }

        if (currentEcho >= 0)
        {
            if (currentEcho > 0) response.Remove(0, currentEcho);
            var correlated = response.ToString();
            var bodyStart = Math.Min(command.Length, correlated.Length);
            return FinalCode().IsMatch(correlated.AsSpan(bodyStart));
        }

        if (!foreignEcho) return true;
        response.Clear();
        return false;
    }

    private void RouteUrcs(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return;
        _urcBuf.Append(raw);
        var pending = _urcBuf.ToString();
        var consumed = 0;
        while (true)
        {
            var newline = pending.IndexOf('\n', consumed);
            if (newline < 0) break;
            var line = pending.AsSpan(consumed, newline - consumed).Trim();
            consumed = newline + 1;
            if (line.Length > 0 && ResponseCleaner.IsUrc(line)) FireUrc(line.ToString());
        }

        if (consumed > 0) _urcBuf.Remove(0, consumed);
        if (_urcBuf.Length > 8192) _urcBuf.Remove(0, _urcBuf.Length - 8192);
    }

    private void FireUrc(string u)
    {
        EventDispatch.Invoke(OnUrc, u, ex => Log("!!! urc subscriber: " + ex.Message));
    }

    public static string? Number(string resp)
    {
        var labeled = LabeledNumberRe().Match(resp ?? string.Empty);
        if (labeled.Success) return labeled.Groups[1].Value;
        var m = NumberRe().Match(resp ?? string.Empty);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static bool IsOk(string? response)
    {
        if (string.IsNullOrEmpty(response)) return false;
        bool? finalStatus = null;
        var rest = response.AsSpan();
        while (!rest.IsEmpty)
        {
            var separator = rest.IndexOfAny('\r', '\n');
            var line = (separator < 0 ? rest : rest.Slice(0, separator)).Trim();
            if (line.Equals("OK".AsSpan(), StringComparison.Ordinal)) finalStatus = true;
            else if (line.Equals("ERROR".AsSpan(), StringComparison.Ordinal)
                     || line.Equals("NO CARRIER".AsSpan(), StringComparison.Ordinal)
                     || line.StartsWith("+CME ERROR:".AsSpan(), StringComparison.Ordinal)
                     || line.StartsWith("+CMS ERROR:".AsSpan(), StringComparison.Ordinal)) finalStatus = false;
            rest = separator < 0 ? ReadOnlySpan<char>.Empty : rest.Slice(separator + 1);
        }

        return finalStatus == true;
    }

    public static string[] Fields(string? resp, string prefix)
    {
        if (string.IsNullOrEmpty(resp) || string.IsNullOrEmpty(prefix)) return Array.Empty<string>();
        var i = -1;
        for (;;)
        {
            i = resp.IndexOf(prefix, i + 1, StringComparison.Ordinal);
            if (i < 0) return Array.Empty<string>();
            var j = i + prefix.Length;
            if (j < resp.Length && (resp[j] == ':' || resp[j] == ' ' || resp[j] == '\t'))
            {
                i = j;
                break;
            }
        }

        while (i < resp.Length && (resp[i] == ':' || char.IsWhiteSpace(resp[i]))) i++;
        var start = i;
        while (i < resp.Length && resp[i] != '\r' && resp[i] != '\n') i++;
        var line = resp.Substring(start, i - start).Trim();

        if (line.Length == 0 || line == "OK" || line == "ERROR" || line.StartsWith("+CME") || line.StartsWith("+CMS"))
            return Array.Empty<string>();
        return SplitAtFields(line);
    }

    private static string[] SplitAtFields(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder(line.Length);
        var quoted = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (character == ',' && !quoted)
            {
                fields.Add(field.ToString().Trim());
                field.Clear();
                continue;
            }

            field.Append(character);
        }

        fields.Add(field.ToString().Trim());
        return fields.ToArray();
    }
}