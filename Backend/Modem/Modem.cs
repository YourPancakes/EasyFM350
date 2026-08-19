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
        lock (_sync)
        {
            ITransport? t;
            lock (_stateSync)
            {
                t = _t;
            }

            if (t == null || !t.IsOpen) throw new InvalidOperationException("port closed");
            try
            {
                var pre = t.ReadAvailable();
                if (pre.Length > 0) RouteUrcs(pre);

                t.Write(cmd + "\r");
                if (!quiet) Log(">> " + CgAuthMask().Replace(cmd, "$1\"***\",\"***\""));
                var sb = _rxBuf;
                sb.Length = 0;
                if (sb.Capacity > 65536) sb.Capacity = 4096;
                var deadline = Environment.TickCount64 + timeoutMs;
                var lastNl = -1;
                Span<char> tailSt = stackalloc char[256];
                while (Volatile.Read(ref _closeRequested) == 0 && Environment.TickCount64 < deadline)
                {
                    var chunk = t.ReadAvailable();
                    if (chunk.Length > 0)
                    {
                        var prevLen = sb.Length;
                        sb.Append(chunk);
                        if (sb.Length > MaxResponseChars)
                        {
                            var remove = sb.Length - MaxResponseChars;
                            sb.Remove(0, remove);
                            lastNl = Math.Max(-1, lastNl - remove);
                            prevLen = Math.Max(0, prevLen - remove);
                        }

                        var scanFrom = lastNl + 1;
                        if (sb.Length - scanFrom > 4096) scanFrom = sb.Length - 4096;
                        var nl = chunk.LastIndexOf('\n');
                        if (nl >= 0) lastNl = Math.Min(sb.Length - 1, prevLen + nl);

                        var tailLen = sb.Length - scanFrom;
                        bool matched;
                        if (tailLen <= 256)
                        {
                            var st = tailSt.Slice(0, tailLen);
                            sb.CopyTo(scanFrom, st, tailLen);
                            matched = FinalCode().IsMatch(st);
                        }
                        else
                        {
                            var scanBuf = ArrayPool<char>.Shared.Rent(tailLen);
                            try
                            {
                                sb.CopyTo(scanFrom, scanBuf, 0, tailLen);
                                matched = FinalCode().IsMatch(new ReadOnlySpan<char>(scanBuf, 0, tailLen));
                            }
                            finally
                            {
                                ArrayPool<char>.Shared.Return(scanBuf);
                            }
                        }

                        if (matched)
                        {
                            if (!PrepareCorrelatedResponse(sb, cmd))
                            {
                                lastNl = LastIndexOf(sb, '\n');
                                continue;
                            }

                            List<string> urcs;
                            var cleaned = ResponseCleaner.Clean(sb.ToString(), cmd, out urcs);
                            if (!quiet) Log("<< " + cleaned.Trim());
                            foreach (var u in urcs) FireUrc(u);

                            var tail = t.ReadAvailable();
                            if (tail.Length > 0) RouteUrcs(tail);
                            _consecutiveTimeouts = 0;
                            return cleaned;
                        }
                    }

                    Thread.Sleep(20);
                }

                if (Volatile.Read(ref _closeRequested) != 0) return string.Empty;

                var partial = sb.ToString();
                if (partial.Length > 0) RouteUrcs(partial);
                Log("<< (timeout) " + partial.Trim());

                if (!slowCommand && ++_consecutiveTimeouts >= MaxTimeouts)
                {
                    Log("!!! modem silent x" + _consecutiveTimeouts + " — считаю порт мёртвым");
                    lost = true;
                }
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is TimeoutException || ex is UnauthorizedAccessException ||
                      ex is InvalidOperationException || ex is ObjectDisposedException))
                    throw;
                Log("!!! io: " + ex.Message);
                lost = true;
            }
        }

        if (lost)
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

        return "";
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