using System.Globalization;
using System.Threading;
using System.Windows;

namespace EasyFM350.Wpf;

public partial class App : Application
{
    public App()
    {
        var en = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = en;
        Thread.CurrentThread.CurrentUICulture = en;

        DispatcherUnhandledException += (s, e) =>
        {
            try
            {
                (MainWindow as UI.MainWindow)?.EmergencyCleanup();
            }
            catch
            {
            }

            e.Handled = true;
            Shutdown(1);
        };
    }
}