using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EasyFM350.Wpf.Backend.Infrastructure;

namespace EasyFM350.Wpf.Backend.Modem;

public sealed class SerialExecutor : IDisposable
{
    private const int QueueCapacity = 256;

    private readonly Channel<Action> _queue = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

    private readonly Task _worker;
    private int _disposed;
    private int _pending;

    public SerialExecutor()
    {
        _worker = Task.Run(ProcessAsync);
    }

    public int Pending => Volatile.Read(ref _pending);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _queue.Writer.TryComplete();
            try
            {
                _worker.GetAwaiter().GetResult();
            }
            catch
            {
            }
        }
    }

    public event Action<string>? OnError;

    public bool Post(Action work)
    {
        if (work == null || Volatile.Read(ref _disposed) != 0) return false;
        Interlocked.Increment(ref _pending);
        if (_queue.Writer.TryWrite(work)) return true;
        Interlocked.Decrement(ref _pending);
        return false;
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    Interlocked.Decrement(ref _pending);
                    continue;
                }

                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    ReportError(ex.Message);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
    }

    private void ReportError(string message)
    {
        EventDispatch.Invoke(OnError, message);
    }
}