using System;

namespace EasyFM350.Wpf.Backend.Infrastructure;

internal static class EventDispatch
{
    public static void Invoke(Action? handlers, Action<Exception>? onSubscriberError = null)
    {
        if (handlers == null) return;
        foreach (var subscriber in handlers.GetInvocationList())
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception exception)
            {
                ReportSubscriberError(onSubscriberError, exception);
            }
    }

    public static void Invoke<T>(Action<T>? handlers, T value, Action<Exception>? onSubscriberError = null)
    {
        if (handlers == null) return;
        foreach (var subscriber in handlers.GetInvocationList())
            try
            {
                ((Action<T>)subscriber)(value);
            }
            catch (Exception exception)
            {
                ReportSubscriberError(onSubscriberError, exception);
            }
    }

    private static void ReportSubscriberError(Action<Exception>? reporter, Exception exception)
    {
        if (reporter == null) return;
        try
        {
            reporter(exception);
        }
        catch
        {
        }
    }
}