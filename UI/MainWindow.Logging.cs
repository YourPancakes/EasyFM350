namespace EasyFM350.Wpf.UI;

public partial class MainWindow
{
    private void LogModem(string message)
    {
        AppendLog(message);
    }

    private void LogError(string message)
    {
        AppendLog(message);
    }

    private void AppendLog(string message)
    {
        if (TxtLog == null || string.IsNullOrWhiteSpace(message)) return;
        _logBuffer.Append(message);
    }

    private void FlushLog()
    {
        if (TxtLog == null) return;
        var batch = _logBuffer.Drain();
        if (batch.Text == null) return;
        var atBottom = TxtLog.VerticalOffset + TxtLog.ViewportHeight >= TxtLog.ExtentHeight - 2;
        TxtLog.Text = batch.Text;
        if (atBottom) TxtLog.ScrollToEnd();
    }
}