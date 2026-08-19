using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EasyFM350.Wpf.Backend.Config;
using EasyFM350.Wpf.Backend.Esim;
using EasyFM350.Wpf.Backend.Infrastructure;
using EasyFM350.Wpf.Backend.Modem;
using EasyFM350.Wpf.Backend.Network;
using EasyFM350.Wpf.Backend.Radio;
using Application = System.Windows.Application;

namespace EasyFM350.Wpf.UI;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ModemPollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ForegroundLogInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BackgroundLogInterval = TimeSpan.FromSeconds(5);
    private readonly DispatcherTimer _apnFeedbackTimer = new();
    private readonly DispatcherTimer _bandsApplyTimer = new();
    private readonly SolidColorBrush _brDim;
    private readonly SolidColorBrush _brError;

    private readonly SolidColorBrush _brSuccess;
    private readonly SolidColorBrush _brText;
    private readonly SolidColorBrush _brWarning;
    private readonly DeviceInfoService _deviceInfo;
    private readonly EsimService _esim;
    private readonly SerialExecutor _exec = new();
    private readonly IdentityService _identityService;
    private readonly LogBuffer _logBuffer = new();
    private readonly DispatcherTimer _logFlushTimer = new();
    private readonly Backend.Modem.Modem _modem = new();

    private readonly object _netSync = new();
    private readonly DispatcherTimer _pollTimer = new();
    private readonly ProxyEngine _proxy = new();
    private readonly ModemSettingsService _settingsService;
    private readonly SignalPollingService _signalPolling;
    private readonly SystemProxySettings _systemProxy = new();
    private readonly DispatcherTimer _watchTimer = new();
    private int _apnReadPending;

    private bool _connActive;
    private volatile bool _connBusy;
    private volatile bool _infoBusyFlag;
    private Backend.Radio.SignalParser.Snapshot? _lastSignal;
    private string? _lastTempC;

    private volatile bool _pollBusy;
    private int _tempTick;
    private NotifyIcon? _tray;
    private ContextMenuStrip? _trayMenu;
    private bool _trayMode;
    private ToolStripItem? _trayOpen, _trayExit;
    private volatile bool _tunBusy;
    private volatile bool _tunOn;

    private int _watchMisses;

    public MainWindow()
    {
        InitializeComponent();
        _brSuccess = (SolidColorBrush)FindResource("Success");
        _brWarning = (SolidColorBrush)FindResource("Warning");
        _brError = (SolidColorBrush)FindResource("Error");
        _brText = (SolidColorBrush)FindResource("TextBr");
        _brDim = (SolidColorBrush)FindResource("DimBr");
        _signalPolling = new SignalPollingService(_modem);
        _settingsService = new ModemSettingsService(_modem);
        _deviceInfo = new DeviceInfoService(_modem);
        _identityService = new IdentityService(_modem);
        _esim = new EsimService(_modem);
        _esim.OnTrace += HandleEsimTrace;
        _esim.OnProgress += HandleEsimProgress;
        _esim.OnWriteBytes += HandleEsimWriteBytes;

        _modem.OnLog += LogModem;
        _modem.OnUrc += HandleModemUrc;
        _modem.OnPortLost += HandleModemPortLost;
        _proxy.OnLog += LogError;
        _proxy.OnDied += HandleProxyDied;
        _exec.OnError += HandleExecutorError;

        Application.Current.SessionEnding += OnApplicationSessionEnding;

        _watchTimer.Interval = TimeSpan.FromSeconds(5);
        _watchTimer.Tick += OnWatchTimerTick;
        _watchTimer.Start();
        _pollTimer.Interval = ModemPollInterval;
        _pollTimer.Tick += OnPollTimerTick;
        _logFlushTimer.Interval = ForegroundLogInterval;
        _logFlushTimer.Tick += OnLogFlushTimerTick;
        _logFlushTimer.Start();
        _bandsApplyTimer.Interval = TimeSpan.FromSeconds(10);
        _bandsApplyTimer.Tick += OnBandsApplyTimerTick;
        _reconnectTimer.Tick += OnReconnectTick;
        _apnFeedbackTimer.Interval = TimeSpan.FromSeconds(1.6);
        _apnFeedbackTimer.Tick += OnApnFeedbackTimerTick;
        _esimDeleteArmTimer.Interval = TimeSpan.FromSeconds(3);
        _esimDeleteArmTimer.Tick += OnEsimDeleteArmTimerTick;

        Cmb5gOpt.Items.Add(Lang.T("5gopt_auto"));
        Cmb5gOpt.Items.Add(Lang.T("5gopt_sa"));
        Cmb5gOpt.Items.Add(Lang.T("5gopt_nsa"));
        Cmb5gOpt.SelectedIndex = 0;
        InpPdp.ItemsSource = PdpProtocol.DisplayValues;
        InpPdp.SelectedIndex = 0;
        FillBandLists();

        Retext();
        SetOffline(null);
        WatchModem();
    }

    private bool InfoBusy
    {
        get => _infoBusyFlag;
        set
        {
            _infoBusyFlag = value;
            OnUi(UpdateInfoBusy);
        }
    }

    private void HandleEsimTrace(string message)
    {
        if (!message.StartsWith("apdu transmit [", StringComparison.Ordinal))
            LogModem("[esim] " + message);
    }

    private void HandleEsimProgress(string message)
    {
        OnUi(() => EsimStatus.Text = message);
    }

    private void HandleEsimWriteBytes(long bytes)
    {
        OnUi(() =>
        {
            if (EsimWriteFill.Visibility == Visibility.Visible)
                EsimWriteFill.Progress = Math.Min(0.95, bytes / 49152.0);
        });
    }

    private void HandleModemUrc(string message)
    {
        LogModem("(urc) " + message);
        if (!message.StartsWith("+CGEV", StringComparison.Ordinal)
            || !PdpContext.TryParsePdnDeactivationCid(message, out var cid)) return;

        var activeCid = _connection.DataCid;
        if (activeCid < 1 || cid != activeCid) return;
        _connection.PdnDeactivated = true;
        OnUi(() =>
        {
            if (_connection.DataCid == cid && (_connection.PdnIp != null || _connection.PdnIpv6 != null)) RestartConnection();
        });
    }

    private void HandleModemPortLost()
    {
        OnUi(() => SetOffline("port lost"));
    }

    private void HandleExecutorError(string message)
    {
        LogError("worker: " + message);
    }

    private void OnApplicationSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        _sessionEnd = true;
    }

    private void OnWatchTimerTick(object? sender, EventArgs e)
    {
        WatchModem();
    }

    private void OnPollTimerTick(object? sender, EventArgs e)
    {
        Poll();
    }

    private void OnLogFlushTimerTick(object? sender, EventArgs e)
    {
        FlushLog();
    }

    private void OnBandsApplyTimerTick(object? sender, EventArgs e)
    {
        _bandsApplyTimer.Stop();
        ApplyBandsMode();
    }

    private void UnsubscribeRuntimeEvents()
    {
        _esim.OnTrace -= HandleEsimTrace;
        _esim.OnProgress -= HandleEsimProgress;
        _esim.OnWriteBytes -= HandleEsimWriteBytes;
        _modem.OnLog -= LogModem;
        _modem.OnUrc -= HandleModemUrc;
        _modem.OnPortLost -= HandleModemPortLost;
        _proxy.OnLog -= LogError;
        _proxy.OnDied -= HandleProxyDied;
        _exec.OnError -= HandleExecutorError;
        Application.Current.SessionEnding -= OnApplicationSessionEnding;
        _watchTimer.Tick -= OnWatchTimerTick;
        _pollTimer.Tick -= OnPollTimerTick;
        _logFlushTimer.Tick -= OnLogFlushTimerTick;
        _bandsApplyTimer.Tick -= OnBandsApplyTimerTick;
        _reconnectTimer.Tick -= OnReconnectTick;
        _apnFeedbackTimer.Tick -= OnApnFeedbackTimerTick;
        _esimDeleteArmTimer.Tick -= OnEsimDeleteArmTimerTick;
    }

    private void OnLangSwitch(object s, RoutedEventArgs e)
    {
        Lang.Current = Lang.Current == Lang.Id.Ru ? Lang.Id.En : Lang.Id.Ru;
        BtnLang.Content = Lang.Current == Lang.Id.Ru ? "EN" : "RU";
        Retext();
    }

    private void Retext()
    {
        NavDash.Content = Lang.T("tab_dash");
        NavBands.Content = Lang.T("tab_bands");
        NavApn.Content = Lang.T("tab_apn");
        NavNet.Content = Lang.T("tab_net");
        NavDevice.Content = Lang.T("tab_device");
        NavLog.Content = Lang.T("tab_log");
        TtlSignal.Text = Lang.T("ui_signal");
        TtlChart.Text = Lang.T("ui_chart");
        TtlLte.Text = "LTE";
        TtlNr.Text = "5G NR";
        TtlMode.Text = Lang.T("ui_mode");
        TtlDevice.Text = Lang.T("ui_dev_title");
        BtnRefreshInfo.ToolTip = Lang.T("ui_refresh_now");
        BtnRefreshBands.ToolTip = Lang.T("ui_refresh_now");
        TtlApnSection.Text = Lang.T("ui_apn_section");
        TtlPdp.Text = Lang.T("ui_pdp");
        TtlAuth.Text = Lang.T("ui_auth");
        TtlUser.Text = Lang.T("ui_user");
        TtlPass.Text = Lang.T("ui_pass");
        TtlApnProxy.Text = Lang.T("ui_apnproxy");
        TtlProxy.Text = Lang.T("ui_net_proxy");
        TtlTun.Text = Lang.T("ui_net_tun");
        TxtTunWarn.Text = Lang.T("ui_tun_warn");
        BtnApnWrite.Content = Lang.T("ui_apn_write");
        EsimTitle.Text = Lang.T("esim_title");
        BtnEsimDownload.Content = Lang.T("esim_download");

        TxtProxyState.Text = Lang.T("net_state") + ": " + Lang.T(_proxy.Running ? "net_on" : "net_off");
        TxtTunState.Text = Lang.T("net_state") + ": " + Lang.T(_tunOn ? "net_on" : "net_off");

        RbModeAuto.Content = Lang.T("mode_auto");
        RbMode5g4g.Content = Lang.T("mode_5g4g");
        RbMode4g.Content = Lang.T("mode_4g");
        RbMode3g.Content = Lang.T("mode_3g");
        RbMode5gsa.Content = Lang.T("mode_5gsa");

        var authIdx = InpAuth.SelectedIndex;
        InpAuth.ItemsSource = new[] { Lang.T("auth_none"), "PAP", "CHAP" };
        InpAuth.SelectedIndex = authIdx >= 0 ? authIdx : 0;

        if (_trayOpen != null) _trayOpen.Text = Lang.T("tray_open");
        if (_trayExit != null) _trayExit.Text = Lang.T("tray_exit");
        _suppressApply = true;
        try
        {
            var sel5 = Cmb5gOpt.SelectedIndex;
            Cmb5gOpt.Items[0] = Lang.T("5gopt_auto");
            Cmb5gOpt.Items[1] = Lang.T("5gopt_sa");
            Cmb5gOpt.Items[2] = Lang.T("5gopt_nsa");
            Cmb5gOpt.SelectedIndex = sel5 >= 0 ? sel5 : 0;
        }
        finally
        {
            _suppressApply = false;
        }

        if (_lastSignal != null) RenderSignalText(_lastSignal);
        Chart.InvalidateVisual();
    }

    private static void FadeIn(UIElement el)
    {
        var anim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180));
        anim.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(OpacityProperty, anim);
    }

    private void OnNav(object s, RoutedEventArgs e)
    {
        PageDash.Visibility = s == NavDash ? Visibility.Visible : Visibility.Collapsed;
        PageBands.Visibility = s == NavBands ? Visibility.Visible : Visibility.Collapsed;
        PageApn.Visibility = s == NavApn ? Visibility.Visible : Visibility.Collapsed;
        PageNet.Visibility = s == NavNet ? Visibility.Visible : Visibility.Collapsed;
        PageDevice.Visibility = s == NavDevice ? Visibility.Visible : Visibility.Collapsed;
        PageLog.Visibility = s == NavLog ? Visibility.Visible : Visibility.Collapsed;
        if (s == NavApn) ReadApnFromModem();
        UpdateActivityCadence();
        FadeIn(PageHost);
    }

    private void RenderSignal(Backend.Radio.SignalParser.Snapshot s)
    {
        _lastSignal = s;
        if (s.TempC != null) _lastTempC = s.TempC;
        if (s.HasSignal)
        {
            var rsrp = s.Rsrp.ToString();
            if (TxtRsrp.Text != rsrp) TxtRsrp.Text = rsrp;
            TxtRsrpUnit.Text = "dBm";
            var brush = s.Rsrp >= -90 ? _brSuccess : s.Rsrp >= -100 ? _brWarning : _brError;
            TxtRsrp.Foreground = brush;
            Chart.Add(s.Rsrp);
        }
        else
        {
            if (TxtRsrp.Text != "--") TxtRsrp.Text = "--";
            TxtRsrpUnit.Text = "";
            TxtRsrp.Foreground = _brText;
        }

        RenderSignalText(s);
    }

    private void RenderSignalText(Backend.Radio.SignalParser.Snapshot s)
    {
        var tail = _lastTempC != null ? "   T " + _lastTempC + " °C" : "";
        if (s.HasSignal)
        {
            var grid =
                Lang.T("band") + " " + s.Band + "   PCI " + s.Pci +
                (double.IsNaN(s.SinrDb)
                    ? ""
                    : "   SINR " + s.SinrDb.ToString("0.#", CultureInfo.InvariantCulture) + " dB") + tail;
            if (TxtGrid.Text != grid) TxtGrid.Text = grid;
        }
        else if (TxtGrid.Text != tail)
        {
            TxtGrid.Text = tail;
        }

        var ca = s.Carriers.Count > 0 ? "CA: " + string.Join(" + ", s.Carriers) : "CA: " + Lang.T("ca_none");
        if (TxtCa.Text != ca) TxtCa.Text = ca;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_realExit && !_trayMode && !_sessionEnd)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _realExit = true;
        Hide();

        try
        {
            _watchTimer.Stop();
            _pollTimer.Stop();
            _bandsApplyTimer.Stop();
            _logFlushTimer.Stop();
            _reconnectTimer.Stop();
            _apnFeedbackTimer.Stop();
            _esimDeleteArmTimer.Stop();
        }
        catch (Exception ex)
        {
            LogError("exit timers: " + ex.Message);
        }

        try
        {
            _esim.CancelActiveOperation();
            _exec.Dispose();
        }
        catch (Exception ex)
        {
            LogError("exit worker: " + ex.Message);
        }

        try
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                if (_tray.Icon != null) _tray.Icon.Dispose();
                _tray.Dispose();
                _tray = null;
            }

            if (_trayMenu != null)
            {
                _trayMenu.Dispose();
                _trayMenu = null;
            }
        }
        catch (Exception ex)
        {
            LogError("exit tray: " + ex.Message);
        }

        try
        {
            if (_proxy.Running) _proxy.Stop();
            if (!_systemProxy.Restore()) LogError("exit proxy: Windows proxy settings could not be restored");
        }
        catch (Exception ex)
        {
            LogError("exit proxy: " + ex.Message);
        }

        try
        {
            var cid = _connection.DataCid;
            if (_connection.OwnsDataContext && cid > 0 && _modem.IsOpen)
            {
                var response = _modem.Send(
                    ModemCommands.ActivatePdp(cid, false),
                    _sessionEnd ? 3000 : 10000,
                    slowCommand: true);
                if (!Backend.Modem.Modem.IsOk(response))
                    LogError("exit PDN: deactivate rejected or timed out");
            }

            _connection.OwnsDataContext = false;
        }
        catch (Exception ex)
        {
            LogError("exit PDN: " + ex.Message);
        }

        try
        {
            var gateway = _connection.PdnGateway ?? _connection.LastPdnGateway;
            var iface = _connection.InterfaceName ?? _connection.LastInterfaceName;

            if (Monitor.TryEnter(_netSync, TimeSpan.FromSeconds(_sessionEnd ? 3 : 20)))
            {
                string? routeErr = null, tunErr = null, cleanErr = null;
                try
                {
                    try
                    {
                        RemoveUpstreamRouteCore();
                    }
                    catch (Exception ex2)
                    {
                        routeErr = ex2.Message;
                    }

                    if (iface != null && gateway != null)
                        try
                        {
                            NetConfig.TunnelOff(iface, gateway, false, 3000);
                        }
                        catch (Exception ex2)
                        {
                            tunErr = ex2.Message;
                        }

                    if (iface != null)
                        try
                        {
                            NetConfig.Cleanup(iface, gateway, _connection.PdnDns1, _connection.PdnDns2, 3000);
                        }
                        catch (Exception ex2)
                        {
                            cleanErr = ex2.Message;
                        }
                }
                finally
                {
                    Monitor.Exit(_netSync);
                }

                if (routeErr != null) LogError("exit proxy route: " + routeErr);
                if (tunErr != null) LogError("exit tunneloff: " + tunErr);
                if (cleanErr != null) LogError("exit cleanup: " + cleanErr);
            }
            else
            {
                LogError("exit: netsh busy — cleanup skipped");
            }
        }
        catch (Exception ex)
        {
            LogError("exit netsh: " + ex.Message);
        }

        try
        {
            _modem.Close();
        }
        catch (Exception ex)
        {
            LogError("exit close: " + ex.Message);
        }

        try
        {
            FlushLog();
        }
        catch
        {
        }

        try
        {
            UnsubscribeRuntimeEvents();
        }
        catch
        {
        }

        base.OnClosing(e);
    }

    internal void EmergencyCleanup()
    {
        _realExit = true;
        try
        {
            if (_proxy.Running) _proxy.Stop();
        }
        catch
        {
        }

        try
        {
            _systemProxy.Restore();
        }
        catch
        {
        }

        var gateway = _connection.PdnGateway ?? _connection.LastPdnGateway;
        var iface = _connection.InterfaceName ?? _connection.LastInterfaceName;
        if (iface == null) return;
        if (Monitor.TryEnter(_netSync, TimeSpan.FromSeconds(3)))
            try
            {
                try
                {
                    RemoveUpstreamRouteCore();
                }
                catch
                {
                }

                if (gateway != null)
                    try
                    {
                        NetConfig.TunnelOff(iface, gateway, false, 3000);
                    }
                    catch
                    {
                    }

                try
                {
                    NetConfig.Cleanup(iface, gateway, _connection.PdnDns1, _connection.PdnDns2, 3000);
                }
                catch
                {
                }
            }
            finally
            {
                Monitor.Exit(_netSync);
            }
    }
}