using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Ochoko.Core;

namespace Ochoko;

/// <summary>PowerRequestSystemRequired だけを出し、画面は点けない。</summary>
public sealed class PowerRequest : IPowerRequest, IDisposable
{
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;
    private const int PowerRequestSystemRequired = 1;

    // REASON_CONTEXT の Reason は共用体で、Detailed 側の大きさに合わせて余白を置く
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
        public IntPtr Pad1;
        public IntPtr Pad2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr _handle;
    private IntPtr _reason;
    private bool _held;

    public void Acquire()
    {
        if (_held)
        {
            return;
        }

        if (_handle == IntPtr.Zero)
        {
            _reason = Marshal.StringToHGlobalUni("ochoko keep-awake");
            var context = new ReasonContext
            {
                Version = PowerRequestContextVersion,
                Flags = PowerRequestContextSimpleString,
                SimpleReasonString = _reason,
            };
            _handle = PowerCreateRequest(ref context);
            if (_handle == IntPtr.Zero || _handle == new IntPtr(-1))
            {
                _handle = IntPtr.Zero;
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        if (!PowerSetRequest(_handle, PowerRequestSystemRequired))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _held = true;
    }

    public void Release()
    {
        if (!_held)
        {
            return;
        }

        if (!PowerClearRequest(_handle, PowerRequestSystemRequired))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _held = false;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }

        if (_reason != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_reason);
            _reason = IntPtr.Zero;
        }
    }
}
