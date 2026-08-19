using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EasyFM350.Wpf.Backend.Infrastructure;

public readonly record struct LogBatch(string? Text, int EntryCount);

public sealed class LogBuffer
{
    private const int MaxEntries = 50;
    private const int MaxEntryLength = 8192;
    private readonly Queue<string> _entries = new(MaxEntries);
    private readonly object _sync = new();
    private bool _changed;

    public void Append(string message)
    {
        message ??= string.Empty;
        if (message.Length > MaxEntryLength) message = message.Substring(0, MaxEntryLength);
        var line = "[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + message +
                   Environment.NewLine;
        lock (_sync)
        {
            if (_entries.Count == MaxEntries) _entries.Dequeue();
            _entries.Enqueue(line);
            _changed = true;
        }
    }

    public LogBatch Drain()
    {
        lock (_sync)
        {
            if (!_changed) return default;
            _changed = false;
            var text = new StringBuilder(_entries.Count * 80);
            foreach (var entry in _entries) text.Append(entry);
            return new LogBatch(text.ToString(), _entries.Count);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _changed = false;
        }
    }
}