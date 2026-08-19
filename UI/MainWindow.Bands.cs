using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EasyFM350.Wpf.Backend.Modem;
using EasyFM350.Wpf.Backend.Radio;

namespace EasyFM350.Wpf.UI;

public partial class MainWindow
{
    private bool _bandPaintActive;
    private bool _bandPaintValue;
    private CheckBox? _lastPaintedBand;
    private bool _suppressApply;

    private void FillBandLists()
    {
        LstLte.Items.Clear();
        foreach (var band in BandPlan.LteAll)
        {
            var checkBox = new CheckBox
            {
                Content = "B" + band,
                ToolTip = BandPlan.BandLabel(band),
                IsChecked = true,
                Style = (Style)FindResource("BandTile"),
                Tag = band
            };
            checkBox.Checked += OnBandChanged;
            checkBox.Unchecked += OnBandChanged;
            LstLte.Items.Add(checkBox);
        }

        LstNr.Items.Clear();
        foreach (var band in BandPlan.NrAll)
        {
            var checkBox = new CheckBox
            {
                Content = "n" + band,
                ToolTip = "5G NR n" + band,
                IsChecked = true,
                Style = (Style)FindResource("BandTile"),
                Tag = band
            };
            checkBox.Checked += OnBandChanged;
            checkBox.Unchecked += OnBandChanged;
            LstNr.Items.Add(checkBox);
        }
    }

    private void OnBandChanged(object sender, RoutedEventArgs e)
    {
        if (!_bandPaintActive) ScheduleBandsModeApply();
    }

    private void OnBandPaintStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list ||
            FindBandCheckBox(list, e.OriginalSource as DependencyObject) is not CheckBox checkBox) return;
        _bandPaintActive = true;
        _bandPaintValue = checkBox.IsChecked != true;
        _lastPaintedBand = null;
        PaintBand(checkBox);
        Mouse.Capture(list, CaptureMode.SubTree);
        e.Handled = true;
    }

    private void OnBandPaintMove(object sender, MouseEventArgs e)
    {
        if (!_bandPaintActive || sender is not ListBox list) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishBandPaint();
            return;
        }

        var target = list.InputHitTest(e.GetPosition(list)) as DependencyObject;
        if (FindBandCheckBox(list, target) is CheckBox checkBox) PaintBand(checkBox);
        e.Handled = true;
    }

    private void OnBandPaintEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_bandPaintActive) return;
        FinishBandPaint();
        e.Handled = true;
    }

    private void OnBandPaintLostCapture(object sender, MouseEventArgs e)
    {
        if (_bandPaintActive) FinishBandPaint();
    }

    private void PaintBand(CheckBox checkBox)
    {
        if (ReferenceEquals(checkBox, _lastPaintedBand)) return;
        checkBox.IsChecked = _bandPaintValue;
        _lastPaintedBand = checkBox;
    }

    private void FinishBandPaint()
    {
        _bandPaintActive = false;
        _lastPaintedBand = null;
        if (Mouse.Captured is ListBox) Mouse.Capture(null);
        ScheduleBandsModeApply();
    }

    private static CheckBox? FindBandCheckBox(ItemsControl owner, DependencyObject? source)
    {
        while (source != null && !ReferenceEquals(source, owner))
        {
            if (source is CheckBox checkBox) return checkBox;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        ScheduleBandsModeApply();
    }

    private void On5gOptChanged(object sender, SelectionChangedEventArgs e)
    {
        ScheduleBandsModeApply();
    }

    private void ScheduleBandsModeApply()
    {
        if (_suppressApply || !_modem.IsOpen) return;
        _bandsApplyTimer.Stop();
        _bandsApplyTimer.Start();
    }

    private void OnRefreshBands(object sender, RoutedEventArgs e)
    {
        if (!_modem.IsOpen) return;
        _pollTimer.Stop();
        _pollTimer.Interval = ModemPollInterval;
        _pollTimer.Start();
        _bandsApplyTimer.Stop();
        ApplyBandsMode(true);
    }

    private void SetApplyBusy(bool busy)
    {
        if (BtnRefreshBands != null) BtnRefreshBands.IsEnabled = !busy;
    }

    private void ApplyBandsMode(bool refreshAfterApply = false)
    {
        if (!_modem.IsOpen) return;
        var rat = CurrentRat();
        var lte = LstLte.Items.OfType<CheckBox>().Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => (int)checkBox.Tag).ToArray();
        var nr = LstNr.Items.OfType<CheckBox>().Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => (int)checkBox.Tag).ToArray();
        var e5gopt = ReferenceEquals(rat, BandPlan.RAT_LTE) || ReferenceEquals(rat, BandPlan.RAT_3G)
            ? 1
            : Cmb5gOpt.SelectedIndex == 1
                ? 3
                : Cmb5gOpt.SelectedIndex == 2
                    ? 5
                    : 7;

        SetApplyBusy(true);
        var changed = false;
        if (!_exec.Post(() =>
            {
                try
                {
                    var currentSettings = _settingsService.ReadBands();
                    var current5gOption = _settingsService.ReadE5gOption();
                    var sendGtact = !currentSettings.HasValues
                                    || !currentSettings.Rat.SequenceEqual(rat)
                                    || !new HashSet<int>(currentSettings.Lte).SetEquals(lte)
                                    || !new HashSet<int>(currentSettings.Nr).SetEquals(nr);
                    if (sendGtact)
                    {
                        if (!Backend.Modem.Modem.IsOk(_modem.Send(BandPlan.BuildGtact(rat, lte, nr), 5000)))
                            throw new InvalidOperationException("GTACT rejected");
                        changed = true;
                    }

                    if (current5gOption != e5gopt)
                    {
                        if (!Backend.Modem.Modem.IsOk(_modem.Send("AT+E5GOPT=" + e5gopt, 5000)))
                            throw new InvalidOperationException("E5GOPT rejected");
                        changed = true;
                    }
                }
                catch (Exception exception)
                {
                    LogError("band settings: " + exception.Message);
                }
                finally
                {
                    OnUi(() => SetApplyBusy(false));
                    ReadBands();
                    if (refreshAfterApply) OnUi(() => Poll(true));
                    if (changed) OnUi(RestartConnection);
                }
            })) SetApplyBusy(false);
    }

    private int[] CurrentRat()
    {
        if (RbMode5g4g.IsChecked == true) return BandPlan.RAT_5G4G;
        if (RbMode4g.IsChecked == true) return BandPlan.RAT_LTE;
        if (RbMode3g.IsChecked == true) return BandPlan.RAT_3G;
        if (RbMode5gsa.IsChecked == true) return BandPlan.RAT_5GSA;
        return BandPlan.RAT_AUTO;
    }

    private void ReadBands()
    {
        _exec.Post(() =>
        {
            ModemSettingsService.BandSettings settings;
            var e5gopt = -1;
            try
            {
                settings = _settingsService.ReadBands();
                e5gopt = _settingsService.ReadE5gOption();
            }
            catch (Exception exception)
            {
                LogError("band read: " + exception.Message);
                return;
            }

            OnUi(() => { ApplyBandSettings(settings, e5gopt); });
        });
    }

    private void ApplyBandSettings(ModemSettingsService.BandSettings settings, int e5gOption)
    {
        _suppressApply = true;
        try
        {
            if (settings.HasValues)
            {
                foreach (CheckBox checkBox in LstLte.Items)
                    checkBox.IsChecked = settings.Lte.Contains((int)checkBox.Tag);
                foreach (CheckBox checkBox in LstNr.Items) checkBox.IsChecked = settings.Nr.Contains((int)checkBox.Tag);
                if (settings.Rat.SequenceEqual(BandPlan.RAT_AUTO)) RbModeAuto.IsChecked = true;
                else if (settings.Rat.SequenceEqual(BandPlan.RAT_5G4G)) RbMode5g4g.IsChecked = true;
                else if (settings.Rat.SequenceEqual(BandPlan.RAT_LTE)) RbMode4g.IsChecked = true;
                else if (settings.Rat.SequenceEqual(BandPlan.RAT_3G)) RbMode3g.IsChecked = true;
                else if (settings.Rat.SequenceEqual(BandPlan.RAT_5GSA)) RbMode5gsa.IsChecked = true;
            }

            if (e5gOption >= 0) Cmb5gOpt.SelectedIndex = e5gOption == 3 ? 1 : e5gOption == 5 ? 2 : 0;
        }
        finally
        {
            _suppressApply = false;
        }
    }

    private void LoadInitialSettings()
    {
        if (!_exec.Post(() =>
            {
                try
                {
                    _modem.Send("AT+CMEE=2");
                }
                catch (Exception exception)
                {
                    LogError("cmee: " + exception.Message);
                }

                ModemSettingsService.InitialSettings settings;
                try
                {
                    settings = _settingsService.ReadInitial();
                }
                catch (Exception exception)
                {
                    LogError("settings load: " + exception.Message);
                    return;
                }

                OnUi(() =>
                {
                    ApplyBandSettings(settings.Bands, settings.E5gOption);
                    if (settings.Pdp.Type != null)
                        InpPdp.SelectedItem = PdpProtocol.ToDisplayValue(settings.Pdp.Type);
                    if (settings.Pdp.Apn != null) InpApn.Text = settings.Pdp.Apn;
                    RefreshInfo(true, true);
                });
            })) LogError("settings load: executor unavailable");
    }
}