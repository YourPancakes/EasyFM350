using System;
using System.Threading;
using System.Windows;
using EasyFM350.Wpf.Backend.Config;
using EasyFM350.Wpf.Backend.Modem;
using EasyFM350.Wpf.Backend.Radio;

namespace EasyFM350.Wpf.UI;

public partial class MainWindow
{
    private void ReadApnFromModem()
    {
        if (!_modem.IsOpen) return;
        if (Interlocked.Exchange(ref _apnReadPending, 1) != 0) return;
        if (!_exec.Post(() =>
            {
                ModemSettingsService.PdpSettings settings;
                try
                {
                    settings = _settingsService.ReadPdp();
                }
                catch (Exception exception)
                {
                    LogError("APN read: " + exception.Message);
                    Interlocked.Exchange(ref _apnReadPending, 0);
                    return;
                }

                OnUi(() =>
                {
                    try
                    {
                        if (settings.Type != null)
                            InpPdp.SelectedItem = PdpProtocol.ToDisplayValue(settings.Type);
                        InpApn.Text = settings.Apn ?? string.Empty;
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _apnReadPending, 0);
                    }
                });
            })) Interlocked.Exchange(ref _apnReadPending, 0);
    }

    private void OnApnWrite(object sender, RoutedEventArgs e)
    {
        if (!_modem.IsOpen || _exec.Pending > 2) return;

        var apn = ApnPolicy.NormalizeForConfiguration(InpApn.Text);
        var pdp = InpPdp.SelectedItem as string ?? "IPv4";
        var user = AtInput.Normalize(InpUser.Text);
        var password = AtInput.Normalize(InpPass.Password, false);
        var authenticationMode = Math.Max(0, InpAuth.SelectedIndex);
        if (!ApnPolicy.IsValidForConfiguration(apn)
            || (authenticationMode > 0 && (!AtInput.IsSafeValue(user) || !AtInput.IsSafeValue(password))))
        {
            ApnWriteFeedback(false);
            return;
        }

        InpApn.Text = apn;
        var managedConnection = _connActive;
        if (!_exec.Post(() =>
            {
                var success = false;
                var wasActive = false;
                var cid = 0;
                try
                {
                    var context = _settingsService.ResolvePdpForConfiguration(apn, pdp);
                    cid = context.Cid;
                    wasActive = context.IsActive;
                    if (cid < 1) throw new InvalidOperationException("No usable PDP context");

                    var desiredType = PdpProtocol.ToModemValue(pdp);
                    var typeMatches = string.Equals(context.Type ?? string.Empty, desiredType,
                        StringComparison.OrdinalIgnoreCase);
                    var activeRepresentsSubmittedApn = wasActive && typeMatches
                                                                 && string.Equals(context.ActiveApn ?? string.Empty,
                                                                     apn, StringComparison.OrdinalIgnoreCase);
                    var definitionAlreadyMatches = typeMatches
                                                   && string.Equals(context.ConfiguredApn ?? string.Empty, apn,
                                                       StringComparison.OrdinalIgnoreCase);
                    var writeDefinition = !activeRepresentsSubmittedApn && !definitionAlreadyMatches;

                    if (writeDefinition && wasActive)
                        throw new InvalidOperationException("Refusing to redefine an unrelated active PDP context");

                    success = !writeDefinition || Backend.Modem.Modem.IsOk(_modem.Send(ModemCommands.DefinePdp(cid, pdp, apn), 4000));
                    if (success)
                        success = Backend.Modem.Modem.IsOk(_modem.Send(
                            ModemCommands.SetAuthentication(cid, authenticationMode, user, password), 4000));
                }
                catch (Exception exception)
                {
                    LogError("APN write: " + exception.Message);
                    success = false;
                }

                OnUi(() =>
                {
                    ApnWriteFeedback(success);
                    if (success && managedConnection) RestartConnection();
                    else if (success) ReadApnFromModem();
                });
            })) ApnWriteFeedback(false);
    }

    private void ApnWriteFeedback(bool ok)
    {
        BtnApnWrite.Content = ok ? Lang.T("ui_apn_saved") : Lang.T("st_error");
        BtnApnWrite.IsEnabled = false;
        _apnFeedbackTimer.Stop();
        _apnFeedbackTimer.Start();
    }

    private void OnApnFeedbackTimerTick(object? sender, EventArgs e)
    {
        _apnFeedbackTimer.Stop();
        BtnApnWrite.Content = Lang.T("ui_apn_write");
        BtnApnWrite.IsEnabled = true;
    }
}