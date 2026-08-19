using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using EasyFM350.Wpf.Backend.Config;

namespace EasyFM350.Wpf.UI;

public partial class MainWindow
{
    private static readonly (string Command, string LabelKey)[] InfoQueries =
    {
        ("ATI", "dev_model"),
        ("AT+CGSN", "IMEI"),
        ("AT+EGMR=0,10", "eSIM IMEI"),
        ("AT+CFSN", "Serial number"),
        ("AT+CIMI", "IMSI"),
        ("AT+CCID", "ICCID"),
        ("AT+CPIN?", "SIM status"),
        ("AT+GTSIMSELECT?", "SIM slot"),
        ("AT+ESLOTSINFO?", "SIM slots"),
        ("AT+ESIMS?", "eSIM"),
        ("AT+GTAPPVER?", "App version"),
        ("AT+GTPKGVER?", "FW package"),
        ("AT+GTBASELINEVER?", "Baseband baseline"),
        ("AT+GTRFHWVER?", "dev_rfw"),
        ("AT+ECAL?", "dev_ecal"),
        ("AT+GTQUERYCALI?", "dev_qflag"),
        ("AT+GTCURCAR?", "Carrier config"),
        ("AT+GTLOCKCAR?", "Carrier lock"),
        ("AT+GTUSBMODE?", "USB mode"),
        ("AT+EHVOLTE?", "VoLTE"),
        ("AT+COPS?", "dev_oper"),
        ("AT+CEREG?", "LTE registration"),
        ("AT+CREG?", "2G/3G registration"),
        ("AT+CGREG?", "GPRS registration"),
        ("AT+CGATT?", "Data attach (PS)"),
        ("AT+ERAT?", "Radio technology"),
        ("AT+CFUN?", "Function mode"),
        ("AT+CSQ", "Signal level"),
        ("AT+RSRP?", "RSRP"),
        ("AT+CESQ", "Signal detail"),
        ("AT+GTACT?", "dev_bands"),
        ("AT+E5GOPT?", "dev_5gopt"),
        ("AT+GTCAINFO?", "dev_ca"),
        ("AT+GTCCINFO?", "Cells"),
        ("AT+GTBANDCFG?", "dev_bandcfg"),
        ("AT+GTDUALSIM?", "dev_dualsim"),
        ("AT+GTSENRDTEMP?", "dev_temp"),
        ("AT+GTSHUTDOWNTEMP?", "Shutdown temperature"),
        ("AT+GTTXPOWER?", "dev_txp"),
        ("AT+CBC", "Supply voltage"),
        ("AT+CCLK?", "Modem clock"),
        ("AT+CGDCONT?", "APN contexts"),
        ("AT+CGPADDR", "dev_pdnip"),
        ("AT+CGCONTRDP", "PDP dynamic")
    };

    private int _imeiAnimGen;

    private int _imeiEditSlot;

    [GeneratedRegex(@"^[A-Za-z0-9]{1,32}$")]
    private static partial Regex SerialNumberRegex();

    [GeneratedRegex(@"^\d{14,15}$")]
    private static partial Regex ImeiRegex();

    private void OnRefreshInfo(object sender, RoutedEventArgs e)
    {
        if (!_modem.IsOpen) return;
        if (_exec.Pending == 0) RefreshInfo(false);
    }

    private void RefreshInfo(bool quiet, bool force = false)
    {
        if (!_modem.IsOpen || InfoBusy || (!force && _exec.Pending > 0)) return;
        InfoBusy = true;

        var queries = new (string Command, string Label)[InfoQueries.Length];
        for (var index = 0; index < InfoQueries.Length; index++)
            queries[index] = (InfoQueries[index].Command, Lang.T(InfoQueries[index].LabelKey, Lang.Id.En));
        if (!_exec.Post(() => RefreshInfoChunk(queries, 0, new List<InfoRow>(queries.Length), quiet, force)))
            InfoBusy = false;
    }

    private void UpdateInfoBusy()
    {
        if (BtnRefreshInfo != null) BtnRefreshInfo.IsEnabled = !InfoBusy;
    }

    private void RefreshInfoChunk((string Command, string Label)[] queries, int position, List<InfoRow> rows,
        bool quiet, bool force)
    {
        try
        {
            var end = Math.Min(position + 5, queries.Length);
            for (var index = position; index < end; index++)
                rows.Add(QueryInfo(queries[index].Command, queries[index].Label, quiet));
            if (end < queries.Length)
            {
                OnUi(() =>
                {
                    var visible = !_trayMode && WindowState != WindowState.Minimized &&
                                  PageDevice.Visibility == Visibility.Visible;
                    if (!_realExit && _modem.IsOpen && (force || visible)
                        && _exec.Post(() => RefreshInfoChunk(queries, end, rows, quiet, force))) return;
                    InfoBusy = false;
                });
                return;
            }

            OnUi(() =>
            {
                InfoBusy = false;
                if (!_realExit && _modem.IsOpen) InfoList.ItemsSource = rows;
            });
        }
        catch (Exception exception)
        {
            LogError("refresh: " + exception.Message);
            InfoBusy = false;
        }
    }

    private InfoRow QueryInfo(string command, string label, bool quiet)
    {
        var result = _deviceInfo.Query(command, label, quiet);
        return new InfoRow(result.Label, result.Value, result.Raw, result.EditSlot);
    }

    private void OnImeiClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InfoRow row || !row.CanEdit) return;
        if (!_modem.IsOpen) return;
        if (_exec.Pending > 0) return;
        var slot = row.EditSlot;

        if (!_exec.Post(() =>
            {
                string? current = null;
                try
                {
                    current = _identityService.Read(slot);
                }
                catch (Exception ex)
                {
                    LogError("identity read: " + ex.Message);
                }

                OnUi(() =>
                {
                    _imeiEditSlot = slot;
                    ImeiOverlayTitle.Text = slot == 10 ? "Edit eSIM IMEI" :
                        slot == 5 ? "Serial number (EGMR slot 5)" : "Edit physical SIM IMEI";
                    ImeiOverlayBox.MaxLength = slot == 5 ? 32 : 15;
                    ImeiOverlayBox.Text = current ?? string.Empty;
                    ImeiOverlayHint.Text = string.Empty;
                    ShowImeiOverlay();
                    ImeiOverlayBox.SelectAll();
                    ImeiOverlayBox.Focus();
                });
            })) return;
    }

    private void ShowImeiOverlay()
    {
        _imeiAnimGen++;
        ImeiOverlay.Visibility = Visibility.Visible;
        ImeiOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(0.12))));
    }

    private void HideImeiOverlay()
    {
        if (ImeiOverlay.Visibility != Visibility.Visible) return;
        _imeiEditSlot = 0;
        var generation = ++_imeiAnimGen;
        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromSeconds(0.15)));
        fade.Completed += (sender, args) =>
        {
            if (generation == _imeiAnimGen) ImeiOverlay.Visibility = Visibility.Collapsed;
        };
        ImeiOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void OnImeiOverlayCancel(object sender, MouseButtonEventArgs e)
    {
        HideImeiOverlay();
    }

    private void OnImeiOverlayText(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(CharOk);
    }

    private void OnImeiOverlayPaste(object sender, DataObjectPastingEventArgs e)
    {
        var text = e.DataObject.GetData(typeof(string)) as string;
        if (text == null || !text.All(CharOk)) e.CancelCommand();
    }

    private bool CharOk(char value)
    {
        if (_imeiEditSlot == 5) return value is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        return value is >= '0' and <= '9';
    }

    private void OnImeiOverlayCard(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void OnImeiOverlayKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideImeiOverlay();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OnImeiOverlayWrite(sender, e);
            e.Handled = true;
        }
    }

    private void OnImeiOverlayWrite(object sender, RoutedEventArgs e)
    {
        var slot = _imeiEditSlot;
        if (slot == 0) return;
        var value = ImeiOverlayBox.Text.Trim();
        var valid = slot == 5 ? SerialNumberRegex().IsMatch(value) : ImeiRegex().IsMatch(value);
        if (!valid)
        {
            ImeiOverlayHint.Text = slot == 5 ? "1–32 letters/digits." : "14–15 digits.";
            return;
        }

        HideImeiOverlay();
        if (!_exec.Post(() =>
            {
                try
                {
                    var success = WriteIdentity(slot, value);
                    OnUi(() =>
                    {
                        if (success) RefreshInfo(false);
                    });
                }
                catch (Exception ex)
                {
                    LogError("identity write: " + ex.Message);
                }
            })) return;
    }

    private bool WriteIdentity(int slot, string value)
    {
        var name = slot == 7 ? "imei[phys]" : slot == 10 ? "imei[esim]" : "sn";
        var result = _identityService.Write(slot, value);
        if (!result.Accepted)
        {
            LogError(name + ": запись отклонена модемом");
            return false;
        }

        return true;
    }

    public sealed class InfoRow
    {
        public InfoRow(string label, string value, string raw, int editSlot = 0)
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
        public bool CanEdit => EditSlot != 0;
    }
}