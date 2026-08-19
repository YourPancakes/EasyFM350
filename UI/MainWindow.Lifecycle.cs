using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using EasyFM350.Wpf.Backend.Config;

namespace EasyFM350.Wpf.UI;

public partial class MainWindow
{
    private volatile bool _realExit;
    private volatile bool _sessionEnd;
    private HwndSource? _windowSource;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WndProc);
    }

    protected override void OnClosed(EventArgs e)
    {
        var source = _windowSource;
        _windowSource = null;
        if (source != null) source.RemoveHook(WndProc);
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0016 && wParam == IntPtr.Zero) _sessionEnd = false;
        return IntPtr.Zero;
    }

    private void OnUi(Action action)
    {
        if (action == null || _realExit || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            Dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateActivityCadence();
    }

    private void UpdateActivityCadence()
    {
        var background = _trayMode || WindowState == WindowState.Minimized || !IsVisible;
        _pollTimer.Interval = ModemPollInterval;
        _logFlushTimer.Interval = background ? BackgroundLogInterval : ForegroundLogInterval;
    }

    private void HideToTray()
    {
        if (_tray == null)
        {
            _tray = new NotifyIcon();
            try
            {
                if (Environment.ProcessPath != null)
                    _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            }
            catch
            {
                try
                {
                    _tray.Icon = (Icon)SystemIcons.Application.Clone();
                }
                catch
                {
                }
            }

            if (_tray.Icon == null)
                try
                {
                    _tray.Icon = (Icon)SystemIcons.Application.Clone();
                }
                catch
                {
                }

            if (_tray.Icon == null)
            {
                _tray.Dispose();
                _tray = null;
                LogError("tray: no usable icon; window left open");
                return;
            }

            _tray.Text = "EasyFM350";
            _tray.Visible = true;
            _tray.DoubleClick += (sender, args) => RestoreFromTray();
            _trayMenu = new ContextMenuStrip();
            _trayOpen = _trayMenu.Items.Add(Lang.T("tray_open"), null, (sender, args) => RestoreFromTray());
            _trayExit = _trayMenu.Items.Add(Lang.T("tray_exit"), null, (sender, args) => RealExit());
            _tray.ContextMenuStrip = _trayMenu;
        }

        _trayMode = true;
        UpdateActivityCadence();
        _tray.Visible = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _trayMode = false;
        UpdateActivityCadence();
        if (_tray != null) _tray.Visible = false;
        FlushLog();
    }

    private void RealExit()
    {
        _realExit = true;
        if (_tray != null) _tray.Visible = false;
        Close();
    }
}