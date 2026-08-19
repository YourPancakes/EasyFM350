using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EasyFM350.Wpf.Backend.Network;

internal sealed class SystemProxySettings
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;

    private readonly object _sync = new();
    private bool _active;
    private string? _appliedAddress;
    private bool _enabledValueExisted;
    private object? _previousEnabled;
    private RegistryValueKind _previousEnabledKind;
    private object? _previousServer;
    private RegistryValueKind _previousServerKind;
    private bool _serverValueExisted;

    public void Enable(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        lock (_sync)
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, true)
                            ?? throw new InvalidOperationException(
                                "Windows Internet Settings registry key is unavailable.");
            if (!_active)
            {
                var names = key.GetValueNames();
                _enabledValueExisted = Array.IndexOf(names, "ProxyEnable") >= 0;
                _serverValueExisted = Array.IndexOf(names, "ProxyServer") >= 0;
                _previousEnabled = key.GetValue("ProxyEnable");
                _previousServer = key.GetValue("ProxyServer");
                _previousEnabledKind =
                    _enabledValueExisted ? key.GetValueKind("ProxyEnable") : RegistryValueKind.Unknown;
                _previousServerKind = _serverValueExisted ? key.GetValueKind("ProxyServer") : RegistryValueKind.Unknown;
                _active = true;
            }

            _appliedAddress = address;
            try
            {
                key.SetValue("ProxyServer", address, RegistryValueKind.String);
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                NotifyWindows();
            }
            catch (Exception applyException)
            {
                try
                {
                    RestorePreviousValues(key);
                    NotifyWindows();
                }
                catch (Exception rollbackException)
                {
                    throw new InvalidOperationException(
                        "Failed to apply Windows proxy settings and rollback also failed.",
                        new AggregateException(applyException, rollbackException));
                }
                finally
                {
                    ClearState();
                }

                throw;
            }
        }
    }

    public bool Restore()
    {
        lock (_sync)
        {
            if (!_active) return true;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, true);
                if (key == null) return false;

                if (StillOwnsCurrentSettings(key))
                {
                    RestorePreviousValues(key);
                    NotifyWindows();
                }
            }
            catch
            {
                return false;
            }

            ClearState();
            return true;
        }
    }

    private void RestorePreviousValues(RegistryKey key)
    {
        RestoreValue(key, "ProxyEnable", _enabledValueExisted, _previousEnabled, _previousEnabledKind);
        RestoreValue(key, "ProxyServer", _serverValueExisted, _previousServer, _previousServerKind);
    }

    private bool StillOwnsCurrentSettings(RegistryKey key)
    {
        if (_appliedAddress == null) return false;
        var enabled = key.GetValue("ProxyEnable");
        var server = key.GetValue("ProxyServer") as string;
        return enabled is int flag && flag != 0
                                   && string.Equals(server, _appliedAddress, StringComparison.Ordinal);
    }

    private void ClearState()
    {
        _active = false;
        _appliedAddress = null;
        _previousEnabled = null;
        _previousServer = null;
    }

    private static void RestoreValue(RegistryKey key, string name, bool existed, object? value, RegistryValueKind kind)
    {
        if (existed && value != null) key.SetValue(name, value, kind);
        else key.DeleteValue(name, false);
    }

    private static void NotifyWindows()
    {
        if (!InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int bufferLength);
}