using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyFM350.Wpf.Backend.Infrastructure;

namespace EasyFM350.Wpf.Backend.Network;

public sealed class ProxyEngine
{
    private const int MaxSessions = 128;
    private const int ConnectTimeoutMs = 10000;

    private const SocketOptionName IpUnicastInterface = (SocketOptionName)31;

    private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");
    private static readonly SearchValues<char> InvalidHostChars = SearchValues.Create("\r\n /\\@[]");

    private static readonly byte[] SocksGreetOk = { 0x05, 0x00 };
    private static readonly byte[] SocksNoAccept = { 0x05, 0xFF };
    private static readonly byte[] SocksFailed = { 0x05, 0x01, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };

    private static readonly byte[] HttpBadRequest =
        Latin1.GetBytes("HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");

    private static readonly byte[] HttpBadGateway =
        Latin1.GetBytes("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");

    private static readonly byte[] HttpOkEstablished = Latin1.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");
    private readonly List<Socket> _connecting = new();
    private readonly object _lifeSync = new();
    private readonly List<TcpClient> _sessions = new();
    private readonly object _sessSync = new();
    private volatile IPAddress? _bindAddr;
    private volatile int _interfaceIndex;

    private volatile TcpListener? _listener;
    private volatile bool _running;
    private volatile bool _stop;

    private volatile UpstreamConfiguration? _upstream;

    public int Port { get; private set; }
    public bool Running => _running;

    public void SetUpstream(string? host, int port)
    {
        if (string.IsNullOrEmpty(host))
        {
            _upstream = null;
            return;
        }

        if (host.AsSpan().ContainsAny(InvalidHostChars)
            || Uri.CheckHostName(host) is UriHostNameType.Unknown or UriHostNameType.IPv6)
            throw new ArgumentException("Valid IPv4 address or DNS name required.", nameof(host));
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _upstream = new UpstreamConfiguration(host, port);
    }

    public event Action<string>? OnLog;

    public event Action? OnDied;

    private void Log(string s)
    {
        EventDispatch.Invoke(OnLog, s);
    }

    public void Start(int port, string bindIp, int interfaceIndex)
    {
        if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrWhiteSpace(bindIp)) throw new ArgumentException("Bind address required.", nameof(bindIp));
        if (interfaceIndex < 1) throw new ArgumentOutOfRangeException(nameof(interfaceIndex));
        lock (_lifeSync)
        {
            Stop();
            _bindAddr = IPAddress.Parse(bindIp);
            if (_bindAddr.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("IPv4 bind address required.", nameof(bindIp));
            _interfaceIndex = interfaceIndex;
            _stop = false;
            var l = new TcpListener(IPAddress.Loopback, port);
            try
            {
                l.Start();
            }
            catch
            {
                try
                {
                    l.Stop();
                }
                catch
                {
                }

                throw;
            }

            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            _listener = l;
            _running = true;

            _ = RunAcceptLoopAsync(l);
        }
    }

    public void Stop()
    {
        lock (_lifeSync)
        {
            _stop = true;
            _running = false;
            var l = _listener;
            _listener = null;
            try
            {
                if (l != null) l.Stop();
            }
            catch
            {
            }

            List<TcpClient> ss;
            List<Socket> cs;
            lock (_sessSync)
            {
                ss = new List<TcpClient>(_sessions);
                _sessions.Clear();
                cs = new List<Socket>(_connecting);
                _connecting.Clear();
            }

            foreach (var c in ss)
                try
                {
                    c.Close();
                }
                catch
                {
                }

            foreach (var s in cs)
                try
                {
                    s.Close();
                }
                catch
                {
                }
        }
    }

    private async Task RunAcceptLoopAsync(TcpListener listener)
    {
        try
        {
            await AcceptLoopAsync(listener).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_stop || !ReferenceEquals(_listener, listener)) return;
            Log("accept loop: " + ex.Message);
            try
            {
                Stop();
            }
            catch
            {
            }

            EventDispatch.Invoke(OnDied);
        }
    }

    private async Task AcceptLoopAsync(TcpListener l)
    {
        var fails = 0;
        while (!_stop)
        {
            TcpClient c;
            try
            {
                c = await l.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_stop || !ReferenceEquals(_listener, l)) break;
                Log("accept: " + ex.Message);
                if (++fails >= 5)
                {
                    bool ours;
                    lock (_lifeSync)
                    {
                        ours = ReferenceEquals(_listener, l);
                        if (ours)
                        {
                            Log("accept: proxy stopped, giving up");
                            try
                            {
                                Stop();
                            }
                            catch
                            {
                            }
                        }
                    }

                    if (!ours) break;
                    EventDispatch.Invoke(OnDied);
                    break;
                }

                await Task.Delay(200).ConfigureAwait(false);
                continue;
            }

            fails = 0;

            lock (_sessSync)
            {
                if (_stop)
                {
                    try
                    {
                        c.Close();
                    }
                    catch
                    {
                    }

                    break;
                }

                if (_sessions.Count >= MaxSessions)
                {
                    try
                    {
                        c.Close();
                    }
                    catch
                    {
                    }

                    continue;
                }

                _sessions.Add(c);
            }

            _ = HandleSessionAsync(c);
        }
    }

    private async Task HandleSessionAsync(TcpClient c)
    {
        try
        {
            await HandleAsync(c).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_stop) Log("session: " + ex.Message);
        }
        finally
        {
            lock (_sessSync)
            {
                _sessions.Remove(c);
            }

            try
            {
                c.Close();
            }
            catch
            {
            }
        }
    }

    private async Task<Socket> ConnectViaAsync(string host, int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        lock (_sessSync)
        {
            if (_stop)
            {
                try
                {
                    s.Close();
                }
                catch
                {
                }

                throw new IOException("proxy stopping");
            }

            _connecting.Add(s);
        }

        try
        {
            var bindAddress = _bindAddr ?? throw new InvalidOperationException("Proxy is not started.");
            var interfaceIndex = _interfaceIndex;
            if (interfaceIndex < 1) throw new InvalidOperationException("Proxy interface is unavailable.");
            s.SetSocketOption(SocketOptionLevel.IP, IpUnicastInterface, IPAddress.HostToNetworkOrder(interfaceIndex));
            s.Bind(new IPEndPoint(bindAddress, 0));
            s.NoDelay = true;
            IPAddress? destination = null;
            if (IPAddress.TryParse(host, out var parsed))
            {
                if (parsed.AddressFamily == AddressFamily.InterNetwork) destination = parsed;
            }
            else
            {
                var addresses = await Dns.GetHostAddressesAsync(host)
                    .WaitAsync(TimeSpan.FromMilliseconds(ConnectTimeoutMs)).ConfigureAwait(false);
                foreach (var address in addresses)
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        destination = address;
                        break;
                    }
            }

            if (destination == null) throw new IOException("no IPv4 address for " + host);
            await s.ConnectAsync(destination, port)
                .WaitAsync(TimeSpan.FromMilliseconds(ConnectTimeoutMs)).ConfigureAwait(false);
            return s;
        }
        catch
        {
            try
            {
                s.Dispose();
            }
            catch
            {
            }

            throw;
        }
        finally
        {
            lock (_sessSync)
            {
                _connecting.Remove(s);
            }
        }
    }

    private async Task<(Socket Socket, byte[] Early)> ConnectTargetAsync(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new IOException("empty target host");
        if (port < 1 || port > 65535) throw new IOException("invalid target port");
        if (host.AsSpan().ContainsAny(InvalidHostChars)
            || Uri.CheckHostName(host) is UriHostNameType.Unknown or UriHostNameType.IPv6)
            throw new IOException("invalid target host");
        var early = Array.Empty<byte>();
        var up = _upstream;
        if (up == null) return (await ConnectViaAsync(host, port).ConfigureAwait(false), early);
        var s = await ConnectViaAsync(up.Host, up.Port).ConfigureAwait(false);
        try
        {
            using var rs = new NetworkStream(s, false);
            var req = Latin1.GetBytes("CONNECT " + host + ":" + port + " HTTP/1.1\r\nHost: " + host + ":" + port +
                                      "\r\n\r\n");
            await WriteWithTimeoutAsync(rs, req).ConfigureAwait(false);

            var response = await ReadHeadAsync(rs, 8192, false, null).ConfigureAwait(false);
            early = response.Extra;
            var resp = Latin1.GetString(response.Head);
            if (!IsSuccessfulHttpResponse(resp))
                throw new IOException("upstream proxy refused CONNECT to " + host + ":" + port);
            return (s, early);
        }
        catch
        {
            try
            {
                s.Close();
            }
            catch
            {
            }

            throw;
        }
    }

    private static bool IsSuccessfulHttpResponse(string response)
    {
        if (response.Length < 12
            || !(response.StartsWith("HTTP/1.1 ", StringComparison.Ordinal)
                 || response.StartsWith("HTTP/1.0 ", StringComparison.Ordinal))) return false;
        return response[9] == '2' && char.IsAsciiDigit(response[10]) && char.IsAsciiDigit(response[11])
               && (response.Length == 12 || response[12] is ' ' or '\r' or '\n');
    }

    private async Task HandleAsync(TcpClient client)
    {
        Socket? remote = null;
        try
        {
            var clientStream = client.GetStream();
            var firstRead = await ReadHeadAsync(clientStream, 16384, false, null).ConfigureAwait(false);
            if (firstRead.Head.Length == 0) return;

            if (firstRead.Head[0] == 0x05)
            {
                remote = await HandleSocks5Async(clientStream, firstRead.Head, firstRead.Extra).ConfigureAwait(false);
            }
            else
            {
                var request = Latin1.GetString(firstRead.Head);
                if (!TryParseHttpRequestLine(request, out var method, out var firstSpace, out var secondSpace)) return;

                if (method.Equals("CONNECT", StringComparison.Ordinal))
                {
                    remote = await HandleHttpConnectAsync(
                            clientStream,
                            request,
                            firstSpace,
                            secondSpace,
                            firstRead.Extra)
                        .ConfigureAwait(false);
                }
                else
                {
                    await HandleHttpForwardAsync(
                            client.Client,
                            clientStream,
                            request,
                            firstSpace,
                            secondSpace,
                            firstRead.Extra)
                        .ConfigureAwait(false);
                    return;
                }
            }

            if (remote != null) await PumpAsync(client.Client, remote).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_stop) Log("proxy client: " + ex.Message);
            try
            {
                client.Close();
            }
            catch
            {
            }
        }
        finally
        {
            try
            {
                remote?.Close();
            }
            catch
            {
            }
        }
    }

    private async Task<Socket?> HandleSocks5Async(Stream clientStream, byte[] greeting, byte[] greetingExtra)
    {
        if (greeting.Length < 2 || greeting[1] == 0 || !OfferedNoAuth(greeting))
        {
            await TryWriteAsync(clientStream, SocksNoAccept).ConfigureAwait(false);
            return null;
        }

        await WriteWithTimeoutAsync(clientStream, SocksGreetOk).ConfigureAwait(false);

        var requestRead = await ReadHeadAsync(clientStream, 522, true, greetingExtra).ConfigureAwait(false);
        if (!TryParseSocksTarget(requestRead.Head, out var host, out var port))
        {
            await SendSocksFailAsync(clientStream).ConfigureAwait(false);
            return null;
        }

        Socket remote;
        byte[] upstreamEarlyData;
        try
        {
            var connected = await ConnectTargetAsync(host, port).ConfigureAwait(false);
            remote = connected.Socket;
            upstreamEarlyData = connected.Early;
        }
        catch
        {
            await SendSocksFailAsync(clientStream).ConfigureAwait(false);
            throw;
        }

        try
        {
            await WriteWithTimeoutAsync(clientStream, BuildSocksGranted(remote)).ConfigureAwait(false);
            if (upstreamEarlyData.Length > 0)
                await WriteWithTimeoutAsync(clientStream, upstreamEarlyData).ConfigureAwait(false);

            if (requestRead.Extra.Length > 0)
            {
                using var remoteStream = new NetworkStream(remote, false);
                await WriteWithTimeoutAsync(remoteStream, requestRead.Extra).ConfigureAwait(false);
            }

            return remote;
        }
        catch
        {
            try
            {
                remote.Close();
            }
            catch
            {
            }

            throw;
        }
    }

    private async Task<Socket?> HandleHttpConnectAsync(
        Stream clientStream,
        string request,
        int firstSpace,
        int secondSpace,
        byte[] clientEarlyData)
    {
        var target = request.Substring(firstSpace + 1, secondSpace - firstSpace - 1);
        if (!TryParseAuthority(target, 443, out var host, out var port)) return null;

        Socket remote;
        byte[] upstreamEarlyData;
        try
        {
            var connected = await ConnectTargetAsync(host, port).ConfigureAwait(false);
            remote = connected.Socket;
            upstreamEarlyData = connected.Early;
        }
        catch
        {
            await SendHttp502Async(clientStream).ConfigureAwait(false);
            return null;
        }

        try
        {
            await WriteWithTimeoutAsync(clientStream, HttpOkEstablished).ConfigureAwait(false);
            if (upstreamEarlyData.Length > 0)
                await WriteWithTimeoutAsync(clientStream, upstreamEarlyData).ConfigureAwait(false);

            if (clientEarlyData.Length > 0)
            {
                using var remoteStream = new NetworkStream(remote, false);
                await WriteWithTimeoutAsync(remoteStream, clientEarlyData).ConfigureAwait(false);
            }

            return remote;
        }
        catch
        {
            try
            {
                remote.Close();
            }
            catch
            {
            }

            throw;
        }
    }

    private async Task HandleHttpForwardAsync(
        Socket clientSocket,
        Stream clientStream,
        string request,
        int firstSpace,
        int secondSpace,
        byte[] initialBody)
    {
        if (!TryParseHttpTarget(
                request,
                firstSpace,
                secondSpace,
                out var host,
                out var port,
                out var originTarget,
                out var authority))
        {
            await TryWriteAsync(clientStream, HttpBadRequest).ConfigureAwait(false);
            return;
        }

        var upstream = _upstream;
        if (!TryBuildForwardRequest(
                request,
                firstSpace,
                secondSpace,
                originTarget,
                authority,
                upstream != null,
                out var forwardRequest,
                out var requestBodyLength))
        {
            await TryWriteAsync(clientStream, HttpBadRequest).ConfigureAwait(false);
            return;
        }

        Socket remote;
        try
        {
            remote = upstream == null
                ? await ConnectViaAsync(host, port).ConfigureAwait(false)
                : await ConnectViaAsync(upstream.Host, upstream.Port).ConfigureAwait(false);
        }
        catch
        {
            await SendHttp502Async(clientStream).ConfigureAwait(false);
            return;
        }

        try
        {
            using (var remoteStream = new NetworkStream(remote, false))
            {
                await WriteWithTimeoutAsync(remoteStream, Latin1.GetBytes(forwardRequest)).ConfigureAwait(false);
            }

            await ForwardHttpExchangeAsync(
                    clientSocket,
                    remote,
                    initialBody,
                    requestBodyLength)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                remote.Close();
            }
            catch
            {
            }
        }
    }

    private static bool TryParseHttpRequestLine(
        string request,
        out string method,
        out int firstSpace,
        out int secondSpace)
    {
        method = string.Empty;
        firstSpace = request.IndexOf(' ');
        secondSpace = firstSpace > 0 ? request.IndexOf(' ', firstSpace + 1) : -1;
        if (firstSpace <= 0 || secondSpace < 0) return false;

        method = request.Substring(0, firstSpace).ToUpperInvariant();
        return true;
    }

    private static Task SendSocksFailAsync(Stream stream)
    {
        return TryWriteAsync(stream, SocksFailed);
    }

    private static Task SendHttp502Async(Stream stream)
    {
        return TryWriteAsync(stream, HttpBadGateway);
    }

    private static async Task TryWriteAsync(Stream stream, ReadOnlyMemory<byte> value)
    {
        try
        {
            await WriteWithTimeoutAsync(stream, value).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static ValueTask WriteWithTimeoutAsync(Stream stream, ReadOnlyMemory<byte> value)
    {
        var write = stream.WriteAsync(value);
        return write.IsCompletedSuccessfully ? ValueTask.CompletedTask : new ValueTask(WaitForWriteAsync(write));
    }

    private static async Task WaitForWriteAsync(ValueTask write)
    {
        await write.AsTask().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    private static bool OfferedNoAuth(byte[] head)
    {
        for (var i = 2; i < head.Length; i++)
            if (head[i] == 0x00)
                return true;
        return false;
    }

    private static bool TryParseAuthority(string value, int defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim()
                                             || value.AsSpan().ContainsAny(InvalidHostChars)) return false;

        var separator = value.LastIndexOf(':');
        var hostPart = value;
        if (separator >= 0)
        {
            if (separator == 0 || separator == value.Length - 1 || value.IndexOf(':') != separator
                || !int.TryParse(value.AsSpan(separator + 1), out port) || port is < 1 or > 65535)
                return false;
            hostPart = value.Substring(0, separator);
        }

        if (Uri.CheckHostName(hostPart) is UriHostNameType.Unknown or UriHostNameType.IPv6) return false;
        host = hostPart;
        return true;
    }

    private static bool TryParseHttpTarget(
        string request,
        int firstSpace,
        int secondSpace,
        out string host,
        out int port,
        out string originTarget,
        out string authority)
    {
        host = string.Empty;
        port = 0;
        originTarget = string.Empty;
        authority = string.Empty;
        var target = request.Substring(firstSpace + 1, secondSpace - firstSpace - 1);
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.HostNameType is UriHostNameType.Unknown or UriHostNameType.IPv6
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.Port is < 1 or > 65535)
            return false;

        host = uri.IdnHost;
        port = uri.Port;
        originTarget = uri.PathAndQuery.Length == 0 ? "/" : uri.PathAndQuery;
        authority = uri.IsDefaultPort ? host : host + ":" + port;
        return true;
    }

    private static bool TryBuildForwardRequest(
        string request,
        int firstSpace,
        int secondSpace,
        string originTarget,
        string authority,
        bool absoluteForm,
        out string forwardRequest,
        out long requestBodyLength)
    {
        forwardRequest = string.Empty;
        requestBodyLength = 0;

        var requestLineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
        if (requestLineEnd <= secondSpace || !request.EndsWith("\r\n\r\n", StringComparison.Ordinal)) return false;
        if (!IsHttpToken(request.Substring(0, firstSpace))) return false;

        var version = request.Substring(secondSpace + 1, requestLineEnd - secondSpace - 1);
        if (!IsSupportedHttpVersion(version)) return false;

        if (!TryParseForwardHeaders(
                request,
                requestLineEnd,
                out var headers,
                out var connectionTokens,
                out var hostCount))
            return false;

        if (hostCount > 1 || (version == "HTTP/1.1" && hostCount != 1)) return false;
        if (connectionTokens.Contains("Host")
            || connectionTokens.Contains("Content-Length")
            || connectionTokens.Contains("Transfer-Encoding"))
            return false;

        if (!TryGetRequestBodyLength(headers, out requestBodyLength)) return false;

        var target = absoluteForm
            ? request.Substring(firstSpace + 1, secondSpace - firstSpace - 1)
            : originTarget;
        forwardRequest = BuildForwardRequest(
            request,
            firstSpace,
            target,
            version,
            authority,
            headers,
            connectionTokens);
        return true;
    }

    private static bool IsSupportedHttpVersion(string version)
    {
        return version.Equals("HTTP/1.1", StringComparison.Ordinal)
               || version.Equals("HTTP/1.0", StringComparison.Ordinal);
    }

    private static bool TryParseForwardHeaders(
        string request,
        int requestLineEnd,
        out List<(string Name, string Value)> headers,
        out HashSet<string> connectionTokens,
        out int hostCount)
    {
        headers = new List<(string Name, string Value)>();
        connectionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        hostCount = 0;

        var cursor = requestLineEnd + 2;
        var headersEnd = request.Length - 2;
        while (cursor < headersEnd)
        {
            var end = request.IndexOf("\r\n", cursor, StringComparison.Ordinal);
            if (end < 0) return false;
            if (end == cursor) break;
            if (request[cursor] is ' ' or '\t') return false;

            var colon = request.IndexOf(':', cursor, end - cursor);
            if (colon <= cursor) return false;

            var name = request.Substring(cursor, colon - cursor);
            if (!IsHttpToken(name)) return false;

            var value = request.Substring(colon + 1, end - colon - 1).Trim(' ', '\t');
            if (!IsValidHttpHeaderValue(value)) return false;

            headers.Add((name, value));
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) hostCount++;
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                && !TryAddConnectionTokens(value, connectionTokens))
                return false;

            cursor = end + 2;
        }

        return true;
    }

    private static bool IsValidHttpHeaderValue(string value)
    {
        foreach (var character in value)
            if ((character < 0x20 && character != '\t') || character == 0x7f)
                return false;
        return true;
    }

    private static bool TryAddConnectionTokens(string value, HashSet<string> connectionTokens)
    {
        foreach (var token in value.Split(','))
        {
            var option = token.Trim();
            if (option.Length == 0 || !IsHttpToken(option)) return false;
            connectionTokens.Add(option);
        }

        return true;
    }

    private static bool TryGetRequestBodyLength(
        List<(string Name, string Value)> headers,
        out long requestBodyLength)
    {
        requestBodyLength = 0;
        var transferEncoding = false;
        var contentLengths = new List<long>();

        foreach (var header in headers)
        {
            if (header.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                if (transferEncoding) return false;

                var codings = header.Value.Split(',');
                if (codings.Length == 0
                    || !codings[^1].Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase))
                    return false;
                transferEncoding = true;
                continue;
            }

            if (!header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var item in header.Value.Split(','))
            {
                if (!long.TryParse(
                        item.Trim(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var length)
                    || length < 0)
                    return false;
                contentLengths.Add(length);
            }
        }

        if (transferEncoding) return false;
        if (!AllContentLengthsMatch(contentLengths)) return false;
        if (contentLengths.Count > 0) requestBodyLength = contentLengths[0];
        return true;
    }

    private static bool AllContentLengthsMatch(List<long> contentLengths)
    {
        if (contentLengths.Count <= 1) return true;

        var expectedLength = contentLengths[0];
        for (var index = 1; index < contentLengths.Count; index++)
            if (contentLengths[index] != expectedLength)
                return false;
        return true;
    }

    private static string BuildForwardRequest(
        string request,
        int firstSpace,
        string target,
        string version,
        string authority,
        List<(string Name, string Value)> headers,
        HashSet<string> connectionTokens)
    {
        var builder = new StringBuilder(request.Length + 32);
        builder.Append(request, 0, firstSpace + 1).Append(target).Append(' ').Append(version).Append("\r\n");
        builder.Append("Host: ").Append(authority).Append("\r\n");

        foreach (var header in headers)
        {
            if (ShouldRemoveForwardHeader(header.Name, connectionTokens)) continue;
            builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }

        builder.Append("Connection: close\r\n\r\n");
        return builder.ToString();
    }

    private static bool ShouldRemoveForwardHeader(string name, HashSet<string> connectionTokens)
    {
        return name.Equals("Host", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
               || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
               || connectionTokens.Contains(name);
    }

    private static bool IsHttpToken(string value)
    {
        if (value.Length == 0) return false;
        foreach (var character in value)
            if (character <= 0x20 || character >= 0x7f
                                  || character is '(' or ')' or '<' or '>' or '@' or ',' or ';' or ':' or '\\'
                                      or '"' or '/' or '[' or ']' or '?' or '=' or '{' or '}')
                return false;
        return true;
    }

    private static byte[] BuildSocksGranted(Socket socket)
    {
        var reply = new byte[10];
        reply[0] = 0x05;
        reply[1] = 0x00;
        reply[2] = 0x00;
        reply[3] = 0x01;
        if (socket.LocalEndPoint is not IPEndPoint endpoint ||
            endpoint.Address.AddressFamily != AddressFamily.InterNetwork)
            return reply;

        var address = endpoint.Address.GetAddressBytes();
        Buffer.BlockCopy(address, 0, reply, 4, 4);
        reply[8] = (byte)(endpoint.Port >> 8);
        reply[9] = (byte)endpoint.Port;
        return reply;
    }

    private static bool TryParseSocksTarget(byte[] request, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (request.Length < 7 || request[0] != 0x05 || request[1] != 0x01 || request[2] != 0x00) return false;

        var addressType = request[3];
        var portIndex = 0;
        if (addressType == 0x01)
        {
            if (request.Length < 10) return false;
            host = request[4] + "." + request[5] + "." + request[6] + "." + request[7];
            portIndex = 8;
        }
        else if (addressType == 0x03)
        {
            var hostLength = request[4];
            portIndex = 5 + hostLength;
            if (hostLength == 0 || request.Length < portIndex + 2) return false;
            host = Latin1.GetString(request, 5, hostLength);
        }
        else
        {
            return false;
        }

        port = (request[portIndex] << 8) | request[portIndex + 1];
        return port != 0;
    }

    private static async Task<(byte[] Head, byte[] Extra)> ReadHeadAsync(Stream stream, int max, bool exact,
        byte[]? seed)
    {
        var capacity = Math.Max(max, seed?.Length ?? 0);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, capacity));
        var length = 0;
        try
        {
            if (seed != null && seed.Length > 0)
            {
                Array.Copy(seed, buffer, seed.Length);
                length = seed.Length;
            }

            var deadline = Environment.TickCount64 + 5000;
            var previousLength = 0;
            var boundary = -1;
            while (Environment.TickCount64 < deadline)
            {
                if (!exact && length >= 2 && buffer[0] == 0x05 && length >= 2 + buffer[1])
                {
                    boundary = 2 + buffer[1];
                    break;
                }

                if (!exact && length > 4)
                {
                    var headerEnd = HeaderEnd(buffer, length, previousLength);
                    if (headerEnd >= 0)
                    {
                        boundary = headerEnd + 4;
                        break;
                    }
                }

                if (exact && length >= 4)
                {
                    var needed = buffer[3] == 0x01 ? 10
                        : buffer[3] == 0x04 ? 22
                        : buffer[3] == 0x03 && length >= 5 ? 7 + buffer[4]
                        : 10;
                    if (length >= needed)
                    {
                        boundary = needed;
                        break;
                    }
                }

                if (length >= max) break;
                var remainingMs = deadline - Environment.TickCount64;
                if (remainingMs <= 0) break;
                var remaining = TimeSpan.FromMilliseconds(remainingMs);
                var readSize = Math.Min(1500, max - length);
                int count;
                try
                {
                    count = await stream.ReadAsync(buffer.AsMemory(length, readSize)).AsTask().WaitAsync(remaining)
                        .ConfigureAwait(false);
                }
                catch (IOException)
                {
                    break;
                }
                catch (TimeoutException)
                {
                    break;
                }

                if (count <= 0) break;
                previousLength = length;
                length += count;
            }

            if (boundary < 0) return (Array.Empty<byte>(), Array.Empty<byte>());
            var head = new byte[boundary];
            Array.Copy(buffer, head, boundary);
            if (length == boundary) return (head, Array.Empty<byte>());
            var extra = new byte[length - boundary];
            Array.Copy(buffer, boundary, extra, 0, extra.Length);
            return (head, extra);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int HeaderEnd(byte[] buffer, int length, int from)
    {
        var index = from > 3 ? from - 3 : 0;
        for (; index + 3 < length; index++)
            if (buffer[index] == 13 && buffer[index + 1] == 10 && buffer[index + 2] == 13 && buffer[index + 3] == 10)
                return index;
        return -1;
    }

    private static async Task ForwardHttpExchangeAsync(
        Socket client,
        Socket remote,
        byte[] initialBody,
        long bodyLength)
    {
        using var clientStream = new NetworkStream(client, false);
        using var remoteStream = new NetworkStream(remote, false);
        using var bodyCancellation = new CancellationTokenSource();

        var responseTask = CopyAsync(remoteStream, clientStream, client);
        var bodyTask = CopyExactAsync(
            clientStream, remoteStream, initialBody, bodyLength, bodyCancellation.Token);

        var first = await Task.WhenAny(bodyTask, responseTask).ConfigureAwait(false);
        if (ReferenceEquals(first, responseTask) && !bodyTask.IsCompleted)
        {
            bodyCancellation.Cancel();
            try
            {
                await bodyTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return;
        }

        await bodyTask.ConfigureAwait(false);
        try
        {
            remote.Shutdown(SocketShutdown.Send);
        }
        catch
        {
        }

        await responseTask.ConfigureAwait(false);
    }

    private static async Task CopyExactAsync(
        Stream from,
        Stream to,
        byte[] initial,
        long count,
        CancellationToken cancellationToken)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

        var initialCount = (int)Math.Min(count, initial.LongLength);
        if (initialCount > 0)
        {
            await WriteWithTimeoutAsync(to, initial.AsMemory(0, initialCount)).ConfigureAwait(false);
            count -= initialCount;
        }

        if (count == 0) return;

        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            while (count > 0)
            {
                var requested = (int)Math.Min(count, buffer.Length);
                var read = await from.ReadAsync(
                    buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("HTTP request body ended before Content-Length.");
                await WriteWithTimeoutAsync(to, buffer.AsMemory(0, read)).ConfigureAwait(false);
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task PumpAsync(Socket a, Socket b)
    {
        using var first = new NetworkStream(a, false);
        using var second = new NetworkStream(b, false);
        await Task.WhenAll(
            CopyAsync(first, second, b),
            CopyAsync(second, first, a)).ConfigureAwait(false);
    }

    private static async Task CopyAsync(Stream from, Stream to, Socket destination)
    {
        var buf = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            while (true)
            {
                var count = await from.ReadAsync(buf.AsMemory(0, buf.Length)).ConfigureAwait(false);
                if (count == 0) break;
                await WriteWithTimeoutAsync(to, buf.AsMemory(0, count)).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        try
        {
            destination.Shutdown(SocketShutdown.Send);
        }
        catch
        {
        }
    }

    private sealed class UpstreamConfiguration
    {
        public UpstreamConfiguration(string host, int port)
        {
            Host = host;
            Port = port;
        }

        public string Host { get; }
        public int Port { get; }
    }
}