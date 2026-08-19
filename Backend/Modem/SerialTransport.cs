using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace EasyFM350.Wpf.Backend.Modem;

public sealed partial class SerialTransport : ITransport
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint PurgeAll = 0x000F;
    private const uint DcbBinaryDtrRts = 0x1011;
    private const uint MaxDword = uint.MaxValue;
    private const int ReadBufferSize = 4096;

    private readonly string _portName;
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private readonly object _stateSync = new();
    private SafeFileHandle? _handle;

    public SerialTransport(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName) || !PortNameRegex().IsMatch(portName))
            throw new ArgumentException("Valid COM port name required.", nameof(portName));
        _portName = portName;
    }

    public bool IsOpen
    {
        get
        {
            lock (_stateSync)
            {
                return IsUsable(_handle);
            }
        }
    }

    public void Open()
    {
        lock (_stateSync)
        {
            CloseCore();
            var handle = CreateFile("\\\\.\\" + _portName, GenericRead | GenericWrite, 0, IntPtr.Zero,
                OpenExisting, 0, IntPtr.Zero);
            if (!IsUsable(handle))
            {
                handle.Dispose();
                throw LastIoException("Open " + _portName);
            }

            try
            {
                Configure(handle);
                if (!PurgeComm(handle, PurgeAll)) throw LastIoException("Purge " + _portName);
                _handle = handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }

    public void Close()
    {
        lock (_stateSync)
        {
            CloseCore();
        }
    }

    public void Write(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.ASCII.GetBytes(value);
        lock (_stateSync)
        {
            var handle = RequireHandle();
            if (!WriteFile(handle, bytes, bytes.Length, out var written, IntPtr.Zero))
                throw LastIoException("Write " + _portName);
            if (written != bytes.Length)
                throw new IOException("Incomplete serial write: " + written + "/" + bytes.Length + " bytes.");
        }
    }

    public string ReadAvailable()
    {
        lock (_stateSync)
        {
            var handle = RequireHandle();
            if (!ClearCommError(handle, out _, out var status))
                throw LastIoException("Read status " + _portName);
            if (status.InputQueueBytes == 0) return string.Empty;

            var requested = (int)Math.Min(status.InputQueueBytes, ReadBufferSize);
            if (!ReadFile(handle, _readBuffer, requested, out var read, IntPtr.Zero))
                throw LastIoException("Read " + _portName);
            return read == 0 ? string.Empty : Encoding.ASCII.GetString(_readBuffer, 0, read);
        }
    }

    [GeneratedRegex(@"^COM\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PortNameRegex();

    private static void Configure(SafeFileHandle handle)
    {
        if (!SetupComm(handle, 65536, 65536)) throw LastIoException("Configure serial buffers");

        var dcb = new DeviceControlBlock { Length = (uint)Marshal.SizeOf<DeviceControlBlock>() };
        if (!GetCommState(handle, ref dcb)) throw LastIoException("Read serial settings");
        dcb.BaudRate = 115200;
        dcb.Flags = DcbBinaryDtrRts;
        dcb.ByteSize = 8;
        dcb.Parity = 0;
        dcb.StopBits = 0;
        if (!SetCommState(handle, ref dcb)) throw LastIoException("Apply serial settings");

        var timeouts = new CommTimeouts
        {
            ReadIntervalTimeout = MaxDword,
            WriteTotalTimeoutConstant = 1000
        };
        if (!SetCommTimeouts(handle, ref timeouts)) throw LastIoException("Apply serial timeouts");
    }

    private SafeFileHandle RequireHandle()
    {
        var handle = _handle;
        return IsUsable(handle) ? handle! : throw new ObjectDisposedException(nameof(SerialTransport));
    }

    private void CloseCore()
    {
        var handle = _handle;
        _handle = null;
        handle?.Dispose();
    }

    private static bool IsUsable(SafeFileHandle? handle)
    {
        return handle != null && !handle.IsInvalid && !handle.IsClosed;
    }

    private static IOException LastIoException(string operation)
    {
        return new IOException(operation + " failed.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupComm(SafeFileHandle handle, uint inputBufferSize, uint outputBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCommState(SafeFileHandle handle, ref DeviceControlBlock dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommState(SafeFileHandle handle, ref DeviceControlBlock dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommTimeouts(SafeFileHandle handle, ref CommTimeouts timeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PurgeComm(SafeFileHandle handle, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClearCommError(SafeFileHandle handle, out uint errors, out CommStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, int bytesToRead,
        out int bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, int bytesToWrite,
        out int bytesWritten, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceControlBlock
    {
        public uint Length;
        public uint BaudRate;
        public uint Flags;
        public ushort Reserved;
        public ushort XonLimit;
        public ushort XoffLimit;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EventChar;
        public ushort Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommStatus
    {
        public uint Flags;
        public uint InputQueueBytes;
        public uint OutputQueueBytes;
    }
}