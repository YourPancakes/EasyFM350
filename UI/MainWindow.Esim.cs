using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EasyFM350.Wpf.Backend.Config;
using EasyFM350.Wpf.Backend.Esim;
using EasyFM350.Wpf.Backend.Modem;
using EasyFM350.Wpf.Backend.Radio;

namespace EasyFM350.Wpf.UI;

public partial class MainWindow
{
    private readonly DispatcherTimer _esimDeleteArmTimer = new();
    private Button? _deleteArmedButton;
    private string? _deleteArmedIccid;
    private int _esimAnimGen;
    private bool _esimBusy;
    private List<EsimProfileRow>? _esimRows;
    private int _simSlot = -1;
    private bool _suppressSimToggle;

    private void OnSimSlotChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressSimToggle) return;
        if (sender == RbEsim && RbEsim.IsChecked == true) SwitchSimSlot(1);
        else if (sender == RbSim && RbSim.IsChecked == true) SwitchSimSlot(0);
    }

    private void OnSimPillClick(object sender, RoutedEventArgs e)
    {
        if (_suppressSimToggle) return;
        if (sender == RbEsim && _simSlot == 1) ShowEsimOverlay();
    }

    private void SwitchSimSlot(int slot)
    {
        if (!_modem.IsOpen || slot == _simSlot || _exec.Pending > 2)
        {
            RevertSimToggle();
            return;
        }

        var reconnect = _connActive;
        if (!_exec.Post(() =>
            {
                if (reconnect)
                {
                    Interlocked.Exchange(ref _disconnectPending, 1);
                    DisconnectWorker();
                }

                bool success;
                try
                {
                    success = Backend.Modem.Modem.IsOk(_modem.Send("AT+GTDUALSIM=" + slot, 8000));
                }
                catch (Exception ex)
                {
                    LogError("sim slot: " + ex.Message);
                    success = false;
                }

                ModemSettingsService.PdpSettings? shown = null;
                if (success)
                    try
                    {
                        Thread.Sleep(1500);
                        shown = _settingsService.ReadPdp();
                    }
                    catch (Exception ex)
                    {
                        LogError("sim slot apn: " + ex.Message);
                    }

                var pdp = shown;
                OnUi(() =>
                {
                    if (!success)
                    {
                        LogError("sim slot: AT+GTDUALSIM=" + slot + " failed");
                        RevertSimToggle();
                        if (reconnect) OnConnect();
                        return;
                    }

                    _simSlot = slot;
                    _connection.DataCid = 0;
                    if (pdp != null)
                    {
                        if (pdp.Type != null)
                            InpPdp.SelectedItem = PdpProtocol.ToDisplayValue(pdp.Type);
                        InpApn.Text = pdp.Apn ?? string.Empty;
                    }
                    else
                    {
                        InpApn.Text = string.Empty;
                    }

                    InpAuth.SelectedIndex = 0;
                    InpUser.Text = string.Empty;
                    InpPass.Password = string.Empty;
                    InpProxyEndpoint.Text = string.Empty;
                    RefreshInfo(false);
                    if (slot == 1) ShowEsimOverlay();
                    else HideEsimOverlay();
                    if (reconnect) OnConnect();
                });
            })) RevertSimToggle();
    }

    private void QuerySimSlot()
    {
        if (!_modem.IsOpen) return;
        _exec.Post(() =>
        {
            var slot = -1;
            try
            {
                var fields = Backend.Modem.Modem.Fields(_modem.Send("AT+GTDUALSIM?", 4000), "+GTDUALSIM");
                if (fields.Length > 0 && int.TryParse(fields[0], out var value) && value is 0 or 1) slot = value;
            }
            catch (Exception exception)
            {
                LogError("sim slot query: " + exception.Message);
            }

            OnUi(() =>
            {
                RbSim.IsEnabled = true;
                RbEsim.IsEnabled = true;
                if (slot < 0) return;
                _simSlot = slot;
                RevertSimToggle();
            });
        });
    }

    private void RevertSimToggle()
    {
        _suppressSimToggle = true;
        RbSim.IsChecked = _simSlot == 0;
        RbEsim.IsChecked = _simSlot == 1;
        _suppressSimToggle = false;
    }

    private void ShowEsimOverlay()
    {
        if (EsimOverlay.Visibility != Visibility.Visible)
        {
            _esimAnimGen++;
            EsimOverlay.Visibility = Visibility.Visible;
            EsimOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(0.12))));
        }

        EsimStatus.Text = "";
        LoadEsimData();
    }

    private void HideEsimOverlay()
    {
        if (EsimOverlay.Visibility != Visibility.Visible) return;
        var generation = ++_esimAnimGen;
        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromSeconds(0.15)));
        fade.Completed += (sender, args) =>
        {
            if (generation == _esimAnimGen) EsimOverlay.Visibility = Visibility.Collapsed;
        };
        EsimOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void OnEsimOverlayCancel(object sender, MouseButtonEventArgs e)
    {
        HideEsimOverlay();
    }

    private void OnEsimOverlayCard(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void OnEsimOverlayKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideEsimOverlay();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OnEsimDownload(sender, e);
            e.Handled = true;
        }
    }

    private void SetEsimBusy(bool busy)
    {
        _esimBusy = busy;
        BtnEsimDownload.IsEnabled = !busy;
    }

    private void LoadEsimData()
    {
        if (!_modem.IsOpen || _esimBusy) return;
        SetEsimBusy(true);
        if (!_exec.Post(() =>
            {
                string? error = null;
                EsimChipInfo? chip = null;
                List<EsimProfile>? profiles = null;
                try
                {
                    if (!_esim.EnsureEsimSlot())
                    {
                        error = "AT+GTDUALSIM=1";
                    }
                    else
                    {
                        var chipResult = _esim.RunLpac("chip", "info");
                        if (!chipResult.Ok)
                        {
                            error = "chip info: " + chipResult.Message;
                        }
                        else
                        {
                            chip = EsimChipInfo.FromJson(chipResult.Data);
                            var profileResult = _esim.RunLpac("profile", "list");
                            if (!profileResult.Ok)
                            {
                                error = "profile list: " + profileResult.Message;
                            }
                            else
                            {
                                profiles = EsimProfile.ListFromJson(profileResult.Data);
                                var notifResult = _esim.RunLpac("notification", "list");
                                if (notifResult.Ok && EsimNotification.ListFromJson(notifResult.Data).Count > 0)
                                {
                                    var processed = _esim.ProcessAllNotifications();
                                    if (!processed.Ok) LogModem("[esim] reports: " + processed.Message);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                OnUi(() => PopulateEsim(chip, profiles, error));
            })) SetEsimBusy(false);
    }

    private void PopulateEsim(EsimChipInfo? chip, List<EsimProfile>? profiles, string? error)
    {
        SetEsimBusy(false);
        if (error != null)
        {
            EsimStatus.Text = Lang.T("st_error") + ": " + error;
            return;
        }

        EsimStatus.Text = "";
        if (chip != null && chip.Eid != null)
            EsimEid.Text = "EID " + chip.Eid
                                  + (chip.FreeMemory != null
                                      ? "   ·   " + Lang.T("esim_free") + " " + chip.FreeMemory.Value / 1024 + " KB"
                                      : "");

        _esimRows = new List<EsimProfileRow>();
        if (profiles != null)
            foreach (var profile in profiles)
                _esimRows.Add(new EsimProfileRow(profile));
        EsimProfiles.ItemsSource = _esimRows;
        EsimEmpty.Text = Lang.T("esim_empty");
        EsimEmpty.Visibility = _esimRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RunEsimOp(Func<LpacResult> op, bool refreshApn = false)
    {
        if (_esimBusy || !_modem.IsOpen) return;
        SetEsimBusy(true);
        if (!_exec.Post(() =>
            {
                LpacResult result;
                try
                {
                    result = op();
                }
                catch (Exception ex)
                {
                    result = LpacResult.Error(ex.Message);
                }

                OnUi(() =>
                {
                    SetEsimBusy(false);
                    StopEsimFill();
                    if (!result.Ok)
                    {
                        var detail = result.ErrorDetail;
                        EsimStatus.Text = Lang.T("st_error") + ": " + result.Message
                                          + (string.IsNullOrEmpty(detail) ? "" : " — " + detail);
                        return;
                    }

                    if (refreshApn)
                    {
                        ReadApnFromModem();
                        RestartConnection();
                    }

                    LoadEsimData();
                });
            })) SetEsimBusy(false);
    }

    private void StopEsimFill()
    {
        EsimWriteFill.Visibility = Visibility.Collapsed;
    }

    private void OnEsimProfileClick(object sender, MouseButtonEventArgs e)
    {
        if (_esimBusy) return;
        if (IsOnButton(e.OriginalSource as DependencyObject)) return;
        var iccid = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(iccid) || _esimRows == null) return;
        var row = _esimRows.Find(r => r.Iccid == iccid);
        if (row == null) return;
        if (row.Enabled)
        {
            RunEsimOp(() => _esim.RestartSimSlot(), true);
            return;
        }

        RunEsimOp(() => _esim.EnableProfile(iccid), true);
    }

    private static bool IsOnButton(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is Button) return true;
            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void OnEsimProfileDelete(object sender, RoutedEventArgs e)
    {
        if (_esimBusy) return;
        var button = sender as Button;
        var iccid = button?.Tag as string;
        if (string.IsNullOrEmpty(iccid) || button == null) return;
        if (_deleteArmedIccid != iccid)
        {
            ResetEsimDeleteArm();
            _deleteArmedIccid = iccid;
            _deleteArmedButton = button;
            button.Content = Lang.T("esim_confirm");
            _esimDeleteArmTimer.Stop();
            _esimDeleteArmTimer.Start();
            return;
        }

        _esimDeleteArmTimer.Stop();
        _deleteArmedIccid = null;
        _deleteArmedButton = null;
        RunEsimOp(() => _esim.DeleteProfile(iccid));
    }

    private void OnEsimDeleteArmTimerTick(object? sender, EventArgs e)
    {
        ResetEsimDeleteArm();
    }

    private void ResetEsimDeleteArm()
    {
        _esimDeleteArmTimer.Stop();
        if (_deleteArmedButton != null)
            _deleteArmedButton.Content = Lang.T("esim_delete");
        _deleteArmedButton = null;
        _deleteArmedIccid = null;
    }

    private void OnEsimDownload(object sender, RoutedEventArgs e)
    {
        if (_esimBusy) return;
        var code = EsimCode.Text.Trim();
        if (!code.StartsWith("LPA:", StringComparison.OrdinalIgnoreCase) || code.Split('$').Length < 3)
        {
            EsimStatus.Text = "LPA:1$<sm-dp+>$<code>";
            return;
        }

        RunEsimOp(() =>
        {
            var result = _esim.RunLpac("profile", "download", "-a", code);
            if (!result.Ok) return result;
            return _esim.RestartSimSlot().Ok
                ? result
                : LpacResult.Error("profile downloaded, but modem restart failed");
        });
        EsimWriteFill.Progress = 0;
        EsimWriteFill.Visibility = Visibility.Visible;
    }

    private sealed class EsimProfileRow
    {
        public EsimProfileRow(EsimProfile profile)
        {
            Iccid = profile.Iccid;
            Title = profile.Title;
            Enabled = profile.Enabled;
            DeleteText = Lang.T("esim_delete");
        }

        public string Iccid { get; }
        public string Title { get; }
        public string DeleteText { get; }
        public bool Enabled { get; }
    }
}