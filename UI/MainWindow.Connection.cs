using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EasyFM350.Wpf.Backend.Config;
using EasyFM350.Wpf.Backend.Modem;
using EasyFM350.Wpf.Backend.Network;

namespace EasyFM350.Wpf.UI;

public partial class MainWindow
{
    private readonly DispatcherTimer _reconnectTimer = new();
    private int _disconnectPending;
    private volatile string? _pdnDns1;
    private volatile string? _pdnDns2;
    private volatile string? _pdnIpv6;
    private volatile bool _proxyBusy;
    private bool _proxyWanted;
    private int _reconnectAfterDisconnect;
    private int _reconnectLeft;
    private bool _suppressNetToggles;
    private bool _tunWanted;
    private string? _upstreamHost;
    private string? _upstreamRouteGateway;
    private string? _upstreamRouteIface;
    private string? _upstreamRouteIp;

    private void UpdatePower()
    {
        PwrText.Text = _connActive ? "ON" : "OFF";
        PwrText.Foreground = _connActive ? _brSuccess : _brText;
        PwrDot.Fill = !_modem.IsOpen ? _brError : _connActive ? _brSuccess : _brWarning;
        BtnPower.IsEnabled = _modem.IsOpen;
    }

    private void StartConnectingBlink()
    {
        PwrDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.2, TimeSpan.FromSeconds(0.9))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    private void StopConnectingBlink()
    {
        PwrDot.BeginAnimation(OpacityProperty, null);
        PwrDot.Opacity = 1.0;
    }

    private void OnPower(object sender, RoutedEventArgs e)
    {
        CancelReconnect();
        if (_connActive) OnDisconnect();
        else OnConnect();
    }

    private void OnConnect()
    {
        if (!_modem.IsOpen || _connBusy) return;

        _connBusy = true;
        StartConnectingBlink();

        var apn = ApnPolicy.NormalizeForConfiguration(InpApn.Text);
        var pdp = InpPdp.SelectedItem as string ?? "IPv4";
        var authentication = Math.Max(0, InpAuth.SelectedIndex);
        var user = AtInput.Normalize(InpUser.Text);
        var password = AtInput.Normalize(InpPass.Password, false);
        var proxyInput = InpProxyEndpoint.Text.Trim();
        if (!ProxyEndpoint.TryParse(proxyInput, out var proxyHost, out var proxyPort) && proxyInput.Length > 0)
        {
            StopConnectingBlink();
            _connBusy = false;
            LogError("invalid upstream proxy endpoint");
            return;
        }

        _upstreamHost = proxyHost.Length > 0 && proxyPort > 0 ? proxyHost : null;

        if (!ApnPolicy.IsValidForConfiguration(apn)
            || (authentication > 0 && (!AtInput.IsSafeValue(user) || !AtInput.IsSafeValue(password))))
        {
            StopConnectingBlink();
            _connBusy = false;
            return;
        }

        if (!_exec.Post(() => ConnectWorker(apn, pdp, authentication, user, password, proxyHost, proxyPort)))
        {
            StopConnectingBlink();
            _connBusy = false;
        }
    }

    private void ConnectWorker(
        string apn,
        string pdp,
        int authentication,
        string user,
        string password,
        string proxyHost,
        int proxyPort)
    {
        _pdnIp = null;
        _pdnGateway = null;
        _pdnIpv6 = null;
        _iface = null;
        _ifaceIndex = 0;
        _dataCid = 0;
        _ownsDataContext = false;
        _pdnDeact = false;
        _cgpaddrMiss = 0;
        string? ip = null;
        string? subnetMask = null;
        string? gateway = null;
        var cid = 0;
        var activatedHere = false;

        try
        {
            var pinReady = false;
            var pinEmpty = false;
            var euiccEmpty = false;
            for (var attempt = 0; attempt < 4 && !pinReady; attempt++)
            {
                if (attempt > 0) Thread.Sleep(1500);
                var pin = _modem.Send("AT+CPIN?");
                var pinFields = Backend.Modem.Modem.Fields(pin, "+CPIN");
                pinReady = pinFields.Length > 0 && pinFields[0].Equals("READY", StringComparison.Ordinal);
                euiccEmpty = pinFields.Length > 0 &&
                             pinFields[0].Equals("EMPTY_EUICC", StringComparison.OrdinalIgnoreCase);
                if (euiccEmpty) break;
                pinEmpty = pin.Length == 0;
            }

            if (euiccEmpty)
                throw new InvalidOperationException(
                    "eSIM has no enabled profile — enable or download one in the eSIM panel");
            if (!pinReady)
                throw new InvalidOperationException(pinEmpty ? "CPIN: modem timeout" : "SIM not ready");

            WaitForRegistration();

            var context = _settingsService.ResolvePdpForConfiguration(apn, pdp);
            cid = context.Cid;
            if (cid < 1) throw new InvalidOperationException("No usable PDP context");

            var desiredType = PdpProtocol.ToModemValue(pdp);
            var typeMatches = string.Equals(context.Type ?? string.Empty, desiredType,
                StringComparison.OrdinalIgnoreCase);
            var activeMatches = context.IsActive && typeMatches
                                                 && string.Equals(context.ActiveApn ?? string.Empty, apn,
                                                     StringComparison.OrdinalIgnoreCase);
            var configuredMatches = typeMatches
                                    && string.Equals(context.ConfiguredApn ?? string.Empty, apn,
                                        StringComparison.OrdinalIgnoreCase);
            var mustDefine = !activeMatches && !configuredMatches;

            if (mustDefine && context.IsActive)
                throw new InvalidOperationException("Refusing to redefine an unrelated active PDP context");

            if (mustDefine)
            {
                var pdpResponse = _modem.Send(ModemCommands.DefinePdp(cid, pdp, apn), 4000);
                if (!Backend.Modem.Modem.IsOk(pdpResponse))
                    throw new InvalidOperationException(pdpResponse.Length == 0
                        ? "CGDCONT: modem timeout"
                        : "CGDCONT rejected");
            }

            if (!context.IsActive)
            {
                var authenticationResponse = _modem.Send(
                    ModemCommands.SetAuthentication(cid, authentication, user, password), 4000);
                if (!Backend.Modem.Modem.IsOk(authenticationResponse))
                    throw new InvalidOperationException(
                        authenticationResponse.Length == 0 ? "CGAUTH: modem timeout" : "CGAUTH rejected");
            }

            if (!context.IsActive)
            {
                const int maxActivationAttempts = 3;
                for (var attempt = 1;; attempt++)
                {
                    var activation = _modem.Send(ModemCommands.ActivatePdp(cid, true), 30000, slowCommand: true);
                    if (Backend.Modem.Modem.IsOk(activation))
                    {
                        activatedHere = true;
                        break;
                    }

                    if (attempt >= maxActivationAttempts)
                        throw new InvalidOperationException(
                            activation.Length == 0 ? "CGACT: modem timeout"
                            : apn.Length == 0 ? "CGACT rejected (" + activation.Trim() +
                                                "); empty APN — set the operator APN"
                            : "CGACT rejected (" + activation.Trim() + ")");
                    AppendLog("[pdn] CGACT attempt " + attempt + " failed: " + activation.Trim() + " — retrying");
                    Thread.Sleep(3000);
                    if (_realExit) throw new OperationCanceledException("Application shutdown.");
                    if (attempt == maxActivationAttempts - 1)
                    {
                        AppendLog("[pdn] re-creating PDP context before the last attempt");
                        try
                        {
                            _modem.Send(ModemCommands.ActivatePdp(cid, false), 10000, true, true);
                        }
                        catch (Exception exception)
                        {
                            LogError("pdn recovery deactivate: " + exception.Message);
                        }

                        Thread.Sleep(1000);
                        try
                        {
                            var redefine = _modem.Send(ModemCommands.DefinePdp(cid, pdp, apn), 4000);
                            if (!Backend.Modem.Modem.IsOk(redefine))
                                AppendLog("[pdn] context re-create rejected: " + redefine.Trim());
                        }
                        catch (Exception exception)
                        {
                            LogError("pdn recovery define: " + exception.Message);
                        }
                    }
                }
            }

            _dataCid = cid;

            string? operatorName = null;
            var copsFields = Backend.Modem.Modem.Fields(_modem.Send("AT+COPS?"), "+COPS");
            if (copsFields.Length > 2 && copsFields[2].Length > 0) operatorName = copsFields[2];

            var addressResponse = _modem.Send(ModemCommands.ReadPdpAddress(cid), 4000);
            var dnsResponse = _modem.Send(ModemCommands.ReadDns(cid), 4000);
            var dynamicResponse = _modem.Send(ModemCommands.ReadDynamicPdp(cid), 4000);

            PdpContext.TryParseAddresses(addressResponse, cid, out var ipv4, out var ipv6);
            PdpContext.TryParseDns(dnsResponse, cid, out var primaryDns, out var secondaryDns);
            if (PdpContext.TryParseIpv4(dynamicResponse, cid, out var ipv4Parameters))
            {
                var parameters = ipv4Parameters!;
                ipv4 ??= parameters.LocalAddress;
                subnetMask = parameters.SubnetMask;
                gateway = parameters.Gateway;
                primaryDns = parameters.PrimaryDns ?? primaryDns;
                secondaryDns = parameters.SecondaryDns ?? secondaryDns;
            }
            else
            {
                var active = PdpContext.FindActive(PdpContext.ParseActive(dynamicResponse), cid);
                ipv4 ??= active?.LocalIpv4;
                subnetMask ??= active?.LocalIpv4SubnetMask;
                ipv6 ??= active?.LocalIpv6;
                gateway ??= active?.GatewayIpv4;
                primaryDns ??= active?.PrimaryDns;
                secondaryDns ??= active?.SecondaryDns;
            }

            if (ipv4 == null && ipv6 == null) throw new InvalidOperationException("PDN active but no IP");
            _pdnIpv6 = ipv6;
            if (primaryDns == null && secondaryDns != null)
            {
                primaryDns = secondaryDns;
                secondaryDns = null;
            }

            _pdnDns1 = primaryDns;
            _pdnDns2 = secondaryDns;

            if (ipv4 == null)
                throw new NotSupportedException(
                    "IPv6-only PDP is active, but Windows NCM IPv6 configuration is not implemented");

            ip = ipv4;
            if (subnetMask == null || subnetMask == "0.0.0.0")
            {
                subnetMask = PdpContext.DefaultIpv4SubnetMask;
                AppendLog("[pdn] CGCONTRDP: no subnet mask reported — assuming " + subnetMask);
            }

            if (gateway == null)
            {
                gateway = NetConfig.OnLinkGateway;
                AppendLog("[pdn] CGCONTRDP: no gateway reported — on-link point-to-point routing");
            }

            _proxy.SetUpstream(proxyHost.Length > 0 && proxyPort > 0 ? proxyHost : null, proxyPort);
            _iface = NetConfig.FindNcmInterface(out var interfaceIndex);
            if (_realExit) throw new OperationCanceledException("Application shutdown.");
            if (_iface == null || interfaceIndex < 1) throw new InvalidOperationException("NCM interface not found");
            _ifaceIndex = interfaceIndex;

            string applyLog;
            lock (_netSync)
            {
                applyLog = NetConfig.Apply(_iface, ip, subnetMask, gateway, primaryDns, secondaryDns);
            }

            LogNetConfigOutput(applyLog);
            if (_realExit) throw new OperationCanceledException("Application shutdown.");

            var check = _modem.Send(ModemCommands.ReadPdpAddress(cid), 3000, true);
            if (_pdnDeact || check.Length == 0 || !PdpContext.HasUsableAddress(check, cid))
                throw new InvalidOperationException(check.Length == 0
                    ? "CGPADDR: modem timeout after connect"
                    : "PDN dropped during connect");

            _pdnIp = ip;
            _pdnGateway = gateway;
            _ownsDataContext = activatedHere;
            _lastPdnIp = ip;
            _lastPdnGateway = gateway;
            _lastIface = _iface;
            _lastIfaceIndex = interfaceIndex;
            OnUi(() => CompleteConnect(operatorName));
        }
        catch (Exception exception)
        {
            RollBackConnect(cid, activatedHere, ip, gateway);
            OnUi(() => FailConnect(exception.Message));
        }
    }

    private void WaitForRegistration()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (_realExit) throw new OperationCanceledException("Application shutdown.");
            var cereg = Backend.Modem.Modem.Fields(_modem.Send("AT+CEREG?"), "+CEREG");
            var cgreg = Backend.Modem.Modem.Fields(_modem.Send("AT+CGREG?", 3000, true), "+CGREG");
            if (IsRegistered(cereg) || IsRegistered(cgreg)) return;
            Thread.Sleep(3000);
        }

        throw new InvalidOperationException("network registration timeout");
    }

    private static bool IsRegistered(string[] fields)
    {
        return fields.Length > 1 && (fields[1] == "1" || fields[1] == "5");
    }

    private void LogNetConfigOutput(string output)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.Equals("OK.", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("OK!",
                                                                              StringComparison.OrdinalIgnoreCase)
                                                                          || trimmed.Equals("ОК.",
                                                                              StringComparison.OrdinalIgnoreCase) ||
                                                                          trimmed.Equals("ОК!",
                                                                              StringComparison.OrdinalIgnoreCase))
                continue;
            if (trimmed.Equals("Элемент не найден.", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Element not found.", StringComparison.OrdinalIgnoreCase)) continue;
            AppendLog("[net] " + trimmed);
        }
    }

    private void CompleteConnect(string? operatorName)
    {
        _connBusy = false;
        _reconnectLeft = 0;
        StopConnectingBlink();
        if (!_modem.IsOpen) return;
        TxtConnState.Text = string.Empty;
        FadeIn(TxtConnState);
        TxtConnState.Foreground = _brSuccess;
        TxtOperIp.Text = (operatorName ?? "?") + " (" + (_pdnIp ?? _pdnIpv6 ?? "?") + ")";
        _connActive = true;
        SwProxy.IsEnabled = _pdnIp != null;
        SwTun.IsEnabled = _pdnIp != null;
        UpdateActivityCadence();
        UpdatePower();
        _pollTimer.Stop();
        _pollTimer.Interval = ModemPollInterval;
        _pollTimer.Start();
        Poll(true);
        ReadApnFromModem();
        if (_proxyWanted && !_proxy.Running)
        {
            _suppressNetToggles = true;
            SwProxy.IsChecked = true;
            _suppressNetToggles = false;
            RequestProxyState(true, false);
        }

        if (_tunWanted && !_tunOn)
        {
            _suppressNetToggles = true;
            SwTun.IsChecked = true;
            _suppressNetToggles = false;
            RequestTunnelState(true, false);
        }

        TxtProxyState.Text = Lang.T("net_state") + ": " + Lang.T(_proxy.Running ? "net_on" : "net_off");
    }

    private void FailConnect(string message)
    {
        _connBusy = false;
        StopConnectingBlink();
        LogError("connect failed: " + message);
        if (!_modem.IsOpen) return;
        if (_reconnectLeft > 0)
        {
            _reconnectLeft--;
            ScheduleReconnect(10);
            return;
        }

        TxtConnState.Text = Lang.T("st_error");
        FadeIn(TxtConnState);
        TxtConnState.Foreground = _brError;
        _connActive = false;
        UpdateActivityCadence();
        UpdatePower();
    }

    private void RollBackConnect(int cid, bool activatedHere, string? ip, string? gateway)
    {
        var iface = _iface;
        var dns1 = _pdnDns1;
        var dns2 = _pdnDns2;
        if (activatedHere && cid > 0)
            try
            {
                var response = _modem.Send(ModemCommands.ActivatePdp(cid, false), 30000, slowCommand: true);
                if (!Backend.Modem.Modem.IsOk(response)) LogError("connect rollback PDN: deactivate rejected");
            }
            catch (Exception exception)
            {
                LogError("connect rollback PDN: " + exception.Message);
            }

        if (iface != null && ip != null)
            try
            {
                string cleanupLog;
                lock (_netSync)
                {
                    cleanupLog = NetConfig.Cleanup(iface, gateway, dns1, dns2);
                }

                LogNetConfigOutput(cleanupLog);
            }
            catch (Exception exception)
            {
                LogError("connect rollback network: " + exception.Message);
            }

        _pdnIp = null;
        _pdnGateway = null;
        _pdnIpv6 = null;
        _pdnDns1 = null;
        _pdnDns2 = null;
        _dataCid = 0;
        _ownsDataContext = false;
        _iface = null;
        _ifaceIndex = 0;
    }

    private void OnDisconnect()
    {
        PostDisconnectTeardown();
    }

    private bool PostDisconnectTeardown()
    {
        if (Interlocked.Exchange(ref _disconnectPending, 1) != 0) return true;
        if (_exec.Post(DisconnectWorker)) return true;
        Interlocked.Exchange(ref _disconnectPending, 0);
        return false;
    }

    private void RestartConnection()
    {
        if (!_connActive || _connBusy) return;
        Interlocked.Exchange(ref _reconnectAfterDisconnect, 1);
        if (!PostDisconnectTeardown())
            Interlocked.Exchange(ref _reconnectAfterDisconnect, 0);
    }

    private void ScheduleReconnect(int seconds)
    {
        _reconnectTimer.Stop();
        _reconnectTimer.Interval = TimeSpan.FromSeconds(seconds);
        _reconnectTimer.Start();
    }

    private void OnReconnectTick(object? sender, EventArgs e)
    {
        _reconnectTimer.Stop();
        if (_realExit || !_modem.IsOpen || _connActive || _connBusy) return;
        OnConnect();
    }

    private void CancelReconnect()
    {
        _reconnectTimer.Stop();
        _reconnectLeft = 0;
    }

    private void DisconnectWorker()
    {
        var gateway = _pdnGateway;
        var cid = _dataCid;
        var ownsDataContext = _ownsDataContext;
        var wasTunnelEnabled = _tunOn || _tunBusy;
        var iface = _iface;
        var dns1 = _pdnDns1;
        var dns2 = _pdnDns2;
        _pdnIp = null;
        _pdnGateway = null;
        _pdnIpv6 = null;
        _pdnDns1 = null;
        _pdnDns2 = null;
        _dataCid = 0;
        _ownsDataContext = false;
        var cleanupSucceeded = iface == null;

        if (wasTunnelEnabled && iface != null && gateway != null)
            TryDisconnectStep("tunnel", () =>
            {
                lock (_netSync)
                {
                    NetConfig.TunnelOff(iface, gateway, false);
                }
            });
        _tunOn = false;

        if (_proxy.Running) TryDisconnectStep("proxy", _proxy.Stop);
        TryDisconnectStep("system proxy", () =>
        {
            if (!_systemProxy.Restore())
                throw new InvalidOperationException("Windows proxy settings could not be restored.");
        });
        TryDisconnectStep("upstream route", RemoveUpstreamRouteCore);
        if (cid > 0 && ownsDataContext)
            TryDisconnectStep("PDN", () =>
            {
                var response = _modem.Send(ModemCommands.ActivatePdp(cid, false), 30000, slowCommand: true);
                if (!Backend.Modem.Modem.IsOk(response))
                    throw new InvalidOperationException(response.Length == 0
                        ? "modem timeout"
                        : "CGACT deactivate rejected");
            });

        if (iface != null)
            try
            {
                string cleanupLog;
                lock (_netSync)
                {
                    cleanupLog = NetConfig.Cleanup(iface, gateway, dns1, dns2);
                }

                LogNetConfigOutput(cleanupLog);
                cleanupSucceeded = true;
            }
            catch (Exception exception)
            {
                LogError("disconnect network: " + exception.Message);
            }

        if (cleanupSucceeded)
        {
            _iface = null;
            _ifaceIndex = 0;
            _lastPdnIp = null;
            _lastPdnGateway = null;
            _lastIface = null;
            _lastIfaceIndex = 0;
        }

        OnUi(CompleteDisconnect);
    }

    private void TryDisconnectStep(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            LogError("disconnect " + name + ": " + exception.Message);
        }
    }

    private void CompleteDisconnect()
    {
        Interlocked.Exchange(ref _disconnectPending, 0);
        var reconnect = Interlocked.Exchange(ref _reconnectAfterDisconnect, 0) != 0;
        TxtConnState.Text = string.Empty;
        FadeIn(TxtConnState);
        TxtConnState.Foreground = _brText;
        TxtOperIp.Text = string.Empty;
        _connActive = false;
        UpdateActivityCadence();
        UpdatePower();
        if (_proxy.Running) _proxy.Stop();
        if (!_systemProxy.Restore()) LogError("disconnect: Windows proxy settings could not be restored");
        _proxyBusy = false;
        _suppressNetToggles = true;
        SwProxy.IsChecked = false;
        SwTun.IsChecked = false;
        _suppressNetToggles = false;
        SwProxy.IsEnabled = false;
        SwTun.IsEnabled = false;
        TxtProxyAddr.Text = string.Empty;
        TxtProxyState.Text = Lang.T("net_state") + ": " + Lang.T("net_off");
        TxtTunState.Text = Lang.T("net_state") + ": " + Lang.T("net_off");
        if (reconnect)
        {
            if (!_modem.IsOpen) return;
            _reconnectLeft = 2;
            ScheduleReconnect(4);
        }
    }

    private void WatchModem()
    {
        if (_modem.IsOpen || _exec.Pending > 0) return;
        if (_watchMisses >= 12 && _watchMisses % 6 != 0)
        {
            _watchMisses++;
            return;
        }

        _exec.Post(() =>
        {
            var port = _modem.FindAtPort();
            if (port == null)
            {
                OnUi(() => _watchMisses++);
                return;
            }

            if (_realExit) return;
            if (_modem.Open(port))
                OnUi(() =>
                {
                    if (_realExit)
                    {
                        _modem.Close();
                        return;
                    }

                    if (!_modem.IsOpen)
                    {
                        SetOffline("race after open");
                        return;
                    }

                    _watchMisses = 0;
                    TxtConnState.Text = "";
                    FadeIn(TxtConnState);
                    TxtConnState.Foreground = _brText;
                    UpdateActivityCadence();
                    _pollTimer.Start();
                    UpdatePower();
                    Poll();
                    LoadInitialSettings();
                    QuerySimSlot();
                    if (EsimOverlay.Visibility == Visibility.Visible) LoadEsimData();
                });
            else
                OnUi(() => _watchMisses++);
        });
    }

    private void SetOffline(string? reason)
    {
        StopConnectingBlink();
        CancelReconnect();
        _connBusy = false;
        _pollBusy = false;
        _pollTimer.Stop();
        _bandsApplyTimer.Stop();
        SetApplyBusy(false);
        if (reason != null) LogError("offline: " + reason);
        if (_proxy.Running) _proxy.Stop();
        if (!_systemProxy.Restore()) LogError("offline: Windows proxy settings could not be restored");
        var lostIp = _pdnIp;
        var lostGateway = _pdnGateway;
        var lostIface = _iface;
        var lostDns1 = _pdnDns1;
        var lostDns2 = _pdnDns2;
        _pdnIp = null;
        _pdnGateway = null;
        _pdnIpv6 = null;
        _pdnDns1 = null;
        _pdnDns2 = null;
        _dataCid = 0;
        _ownsDataContext = false;
        _tunOn = false;
        _proxyBusy = false;
        _iface = null;
        _ifaceIndex = 0;
        if (lostIface != null)
        {
            _lastPdnIp = lostIp;
            _lastPdnGateway = lostGateway;
            _lastIface = lostIface;
            _exec.Post(() =>
            {
                if (_realExit) return;
                try
                {
                    RemoveUpstreamRouteCore();
                    string cleanupLog;
                    lock (_netSync)
                    {
                        cleanupLog = NetConfig.Cleanup(lostIface, lostGateway, lostDns1, lostDns2);
                    }

                    LogNetConfigOutput(cleanupLog);
                    _lastPdnIp = null;
                    _lastPdnGateway = null;
                    _lastIface = null;
                    _lastIfaceIndex = 0;
                }
                catch (Exception ex)
                {
                    LogError("offline cleanup: " + ex.Message);
                }
            });
        }

        _suppressNetToggles = true;
        SwProxy.IsChecked = false;
        SwTun.IsChecked = false;
        _suppressNetToggles = false;
        SwProxy.IsEnabled = false;
        SwTun.IsEnabled = false;
        TxtProxyAddr.Text = string.Empty;
        TxtProxyState.Text = Lang.T("net_state") + ": " + Lang.T("net_off");
        TxtTunState.Text = Lang.T("net_state") + ": " + Lang.T("net_off");
        _connActive = false;
        UpdateActivityCadence();
        UpdatePower();
        TxtOperIp.Text = "";
        TxtCa.Text = "CA: --";
        TxtConnState.Text = "—";
        FadeIn(TxtConnState);
        TxtConnState.Foreground = _brDim;
        TxtRsrp.Text = "--";
        TxtRsrp.Foreground = _brText;
        TxtRsrpUnit.Text = "";
        TxtGrid.Text = "";
        Chart.Clear();
        _lastTempC = null;
        _simSlot = -1;
        _suppressSimToggle = true;
        RbSim.IsChecked = false;
        RbEsim.IsChecked = false;
        _suppressSimToggle = false;
        RbSim.IsEnabled = false;
        RbEsim.IsEnabled = false;
        HideEsimOverlay();
        _lastSignal = null;
    }

    private void Poll(bool forceFull = false)
    {
        if (_realExit || !_modem.IsOpen || _pollBusy || (!forceFull && _exec.Pending > 0)) return;
        _pollBusy = true;
        var cid = _dataCid;
        var includePdn = cid > 0 && (_pdnIp != null || _pdnIpv6 != null);
        var foreground = IsVisible && !_trayMode && WindowState != WindowState.Minimized &&
                         PageDash.Visibility == Visibility.Visible;
        if (!foreground && !forceFull)
        {
            if (!_exec.Post(() => PollHealthOnce(cid, includePdn))) _pollBusy = false;
            return;
        }

        var withTemp = forceFull || ++_tempTick % 4 == 0;
        if (!_exec.Post(() => PollOnce(withTemp, cid, includePdn))) _pollBusy = false;
    }

    private void PollHealthOnce(int cid, bool includePdn)
    {
        try
        {
            var response = _signalPolling.ReadHealth(cid, includePdn);
            OnUi(() =>
            {
                _pollBusy = false;
                if (_realExit || !_modem.IsOpen || !includePdn) return;
                UpdatePdnHealth(response, cid);
            });
        }
        catch (Exception exception)
        {
            LogError("health poll: " + exception.Message);
            OnUi(() => _pollBusy = false);
        }
    }

    private void PollOnce(bool withTemp, int cid, bool includePdn)
    {
        try
        {
            var result = _signalPolling.Read(withTemp, cid, includePdn);
            OnUi(() =>
            {
                _pollBusy = false;
                if (_realExit || !_modem.IsOpen) return;
                UpdatePdnHealth(result.PdnResponse, cid);
                RenderSignal(result.Signal);
            });
        }
        catch (Exception ex)
        {
            LogError("poll: " + ex.Message);
            OnUi(() => _pollBusy = false);
        }
    }

    private void UpdatePdnHealth(string? response, int cid)
    {
        if (cid < 1 || cid != _dataCid || string.IsNullOrEmpty(response)) return;

        if (PdpContext.TryParseAddresses(response, cid, out var ipv4, out var ipv6))
        {
            _cgpaddrMiss = 0;
            var currentIpv4 = _pdnIp;
            var currentIpv6 = _pdnIpv6;
            var addressChanged = (currentIpv4 != null
                                  && !string.Equals(currentIpv4, ipv4, StringComparison.OrdinalIgnoreCase))
                                 || (currentIpv6 != null
                                     && ipv6 != null
                                     && !string.Equals(currentIpv6, ipv6, StringComparison.OrdinalIgnoreCase));
            if (addressChanged && (currentIpv4 != null || currentIpv6 != null))
            {
                LogError("pdn address changed — reconnect");
                RestartConnection();
            }

            return;
        }

        if (++_cgpaddrMiss >= 2 && (_pdnIp != null || _pdnIpv6 != null))
        {
            LogError("pdn lost (cgpaddr) — reconnect");
            RestartConnection();
        }
    }

    private void OnProxyToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressNetToggles) return;
        RequestProxyState(SwProxy.IsChecked == true, true);
    }

    private void RequestProxyState(bool enabled, bool updatePreference)
    {
        if (updatePreference) _proxyWanted = enabled;
        if (_proxyBusy)
        {
            _suppressNetToggles = true;
            SwProxy.IsChecked = _proxy.Running;
            _suppressNetToggles = false;
            return;
        }

        var ip = _pdnIp;
        var gateway = _pdnGateway;
        var iface = _iface;
        var interfaceIndex = _ifaceIndex;
        if (enabled && (ip == null || gateway == null || iface == null || interfaceIndex < 1))
        {
            if (updatePreference) _proxyWanted = false;
            _suppressNetToggles = true;
            SwProxy.IsChecked = false;
            _suppressNetToggles = false;
            return;
        }

        _proxyBusy = true;
        SwProxy.IsEnabled = false;
        if (!_exec.Post(() => ConfigureProxy(enabled, ip, gateway, iface, interfaceIndex)))
        {
            _proxyBusy = false;
            SwProxy.IsEnabled = true;
            _suppressNetToggles = true;
            SwProxy.IsChecked = _proxy.Running;
            _suppressNetToggles = false;
        }
    }

    private void ConfigureProxy(bool enabled, string? ip, string? gateway, string? iface, int interfaceIndex)
    {
        var active = false;
        string? address = null;
        string? error = null;
        try
        {
            if (enabled)
            {
                if (_realExit || ip == null || gateway == null || iface == null || interfaceIndex < 1
                    || _pdnIp != ip || _pdnGateway != gateway || _iface != iface || _ifaceIndex != interfaceIndex)
                    throw new OperationCanceledException("PDN changed while starting the proxy.");

                lock (_netSync)
                {
                    LogNetConfigOutput(NetConfig.ProxyRouteOn(iface, gateway, _tunOn));
                }

                AddUpstreamRouteCore(iface, gateway);
                if (_realExit || _pdnIp != ip || _pdnGateway != gateway || _iface != iface)
                    throw new OperationCanceledException("PDN changed while starting the proxy.");

                _proxy.Start(0, ip, interfaceIndex);
                address = "127.0.0.1:" + _proxy.Port;
                _systemProxy.Enable(address);
                active = true;
            }
            else
            {
                _proxy.Stop();
                if (!_systemProxy.Restore())
                    throw new InvalidOperationException("Windows proxy settings could not be restored.");
                RemoveUpstreamRouteCore();
                if (iface != null && gateway != null)
                    lock (_netSync)
                    {
                        LogNetConfigOutput(NetConfig.ProxyRouteOff(iface, gateway, _tunOn));
                    }
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            try
            {
                _proxy.Stop();
            }
            catch
            {
            }

            if (!_systemProxy.Restore())
                error += " Windows proxy settings also could not be restored.";
            try
            {
                RemoveUpstreamRouteCore();
            }
            catch (Exception cleanup)
            {
                error += " Route cleanup: " + cleanup.Message;
            }

            if (iface != null && gateway != null)
                try
                {
                    lock (_netSync)
                    {
                        NetConfig.ProxyRouteOff(iface, gateway, _tunOn);
                    }
                }
                catch (Exception cleanup)
                {
                    error += " Default-route cleanup: " + cleanup.Message;
                }
        }

        OnUi(() =>
        {
            _proxyBusy = false;
            SwProxy.IsEnabled = _connActive;
            _suppressNetToggles = true;
            SwProxy.IsChecked = active;
            _suppressNetToggles = false;
            TxtProxyAddr.Text = active ? address ?? string.Empty : string.Empty;
            TxtProxyState.Text = Lang.T("net_state") + ": " + Lang.T(active ? "net_on" : "net_off");
            if (error != null)
            {
                if (enabled) _proxyWanted = false;
                LogError("proxy " + (enabled ? "start" : "stop") + " failed: " + error);
            }
        });
    }

    private void AddUpstreamRouteCore(string iface, string gateway)
    {
        var host = _upstreamHost;
        if (host == null) return;

        IPAddress? address = null;
        if (!IPAddress.TryParse(host, out address))
        {
            var candidates = Dns.GetHostAddressesAsync(host)
                .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            foreach (var candidate in candidates)
                if (candidate.AddressFamily == AddressFamily.InterNetwork)
                {
                    address = candidate;
                    break;
                }
        }

        if (address == null || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("No IPv4 address for upstream proxy " + host + ".");

        var target = address.ToString();
        if (_upstreamRouteIp == target && _upstreamRouteIface == iface && _upstreamRouteGateway == gateway)
            return;
        RemoveUpstreamRouteCore();
        lock (_netSync)
        {
            NetConfig.HostRoute(iface, gateway, target, true);
        }

        _upstreamRouteIp = target;
        _upstreamRouteIface = iface;
        _upstreamRouteGateway = gateway;
    }

    private void RemoveUpstreamRouteCore()
    {
        var routed = _upstreamRouteIp;
        var iface = _upstreamRouteIface;
        var gateway = _upstreamRouteGateway;
        if (routed != null && iface != null && gateway != null)
            lock (_netSync)
            {
                NetConfig.HostRoute(iface, gateway, routed, false);
            }

        _upstreamRouteIp = null;
        _upstreamRouteIface = null;
        _upstreamRouteGateway = null;
    }

    private void HandleProxyDied()
    {
        if (!_exec.Post(() =>
            {
                if (_proxy.Running) return;
                var iface = _iface;
                var gateway = _pdnGateway;
                try
                {
                    RemoveUpstreamRouteCore();
                }
                catch (Exception exception)
                {
                    LogError("proxy route cleanup: " + exception.Message);
                }

                if (!_systemProxy.Restore()) LogError("proxy died: Windows proxy settings could not be restored");
                if (iface != null && gateway != null)
                    try
                    {
                        lock (_netSync)
                        {
                            NetConfig.ProxyRouteOff(iface, gateway, _tunOn);
                        }
                    }
                    catch (Exception exception)
                    {
                        LogError("proxy default-route cleanup: " + exception.Message);
                    }

                OnUi(() =>
                {
                    _proxyBusy = false;
                    _proxyWanted = false;
                    SwProxy.IsEnabled = _connActive;
                    _suppressNetToggles = true;
                    SwProxy.IsChecked = false;
                    _suppressNetToggles = false;
                    TxtProxyAddr.Text = string.Empty;
                    TxtProxyState.Text = Lang.T("net_state") + ": " + Lang.T("net_off");
                });
            })) LogError("proxy died: cleanup executor unavailable");
    }

    private void OnTunToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressNetToggles) return;
        RequestTunnelState(SwTun.IsChecked == true, true);
    }

    private void RequestTunnelState(bool enabled, bool updatePreference)
    {
        if (updatePreference) _tunWanted = enabled;
        if (_tunBusy)
        {
            _suppressNetToggles = true;
            SwTun.IsChecked = _tunOn;
            _suppressNetToggles = false;
            return;
        }

        var ip = _pdnIp;
        var gateway = _pdnGateway;
        var iface = _iface;
        if (enabled && (ip == null || gateway == null || iface == null))
        {
            if (updatePreference) _tunWanted = false;
            _suppressNetToggles = true;
            SwTun.IsChecked = false;
            _suppressNetToggles = false;
            return;
        }

        if (!enabled && (gateway == null || iface == null))
        {
            _tunOn = false;
            TxtTunState.Text = Lang.T("net_state") + ": " + Lang.T("net_off");
            return;
        }

        _tunBusy = true;
        SwTun.IsEnabled = false;
        if (!_exec.Post(() => ConfigureTunnel(enabled, ip, gateway!, iface!)))
        {
            _tunBusy = false;
            SwTun.IsEnabled = true;
            _suppressNetToggles = true;
            SwTun.IsChecked = _tunOn;
            _suppressNetToggles = false;
        }
    }

    private void ConfigureTunnel(bool enabled, string? ip, string gateway, string iface)
    {
        var priorState = _tunOn;
        string? error = null;
        try
        {
            if (_realExit) throw new OperationCanceledException("Application shutdown.");
            if (enabled)
            {
                if (ip == null || _pdnIp != ip || _pdnGateway != gateway || _iface != iface)
                    throw new OperationCanceledException("PDN changed while enabling tunnel routing.");
                string output;
                lock (_netSync)
                {
                    output = NetConfig.TunnelOn(iface, gateway);
                }

                LogNetConfigOutput(output);
                if (_pdnIp != ip || _pdnGateway != gateway || _iface != iface)
                {
                    lock (_netSync)
                    {
                        NetConfig.TunnelOff(iface, gateway, _proxy.Running);
                    }

                    throw new OperationCanceledException("PDN changed while enabling tunnel routing.");
                }
            }
            else
            {
                string output;
                lock (_netSync)
                {
                    output = NetConfig.TunnelOff(iface, gateway, _proxy.Running);
                }

                LogNetConfigOutput(output);
            }

            _tunOn = enabled;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            _tunOn = priorState;
        }
        finally
        {
            _tunBusy = false;
        }

        OnUi(() =>
        {
            SwTun.IsEnabled = _connActive;
            _suppressNetToggles = true;
            SwTun.IsChecked = _tunOn;
            _suppressNetToggles = false;
            TxtTunState.Text = Lang.T("net_state") + ": " + Lang.T(_tunOn ? "net_on" : "net_off");
            if (error != null)
            {
                _tunWanted = _tunOn;
                LogError("tunnel " + (enabled ? "start" : "stop") + " failed: " + error);
            }
        });
    }
}